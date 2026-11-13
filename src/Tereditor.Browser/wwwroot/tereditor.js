let renderer;
let nextRequest = 0;
const requests = new Map();
function render(kind, args) {
  if (!renderer) {
    renderer = new Worker(new URL('./render-worker.js', import.meta.url), { type: 'module' });
    renderer.onmessage = ({ data }) => {
      const request = requests.get(data.id);
      if (!request) return;
      requests.delete(data.id);
      if (data.error) request.reject(new Error(data.error)); else request.resolve(data.result);
    };
    renderer.onerror = error => {
      for (const request of requests.values()) request.reject(new Error(error.message));
      requests.clear(); renderer.terminate(); renderer = undefined;
    };
  }
  return new Promise((resolve, reject) => {
    const id = ++nextRequest;
    requests.set(id, { resolve, reject });
    renderer.postMessage({ id, kind, args });
  });
}
export function renderMermaid(source, options) { return render('mermaid', [source, options]); }
export function readPdf(bytes) { return render('pdf-read', [bytes]); }
export function renderPdf(bytes, scale) { return render('pdf-render', [bytes, scale]); }
let events;
export function registerEvents(callback) { events = callback; }
export function terminalEvent(message) { return events?.('restty:' + JSON.stringify(message)); }
export function setWorkbench(id, workbench) {
  document.getElementById(id).classList.toggle('tereditor-workbench', workbench);
}
addEventListener("blur", () => events?.("blur"));
addEventListener("focus", () => {
  const windows = [...document.querySelectorAll(".tereditor-window")].filter(element => element.style.visibility === "visible");
  windows.sort((a, b) => Number(a.style.zIndex) - Number(b.style.zIndex));
  if (windows.length) events?.(`focus:${windows.at(-1).id}`);
});
export function requestNotificationPermission() {
  if ("Notification" in globalThis) Notification.requestPermission().catch(() => {});
}
const notifications = new Map();
export function notify(id, title, body) {
  if (!("Notification" in globalThis) || Notification.permission !== "granted") return;
  try {
    notifications.get(id)?.close();
    const notification = new Notification(title, { body, tag: id });
    notifications.set(id, notification);
    notification.onclick = () => { window.focus(); events?.(`notification:${id}`); notification.close(); };
    notification.onclose = () => { if (notifications.get(id) === notification) notifications.delete(id); };
  } catch {}
}
let nextId = 0;
let nextZ = 1;
export function createWindow() {
  const element = document.createElement('div');
  element.id = `tereditor-window-${++nextId}`;
  element.className = 'tereditor-window';
  element.tabIndex = 0;
  element.addEventListener("focusin", () => events?.(`focus:${element.id}`));
  document.body.append(element);
  return element.id;
}
export function placeWindow(id, width, height, dialog, shown) {
  const element = document.getElementById(id);
  element.classList.toggle('tereditor-dialog', dialog);
  element.style.width = dialog ? `${Math.min(width, innerWidth)}px` : '100%';
  element.style.height = dialog ? `${Math.min(height, innerHeight)}px` : '100%';
  element.style.visibility = shown ? 'visible' : 'hidden';
}
export function windowWidth(id) { return document.getElementById(id).getBoundingClientRect().width; }
export function windowHeight(id) { return document.getElementById(id).getBoundingClientRect().height; }
export function removeWindow(id) { document.getElementById(id)?.remove(); }
export function activateWindow(id) {
  const element = document.getElementById(id);
  element.style.zIndex = `${++nextZ}`;
  element.focus();
}
export function load(key) { return (key === 'endpoint' ? sessionStorage : localStorage).getItem(`tereditor:${key}`); }
export function save(key, value) { (key === 'endpoint' ? sessionStorage : localStorage).setItem(`tereditor:${key}`, value); }
export function origin() { return location.origin; }
// Imports a module beside this one. The .NET runtime keeps a failed JSHost.ImportAsync for good; a module this loaded is not
// fetched again, and one that failed here is fetched again on the next call.
export async function preload(path) { await import(new URL(path, import.meta.url).href); }
export function isMac() { return /mac|iphone|ipad/i.test(navigator.userAgentData?.platform ?? navigator.platform); }
export function openLink(url) {
  const target = new URL(url);
  if (target.protocol !== 'https:' && target.protocol !== 'http:') throw new Error('Only web links can be opened.');
  window.open(target.href, '_blank', 'noopener,noreferrer');
}

// The layout viewport (not the visual one): a pinch on a phone never changes it, a browser zoom on a desktop does.
export function viewportWidth() { return document.documentElement.clientWidth; }
export function isCoarse() { return matchMedia('(pointer: coarse)').matches; }
// Like Android's smallest width, so a phone turned sideways stays a phone.
export function smallestScreenSide() { return Math.min(screen.width, screen.height); }
let viewportTimer;
addEventListener('resize', () => { clearTimeout(viewportTimer); viewportTimer = setTimeout(() => events?.('viewport'), 150); });
// The visible height: on a phone the soft keyboard and the address bar shrink it without resizing the layout viewport everywhere.
function fitVisualViewport() {
  const view = visualViewport;
  if (!view) return;
  const style = document.documentElement.style;
  style.setProperty('--tereditor-vh', `${view.height}px`);
  style.setProperty('--tereditor-vt', `${view.offsetTop}px`);
}
visualViewport?.addEventListener('resize', fitVisualViewport);
visualViewport?.addEventListener('scroll', fitVisualViewport);
fitVisualViewport();
export function isVisible() { return !document.hidden; }

export function enableWindow(id, enabled) { document.getElementById(id).inert = !enabled; }
export function setTitle(_id, title) { document.title = title; }

// Popups are drawn on the canvas, under a native terminal element. The terminal is clipped around each open popup, so the
// popup shows and takes the clicks (a clip also removes the clipped area from hit testing).
export function cutNative(id, rects) {
  const host = document.getElementById(id);
  if (!host) return;
  const holes = rects ? rects.split(';').map(rect => rect.split(',').map(Number)) : [];
  const origin = host.getBoundingClientRect();
  for (const element of host.querySelectorAll('.tereditor-restty')) {
    const box = element.getBoundingClientRect();
    const parts = holes.map(([x, y, width, height]) => {
      const left = origin.left + x - box.left, top = origin.top + y - box.top;
      return `M${left} ${top}h${width}v${height}h${-width}z`;
    });
    element.style.clipPath = parts.length ? `path(evenodd,'M0 0h${box.width}v${box.height}h${-box.width}z${parts.join('')}')` : '';
  }
}

export function dimNative(id, red, green, blue, alpha) {
  const style = document.getElementById(id)?.style;
  style?.setProperty('--tereditor-native-scrim', `rgba(${red},${green},${blue},${alpha})`);
  // A native terminal is a page element above the canvas, so it would hide an in-window overlay (Settings) drawn under it:
  // behind a dimmed workbench it is hidden, as dim as it would be.
  style?.setProperty('--tereditor-native-visibility', alpha > 0 ? 'hidden' : 'visible');
}
