"use client";

import { useEffect } from "react";

/**
 * Actively UNREGISTERS any service worker and clears Cache Storage.
 *
 * The PWA service worker was caching the app shell and caused stale-bundle
 * wedges (login stuck on old auth state). We no longer register it; instead we
 * make sure any previously-installed SW is torn down and its caches purged, so
 * the app always runs live from the network. (public/sw.js is now a
 * self-destruct shim for clients that still had it registered.)
 */
export function ServiceWorkerRegistrar() {
  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    navigator.serviceWorker
      .getRegistrations()
      .then((regs) => regs.forEach((r) => r.unregister()))
      .catch(() => {});
    if (typeof caches !== "undefined") {
      caches.keys().then((keys) => keys.forEach((k) => caches.delete(k))).catch(() => {});
    }
  }, []);

  return null;
}
