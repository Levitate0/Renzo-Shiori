"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { ArrowLeft, Check, Loader2, MapPin, ShieldQuestion, Tv, UserRound, X } from "lucide-react";

import { useAuth } from "@/contexts/auth-context";
import { ApiError } from "@/lib/api/client";
import { userService } from "@/lib/api/services/userService";
import { type TvPendingRequest } from "@/lib/api/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

/**
 * The server's own code alphabet has no dashes or spaces and is upper-case
 * only (see TvPairingService.NormalizeUserCode) — so "abcd 2345", "ABCD-2345"
 * and "abcd2345" are the same code. Normalise here too, or a perfectly
 * correct code typed the way it's *displayed* would 404.
 */
function normalizeCode(raw: string): string {
  return raw.toUpperCase().replace(/[^A-Z0-9]/g, "");
}

/** Re-groups as the user types so the field mirrors the TV's `ABCD-2345`. */
function formatCode(raw: string): string {
  const clean = normalizeCode(raw).slice(0, 12);
  return clean.length > 4 ? `${clean.slice(0, 4)}-${clean.slice(4)}` : clean;
}

/**
 * The three failures a wrong-looking pairing attempt can have are genuinely
 * different actions for the user, so they get genuinely different sentences.
 * The API already surfaces the server's `{ "error": ... }` text and it's
 * written for exactly this screen — these are the fallbacks for when only a
 * status code survives (a proxy that eats the body, say).
 */
function describeError(err: unknown, fallback: string): string {
  if (err instanceof ApiError) {
    if (err.message && !err.message.startsWith("API Error:")) return err.message;
    if (err.status === 404) return "That code isn't valid — check it and try again, or restart pairing on the TV.";
    if (err.status === 429) return "Too many attempts for that code. Restart pairing on the TV.";
    if (err.status === 409) return "That code was already used.";
  }
  return err instanceof Error ? err.message : fallback;
}

function formatExpiry(iso: string): string | null {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return null;
  const minutes = Math.round((at.getTime() - Date.now()) / 60000);
  if (minutes <= 0) return "This code has expired — restart pairing on the TV.";
  return `This code expires in about ${minutes} minute${minutes === 1 ? "" : "s"}.`;
}

/**
 * /tv — where you approve the code your television is showing.
 *
 * Sign-in on a TV is done by proxy: the TV displays a short code, and the
 * person holding a phone types it here. Approving grants *this* account, so
 * the page always names the signed-in user, and never approves anything
 * before showing what asked (device name + the IP it asked from) — a code
 * typed into the wrong install, or one someone else read off a screen, has to
 * be visibly wrong before the button is pressed.
 *
 * Mostly used on a phone, sometimes on a LAN with no internet, so: one column,
 * big tap targets, nothing loaded from off-box.
 */
