"use client";

import { useOfflineMode } from "@/contexts/offline-mode-context";
import { useIsNative } from "@/lib/native/hooks";

/**
 * Online/Offline view-mode pill — native only. Click toggles which library
 * the app is browsing (live server vs. what's saved on this device), same
 * pattern as the equivalent control in the sibling Renzo app's top bar.
 */
export function OnlineOfflinePill({ className = "" }: { className?: string }) {
  const isNative = useIsNative();
  const { isOnline, setOverride } = useOfflineMode();

  if (!isNative) return null;

  return (
    <button
      type="button"
      onClick={() => setOverride(isOnline ? "offline" : "online")}
      aria-pressed={!isOnline}
      title={isOnline ? "Browsing the live library — click to switch to offline" : "Browsing the offline library — click to go back online"}
      className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-medium transition-colors shrink-0 ${
        isOnline
          ? "border-border/40 bg-foreground/[0.04] text-muted-foreground hover:bg-foreground/[0.06] hover:text-foreground"
          : "border-amber-500/40 bg-amber-500/15 text-amber-600 dark:text-amber-400"
      } ${className}`}
    >
      <span className={`h-1.5 w-1.5 rounded-full ${isOnline ? "bg-emerald-500" : "bg-amber-500"}`} />
      {isOnline ? "Online" : "Offline"}
    </button>
  );
}
