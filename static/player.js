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
    socket.emit('playback_report', {revision: run.media.revision, phase, file: run.file, message});
  }
}

function dispose(node) {
  if (!node) return;
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
  if (type === 'video' || type === 'image') return [src];
  const folder = type === 'playlist' ? 'videos_playlist' : String(src).replaceAll('\\', '/').replace(/\/+$/, '');
  const response = await fetch('/filelist?folder=' + encodeURIComponent(folder), {
    signal: AbortSignal.any([run.controller.signal, AbortSignal.timeout(15000)])
  });
  if (!response.ok) throw new Error(folder + ' (HTTP ' + response.status + ')');
  const extension = type === 'playlist' ? /\.mp4$/i : /\.(png|jpe?g|gif|bmp)$/i;
  const files = (await response.json()).filter(name => extension.test(name));
  if (!files.length) throw new Error(folder + ' – no playable files');
  return files.map(name => folder + '/' + name);
}

// Prepare off screen. The currently visible element keeps its source, timer and audio.
async function prepare(run, file) {
  const video = ['video', 'playlist'].includes(run.media.type);
  const node = document.createElement(video ? 'video' : 'img');
  const signal = AbortSignal.any([run.controller.signal, AbortSignal.timeout(15000)]);
  let cancel;
  try {
    await new Promise((resolve, reject) => {
      cancel = () => reject(new Error(file + ' – load cancelled or timed out'));
      signal.throwIfAborted();
      signal.addEventListener('abort', cancel, {once: true});
      node.onerror = () => reject(new Error(file + ' – ' + (node.error?.message || 'cannot load or decode')));
      const url = '/static/' + file.split('/').map(encodeURIComponent).join('/');
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
  if (run.media.type === 'slideshow' && run.files.length > 1) {
    run.timer = setTimeout(next, (run.media.seconds ?? 3) * 1000);
  }
  if (run.media.type === 'playlist') node.onended = next;
  node.onerror = () => recover(run, node);
  if (node.tagName === 'VIDEO') {
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
      restored: restored.media.revision, file: restored.file, message});
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
