"use client";

import { useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, Loader2, Package, Pin, Upload } from "lucide-react";
import { toast } from "sonner";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { extensionsService, type ExtensionInfo } from "@/lib/api/services/extensionsService";
import { cn } from "@/lib/utils";

/**
 * Extension version manager — the "keep it working while upstream fixes it"
 * panel: per extension, switch between installed versions (rollback), pin
 * against auto-update, or sideload a patched APK.
 */
export function ExtensionVersions() {
  const [open, setOpen] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);
  const queryClient = useQueryClient();

  const { data: extensions, isLoading } = useQuery({
    queryKey: ["extensions", "list"],
    queryFn: () => extensionsService.list(),
    enabled: open,
    staleTime: 30 * 1000,
  });

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ["extensions", "list"] });

  const setActive = useMutation({
    mutationFn: ({ name, version }: { name: string; version: string }) =>
      extensionsService.setActive(name, version),
    onSuccess: (ext) => {
      toast.success(`${ext.name} switched to ${ext.activeVersion}${ext.autoUpdate ? "" : " (pinned)"}`);
      refresh();
    },
    onError: (e) => toast.error(e instanceof Error ? e.message : "Could not switch version."),
  });

  const setAutoUpdate = useMutation({
    mutationFn: ({ name, enabled }: { name: string; enabled: boolean }) =>
      extensionsService.setAutoUpdate(name, enabled),
    onSuccess: (ext) => {
      toast.success(ext.autoUpdate
        ? `${ext.name} unpinned — back on ${ext.activeVersion}, auto-updating`
        : `${ext.name} pinned on ${ext.activeVersion}`);
      refresh();
    },
    onError: (e) => toast.error(e instanceof Error ? e.message : "Could not update pin."),
  });

  const sideload = useMutation({
    mutationFn: (file: File) => extensionsService.sideload(file),
    onSuccess: (ext) => {
      toast.success(`Sideloaded ${ext.name} ${ext.activeVersion} (pinned against auto-update)`);
      refresh();
    },
    onError: (e) => toast.error(e instanceof Error ? e.message : "Sideload failed — previous version remains active."),
  });

  const busy = setActive.isPending || setAutoUpdate.isPending || sideload.isPending;

  return (
    <section className="mb-6 rounded-xl border border-border/60 bg-card/40">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-center gap-2 px-4 py-3 text-left"
      >
        <Package className="h-4 w-4 text-muted-foreground" />
        <span className="text-sm font-semibold">Extension versions</span>
        <span className="text-xs text-muted-foreground">
          rollback · pin · sideload — keep a source working while a fix is in the works
        </span>
        <ChevronDown className={cn("ml-auto h-4 w-4 text-muted-foreground transition-transform", open && "rotate-180")} />
      </button>

      {open && (
        <div className="border-t border-border/60 px-4 pb-4">
          <div className="flex items-center justify-between py-3">
            <p className="text-xs text-muted-foreground">
              Switching to an older version pins it — auto-update won&apos;t replace a
              pinned extension until you unpin it. Sideloaded APKs only replace the
              running version if they compile successfully.
            </p>
            <button
              type="button"
              disabled={busy}
              onClick={() => fileRef.current?.click()}
              className="ml-4 inline-flex shrink-0 items-center gap-1.5 rounded-full border border-primary/40 bg-primary/10 px-3 py-1.5 text-xs font-medium text-primary transition-colors hover:bg-primary/20 disabled:opacity-60"
            >
              {sideload.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Upload className="h-3.5 w-3.5" />}
              Install APK
            </button>
            <input
              ref={fileRef}
              type="file"
              accept=".apk"
              className="hidden"
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) sideload.mutate(f);
                e.target.value = "";
              }}
            />
          </div>

          {isLoading ? (
            <div className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
              <Loader2 className="h-4 w-4 animate-spin" /> Loading extensions…
            </div>
          ) : (
            <div className="max-h-[420px] space-y-1.5 overflow-y-auto pr-1">
              {(extensions ?? []).map((ext: ExtensionInfo) => (
                <div
                  key={ext.name}
                  className="flex items-center gap-3 rounded-lg border border-border/40 bg-card/50 px-3 py-2"
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-1.5">
                      <span className="truncate text-sm font-medium">{ext.name}</span>
                      {!ext.autoUpdate && <Pin className="h-3 w-3 shrink-0 text-amber-500" />}
                    </div>
                    <div className="text-[11px] text-muted-foreground">
                      {ext.versions.length} version{ext.versions.length === 1 ? "" : "s"} installed
                    </div>
                  </div>

                  <Select
                    value={ext.activeVersion}
                    disabled={busy}
                    onValueChange={(v) => setActive.mutate({ name: ext.name, version: v })}
                  >
                    <SelectTrigger className="h-8 w-[150px] text-xs">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {ext.versions.map((v) => (
                        <SelectItem key={`${v.version}_${v.repositoryId}`} value={v.version}>
                          {v.version}{v.isLocal ? " (sideloaded)" : ""}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>

                  <div className="flex shrink-0 items-center gap-1.5" title={ext.autoUpdate ? "Auto-update on" : "Pinned — auto-update off"}>
                    <span className="text-[10px] uppercase tracking-wide text-muted-foreground">auto</span>
                    <Switch
                      checked={ext.autoUpdate}
                      disabled={busy}
                      onCheckedChange={(v) => setAutoUpdate.mutate({ name: ext.name, enabled: v })}
                    />
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </section>
  );
}
