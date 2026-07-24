"use client";

import { useRef, useState } from "react";
import { FileUp, Loader2 } from "lucide-react";

import { Button } from "@/components/ui/button";
import { ResponsiveModal } from "@/components/ui/responsive-modal";
import { readerService } from "@/lib/api/services/readerService";
import { type BackupImportResult } from "@/lib/api/types";

/**
 * Imports read progress / completed chapters / bookmarks from a
 * Tachiyomi/Suwayomi backup (.tachibk / .proto.gz). Series are matched by
 * title against the library; existing local progress is never downgraded.
 */
export function ImportBackupDialog({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const fileRef = useRef<HTMLInputElement>(null);
  const [pending, setPending] = useState(false);
  const [result, setResult] = useState<BackupImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handleFile = async (file: File | undefined) => {
    if (!file) return;
    setPending(true);
    setError(null);
    setResult(null);
    try {
      setResult(await readerService.importBackup(file));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Import failed");
    } finally {
      setPending(false);
      if (fileRef.current) fileRef.current.value = "";
    }
  };

  return (
    <ResponsiveModal
      open={open}
      onOpenChange={(next) => {
        if (!next) { setResult(null); setError(null); }
        onOpenChange(next);
      }}
      title="Import Suwayomi Backup"
      description="Sync read progress, completed chapters, and bookmarks from a .tachibk backup into your account."
    >
      <div className="space-y-4 py-1">
        {error && (
          <div className="rounded-md bg-red-50 p-3 text-sm text-red-500 dark:bg-red-950">{error}</div>
        )}

        {result ? (
          <div className="space-y-2 text-sm">
            <div className="rounded-md bg-muted p-3 space-y-1">
              <p><span className="font-medium">{result.matchedSeries}</span> of {result.backupSeries} backup series matched your library.</p>
              <p><span className="font-medium">{result.updatedChapters}</span> chapter read-states imported{result.bookmarks > 0 ? `, ${result.bookmarks} bookmarks` : ""}.</p>
            </div>
            {result.unmatched.length > 0 && (
              <details className="text-xs text-muted-foreground">
                <summary className="cursor-pointer">
                  {result.unmatched.length} series with progress weren&apos;t found in your library
                </summary>
                <ul className="mt-1 max-h-40 space-y-0.5 overflow-y-auto pl-4 list-disc">
                  {result.unmatched.map((t) => <li key={t}>{t}</li>)}
                </ul>
              </details>
            )}
            <Button variant="outline" className="w-full" onClick={() => setResult(null)}>
              Import another file
            </Button>
          </div>
        ) : (
          <>
            <input
              ref={fileRef}
              type="file"
              accept=".tachibk,.gz,.proto.gz"
              className="hidden"
              onChange={(e) => void handleFile(e.target.files?.[0])}
            />
            <Button
              className="w-full gap-2"
              disabled={pending}
              onClick={() => fileRef.current?.click()}
            >
              {pending ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileUp className="h-4 w-4" />}
              {pending ? "Importing…" : "Choose backup file"}
            </Button>
            <p className="text-xs text-muted-foreground">
              In Suwayomi: Settings → Backup → Create backup. Matching is by title;
              chapters are matched by number. Nothing is deleted, and chapters you&apos;ve
              already read here stay read.
            </p>
          </>
        )}
      </div>
    </ResponsiveModal>
  );
}
