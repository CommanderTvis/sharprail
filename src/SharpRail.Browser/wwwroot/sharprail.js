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
  document.getElementById(id).classList.toggle('sharprail-workbench', workbench);
}
addEventListener("blur", () => events?.("blur"));
addEventListener("focus", () => {
  const windows = [...document.querySelectorAll(".sharprail-window")].filter(element => element.style.visibility === "visible");
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
  element.id = `sharprail-window-${++nextId}`;
  element.className = 'sharprail-window';
  element.tabIndex = 0;
  element.addEventListener("focusin", () => events?.(`focus:${element.id}`));
  document.body.append(element);
  return element.id;
}
export function placeWindow(id, width, height, dialog, shown) {
  const element = document.getElementById(id);
  element.classList.toggle('sharprail-dialog', dialog);
  element.style.width = dialog ? `${Math.min(width, innerWidth)}px` : '100%';
  element.style.height = dialog ? `${Math.min(height, innerHeight)}px` : '100%';
  element.style.visibility = shown ? 'visible' : 'hidden';
}
export function removeWindow(id) { document.getElementById(id)?.remove(); }
export function activateWindow(id) {
  const element = document.getElementById(id);
  element.style.zIndex = `${++nextZ}`;
  element.focus();
}
export function load(key) { return (key === 'endpoint' ? sessionStorage : localStorage).getItem(`sharprail:${key}`); }
export function save(key, value) { (key === 'endpoint' ? sessionStorage : localStorage).setItem(`sharprail:${key}`, value); }
export function origin() { return location.origin; }
export function isMac() { return /mac|iphone|ipad/i.test(navigator.userAgentData?.platform ?? navigator.platform); }
export function openLink(url) {
  const target = new URL(url);
  if (target.protocol !== 'https:' && target.protocol !== 'http:') throw new Error('Only web links can be opened.');
  window.open(target.href, '_blank', 'noopener,noreferrer');
}

export function isVisible() { return !document.hidden; }

export function enableWindow(id, enabled) { document.getElementById(id).inert = !enabled; }
export function setTitle(_id, title) { document.title = title; }

export function dimNative(id, red, green, blue, alpha) {
  document.getElementById(id)?.style.setProperty('--sharprail-native-scrim', `rgba(${red},${green},${blue},${alpha})`);
}
