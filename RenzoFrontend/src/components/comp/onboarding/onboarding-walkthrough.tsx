"use client";

import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Library,
  Sparkles,
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
}

/**
 * First-run walkthrough. A short, dismissable tour that introduces the app to a
 * new account (and to the owner once server setup is done). Shows once per
 * account — gated on `onboardedVersion` in the user's preferences — and can be
 * re-opened from the account menu (dispatches {@link REPLAY_WALKTHROUGH_EVENT}).
 * Rendered globally from the root layout; a no-op while signed out or while the
 * server setup wizard is still running.
 */
export function OnboardingWalkthrough() {
  const { user, canManage, canAdmin, isLoading } = useAuth();
  const { isWizardActive } = useSetupWizard();

  const [open, setOpen] = useState(false);
  const [step, setStep] = useState(0);
  // Don't auto-reopen after the user dismisses it this session (the persisted
  // flag catches up on the next load).
  const dismissedRef = useRef(false);

  const steps = useMemo<Step[]>(() => {
    const list: Step[] = [
      {
        icon: <Library className="h-7 w-7" />,
        title: "Your library",
        body: "Everything you follow lives in Library — covers, reading progress, and quick resume. Open a series to see its chapters and start reading.",
      },
      {
        icon: <Sparkles className="h-7 w-7" />,
        title: canManage ? "Discover & add series" : "Discover & request series",
        body: canManage
          ? "Browse pulls the latest from your enabled sources. Tap a series to preview it, then add it to your library — Renzo tracks new chapters for you."
          : "Browse the latest from your server's sources. Tap a series to preview it and request it for the library.",
      },
      {
        icon: <BookOpen className="h-7 w-7" />,
        title: "A reader you can tune",
        body: "Continuous or paged, fit-to-width or original, themes, tap zones, and keyboard shortcuts. It remembers where you left off and syncs progress to your trackers.",
      },
      {
        icon: <DownloadCloud className="h-7 w-7" />,
        title: "Read offline on the go",
        body: "In the Android and desktop apps, save chapters (or a whole series) for offline reading on a trip. When you're back online it offers to clean them up — never touching what you're mid-read.",
      },
      {
        icon: <Palette className="h-7 w-7" />,
        title: "Make it yours",
        body: "Pick a theme and accent color in Appearance. Your look is saved to your account, so it follows you on every device.",
      },
    ];
    if (canAdmin) {
      list.push({
        icon: <ShieldCheck className="h-7 w-7" />,
        title: "You're an admin",
        body: "Manage users, sources, scheduled updates, and server settings from the account menu. Invite people and each gets their own library, progress, and theme.",
      });
    }
    return list;
  }, [canManage, canAdmin]);

  const total = steps.length + 1; // +1 for the welcome step

  // Auto-open for a user who hasn't seen the current walkthrough — but only once
  // the server setup wizard is done, so the owner finishes setup first.
  useEffect(() => {
    if (isLoading || isWizardActive) return;
    if (dismissedRef.current) return;
    if (user && !hasSeenOnboarding(user)) {
      setStep(0);
      setOpen(true);
    }
  }, [user, isLoading, isWizardActive]);

  // Replay on demand (account menu), regardless of the flag.
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
    setStep((s) => {
      if (s >= total - 1) {
        close();
        return s;
      }
      return s + 1;
    });
  }, [total, close]);

  const back = useCallback(() => setStep((s) => Math.max(0, s - 1)), []);

  // Esc to skip.
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

  const isWelcome = step === 0;
  const isLast = step === total - 1;
  const content = isWelcome ? null : steps[step - 1];

  return (
    <div
      className="fixed inset-0 z-[100] flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      aria-label="Welcome walkthrough"
    >
      {/* Backdrop */}
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={close} />

      {/* Card */}
      <div className="relative flex w-full max-w-md flex-col overflow-hidden rounded-2xl border border-border bg-card shadow-2xl">
        <button
          type="button"
          onClick={close}
          aria-label="Skip"
          className="absolute right-3 top-3 z-10 inline-flex h-8 w-8 items-center justify-center rounded-full text-muted-foreground transition-colors hover:bg-foreground/10 hover:text-foreground"
        >
          <X className="h-4 w-4" />
        </button>

        <div className="flex min-h-[19rem] flex-col px-6 pb-6 pt-8">
          {isWelcome ? (
            <div className="flex flex-1 flex-col items-center justify-center text-center">
              <RenzoBanner className="h-12 sm:h-14" />
              <h2 className="mt-6 text-xl font-semibold tracking-tight">Welcome{user?.username ? `, ${user.username}` : ""}!</h2>
              <p className="mt-2 max-w-sm text-sm leading-relaxed text-muted-foreground">
                Your self-hosted library for manga, manhwa &amp; manhua — read, track, and take it
                offline. Here&apos;s a 30-second tour.
              </p>
            </div>
          ) : (
            content && (
              <div className="flex flex-1 flex-col items-center justify-center text-center">
                <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-primary/10 text-primary">
                  {content.icon}
                </div>
                <h2 className="mt-5 text-lg font-semibold tracking-tight">{content.title}</h2>
                <p className="mt-2 max-w-sm text-sm leading-relaxed text-muted-foreground">{content.body}</p>
              </div>
            )
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between gap-3 border-t border-border px-5 py-3">
          <button
            type="button"
            onClick={close}
            className="text-xs font-medium text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
          >
            {isLast ? "" : "Skip"}
          </button>

          {/* Progress dots */}
          <div className="flex items-center gap-1.5" aria-hidden>
            {Array.from({ length: total }, (_, i) => (
              <span
                key={i}
                className={`h-1.5 rounded-full transition-all ${
                  i === step ? "w-4 bg-primary" : "w-1.5 bg-foreground/20"
                }`}
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
