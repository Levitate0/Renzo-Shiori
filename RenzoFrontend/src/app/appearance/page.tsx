"use client";

import React, { useCallback, useRef } from "react";
import { Sun, Moon, Monitor, Check } from "lucide-react";
import { useTheme } from "next-themes";
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
  useAccentTheme,
  ACCENT_THEMES,
  hexToHsl,
  hslToHex,
  type AccentThemeId,
} from "@/lib/utils/accent-theme";
import { serializeThemePrefs, type ThemePrefs } from "@/lib/utils/theme-prefs";

type Mode = "light" | "dark" | "system";
const MODES: { id: Mode; label: string; icon: React.ReactNode }[] = [
  { id: "light", label: "Light", icon: <Sun className="h-4 w-4" /> },
  { id: "dark", label: "Dark", icon: <Moon className="h-4 w-4" /> },
  { id: "system", label: "System", icon: <Monitor className="h-4 w-4" /> },
];

/**
 * Per-user Appearance page. Light/dark/system + accent (6 presets or a custom
 * color). Changes apply instantly (via the theme/accent stores) AND persist to
 * the user's account (PUT /api/auth/me preferences) so they follow the account
 * across devices — see ThemeSync, which re-applies them on login elsewhere.
 */
export default function AppearancePage() {
  const { user } = useAuth();
  const { theme, setTheme } = useTheme();
  const { accent, customHsl, setPreset, setCustom } = useAccentTheme();
  const saveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Persist the full prefs snapshot, using overrides for the value just changed
  // (hook state hasn't re-rendered yet at call time).
  const persist = useCallback(
    (overrides: Partial<ThemePrefs>, debounce = false) => {
      const prefs: ThemePrefs = {
        theme: (overrides.theme ?? (theme as Mode) ?? "system"),
        accent: overrides.accent ?? accent,
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
    [theme, accent, customHsl],
  );

  const chooseMode = (m: Mode) => {
    setTheme(m);
    persist({ theme: m });
  };

  const choosePreset = (id: AccentThemeId) => {
    setPreset(id);
    persist({ accent: id });
  };

  const chooseCustom = (hex: string) => {
    const hsl = hexToHsl(hex);
    setCustom(hsl);
    persist({ accent: "custom", accentCustom: hsl }, true);
  };

  const customHex = hslToHex(customHsl);

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Appearance</h1>
        <p className="text-sm text-muted-foreground">
          Personalize how Renzo Shiori looks for {user?.username ?? "you"}. Saved to your
          account, so it follows you on every device.
        </p>
      </div>

      <div className="grid gap-6">
        {/* Mode */}
        <Card>
          <CardHeader>
            <CardTitle>Mode</CardTitle>
            <CardDescription>Light, dark, or follow your device.</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="flex flex-wrap gap-2">
              {MODES.map((m) => {
                const active = (theme ?? "system") === m.id;
                return (
                  <Button
                    key={m.id}
                    type="button"
                    variant={active ? "default" : "outline"}
                    onClick={() => chooseMode(m.id)}
                    className="gap-2"
                  >
                    {m.icon}
                    {m.label}
                    {active && <Check className="h-3.5 w-3.5" />}
                  </Button>
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
              Recolors buttons, highlights, and focus rings across the app. Pick a preset or
              choose your own.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-5">
            <div className="flex flex-wrap items-center gap-3">
              {ACCENT_THEMES.map((t) => {
                const active = accent === t.id;
                return (
                  <button
                    key={t.id}
                    type="button"
                    onClick={() => choosePreset(t.id)}
                    title={t.label}
                    aria-label={`Accent color: ${t.label}`}
                    aria-pressed={active}
                    className={`h-9 w-9 rounded-full shrink-0 transition-transform flex items-center justify-center ${
                      active
                        ? "ring-2 ring-offset-2 ring-offset-background ring-foreground scale-110"
                        : "hover:scale-110"
                    }`}
                    style={{ backgroundColor: t.swatch }}
                  >
                    {active && <Check className="h-4 w-4 text-white drop-shadow" />}
                  </button>
                );
              })}

              {/* Custom color */}
              <label
                className={`relative h-9 w-9 rounded-full shrink-0 cursor-pointer transition-transform overflow-hidden flex items-center justify-center ${
                  accent === "custom"
                    ? "ring-2 ring-offset-2 ring-offset-background ring-foreground scale-110"
                    : "hover:scale-110"
                }`}
                style={{
                  background:
                    accent === "custom"
                      ? customHex
                      : "conic-gradient(from 0deg, #f43f5e, #f59e0b, #22c55e, #3b82f6, #a855f7, #f43f5e)",
                }}
                title="Custom color"
              >
                {accent === "custom" && <Check className="h-4 w-4 text-white drop-shadow" />}
                <input
                  type="color"
                  value={customHex}
                  onChange={(e) => chooseCustom(e.target.value)}
                  className="absolute inset-0 opacity-0 cursor-pointer"
                  aria-label="Choose a custom accent color"
                />
              </label>
            </div>

            {/* Live preview */}
            <div className="rounded-lg border p-4 space-y-3">
              <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                Preview
              </div>
              <div className="flex flex-wrap items-center gap-3">
                <Button>Primary button</Button>
                <Button variant="outline">Outline</Button>
                <span className="text-primary font-medium">Accented text</span>
                <span className="inline-block h-6 w-6 rounded-full bg-primary" />
              </div>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
