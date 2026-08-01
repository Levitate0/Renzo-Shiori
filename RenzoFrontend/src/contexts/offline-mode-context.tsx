"use client";

import React, { createContext, useContext, useEffect, useState } from "react";
import { nativePrimitives } from "@/lib/native/bridge";

export type OfflineModeOverride = "auto" | "online" | "offline";

/**
 * The Library screen's Online/Offline view switch — matches Renzo's top-bar
 * pill. "auto" tracks live connectivity; "online"/"offline" are a manual
 * override (e.g. browsing the offline library while still on wifi). Native
 * only — on web this just always resolves to online, no-op.
 */
const OfflineModeContext = createContext<{
  override: OfflineModeOverride;
  setOverride: (o: OfflineModeOverride) => void;
  isOnline: boolean;
  isOffline: boolean;
} | null>(null);

const STORAGE_KEY = "renzo.viewmode.override";

export function OfflineModeProvider({ children }: { children: React.ReactNode }) {
  const [override, setOverrideState] = useState<OfflineModeOverride>("auto");
  const [liveOnline, setLiveOnline] = useState(true);

  useEffect(() => {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === "online" || stored === "offline" || stored === "auto") {
      setOverrideState(stored);
    }
  }, []);

  useEffect(() => {
    const nat = nativePrimitives();
    if (!nat) return;
    void nat.isOnline().then(setLiveOnline);
    return nat.onNetworkChange(setLiveOnline);
  }, []);

  const setOverride = (o: OfflineModeOverride) => {
    setOverrideState(o);
    localStorage.setItem(STORAGE_KEY, o);
  };

  const isOnline = override === "auto" ? liveOnline : override === "online";
  const isOffline = !isOnline;

  return (
    <OfflineModeContext.Provider value={{ override, setOverride, isOnline, isOffline }}>
      {children}
    </OfflineModeContext.Provider>
  );
}

/** Safe to call anywhere (including web) — resolves to always-online off native. */
export function useOfflineMode() {
  const ctx = useContext(OfflineModeContext);
  return ctx ?? { override: "auto" as const, setOverride: () => {}, isOnline: true, isOffline: false };
}
