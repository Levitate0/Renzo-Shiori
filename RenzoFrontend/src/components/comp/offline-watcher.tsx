"use client";
import { useReconnectPurge } from "@/lib/native/hooks";

/**
 * Confirmation prompt for reconnect-purge on the native shells. When you come
 * back online with trip downloads saved, it asks before removing them — a brief
 * signal blip won't wipe anything. Renders nothing (and never triggers) on the
 * web build, where there's no native bridge.
 */
export function OfflineWatcher(): React.ReactElement | null {
  const { pending, count, confirm, dismiss } = useReconnectPurge();
  if (!pending) return null;

  return (
    <div
      className="fixed inset-x-0 bottom-0 z-[60] flex justify-center px-3 pb-[calc(env(safe-area-inset-bottom,0px)+12px)]"
      role="dialog"
      aria-live="polite"
    >
      <div className="w-full max-w-md rounded-lg border bg-card text-card-foreground shadow-lg p-3 flex items-center gap-3">
        <div className="flex-1 text-sm">
          <div className="font-semibold">Back online</div>
          <div className="text-muted-foreground">
            Remove {count} downloaded chapter{count === 1 ? "" : "s"}? The chapter you&apos;re reading is kept.
          </div>
        </div>
        <button
          type="button"
          onClick={dismiss}
          className="px-3 py-1.5 rounded-md text-sm border hover:bg-accent hover:text-accent-foreground"
        >
          Keep
        </button>
        <button
          type="button"
          onClick={() => void confirm()}
          className="px-3 py-1.5 rounded-md text-sm bg-destructive text-destructive-foreground hover:opacity-90"
        >
          Remove
        </button>
      </div>
    </div>
  );
}