export default function TvPairingPage() {
  const { user, isAuthenticated, isLoading } = useAuth();

  const [code, setCode] = useState("");
  const [pending, setPending] = useState<TvPendingRequest | null>(null);
  const [approvedName, setApprovedName] = useState<string | null>(null);
  const [deniedName, setDeniedName] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  // A TV may print the URL with the code already in it; don't make someone
  // re-type what they just followed a link for.
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const prefill = params.get("code") ?? params.get("userCode");
    if (prefill) setCode(formatCode(prefill));
  }, []);

  const normalized = normalizeCode(code);

  const handleLookup = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!normalized) return;
    setError("");
    setBusy(true);
    try {
      setPending(await userService.getTvPending(normalized));
    } catch (err) {
      setPending(null);
      setError(describeError(err, "Couldn't check that code."));
    } finally {
      setBusy(false);
    }
  };

  const handleApprove = async () => {
    setError("");
    setBusy(true);
    try {
      await userService.approveTvPairing(normalized);
      setApprovedName(pending?.deviceName ?? "your TV");
    } catch (err) {
      setError(describeError(err, "Couldn't approve that code."));
    } finally {
      setBusy(false);
    }
  };

  const handleDeny = async () => {
    setError("");
    setBusy(true);
    try {
      await userService.denyTvPairing(normalized);
      setDeniedName(pending?.deviceName ?? "that device");
    } catch (err) {
      setError(describeError(err, "Couldn't deny that code."));
    } finally {
      setBusy(false);
    }
  };

  const startOver = () => {
    setCode("");
    setPending(null);
    setApprovedName(null);
    setDeniedName(null);
    setError("");
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-background">
      <Card className="mx-4 w-full max-w-md">
        <CardHeader className="space-y-3">
          <CardTitle className="flex items-center gap-2">
            <Tv className="h-5 w-5 text-primary" />
            Approve a TV
          </CardTitle>
          <CardDescription>
            Type the code shown on your television to sign it in to Renzo Shiori.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {isLoading ? (
            <div className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
              <Loader2 className="h-4 w-4 animate-spin" /> Loading…
            </div>
          ) : !isAuthenticated ? (
            /* The auth context already sends unauthenticated visitors to
               /login and remembers /tv as the return path — this is just the
               moment before that navigation lands. */
            <div className="space-y-4">
              <p className="text-sm text-muted-foreground">
                Sign in first — approving a TV signs it in as your account, so we need to know
                whose account to grant. You&apos;ll come straight back here.
              </p>
              <Button asChild className="w-full">
                <Link href="/login">Sign in</Link>
              </Button>
            </div>
          ) : approvedName ? (
            <div className="space-y-4">
              <div className="flex items-start gap-2 rounded-md bg-emerald-500/10 p-3 text-sm text-emerald-500">
                <Check className="mt-0.5 h-4 w-4 shrink-0" />
                <span>
                  <span className="font-medium">{approvedName}</span> is signed in as{" "}
                  <span className="font-medium">{user?.username}</span>. You can go back to your TV
                  now — it should pick this up in a few seconds.
                </span>
              </div>
              <p className="text-xs text-muted-foreground">
                It stays signed in until you revoke it under Account → Devices.
              </p>
              <div className="flex flex-col gap-2 sm:flex-row">
                <Button asChild variant="outline" className="w-full sm:flex-1">
                  <Link href="/account">
                    <ArrowLeft className="mr-2 h-4 w-4" />
                    Manage devices
                  </Link>
                </Button>
                <Button variant="ghost" className="w-full sm:flex-1" onClick={startOver}>
                  Approve another
                </Button>
              </div>
            </div>
          ) : deniedName ? (
            <div className="space-y-4">
              <div className="rounded-md bg-muted p-3 text-sm text-muted-foreground">
                Denied — <span className="font-medium text-foreground">{deniedName}</span> was not
                signed in, and the code no longer works.
              </div>
              <Button variant="outline" className="w-full" onClick={startOver}>
                Enter another code
              </Button>
            </div>
          ) : pending ? (
            /* Confirmation step: what's being approved, and on whose account.
               Never skipped — the details are the only thing standing between
               a mistyped/overheard code and a permanent session. */
            <div className="space-y-4">
              {error && (
                <div className="rounded-md bg-red-50 p-3 text-sm text-red-500 dark:bg-red-950">
                  {error}
                </div>
              )}

              <p className="text-sm text-muted-foreground">
                Check this is the TV in front of you before you approve it.
              </p>

              <div className="space-y-2.5 rounded-lg border border-border/50 bg-card/50 p-3.5">
                <DetailRow icon={Tv} label="Device" value={pending.deviceName} />
                <DetailRow
                  icon={MapPin}
                  label="Asked from"
                  value={pending.requestIp?.trim() ? pending.requestIp : "Unknown address"}
                />
                <DetailRow
                  icon={UserRound}
                  label="Signs in as"
                  value={user?.username ?? "your account"}
                />
              </div>

              <p className="text-xs text-muted-foreground">
                Approving gives this TV a long-lived sign-in to your account — it stays signed in
                until you revoke it under Account → Devices.{" "}
                {formatExpiry(pending.expiresAt)}
              </p>

              <div className="flex flex-col gap-2">
                <Button className="h-11 w-full" onClick={() => void handleApprove()} disabled={busy}>
                  {busy ? (
                    <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  ) : (
                    <Check className="mr-2 h-4 w-4" />
                  )}
                  Approve {pending.deviceName}
                </Button>
                <div className="flex flex-col gap-2 sm:flex-row">
                  <Button
                    variant="outline"
                    className="w-full text-destructive sm:flex-1"
                    onClick={() => void handleDeny()}
                    disabled={busy}
                  >
                    <X className="mr-2 h-4 w-4" />
                    Deny
                  </Button>
                  <Button variant="ghost" className="w-full sm:flex-1" onClick={startOver} disabled={busy}>
                    Use a different code
                  </Button>
                </div>
              </div>
            </div>
          ) : (
            <form onSubmit={(e) => void handleLookup(e)} className="space-y-4">
              {error && (
                <div className="rounded-md bg-red-50 p-3 text-sm text-red-500 dark:bg-red-950">
                  {error}
                </div>
              )}
              <div className="space-y-2">
                <Label htmlFor="tv-code">Code on your TV</Label>
                <Input
                  id="tv-code"
                  value={code}
                  onChange={(e) => setCode(formatCode(e.target.value))}
                  placeholder="ABCD-2345"
                  autoFocus
                  autoComplete="one-time-code"
                  autoCapitalize="characters"
                  autoCorrect="off"
                  spellCheck={false}
                  aria-describedby="tv-code-help"
                  className="h-14 text-center font-mono text-2xl uppercase tracking-[0.25em]"
                />
                <p id="tv-code-help" className="text-xs text-muted-foreground">
                  Upper or lower case, with or without the dash — it&apos;s all the same code.
                </p>
              </div>
              <Button type="submit" className="h-11 w-full" disabled={busy || !normalized}>
                {busy ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
                Continue
              </Button>
              <p className="flex items-start gap-1.5 text-xs text-muted-foreground">
                <ShieldQuestion className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                You&apos;ll see which device is asking, and from where, before anything is approved.
              </p>
            </form>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function DetailRow({
  icon: Icon,
  label,
  value,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value: string;
}) {
  return (
    <div className="flex items-start gap-2.5 text-sm">
      <Icon className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
      <div className="min-w-0 flex-1">
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className="break-all font-medium">{value}</div>
      </div>
    </div>
  );
}
