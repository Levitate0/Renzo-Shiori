"use client";

import * as React from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { PlugZap } from "lucide-react";

import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "@/contexts/auth-context";
import { sourceHealthService } from "@/lib/api/services/sourceHealthService";

/*
 * Dead-source alert: a red plug in the command bar while a source your series
 * use has failed on them all day without serving a chapter (server:
 * SourceHealthController "alerts"). Opens a short list, each with Migrate;
 * "Hide for a day" mutes the current set until another source joins it.
 *
 * Hub twin: the same list in the Sources › Health tab, plus an Android
 * notification (ui/sources/HealthTab.kt, notifications/SourceAlertWorker).
 */

const HIDE_KEY = "renzo.sourceAlert.hidden";

function hiddenSignature(): string | null {
  try {
    const raw = localStorage.getItem(HIDE_KEY);
    if (!raw) return null;
    const v = JSON.parse(raw) as { sig: string; until: number };
    return v.until > Date.now() ? v.sig : null;
  } catch {
    return null;
  }
}

export function SourceAlert() {
  const { canManage } = useAuth();
  const { data } = useQuery({
    queryKey: ["sources", "health", "alerts"],
    queryFn: () => sourceHealthService.alerts(),
    enabled: canManage,
    refetchInterval: 5 * 60_000,
    staleTime: 60_000,
  });
  const [hidden, setHidden] = React.useState<string | null>(null);
  React.useEffect(() => setHidden(hiddenSignature()), []);

  const dead = data ?? [];
  const sig = dead.map((s) => s.provider).sort().join("\n");
  if (!canManage || dead.length === 0 || sig === hidden) return null;

  const hide = () => {
    try {
      localStorage.setItem(HIDE_KEY, JSON.stringify({ sig, until: Date.now() + 24 * 60 * 60_000 }));
    } catch { /* private window: it just shows again */ }
    setHidden(sig);
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className="relative rounded-md p-2 text-red-500 transition-colors hover:bg-red-500/10"
          title={dead.length === 1 ? `${dead[0]?.provider} isn't working` : `${dead.length} sources aren't working`}
        >
          <PlugZap className="h-5 w-5" />
          <span className="absolute -right-0.5 -top-0.5 rounded-full bg-red-500 px-1 text-[10px] font-semibold leading-4 text-white">
            {dead.length}
          </span>
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-80 max-w-[calc(100vw-2rem)]">
        <DropdownMenuLabel>Sources not working</DropdownMenuLabel>
        <p className="px-2 pb-2 text-xs text-muted-foreground">
          Failing on your series all day without serving a chapter. Other sources still cover them where they can.
        </p>
        {dead.map((s) => (
          <DropdownMenuItem key={s.provider} asChild>
            <Link href={`/providers/?tab=migrate&from=${encodeURIComponent(s.provider)}`} className="flex items-center justify-between gap-2">
              <span className="min-w-0 truncate">
                {s.provider}
                <span className="text-muted-foreground"> · on {s.enabledCount} series</span>
              </span>
              <span className="shrink-0 text-xs text-primary">Migrate</span>
            </Link>
          </DropdownMenuItem>
        ))}
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <Link href="/providers/?tab=health">Open source health</Link>
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={hide}>Hide for a day</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
