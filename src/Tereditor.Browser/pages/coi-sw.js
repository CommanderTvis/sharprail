// GitHub Pages cannot set response headers, and multithreaded WebAssembly needs cross-origin isolation.
// This worker, served from the site root, re-serves every response in its scope with the two headers.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.cache === 'only-if-cached' && request.mode !== 'same-origin') return;
  event.respondWith(fetch(request).then(response => {
    if (response.status === 0) return response;
    const headers = new Headers(response.headers);
    headers.set('Cross-Origin-Opener-Policy', 'same-origin');
    headers.set('Cross-Origin-Embedder-Policy', 'require-corp');
    headers.set('Cross-Origin-Resource-Policy', 'cross-origin');
    return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
  }).catch(error => { console.error(error); return Response.error(); }));
});
