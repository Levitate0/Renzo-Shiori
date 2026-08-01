"use client";

import React, { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";

/**
 * Lets a page-level sticky bottom bar (e.g. the Sources/Default-Priority
 * "Apply" bar) make itself known so the floating ActivityDock can shift up to
 * clear it. The offset is computed from the bar's LIVE viewport position —
 * the dock only lifts while a bar is genuinely pinned at the bottom of the
 * viewport (its sticky state engaged, overlapping dock territory). A bar
 * that's merely present on the page but currently sitting mid-column (its
 * un-stuck position, or pinned inside a sub-container that doesn't reach the
 * viewport bottom) contributes nothing, so the dock stays in its normal spot.
 */
const BottomBarContext = createContext<{
  register: (id: string, el: HTMLElement) => void;
  unregister: (id: string) => void;
  offset: number;
} | null>(null);

export function BottomBarOffsetProvider({ children }: { children: React.ReactNode }) {
  const [els, setEls] = useState<Map<string, HTMLElement>>(new Map());
  const [offset, setOffset] = useState(0);

  const register = useCallback((id: string, el: HTMLElement) => {
    setEls((prev) => {
      if (prev.get(id) === el) return prev;
      const next = new Map(prev);
      next.set(id, el);
      return next;
    });
  }, []);

  const unregister = useCallback((id: string) => {
    setEls((prev) => {
      if (!prev.has(id)) return prev;
      const next = new Map(prev);
      next.delete(id);
      return next;
    });
  }, []);

  useEffect(() => {
    if (els.size === 0) {
      setOffset(0);
      return;
    }
    let frame = 0;
    const measure = () => {
      frame = 0;
      const vh = window.innerHeight;
      let max = 0;
      for (const el of els.values()) {
        const r = el.getBoundingClientRect();
        // "Pinned at the viewport bottom" — sticky engaged and the bar's
        // bottom edge is (nearly) flush with the window's. 24px of slack
        // covers container padding below the bar.
        if (r.height > 0 && vh - r.bottom >= -2 && vh - r.bottom < 24) {
          max = Math.max(max, r.height + 12);
        }
      }
      setOffset(max);
    };
    const schedule = () => {
      if (!frame) frame = requestAnimationFrame(measure);
    };
    measure();
    // Bars live inside inner scroll containers (<main> overflow-y-auto), so
    // listen in capture phase to catch scrolls on any ancestor.
    document.addEventListener("scroll", schedule, { capture: true, passive: true });
    window.addEventListener("resize", schedule);
    const ro = new ResizeObserver(schedule);
    els.forEach((el) => ro.observe(el));
    return () => {
      document.removeEventListener("scroll", schedule, { capture: true });
      window.removeEventListener("resize", schedule);
      ro.disconnect();
      if (frame) cancelAnimationFrame(frame);
    };
  }, [els]);

  const value = useMemo(() => ({ register, unregister, offset }), [register, unregister, offset]);

  return <BottomBarContext.Provider value={value}>{children}</BottomBarContext.Provider>;
}

/** Read the extra bottom offset the dock should keep clear right now. */
export function useBottomBarOffset(): number {
  return useContext(BottomBarContext)?.offset ?? 0;
}

/**
 * Attach to the sticky bar's root element. While `active`, the bar's live
 * position feeds the dock-clearance calculation; cleans up on unmount.
 */
export function useRegisterBottomBar(id: string, active: boolean): React.RefObject<HTMLDivElement | null> {
  const ctx = useContext(BottomBarContext);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!ctx) return;
    const el = ref.current;
    if (!active || !el) {
      ctx.unregister(id);
      return;
    }
    ctx.register(id, el);
    return () => ctx.unregister(id);
  }, [ctx, id, active]);

  return ref;
}
