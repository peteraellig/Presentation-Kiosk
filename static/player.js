const socket = io();
const role = new URLSearchParams(location.search).has('omt') ? 'omt' : 'screen';
const stage = document.getElementById('stage');
const ipBadge = document.getElementById('serverIpBadge');
let active = null;
let fallback = null;
let pending = null;
let latest = -1;
let resuming = false;

socket.on('connect', () => {
  latest = -1;
  resuming = true;
  socket.emit('register_presentation', role);
});
window.addEventListener('pagehide', () => socket.disconnect());
window.addEventListener('pageshow', event => { if (event.persisted) socket.connect(); });

function report(run, phase, message = '') {
  if (socket.connected && (run.media.revision === latest || run === active)) {
    socket.emit('playback_report', {revision: run.media.revision, phase, file: run.file, fileVersion: run.node?.dataset.version, message});
  }
}

function dispose(node) {
  if (!node) return;
  if (node._live) { node._live.close(); node.remove(); return; }
  node.onended = node.onerror = node.onload = node.onloadeddata = null;
  if (node.tagName === 'VIDEO') {
    node.pause();
    node.removeAttribute('src');
    node.load();
  }
  node.remove();
}

function stop(run) {
  if (!run) return;
  run.controller.abort();
  clearTimeout(run.timer);
  dispose(run.node);
}

async function mediaList(run) {
  const {type, src} = run.media;
  if (type === 'video' || type === 'image' || type === 'live') return [src];
  const folder = type === 'playlist' ? 'videos_playlist' : String(src).replaceAll('\\', '/').replace(/\/+$/, '');
  const response = await fetch('/filelist?folder=' + encodeURIComponent(folder), {
    cache: 'no-store',
    signal: AbortSignal.any([run.controller.signal, AbortSignal.timeout(15000)])
  });
  if (!response.ok) throw new Error(folder + ' (HTTP ' + response.status + ')');
  const extension = type === 'playlist' ? /\.mp4$/i : /\.(png|jpe?g|gif|bmp)$/i;
  const files = (await response.json()).filter(name => extension.test(name));
  if (!files.length) throw new Error(folder + ' – no playable files');
  return files.map(name => folder + '/' + name);
}

async function imageVersion(file, signal) {
  const path = '/static/' + file.split('/').map(encodeURIComponent).join('/');
  const response = await fetch(path, {method: 'HEAD', cache: 'no-store', signal});
  if (!response.ok) throw new Error(file + ' (HTTP ' + response.status + ')');
  return response.headers.get('ETag') || response.headers.get('Last-Modified') || String(Date.now());
}

function scheduleImageCheck(run) {
  run.timer = setTimeout(() => refreshImage(run), (run.media.seconds ?? 3) * 1000);
}

async function refreshImage(run) {
  const {signal} = run.controller;
  try {
    const files = await mediaList(run);
    const previous = files.indexOf(run.lastAttempt ?? run.file);
    const index = (previous + 1) % files.length;
    const file = files[index];
    if (signal.aborted || active !== run) return;
    run.lastAttempt = file;
    const version = await imageVersion(file, AbortSignal.any([signal, AbortSignal.timeout(15000)]));
    if (signal.aborted || active !== run) return;
    if (file === run.file && version === run.node.dataset.version) {
      if (run.refreshFailed) report(run, 'playing');
      run.refreshFailed = false;
      scheduleImageCheck(run);
      return;
    }
    const node = await prepare(run, file, version);
    if (signal.aborted || active !== run) { dispose(node); return; }
    run.refreshFailed = false;
    run.files = files;
    display(run, node, index);
  } catch (error) {
    if (signal.aborted || active !== run) return;
    run.refreshFailed = true;
    report(run, 'error', error.message);
    scheduleImageCheck(run);
  }
}

// Live source (OMT, or NDI as "ndi:HOST (Name)"): ready when the first picture has arrived (the on-air item stays until then).
async function prepareLive(run, source) {
  const signal = AbortSignal.any([run.controller.signal, AbortSignal.timeout(15000)]);
  let node, cancel;
  try {
    await new Promise((resolve, reject) => {
      cancel = () => reject(new Error(source + ' – no picture from this live source'));
      signal.throwIfAborted();
      signal.addEventListener('abort', cancel, {once: true});
      node = createLiveNode(source, run.media.live_port, role === 'omt', {
        onFirstPicture: resolve,
        onSignal: (lost, message) => {
          if (active !== run || run.node !== node) return;
          report(run, lost ? 'error' : 'playing', message);
        }
      });
    });
    return node;
  } catch (error) {
    dispose(node);
    throw error;
  } finally {
    signal.removeEventListener('abort', cancel);
  }
}

// Prepare off screen. The currently visible element keeps its source, timer and audio.
async function prepare(run, file, version) {
  if (run.media.type === 'live') return prepareLive(run, file);
  const video = ['video', 'playlist'].includes(run.media.type);
  const node = document.createElement(video ? 'video' : 'img');
  const signal = AbortSignal.any([run.controller.signal, AbortSignal.timeout(15000)]);
  const path = '/static/' + file.split('/').map(encodeURIComponent).join('/');
  if (!video && version === undefined) version = await imageVersion(file, signal);
  const url = video ? path : path + '?v=' + encodeURIComponent(version);
  if (!video) node.dataset.version = version;
  let cancel;
  try {
    await new Promise((resolve, reject) => {
      cancel = () => reject(new Error(file + ' – load cancelled or timed out'));
      signal.throwIfAborted();
      signal.addEventListener('abort', cancel, {once: true});
      node.onerror = () => reject(new Error(file + ' – ' + (node.error?.message || 'cannot load or decode')));
      if (video) {
        node.playsInline = true;
        node.preload = 'auto';
        node.onloadeddata = resolve;
        node.loop = run.media.type === 'video';
        node.src = url;
        node.load();
      } else {
        node.onload = resolve;
        node.src = url;
      }
    });
    signal.throwIfAborted();
    return node;
  } catch (error) {
    dispose(node);
    throw error;
  } finally {
    signal.removeEventListener('abort', cancel);
  }
}

