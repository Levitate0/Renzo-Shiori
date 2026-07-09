"use client";

import { useEffect } from "react";

/**
 * Registers the PWA service worker (public/sw.js) once on mount.
 * Rendered from the root layout; no UI.
 */
export function ServiceWorkerRegistrar() {
  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    navigator.serviceWorker.register("/sw.js").catch(() => {
      // Registration failing (http on LAN without localhost exemption, old
      // browser, …) just means no install prompt/offline fallback — the app
      // itself works fine without it.
    });
  }, []);

  return null;
}
