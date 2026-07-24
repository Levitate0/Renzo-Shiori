"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, Globe, Loader2, User } from "lucide-react";
import { Switch } from "@/components/ui/switch";
import { mySourcesService, type AllSourceRow } from "@/lib/api/services/mySourcesService";
import { cn } from "@/lib/utils";

/**
 * Per-user source visibility — every installed source is shared (one JVM
 * bridge for the whole instance), but each user decides which of them show up
 * in their own Search/Browse/Add-series. Installing a source doesn't make it
 * visible to anyone else; new users start with nothing enabled until they opt
 * in here themselves.
 */
export function MySourcesPanel() {
  const [open, setOpen] = useState(false);
  const queryClient = useQueryClient();

  const { data: sources, isLoading } = useQuery({
    queryKey: ["provider", "all-sources"],
    queryFn: () => mySourcesService.listAll(),
    enabled: open,
    staleTime: 30 * 1000,
  });

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ["provider", "all-sources"] });
    void queryClient.invalidateQueries({ queryKey: ["search", "sources"] });
  };

  const toggle = useMutation({
    mutationFn: ({ mihonProviderId, enabled }: { mihonProviderId: string; enabled: boolean }) =>
      enabled ? mySourcesService.enable(mihonProviderId) : mySourcesService.disable(mihonProviderId),
    onSuccess: refresh,
  });

  const enabledCount = (sources ?? []).filter((s) => s.enabled).length;

  return (
    <section className="mb-6 rounded-xl border border-border/60 bg-card/40">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-center gap-2 px-4 py-3 text-left"
      >
        <User className="h-4 w-4 text-muted-foreground" />
        <span className="text-sm font-semibold">My sources</span>
        <span className="text-xs text-muted-foreground">
          which installed sources show up in your Search / Browse / Add series
        </span>
        {sources && (
          <span className="ml-2 rounded-full bg-foreground/[0.06] px-2 py-0.5 text-[11px] text-muted-foreground">
            {enabledCount} enabled
          </span>
        )}
        <ChevronDown className={cn("ml-auto h-4 w-4 text-muted-foreground transition-transform", open && "rotate-180")} />
      </button>

      {open && (
        <div className="border-t border-border/60 px-4 pb-4">
          <p className="py-3 text-xs text-muted-foreground">
            A source only appears in your Search, Browse, and Add-series once you enable it here —
            regardless of who installed it. Enabling a source doesn&apos;t change it for anyone else.
          </p>

          {isLoading ? (
            <div className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
              <Loader2 className="h-4 w-4 animate-spin" /> Loading sources…
            </div>
          ) : (sources ?? []).length === 0 ? (
            <div className="py-6 text-center text-sm text-muted-foreground">
              No sources installed yet — install one from the list below first.
            </div>
          ) : (
            <div className="max-h-[420px] space-y-1 overflow-y-auto pr-1">
              {(sources ?? []).map((s: AllSourceRow) => (
                <div
                  key={s.mihonProviderId}
                  className="flex items-center gap-3 rounded-lg border border-border/40 bg-card/50 px-3 py-2"
                >
                  <Globe className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm font-medium">{s.provider}</div>
                    <div className="text-[11px] text-muted-foreground">
                      {s.language.toUpperCase()}{s.scanlator && s.scanlator !== s.provider ? ` · ${s.scanlator}` : ""}
                    </div>
                  </div>
                  <Switch
                    checked={s.enabled}
                    disabled={toggle.isPending}
                    onCheckedChange={(v) => toggle.mutate({ mihonProviderId: s.mihonProviderId, enabled: v })}
                  />
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </section>
  );
}
