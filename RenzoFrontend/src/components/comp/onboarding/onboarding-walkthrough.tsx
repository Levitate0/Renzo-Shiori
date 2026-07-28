"use client";

import React, { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import {
  Library,
  Search as SearchIcon,
  Compass,
  BookOpen,
  DownloadCloud,
  Palette,
  ShieldCheck,
  Rocket,
  ChevronLeft,
  ChevronRight,
  X,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { RenzoBanner } from "@/components/comp/layout/renzo-banner";
import { useAuth } from "@/contexts/auth-context";
import { useSetupWizard } from "@/components/providers/setup-wizard-provider";
import { hasSeenOnboarding, markOnboarded, REPLAY_WALKTHROUGH_EVENT } from "@/lib/utils/onboarding";

interface Step {
  icon: React.ReactNode;
  title: string;
  body: string;
  /** data-tour id to spotlight; when present + on-screen the window anchors to it. */
  target?: string;
}

const SPOTLIGHT_PAD = 8; // px of breathing room around a highlighted element
const GAP = 12; // px between the highlighted element and the window

/** First visible element carrying the given data-tour id (breakpoint-safe). */
function findTarget(id: string): HTMLElement | null {
  if (typeof document === "undefined") return null;
  for (const el of Array.from(document.querySelectorAll<HTMLElement>(`[data-tour="${id}"]`))) {
    const r = el.getBoundingClientRect();
    if (r.width > 4 && r.height > 4) return el;
  }
  return null;
}

/**
 * Interactive first-run walkthrough. A guided tour that spotlights real parts of
 * the app (navigation, search, account) in anchored windows, with clean centered
 * cards for features that aren't a single on-screen element. Shows once per
 * account (see {@link hasSeenOnboarding}); re-openable from the account menu.
 */
export function OnboardingWalkthrough() {
  const { user, canManage, canAdmin, isLoading } = useAuth();
  const { isWizardActive } = useSetupWizard();

  const [open, setOpen] = useState(false);
  const [step, setStep] = useState(0);
  const [rect, setRect] = useState<DOMRect | null>(null);
  const [pos, setPos] = useState<{ top: number; left: number; arrow: number; placement: "top" | "bottom" | "center" }>({
    top: 0,
    left: 0,
    arrow: 0,
    placement: "center",
  });
  const winRef = useRef<HTMLDivElement | null>(null);
  const dismissedRef = useRef(false);

  const steps = useMemo<Step[]>(() => {
    const list: Step[] = [
      {
        icon: <Compass className="h-7 w-7" />,
        title: "Move around",
        target: "nav",
        body: canManage
          ? "Jump between Library, Browse, Updates, Sources and more from here. On a phone it's the ☰ menu; on a wide screen it's the tabs up top."
          : "Jump between Library, Updates and your reading from here. On a phone it's the ☰ menu; on a wide screen it's the tabs up top.",
      },
      {
        icon: <SearchIcon className="h-7 w-7" />,
        title: "Find anything",
        target: "search",
        body: "Search your library — or, on Browse, search straight across your enabled sources to discover new series. ⌘/Ctrl+K focuses it anywhere.",
      },
      {
        icon: <Library className="h-7 w-7" />,
        title: "Your library",
        body: "Everything you follow lives in Library with covers and reading progress. Open a series to see its chapters, resume where you left off, and manage tracking.",
      },
      {
        icon: <BookOpen className="h-7 w-7" />,
        title: "A reader you can tune",
        body: "Continuous or paged, fit-to-width or original, themes, tap zones and keyboard shortcuts. It remembers your place and syncs progress to your trackers.",
      },
      {
        icon: <DownloadCloud className="h-7 w-7" />,
        title: "Read offline on the go",
        body: "In the Android and desktop apps, save chapters — or a whole series — for a trip. Back online, it offers to clean them up without touching what you're mid-read.",
      },
      {
        icon: <Palette className="h-7 w-7" />,
        title: "Everything you, in one place",
        target: "account",
        body: "Your account menu holds trackers (MAL/AniList/…), Appearance themes, offline downloads, and — any time — this tour under “Take a tour.”",
      },
    ];
    if (canAdmin) {
      list.push({
        icon: <ShieldCheck className="h-7 w-7" />,
        title: "You're an admin",
        body: "Manage users, sources, scheduled updates and server settings from the account menu. Invite people — each gets their own library, progress and theme.",
      });
    }
    return list;
  }, [canManage, canAdmin]);

  const total = steps.length + 1; // +1 welcome
  const isWelcome = step === 0;
  const isLast = step === total - 1;
  const content = isWelcome ? undefined : steps[step - 1];
  const targetId = content?.target;

  // Auto-open once per account, after the server setup wizard is done.
  useEffect(() => {
    if (isLoading || isWizardActive || dismissedRef.current) return;
    if (user && !hasSeenOnboarding(user)) {
      setStep(0);
      setOpen(true);
    }
  }, [user, isLoading, isWizardActive]);

  // Replay on demand (account menu).
  useEffect(() => {
    const onReplay = () => {
      dismissedRef.current = false;
      setStep(0);
      setOpen(true);
    };
    window.addEventListener(REPLAY_WALKTHROUGH_EVENT, onReplay);
    return () => window.removeEventListener(REPLAY_WALKTHROUGH_EVENT, onReplay);
  }, []);

  const close = useCallback(() => {
    dismissedRef.current = true;
    setOpen(false);
    void markOnboarded(user);
  }, [user]);

  const next = useCallback(() => {
    setStep((s) => (s >= total - 1 ? (close(), s) : s + 1));
  }, [total, close]);
  const back = useCallback(() => setStep((s) => Math.max(0, s - 1)), []);

  // Track the highlighted element's rect (recompute on resize/scroll).
  useEffect(() => {
    if (!open || !targetId) {
      setRect(null);
      return;
    }
    let raf = 0;
    const measure = () => {
      raf = 0;
      const el = findTarget(targetId);
      setRect(el ? el.getBoundingClientRect() : null);
    };
    const onMove = () => {
      if (!raf) raf = requestAnimationFrame(measure);
    };
    measure();
    window.addEventListener("resize", onMove);
    window.addEventListener("scroll", onMove, true);
    return () => {
      if (raf) cancelAnimationFrame(raf);
      window.removeEventListener("resize", onMove);
      window.removeEventListener("scroll", onMove, true);
    };
  }, [open, targetId, step]);

  // Place the window relative to the highlighted rect (or center it).
  useLayoutEffect(() => {
    if (!open) return;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const win = winRef.current;
    const w = win?.offsetWidth ?? Math.min(360, vw - 24);
    const h = win?.offsetHeight ?? 240;
    const margin = 12;

    if (!rect) {
      setPos({ top: Math.max(margin, (vh - h) / 2), left: Math.max(margin, (vw - w) / 2), arrow: 0, placement: "center" });
      return;
    }
    // Prefer below the target, flip above if it would run off the bottom.
    const below = rect.bottom + GAP;
    const placeBelow = below + h + margin <= vh || rect.top - GAP - h < margin;
    const top = placeBelow ? below : rect.top - GAP - h;
    const targetCx = rect.left + rect.width / 2;
    const left = Math.min(Math.max(margin, targetCx - w / 2), vw - w - margin);
    const arrow = Math.min(Math.max(16, targetCx - left), w - 16);
    setPos({ top, left, arrow, placement: placeBelow ? "bottom" : "top" });
  }, [rect, open, step, total]);

  // Keyboard: Esc skips, arrows navigate.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
      else if (e.key === "ArrowRight") next();
      else if (e.key === "ArrowLeft") back();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, close, next, back]);

  if (!open) return null;

  const spotlight = rect
    ? {
        top: rect.top - SPOTLIGHT_PAD,
        left: rect.left - SPOTLIGHT_PAD,
        width: rect.width + SPOTLIGHT_PAD * 2,
        height: rect.height + SPOTLIGHT_PAD * 2,
      }
    : null;

  const anchored = pos.placement !== "center";

  return (
    <div className="fixed inset-0 z-[100]" role="dialog" aria-modal="true" aria-label="Welcome walkthrough">
      {/* Backdrop — dims everything and skips on tap. When a target is spotlit the
          dimming is drawn by the spotlight's big box-shadow instead, so this layer
          stays transparent (but still catches taps to skip). */}
      <div className={`absolute inset-0 ${spotlight ? "" : "bg-black/70"}`} onClick={close} />

      {/* Spotlight cut-out around the highlighted element. */}
      {spotlight && (
        <div
          className="pointer-events-none absolute rounded-xl ring-2 ring-primary/70 transition-all duration-200"
          style={{
            top: spotlight.top,
            left: spotlight.left,
            width: spotlight.width,
            height: spotlight.height,
            boxShadow: "0 0 0 9999px rgba(0,0,0,0.72)",
          }}
        />
      )}

      {/* The window */}
      <div
        ref={winRef}
        className="absolute w-[min(360px,calc(100vw-24px))] overflow-hidden rounded-2xl border border-border bg-card shadow-2xl transition-[top,left] duration-200"
        style={{ top: pos.top, left: pos.left }}
        onClick={(e) => e.stopPropagation()}
      >
        {/* Arrow pointing at the target (anchored steps only). */}
        {anchored && (
          <div
            className="absolute h-3 w-3 rotate-45 border-border bg-card"
            style={
              pos.placement === "bottom"
                ? { top: -6, left: pos.arrow - 6, borderLeftWidth: 1, borderTopWidth: 1 }
                : { bottom: -6, left: pos.arrow - 6, borderRightWidth: 1, borderBottomWidth: 1 }
            }
          />
        )}

        <button
          type="button"
          onClick={close}
          aria-label="Skip"
          className="absolute right-3 top-3 z-10 inline-flex h-8 w-8 items-center justify-center rounded-full text-muted-foreground transition-colors hover:bg-foreground/10 hover:text-foreground"
        >
          <X className="h-4 w-4" />
        </button>

        <div className="px-5 pb-5 pt-6">
          {isWelcome ? (
            <div className="flex flex-col items-center text-center">
              <RenzoBanner className="h-11 sm:h-12" />
              <h2 className="mt-5 text-lg font-semibold tracking-tight">
                Welcome{user?.username ? `, ${user.username}` : ""}!
              </h2>
              <p className="mt-2 break-words text-sm leading-relaxed text-muted-foreground">
                Your self-hosted library for manga, manhwa &amp; manhua — read, track and take it
                offline. Here&apos;s a quick tour.
              </p>
            </div>
          ) : (
            content && (
              <div className={anchored ? "" : "flex flex-col items-center text-center"}>
                <div
                  className={`flex h-12 w-12 items-center justify-center rounded-2xl bg-primary/10 text-primary ${
                    anchored ? "" : "mx-auto"
                  }`}
                >
                  {content.icon}
                </div>
                <h2 className="mt-4 text-base font-semibold tracking-tight">{content.title}</h2>
                <p className="mt-1.5 break-words text-sm leading-relaxed text-muted-foreground">{content.body}</p>
              </div>
            )
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between gap-3 border-t border-border px-4 py-3">
          <button
            type="button"
            onClick={close}
            className="text-xs font-medium text-muted-foreground underline-offset-2 transition-colors hover:text-foreground hover:underline"
          >
            {isLast ? " " : "Skip"}
          </button>

          <div className="flex items-center gap-1.5" aria-hidden>
            {Array.from({ length: total }, (_, i) => (
              <span
                key={i}
                className={`h-1.5 rounded-full transition-all ${i === step ? "w-4 bg-primary" : "w-1.5 bg-foreground/20"}`}
              />
            ))}
          </div>

          <div className="flex items-center gap-2">
            {step > 0 && (
              <Button variant="ghost" size="sm" onClick={back} className="gap-1 px-2">
                <ChevronLeft className="h-4 w-4" />
                Back
              </Button>
            )}
            <Button size="sm" onClick={next} className="gap-1">
              {isLast ? (
                <>
                  <Rocket className="h-4 w-4" />
                  Get started
                </>
              ) : (
                <>
                  Next
                  <ChevronRight className="h-4 w-4" />
                </>
              )}
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