const soundButton = document.getElementById('enableSound');

async function playVisibleVideo(run, node) {
  node.muted = false;
  try {
    await node.play();
  } catch (error) {
    if (active !== run || run.node !== node) return false;
    if (error.name !== 'NotAllowedError') {
      recover(run, node, error);
      return false;
    }
    // Chrome can pause a muted autoplay video the moment sound is enabled.
    node.muted = true;
    try { await node.play(); }
    catch (mutedError) { if (active === run && run.node === node) recover(run, node, mutedError); return false; }
    if (active === run && run.node === node && role === 'screen') soundButton.hidden = false;
  }
  return active === run && run.node === node;
}

soundButton.onclick = async () => {
  const node = active?.node;
  if (node?._live) {
    try { await node._live.sound.resume(); soundButton.hidden = !node._live.sound.blocked; } catch {}
    return;
  }
  if (node?.tagName !== 'VIDEO') return;
  node.muted = false;
  try { await node.play(); soundButton.hidden = true; }
  catch { node.muted = true; await node.play().catch(() => {}); }
};

function display(run, node, index) {
  const previous = run.node;
  run.node = node;
  run.file = run.files[index];
  run.index = index;
  stage.replaceChildren(node);
  dispose(previous);
  soundButton.hidden = true;
  const next = () => advance(run);
  if (run.media.type === 'slideshow' || run.media.type === 'image') scheduleImageCheck(run);
  if (run.media.type === 'playlist') node.onended = next;
  node.onerror = () => recover(run, node);
  if (node._live) {
    node._live.show();
    report(run, 'playing');
    // Chrome without a saved sound permission keeps the AudioContext suspended
    // the button follows the AudioContext state, so it also disappears when the sound starts a moment later
    const sound = node._live.sound;
    sound.ready.then(() => {
      const update = () => { if (active === run && run.node === node && role === 'screen') soundButton.hidden = !sound.blocked; };
      update();
      sound.ctx?.addEventListener('statechange', update);
    }).catch(() => {});
  } else if (node.tagName === 'VIDEO') {
    playVisibleVideo(run, node).then(started => { if (started) report(run, 'playing'); });
  } else {
    report(run, 'playing');
  }
}

// A decoder can also fail after a successful start. Keep one previous item paused
// so it can be restored without fetching it again.
function keepPrevious(run) {
  stop(fallback);
  fallback = run;
  if (!run) return;
  // a live source is not kept in the background (it would keep receiving 200 MB/s)
  if (run.node?._live) { stop(run); fallback = null; return; }
  run.controller.abort();
  clearTimeout(run.timer);
  run.node.onended = run.node.onerror = null;
  if (run.node.tagName === 'VIDEO') run.node.pause();
}

async function recover(run, failedNode, error = failedNode.error) {
  if (active !== run || run.node !== failedNode) return;
  const message = run.file + ' – ' + (error?.message || error?.name || 'playback interrupted');
  report(run, 'error', message);
  if (!fallback) return;
  const restored = fallback;
  fallback = null;
  stop(run);
  active = restored;
  restored.controller = new AbortController();
  const node = restored.node;
  restored.node = null;
  display(restored, node, restored.index);
  if (node.tagName === 'VIDEO') {
    try { await node.play(); }
    catch { report(run, 'error', message + '; previous video could not resume'); return; }
  }
  if (active === restored && socket.connected) {
    socket.emit('playback_report', {revision: run.media.revision, phase: 'restored',
      restored: restored.media.revision, file: restored.file, fileVersion: restored.node.dataset.version, message});
  }
}

async function advance(run) {
  if (active !== run) return;
  const index = (run.nextIndex ?? (run.index + 1)) % run.files.length;
  run.nextIndex = index + 1;
  try {
    const node = await prepare(run, run.files[index]);
    if (active !== run) { dispose(node); return; }
    display(run, node, index);
  } catch (error) {
    if (active !== run) return;
    report(run, 'error', error.message);
    if (run.node.tagName === 'VIDEO' && run.node.ended) {
      run.node.currentTime = 0;
      run.node.play().catch(() => report(run, 'error', run.file + ' – replay failed'));
    } else {
      run.timer = setTimeout(() => advance(run), (run.media.seconds ?? 3) * 1000);
    }
  }
}

socket.on('show_media', async media => {
  if (!Number.isInteger(media.revision) || media.revision <= latest) return;
  latest = media.revision;
  stop(pending);
  pending = null;
  if (active && active.media.type === media.type && active.media.src === media.src &&
      active.media.button_id === media.button_id && active.media.seconds === media.seconds && active.node && !active.node.error &&
      (active.node.tagName !== 'VIDEO' || !active.node.paused) && resuming) {
    active.media = media;
    resuming = false;
    report(active, 'playing');
    return;
  }
  resuming = false;
  // Keep the on-air item. Release the older backup before allocating a new decoder.
  stop(fallback);
  fallback = null;
  const run = {media, controller: new AbortController(), file: '', node: null};
  pending = run;
  try {
    run.files = await mediaList(run);
    const node = await prepare(run, run.files[0]);
    if (pending !== run) { dispose(node); return; }
    keepPrevious(active);
    active = run;
    pending = null;
    display(run, node, 0);
  } catch (error) {
    if (pending !== run) return;
    pending = null;
    stop(run);
    report(run, 'error', error.message);
  }
});
