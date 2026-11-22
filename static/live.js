// Kiosk3 live input (OMT / NDI): LiveIn.exe receives the OMT or NDI source, encodes it to H.264 (hardware encoder, low latency)
// and sends it over a WebSocket on 127.0.0.1. This page decodes it with WebCodecs and draws each picture as soon as it is decoded.
// Binary messages:
//   video: [0]=3 [1]=flags(bit0 keyframe) [2..3]=width [4..5]=height [8..15]=time in µs, then H.264 Annex B (SPS/PPS before keyframes)
//   audio: [0]=2 [1]=2 channels [4..7]=sample rate [8..11]=samples per channel, then float32 L plane, R plane
// Page -> LiveIn: {"source": "HOST (Name)"} to watch a source, {"keyframe": 1} after a decoder error.
// In the OMT output (PresenterOmt, ?omt=1) the sound goes into its tap (window.__omtCtx/__omtBus), not to a speaker.

const LIVE_HEADER = 16;

// Audio worklet: ring buffer with ~80 ms start buffer; refills after an underrun, drops when more than 300 ms pile up.
const LIVE_WORKLET = `class LiveOut extends AudioWorkletProcessor {
  constructor() {
    super();
    this.size = sampleRate * 2; this.l = new Float32Array(this.size); this.r = new Float32Array(this.size);
    this.w = 0; this.rd = 0; this.n = 0; this.playing = false;
    this.target = Math.round(sampleRate * 0.08); this.max = Math.round(sampleRate * 0.3);
    this.port.onmessage = e => {
      const {l, r} = e.data;
      for (let i = 0; i < l.length; i++) {
        this.l[this.w] = l[i]; this.r[this.w] = r[i];
        this.w = (this.w + 1) % this.size;
      }
      this.n = Math.min(this.size, this.n + l.length);
      if (this.n > this.max) { const skip = this.n - this.target; this.rd = (this.rd + skip) % this.size; this.n -= skip; }
    };
  }
  process(inputs, outputs) {
    const out = outputs[0], L = out[0], R = out[1] || out[0];
    if (!this.playing && this.n >= this.target) this.playing = true;
    for (let i = 0; i < L.length; i++) {
      if (this.playing && this.n > 0) {
        L[i] = this.l[this.rd]; R[i] = this.r[this.rd];
        this.rd = (this.rd + 1) % this.size; this.n--;
      } else { L[i] = 0; R[i] = 0; }
    }
    if (this.n === 0) this.playing = false;
    return true;
  }
}
registerProcessor('kiosk-live-out', LiveOut);`;

function resample(data, from, to) {
  if (from === to || !from) return data;
  const out = new Float32Array(Math.max(1, Math.round(data.length * to / from)));
  const step = from / to;
  for (let i = 0; i < out.length; i++) {
    const pos = i * step, k = Math.floor(pos), f = pos - k;
    out[i] = (data[k] ?? 0) * (1 - f) + (data[Math.min(k + 1, data.length - 1)] ?? 0) * f;
  }
  return out;
}

class LiveSound {
  constructor(omt) {
    this.omt = omt;
    this.node = null;
    this.ready = this.start();
  }
  async start() {
    if (this.omt && window.__omtCtx && window.__omtBus) {
      this.ctx = window.__omtCtx;   // PresenterOmt: sound only into the OMT stream
      this.shared = true;
    } else {
      this.ctx = new AudioContext({sampleRate: 48000, latencyHint: 'interactive'});
    }
    await this.ctx.audioWorklet.addModule(URL.createObjectURL(new Blob([LIVE_WORKLET], {type: 'application/javascript'})));
    if (this.closed) return;
    this.node = new AudioWorkletNode(this.ctx, 'kiosk-live-out', {numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2]});
    this.node.connect(this.shared ? window.__omtBus : this.ctx.destination);
    // wait for the start (at most 1 s): asking right after resume() still sees "suspended" for a moment
    if (this.ctx.state !== 'running') {
      await Promise.race([this.ctx.resume().catch(() => {}), new Promise(r => setTimeout(r, 1000))]);
    }
  }
  get blocked() { return !this.shared && this.ctx?.state === 'suspended'; }
  resume() { return this.ctx?.resume(); }
  push(buffer) {
    if (!this.node || !this.on) return;   // silent until the picture is on screen
    const view = new DataView(buffer);
    const rate = view.getInt32(4, true), n = view.getInt32(8, true);
    const l = new Float32Array(buffer.slice(LIVE_HEADER, LIVE_HEADER + n * 4));
    const r = new Float32Array(buffer.slice(LIVE_HEADER + n * 4, LIVE_HEADER + n * 8));
    const sr = this.ctx.sampleRate;
    this.node.port.postMessage({l: resample(l, rate, sr), r: resample(r, rate, sr)});
  }
  close() {
    this.closed = true;
    if (this.node) { this.node.port.onmessage = null; this.node.disconnect(); }
    if (!this.shared && this.ctx) this.ctx.close().catch(() => {});
  }
}

