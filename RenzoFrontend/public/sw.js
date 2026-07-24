/*
 * Self-destructing service worker.
 *
 * The previous SW cached the app shell, and stale cached bundles/pages were
 * wedging clients (login flow stuck on old auth state). This version does the
 * opposite: on activation it purges every Cache Storage entry, unregisters
 * itself, and reloads all open tabs so they fetch everything fresh from the
 * network. After this runs once, no service worker controls the app.
 */
self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      try {
        const keys = await caches.keys();
        await Promise.all(keys.map((k) => caches.delete(k)));
      } catch {
        /* ignore */
      }
      try {
        await self.registration.unregister();
      } catch {
        /* ignore */
      }
      const clients = await self.clients.matchAll({ type: 'window' });
      for (const client of clients) {
        try {
          client.navigate(client.url);
        } catch {
          /* ignore */
        }
      }
    })()
  );
});

// While tearing down, never serve from cache — let everything hit the network.
self.addEventListener('fetch', () => {});
