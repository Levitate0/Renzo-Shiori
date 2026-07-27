"use client";
import * as React from "react";
import { usePathname } from "next/navigation";
import { getApiConfig } from "@/lib/api/config";

/**
 * Silent auto-refresh. Polls the user's own server for its build fingerprint
 * (`/api/system/version` → `build`, which changes on every UI deploy) and
 * reloads the page when it changes — so a stale cached page never blocks you
 * from a new build. Applies to every client (web, WebView, Capacitor, Electron)
 * since they all run this code against whatever server they're connected to.
 *
 * Never interrupts active reading: while the reader is open the reload is
 * deferred and applied the moment you leave it (or on window focus).
 */
const POLL_MS = 60_000;

async function fetchBuild(baseUrl: string): Promise<string | null> {
  try {
    const res = await fetch(`${baseUrl}/api/system/version`, { cache: "no-store" });
    if (!res.ok) return null;
    const data = (await res.json()) as { build?: string };
    return typeof data?.build === "string" ? data.build : null;
  } catch {
    return null;
  }
}

export function VersionRefresher(): null {
  const pathname = usePathname();
  const pathRef = React.useRef(pathname);
  pathRef.current = pathname;

  const knownBuild = React.useRef<string | null>(null);
  const pendingReload = React.useRef(false);

  // The reader is "active reading" — defer reloads while it's open.
  const isBusy = React.useCallback(() => (pathRef.current ?? "").startsWith("/reader"), []);

  const maybeReload = React.useCallback(() => {
    if (pendingReload.current && !isBusy()) {
      pendingReload.current = false;
      window.location.reload();
    }
  }, [isBusy]);

  React.useEffect(() => {
    const baseUrl = getApiConfig().baseUrl ?? "";
    let cancelled = false;

    const check = async () => {
      const build = await fetchBuild(baseUrl);
      if (cancelled || !build) return;
      if (knownBuild.current === null) {
        knownBuild.current = build; // baseline — never reload on first read
        return;
      }
      if (build !== knownBuild.current) {
        knownBuild.current = build; // adopt so we act once per new build
        pendingReload.current = true;
        maybeReload();
      }
    };

    void check();
    const id = window.setInterval(check, POLL_MS);
    const onWake = () => {
      void check();
      maybeReload();
    };
    window.addEventListener("focus", onWake);
    window.addEventListener("online", onWake);
    return () => {
      cancelled = true;
      window.clearInterval(id);
      window.removeEventListener("focus", onWake);
      window.removeEventListener("online", onWake);
    };
  }, [maybeReload]);

  // Leaving the reader flushes any reload that was held back.
  React.useEffect(() => {
    maybeReload();
  }, [pathname, maybeReload]);

  return null;
}