// Codec string from the SPS of a keyframe (profile, constraints, level), e.g. "avc1.640028"
function avcCodec(bytes) {
  for (let i = 0; i + 6 < bytes.length; i++) {
    if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1 && (bytes[i + 3] & 0x1f) === 7) {
      return 'avc1.' + [bytes[i + 4], bytes[i + 5], bytes[i + 6]].map(b => b.toString(16).padStart(2, '0')).join('');
    }
  }
  return null;
}

// Creates the canvas for one live source. events: onFirstPicture(), onSignal(lost: bool, message)
function createLiveNode(source, port, omt, events) {
  // WebCodecs and AudioWorklet exist only in a secure context (here: http://localhost on the server PC)
  if (!window.isSecureContext) throw new Error('live video only works on the server PC, opened as http://localhost:' + location.port);
  if (typeof VideoDecoder === 'undefined') throw new Error('this browser has no WebCodecs (needed for the live input)');
  const canvas = document.createElement('canvas');
  canvas.width = 1920; canvas.height = 1080;
  const ctx2d = canvas.getContext('2d', {alpha: false, desynchronized: true});

  const state = {ws: null, closed: false, first: false, lastPicture: 0, lost: false, retry: 0, watchdog: 0,
                 decoder: null, codec: '', waitKey: true};
  const sound = new LiveSound(omt);
  sound.ready.catch(error => events.onSignal?.(false, 'sound: ' + error.message));

  function show(frame) {
    if (state.closed) { frame.close(); return; }
    if (canvas.width !== frame.displayWidth || canvas.height !== frame.displayHeight) {
      canvas.width = frame.displayWidth; canvas.height = frame.displayHeight;
    }
    ctx2d.drawImage(frame, 0, 0, canvas.width, canvas.height);   // drawn right away: no wait for the next animation frame
    frame.close();
    state.lastPicture = performance.now();
    if (!state.first) { state.first = true; events.onFirstPicture?.(); }
    if (state.lost) { state.lost = false; events.onSignal?.(false, ''); }
  }

  // decoder error or backlog: start again at the next keyframe
  function resetDecoder(message) {
    if (state.decoder && state.decoder.state !== 'closed') state.decoder.close();
    state.decoder = null;
    state.waitKey = true;
    if (state.ws?.readyState === WebSocket.OPEN) state.ws.send(JSON.stringify({keyframe: 1}));
    if (message) console.warn('live decoder:', message);
  }

  function decode(buffer) {
    const v = new DataView(buffer);
    const key = (v.getUint8(1) & 1) === 1;
    const data = new Uint8Array(buffer, LIVE_HEADER);
    if (state.waitKey && !key) return;
    if (key) {
      const codec = avcCodec(data) || state.codec || 'avc1.640033';
      if (!state.decoder || codec !== state.codec) {
        if (state.decoder && state.decoder.state !== 'closed') state.decoder.close();
        state.decoder = new VideoDecoder({output: show, error: e => resetDecoder(e.message)});
        state.decoder.configure({codec, optimizeForLatency: true});
        state.codec = codec;
      }
      state.waitKey = false;
    }
    if (!state.decoder) return;
    if (state.decoder.decodeQueueSize > 10) { resetDecoder('decoder falls behind'); return; }
    try {
      state.decoder.decode(new EncodedVideoChunk({type: key ? 'key' : 'delta', timestamp: Number(v.getBigInt64(8, true)), data}));
    } catch (error) {
      resetDecoder(error.message);
    }
  }

  function connect() {
    if (state.closed) return;
    const ws = new WebSocket(`ws://127.0.0.1:${port}/live`);
    ws.binaryType = 'arraybuffer';
    state.ws = ws;
    state.waitKey = true;
    ws.onopen = () => ws.send(JSON.stringify({source}));
    ws.onmessage = e => {
      const type = new Uint8Array(e.data, 0, 1)[0];
      if (type === 3) decode(e.data);
      else if (type === 2) sound.push(e.data);
    };
    ws.onclose = () => {
      if (state.closed) return;
      state.retry = setTimeout(connect, 1000);
    };
  }

  // no picture for 2 s: report it, keep the last picture on screen and keep waiting
  state.watchdog = setInterval(() => {
    const quiet = performance.now() - state.lastPicture > 2000;
    if (state.first && quiet && !state.lost) {
      state.lost = true;
      events.onSignal?.(true, source + ' – no signal');
    }
  }, 500);

  connect();
  canvas._live = {
    sound,
    show() { sound.on = true; },
    close() {
      state.closed = true;
      clearTimeout(state.retry);
      clearInterval(state.watchdog);
      if (state.ws) { state.ws.onclose = state.ws.onmessage = null; state.ws.close(); }
      if (state.decoder && state.decoder.state !== 'closed') state.decoder.close();
      sound.close();
    }
  };
  return canvas;
}
