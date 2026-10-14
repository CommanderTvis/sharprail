// DOM surface. Host lifetime and transport stay in the managed adapter.
import { terminalEvent } from './sharprail.js';
const states = new WeakMap();
export function create() {
  const element = document.createElement('div');
  element.className = 'sharprail-restty';
  element.style.cssText = 'position:relative;width:100%;height:100%;overflow:hidden';
  return element;
}
export async function initialize(element, id, themeJson) {
  const input = text => terminalEvent({ id, text });
  const resize = (columns, rows) => terminalEvent({ id, columns, rows });
  const state = { disposed: false, decoder: new TextDecoder(), columns: 80, rows: 24 };
  states.set(element, state);
  const { createResttyRuntime, createResttyRuntimeSession } = await import('./vendor/restty/internal/runtime.js');
  if (state.disposed) return '80,24';
  const canvas = document.createElement('canvas');
  // Retain the context actually acquired by Restty, including failed initialization.
  // Do not acquire a new context from destroy: disposal may precede initialization.
  const getContext = canvas.getContext.bind(canvas);
  canvas.getContext = (kind, ...args) => {
    if (state.disposed) return null;
    const context = getContext(kind, ...args);
    if (kind === 'webgl2') state.gl = context;
    return context;
  };
  canvas.tabIndex = 0;
  canvas.style.cssText = 'width:100%;height:100%;display:block;outline:none';
  const ime = document.createElement('textarea');
  ime.setAttribute('aria-label', 'Terminal input');
  ime.style.cssText = 'position:absolute;left:0;top:0;width:1px;height:1px;opacity:0;resize:none';
  element.append(canvas, ime);
  // Avalonia consumes bubbling keyboard events before restty's window listener.
  state.keyListeners = [];
  for (const type of ['keydown', 'keyup']) {
    const forward = event => {
      const forwarded = new KeyboardEvent(type, event);
      Object.defineProperty(forwarded, 'target', { value: event.target });
      window.dispatchEvent(forwarded);
      if (forwarded.defaultPrevented) event.preventDefault();
      event.stopPropagation();
    };
    element.addEventListener(type, forward);
    state.keyListeners.push([type, forward]);
  }
  state.dropListeners = [];
  for (const type of ['dragenter', 'dragover', 'dragleave', 'drop']) {
    const forward = event => {
      const host = element.closest('.avalonia-container');
      if (!host) return;
      const bounds = host.getBoundingClientRect();
      const forwarded = new DragEvent(type, {
        dataTransfer: event.dataTransfer, clientX: event.clientX, clientY: event.clientY,
        altKey: event.altKey, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey, metaKey: event.metaKey
      });
      Object.defineProperties(forwarded, {
        offsetX: { value: event.clientX - bounds.left },
        offsetY: { value: event.clientY - bounds.top }
      });
      host.dispatchEvent(forwarded);
      if (type === 'dragover' || type === 'drop') event.preventDefault();
      event.stopPropagation();
    };
    element.addEventListener(type, forward, true);
    state.dropListeners.push([type, forward]);
  }
  const session = createResttyRuntimeSession();
  const wasm = await session.getWasm();
  if (state.disposed) return '80,24';
  // Each runtime owns one terminal handle; the read-screen adapter shares its state.
  const localWasm = Object.create(wasm);
  localWasm.create = (...args) => state.handle = wasm.create(...args);
  state.wasm = wasm;
  const transport = {
    connect({ callbacks, cols, rows }) {
      state.callbacks = callbacks;
      state.connected = true;
      transport.resize(cols || 80, rows || 24);
      callbacks.onConnect?.();
    },
    disconnect() { state.connected = false; state.callbacks?.onDisconnect?.(); },
    sendInput(text) { if (!state.disposed && !state.watching) input(text); return !state.disposed && !state.watching; },
    resize(cols, rows) {
      state.columns = cols; state.rows = rows;
      element.dataset.columns = String(cols); element.dataset.rows = String(rows);
      if (!state.watching) resize(cols, rows); return true;
    },
    isConnected() { return !!state.connected; }
  };
  state.sendInput = input;
  state.runtime = createResttyRuntime({
    mount: { canvas, imeInput: ime, session: { ...session, getWasm: async () => localWasm } },
    terminal: { renderer: 'webgl2', fontSize: 14, fontSizeMode: 'em', autoResize: false,
      fonts: [{ url: new URL('./vendor/JetBrainsMono-Regular.ttf', import.meta.url).href }],
      theme: JSON.parse(themeJson), showResizeOverlay: false },
    services: { ptyTransport: transport }
  });
  await state.runtime.lifecycle.init();
  if (state.disposed) { state.runtime.lifecycle.destroy(); return '80,24'; }
  if (state.runtime.lifecycle.state() !== 'ready' || state.runtime.render.getBackend() === 'none')
    throw new Error('GPU terminal initialization failed');
  state.runtime.io.connectPty();
  if (state.disposed) return '80,24';
  state.observer = new ResizeObserver(() => { if (!state.watching) state.runtime.interaction.updateSize(); });
  state.observer.observe(element);
  element.dataset.backend = state.runtime.render.getBackend();
  element.dataset.ready = 'true';
  return `${state.columns},${state.rows}`;
}
export function output(element, bytes) {
  const state = states.get(element);
  state.callbacks.onData?.(state.decoder.decode(bytes, { stream: true }));
}
export function watch(element, columns, rows) {
  const state = states.get(element);
  state.watching = true;
  state.runtime.interaction.blur();
  const canvas = element.querySelector('canvas');
  canvas.tabIndex = -1;
  canvas.style.pointerEvents = 'none';
  element.querySelector('textarea').disabled = true;
  element.style.overflow = 'auto';
  state.runtime.interaction.resize(columns, rows);
  canvas.style.width = `${canvas.width / devicePixelRatio}px`;
  canvas.style.height = `${canvas.height / devicePixelRatio}px`;
  element.dataset.watching = 'true';
}
export function screen(element) {
  const s = states.get(element);
  return s?.handle ? s.wasm.getSelectionText(s.handle, 0, 0, s.rows - 1, s.columns - 1) : '';
}
export function focus(element) { states.get(element)?.runtime?.interaction.focus(); }
export function theme(element, value) { states.get(element)?.runtime?.terminal.applyTheme(JSON.parse(value)); }
export function agentNewline(element, enabled) {
  const s = states.get(element);
  if (!s) return;
  s.agentNewline = enabled;
  if (s.newlineHandler) return;
  s.newlineHandler = event => {
    if (!s.watching && s.agentNewline && event.key === 'Enter' && event.shiftKey && !event.ctrlKey && !event.metaKey &&
        !s.wasm.getKittyKeyboardFlags(s.handle)) {
      event.preventDefault(); event.stopImmediatePropagation(); s.sendInput('\x1b\r');
    }
  };
  element.addEventListener('keydown', s.newlineHandler, true);
}
export function destroy(element) {
  const s = states.get(element);
  for (const [type, listener] of s?.dropListeners ?? []) element.removeEventListener(type, listener, true);
  if (!s) return;
  s.disposed = true;
  s.observer?.disconnect();
  for (const [type, listener] of s.keyListeners ?? []) element.removeEventListener(type, listener);
  if (s.newlineHandler) element.removeEventListener('keydown', s.newlineHandler, true);
  try { s.runtime?.lifecycle.destroy(); }
  finally {
    // Restty 0.3.0 does not delete its base WebGL buffers, shaders or atlas.
    // Context loss releases all graphics resources even if a detached canvas is retained.
    if (s.gl && !s.gl.isContextLost()) s.gl.getExtension('WEBGL_lose_context')?.loseContext();
    element.replaceChildren();
  }
}
