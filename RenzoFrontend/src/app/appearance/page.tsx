"use client";

import React, { useCallback, useRef } from "react";
import { Check, Play, Plus } from "lucide-react";
import { useTheme as useNextTheme } from "next-themes";
import { toast } from "sonner";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/contexts/auth-context";
import { userService } from "@/lib/api/services/userService";
import {
  THEME_PRESETS,
  presetById,
  useTheme,
  setPreset,
  setCustomAccent,
  clearCustomAccent,
  hslStrToHex,
  hexToHslStr,
} from "@/lib/utils/theme-preset";
import { parseThemePrefs, serializeThemePrefs, type ThemePrefs } from "@/lib/utils/theme-prefs";

/**
 * Per-user Appearance page. Named theme presets (palette + accent + mode) with
 * live preview cards, plus a custom-accent override. Changes apply instantly
 * (theme-preset store + next-themes) AND persist to the account
 * (PUT /api/auth/me preferences) so they follow the user across devices — see
 * ThemeSync, which re-applies them on login elsewhere.
 */
export default function AppearancePage() {
  const { user } = useAuth();
  const { setTheme } = useNextTheme();
  const { preset, customOn, customHsl } = useTheme();
  const saveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const persist = useCallback(
    (overrides: Partial<ThemePrefs>, debounce = false) => {
      const cur = presetById(overrides.preset ?? preset);
      const prefs: ThemePrefs = {
        // Preserve non-appearance keys in the shared preferences blob (e.g. the
        // onboarding flag) so saving a theme never clobbers them.
        ...parseThemePrefs(user?.preferences),
        theme: cur.mode,
        preset: overrides.preset ?? preset,
        accent: overrides.accent ?? (customOn ? "custom" : "preset"),
        accentCustom: overrides.accentCustom ?? customHsl,
      };
      const send = () =>
        userService
          .updateMe({ preferences: serializeThemePrefs(prefs) })
          .catch(() => toast.error("Couldn't save your appearance settings."));
      if (saveTimer.current) clearTimeout(saveTimer.current);
      if (debounce) saveTimer.current = setTimeout(send, 400);
      else send();
    },
    [preset, customOn, customHsl, user?.preferences],
  );

  const choosePreset = (id: string) => {
    setPreset(id);
    setTheme(presetById(id).mode);
    persist({ preset: id });
  };

  const chooseCustom = (hex: string) => {
    const hsl = hexToHslStr(hex);
    setCustomAccent(hsl);
    persist({ accent: "custom", accentCustom: hsl }, true);
  };

  const usePreset = () => {
    clearCustomAccent();
    persist({ accent: "preset" });
  };

  const customHex = hslStrToHex(customHsl);

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Appearance</h1>
        <p className="text-sm text-muted-foreground">
          Personalize how Renzo Shiori looks for {user?.username ?? "you"} — saved to your
          account, so it follows you on every device.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-6">
        {/* Theme */}
        <Card>
          <CardHeader>
            <CardTitle>Theme</CardTitle>
            <CardDescription>Pick a look. Applies instantly and syncs to your account.</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              {THEME_PRESETS.map((t) => {
                const active = preset === t.id;
                return (
                  <button
                    key={t.id}
                    type="button"
                    onClick={() => choosePreset(t.id)}
                    aria-pressed={active}
                    className={`group rounded-xl border p-3 text-left transition-colors ${
                      active ? "border-primary ring-1 ring-primary" : "border-border hover:border-foreground/30"
                    }`}
                  >
                    <div
                      className="relative h-16 w-full overflow-hidden rounded-lg border"
                      style={{ background: t.bg, borderColor: "rgba(255,255,255,0.08)" }}
                    >
                      <div
                        className="absolute left-2 top-4 h-6 w-16 rounded-md"
                        style={{ background: t.card }}
                      />
                      <div
                        className="absolute right-2 top-2 h-3.5 w-3.5 rounded-full"
                        style={{ background: t.accent }}
                      />
                    </div>
                    <div className="mt-2 flex items-center justify-between">
                      <span className="text-sm font-semibold">{t.label}</span>
                      {active && <Check className="h-3.5 w-3.5 text-primary" />}
                    </div>
                  </button>
                );
              })}
            </div>
          </CardContent>
        </Card>

        {/* Accent */}
        <Card>
          <CardHeader>
            <CardTitle>Accent color</CardTitle>
            <CardDescription>
              Recolors buttons, highlights, and focus rings across the app. Use a preset theme
              above or choose your own.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-6">
            <div className="flex items-center justify-between gap-4">
              <div className="min-w-0">
                <div className="text-sm font-medium">Custom accent</div>
                <p className="text-xs text-muted-foreground">Override the selected theme&apos;s highlight color.</p>
              </div>
              <div className="flex items-center gap-3">
                <label
                  className={`relative h-9 w-14 shrink-0 cursor-pointer overflow-hidden rounded-md border ${
                    customOn ? "ring-2 ring-offset-2 ring-offset-background ring-foreground" : ""
                  }`}
                  style={{ background: customOn ? customHex : "hsl(var(--primary))" }}
                  title="Custom accent color"
                >
                  <input
                    type="color"
                    value={customHex}
                    onChange={(e) => chooseCustom(e.target.value)}
                    className="absolute inset-0 cursor-pointer opacity-0"
                    aria-label="Choose a custom accent color"
                  />
                </label>
                <Button type="button" variant="outline" size="sm" disabled={!customOn} onClick={usePreset}>
                  Use preset accent
                </Button>
              </div>
            </div>

            {/* Live preview */}
            <div>
              <div className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Preview</div>
              <div className="w-64 max-w-full rounded-xl border bg-card p-4">
                <div className="text-xs text-muted-foreground">Series</div>
                <div className="mt-0.5 text-lg font-semibold">Renzo Shiori</div>
                <div className="text-xs text-muted-foreground">2026 · Action</div>
                <div className="mt-3 flex items-center gap-2">
                  <Button size="sm" className="gap-1.5">
                    <Play className="h-3.5 w-3.5 fill-current" /> Read
                  </Button>
                  <Button size="sm" variant="outline" className="gap-1.5">
                    <Plus className="h-3.5 w-3.5" /> List
                  </Button>
                </div>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
