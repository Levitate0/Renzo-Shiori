"use client";

import { useEffect, useState } from "react";
import { Loader2, Monitor, Trash2, Tv } from "lucide-react";
import { toast } from "sonner";

import { CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useAuth } from "@/contexts/auth-context";
import { userService } from "@/lib/api/services/userService";
import { type RememberedDevice } from "@/lib/api/types";

/** "3 days ago", with the exact timestamp kept for the hover title. */
function relative(iso: string): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return "Unknown";
  const seconds = Math.round((Date.now() - at.getTime()) / 1000);
  if (seconds < 60) return "Just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"} ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days} day${days === 1 ? "" : "s"} ago`;
  return at.toLocaleDateString();
}

/** Forward-facing counterpart of {@link relative} — expiry is always ahead. */
function expiresIn(iso: string): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return "Expiry unknown";
  const days = Math.round((at.getTime() - Date.now()) / 86_400_000);
  if (days < 0) return "Expired";
  if (days === 0) return "Expires today";
  return `Expires in ${days} day${days === 1 ? "" : "s"}`;
}

function absolute(iso: string): string {
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? "" : at.toLocaleString();
}

/**
 * Account → Devices. Every "Remember me" sign-in (and every approved TV) is a
 * separate long-lived session — 90 days by default, and a TV in a shared room
 * is meant to stay signed in indefinitely. That's only safe if you can see the
 * list and end any one of them from here, which is what this is.
 */
export function DevicesSection() {
  const { logout } = useAuth();
  const [devices, setDevices] = useState<RememberedDevice[]>([]);
  const [loading, setLoading] = useState(true);
  const [confirming, setConfirming] = useState<RememberedDevice | null>(null);
  const [revoking, setRevoking] = useState(false);

  const refresh = async () => {
    try {
      setDevices(await userService.listDevices());
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to load devices");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void refresh(); /* eslint-disable-next-line */ }, []);

  const handleRevoke = async () => {
    if (!confirming) return;
    setRevoking(true);
    try {
      await userService.revokeDevice(confirming.id);
      // Revoking your own session means this browser is no longer signed in —
      // finish the job rather than leaving a half-dead session on screen.
      if (confirming.isCurrent) {
        setConfirming(null);
        await logout();
        return;
      }
      toast.success(`${confirming.deviceName} signed out`);
      setConfirming(null);
      await refresh();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to revoke device");
    } finally {
      setRevoking(false);
    }
  };

  return (
    <CardContent className="space-y-5">
      <p className="text-sm text-muted-foreground">
        Each time you sign in with <span className="font-medium text-foreground">Remember me</span>{" "}
        — or approve a television — that device gets its own long-lived session (90 days), renewed
        every time it's used. Sign one out here and the rest stay signed in.
      </p>

      {loading ? (
        <div className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading…
        </div>
      ) : devices.length === 0 ? (
        <p className="rounded-md border border-dashed border-border/60 bg-card/40 p-4 text-center text-sm text-muted-foreground">
          No remembered devices. Tick &quot;Remember me&quot; when you sign in — or approve a TV —
          and it&apos;ll show up here.
        </p>
      ) : (
        <div className="space-y-2">
          {devices.map((d) => (
            <div
              key={d.id}
              className="flex items-start gap-3 rounded-lg border border-border/50 bg-card/50 px-3 py-2.5"
            >
              {d.isTvPairing ? (
                <Tv className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
              ) : (
                <Monitor className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
              )}
              <div className="min-w-0 flex-1 space-y-0.5">
                <div className="flex flex-wrap items-center gap-1.5 text-sm font-medium">
                  <span className="truncate">{d.deviceName}</span>
                  {d.isTvPairing && (
                    <span className="rounded-full bg-primary/15 px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wide text-primary">
                      TV
                    </span>
                  )}
                  {d.isCurrent && (
                    <span className="rounded-full bg-emerald-500/15 px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wide text-emerald-500">
                      This device
                    </span>
                  )}
                </div>
                <div className="text-[11px] text-muted-foreground">
                  <span title={absolute(d.lastSeenAt)}>Last used {relative(d.lastSeenAt)}</span>
                  {" · "}
                  <span title={absolute(d.createdAt)}>
                    {d.isTvPairing ? "Paired" : "Signed in"} {relative(d.createdAt)}
                  </span>
                </div>
                <div className="text-[11px] text-muted-foreground">
                  {d.createdIp?.trim() ? `From ${d.createdIp}` : "Address unknown"}
                  <span title={absolute(d.expiresAt)}> · {expiresIn(d.expiresAt)}</span>
                </div>
              </div>
              <Button
                variant="ghost"
                size="sm"
                className="shrink-0 text-destructive"
                onClick={() => setConfirming(d)}
                title="Sign this device out"
              >
                <Trash2 className="h-4 w-4" />
              </Button>
            </div>
          ))}
        </div>
      )}

      <Dialog open={!!confirming} onOpenChange={(open) => !open && !revoking && setConfirming(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Sign out {confirming?.deviceName}?</DialogTitle>
            <DialogDescription>
              {confirming?.isCurrent
                ? "This is the device you're using right now — signing it out logs you out here immediately, and you'll need to sign in again. Your other devices stay signed in."
                : `${confirming?.deviceName ?? "This device"} will have to sign in again to reach your library. Your other devices are unaffected.`}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(null)} disabled={revoking}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={() => void handleRevoke()} disabled={revoking}>
              {revoking ? "Signing out…" : confirming?.isCurrent ? "Sign out & log me out" : "Sign out"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </CardContent>
  );
}
