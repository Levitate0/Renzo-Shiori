"use client";

import React, { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";

/**
 * Lets a page-level sticky bottom bar (e.g. the Sources/Default-Priority
 * "Apply" bar) register its height so the floating ActivityDock can shift
 * up to clear it, instead of the two fighting over the same z-index — one
 * of them ends up invisible either way if they're just stacked.
 */
const BottomBarContext = createContext<{
  register: (id: string, height: number) => void;
  unregister: (id: string) => void;
  offset: number;
} | null>(null);

export function BottomBarOffsetProvider({ children }: { children: React.ReactNode }) {
  const [heights, setHeights] = useState<Map<string, number>>(new Map());

  const register = useCallback((id: string, height: number) => {
    setHeights((prev) => {
      if (prev.get(id) === height) return prev;
      const next = new Map(prev);
      next.set(id, height);
      return next;
    });
  }, []);

  const unregister = useCallback((id: string) => {
    setHeights((prev) => {
      if (!prev.has(id)) return prev;
      const next = new Map(prev);
      next.delete(id);
      return next;
    });
  }, []);

  const offset = useMemo(() => Math.max(0, ...heights.values()), [heights]);
  const value = useMemo(() => ({ register, unregister, offset }), [register, unregister, offset]);

  return <BottomBarContext.Provider value={value}>{children}</BottomBarContext.Provider>;
}

/** Read the extra bottom offset currently reserved by any registered bar(s). */
export function useBottomBarOffset(): number {
  return useContext(BottomBarContext)?.offset ?? 0;
}

/**
 * Attach to the sticky bar's root element. Registers its live height
 * (ResizeObserver-tracked, so it stays correct across breakpoints/wrapping)
 * while `active` is true, and cleans up on unmount or when it goes inactive.
 */
export function useRegisterBottomBar(id: string, active: boolean): React.RefObject<HTMLDivElement | null> {
  const ctx = useContext(BottomBarContext);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!ctx) return;
    if (!active) {
      ctx.unregister(id);
      return;
    }
    const el = ref.current;
    if (!el) return;
    const measure = () => ctx.register(id, el.offsetHeight);
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => {
      ro.disconnect();
      ctx.unregister(id);
    };
  }, [ctx, id, active]);

  return ref;
}
