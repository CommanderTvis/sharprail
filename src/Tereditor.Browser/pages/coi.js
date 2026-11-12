// Registers the header-supplying worker and reloads once, so the page itself is served through it.
if (!window.crossOriginIsolated && 'serviceWorker' in navigator && window.isSecureContext) {
  navigator.serviceWorker.register(new URL('coi-sw.js', document.currentScript.src)).then(registration => {
    const reload = () => { if (!sessionStorage.getItem('sharprail-coi-reload')) { sessionStorage.setItem('sharprail-coi-reload', '1'); location.reload(); } };
    if (registration.active && !navigator.serviceWorker.controller) reload();
    else navigator.serviceWorker.addEventListener('controllerchange', reload);
  }, error => console.error('Cross-origin isolation worker failed to register', error));
}
