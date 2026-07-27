"use client";
import { useOfflinePurgeWatcher } from "@/lib/native/hooks";

/**
 * Runs the purge-on-reconnect watcher for the native (Capacitor/Electron)
 * shells. Renders nothing and is a complete no-op in the web build (no
 * injected native bridge → the hook's effect returns early).
 */
export function OfflineWatcher(): null {
  useOfflinePurgeWatcher();
  return null;
}
