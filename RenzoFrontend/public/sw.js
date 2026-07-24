/*
 * Minimal service worker: just enough for PWA installability and sane offline
 * behavior, deliberately conservative about caching. This app was recently
 * bitten by stale-bundle caching (over-broad Cache-Control on index.html), so:
 *   - navigations/HTML: network-first, cache only as offline fallback
 *   - /_next/static/ (content-hashed, immutable): cache-first
 *   - everything else (API, images, SignalR): untouched — straight to network
 */

const CACHE_NAME = 'renzo-v1';

self.addEventListener('install', (event) => {
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k)))
    ).then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  // API, hubs, and dynamic content: never intercept.
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/progress')) return;

  // Content-hashed build assets: cache-first (a hit can never be stale).
  if (url.pathname.startsWith('/_next/static/')) {
    event.respondWith(
      caches.open(CACHE_NAME).then(async (cache) => {
        const cached = await cache.match(request);
        if (cached) return cached;
        const response = await fetch(request);
        if (response.ok) cache.put(request, response.clone());
        return response;
      })
    );
    return;
  }

  // Page navigations: network-first so a deploy is picked up immediately;
  // fall back to the last cached copy only when actually offline.
  if (request.mode === 'navigate') {
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response.ok) {
            const clone = response.clone();
            caches.open(CACHE_NAME).then((cache) => cache.put(request, clone));
          }
          return response;
        })
        .catch(() => caches.match(request).then((cached) => cached ?? Response.error()))
    );
  }
});
