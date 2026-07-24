"use client";

import { useEffect, useMemo, useState } from "react";
import { KeyRound, Loader2, RefreshCw, Trash2, Check, AlertTriangle, Clock } from "lucide-react";
import { toast } from "sonner";

import { CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import {
  siteAuthService, type SiteCredential, type SiteInfo, type SiteLoginResult,
} from "@/lib/api/services/siteAuthService";

/**
 * Settings → Site Logins. Logins for coin/paid scanlation sites: enter your
 * site username/password once, Renzō logs in server-side, harvests the
 * session cookies into the shared source jar, and the source then serves the
 * chapters you own — re-logging-in automatically when the session lapses.
 * Sites that can't be automated accept a pasted session cookie instead.
 */
export function SiteLoginsSection() {
  const [sites, setSites] = useState<SiteInfo[]>([]);
  const [creds, setCreds] = useState<SiteCredential[]>([]);
  const [loading, setLoading] = useState(true);

  const [provider, setProvider] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [cookie, setCookie] = useState("");
  const [mode, setMode] = useState<"password" | "cookie">("password");
  const [busy, setBusy] = useState(false);
  const [pendingId, setPendingId] = useState<string | null>(null);

  const refresh = async () => {
    try {
      const [s, c] = await Promise.all([siteAuthService.listSites(), siteAuthService.list()]);
      setSites(s);
      setCreds(c);
      if (!provider && s.length) setProvider(s[0]!.provider);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to load site logins");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void refresh(); /* eslint-disable-next-line */ }, []);

  const configured = useMemo(() => new Set(creds.map((c) => c.provider)), [creds]);
  const availableSites = sites.filter((s) => !configured.has(s.provider));

  const report = (r: SiteLoginResult) => {
    if (r.success) toast.success(r.detail ?? "Logged in");
    else toast.error(r.detail ?? "Login failed", { duration: 6000 });
  };

  const handleAdd = async () => {
    if (!provider) return;
    setBusy(true);
    try {
      const res = mode === "password"
        ? await siteAuthService.save(provider, username.trim(), password)
        : await siteAuthService.saveCookie(provider, username.trim(), cookie.trim());
      report(res.result);
      setUsername(""); setPassword(""); setCookie("");
      await refresh();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to save login");
    } finally {
      setBusy(false);
    }
  };

  const handleRelogin = async (id: string) => {
    setPendingId(id);
    try {
      const res = await siteAuthService.relogin(id);
      report(res.result);
      await refresh();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Re-login failed");
    } finally {
      setPendingId(null);
    }
  };

  const handleDelete = async (id: string, prov: string) => {
    if (!window.confirm(`Remove the saved login for ${prov}? Locked chapters will stop loading.`)) return;
    try {
      await siteAuthService.remove(id);
      await refresh();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to remove login");
    }
  };

  return (
    <CardContent className="space-y-5">
      <p className="text-sm text-muted-foreground">
        Log in to coin/paid scanlation sites so Renzō can load the chapters you&apos;ve
        paid for. Your password is encrypted and never shown again; Renzō logs in for you
        and re-logs-in automatically when the site&apos;s session expires. For sites that
        can&apos;t be automated (CAPTCHA / Google sign-in), paste a session cookie instead.
      </p>

      {/* Existing logins */}
      {loading ? (
        <div className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading…
        </div>
      ) : creds.length === 0 ? (
        <p className="rounded-md border border-dashed border-border/60 bg-card/40 p-4 text-center text-sm text-muted-foreground">
          No site logins yet.
        </p>
      ) : (
        <div className="space-y-2">
          {creds.map((c) => (
            <div key={c.id} className="flex items-center gap-3 rounded-lg border border-border/50 bg-card/50 px-3 py-2.5">
              <StatusIcon status={c.status} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2 text-sm font-medium">
                  {c.provider}
                  <span className="truncate text-xs font-normal text-muted-foreground">· {c.username}</span>
                </div>
                {c.statusDetail && (
                  <div className="truncate text-[11px] text-muted-foreground">{c.statusDetail}</div>
                )}
              </div>
              {c.supportsAutoLogin && (
                <Button variant="ghost" size="sm" disabled={pendingId === c.id}
                  onClick={() => void handleRelogin(c.id)} title="Test / re-login now">
                  {pendingId === c.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                </Button>
              )}
              <Button variant="ghost" size="sm" className="text-destructive"
                onClick={() => void handleDelete(c.id, c.provider)} title="Remove login">
                <Trash2 className="h-4 w-4" />
              </Button>
            </div>
          ))}
        </div>
      )}

      {/* Add a login */}
      {availableSites.length > 0 ? (
        <div className="space-y-3 border-t pt-4">
          <div className="flex items-center gap-2">
            <KeyRound className="h-4 w-4 text-muted-foreground" />
            <span className="text-sm font-medium">Add a site login</span>
          </div>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label>Site</Label>
              <Select value={provider} onValueChange={setProvider}>
                <SelectTrigger><SelectValue placeholder="Choose a site" /></SelectTrigger>
                <SelectContent>
                  {availableSites.map((s) => (
                    <SelectItem key={s.provider} value={s.provider}>
                      <span className="flex items-center gap-2">
                        {s.provider}
                        {s.domain && <span className="text-[11px] text-muted-foreground">· {s.domain}</span>}
                        {s.coin && (
                          <span className="rounded-full bg-primary/15 px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wide text-primary">paid</span>
                        )}
                      </span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label>Method</Label>
              <Select value={mode} onValueChange={(v) => setMode(v as "password" | "cookie")}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="password">Username &amp; password (auto-login)</SelectItem>
                  <SelectItem value="cookie">Paste session cookie</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>

          {mode === "password" ? (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div className="space-y-1.5">
                <Label htmlFor="site-user">Username / email</Label>
                <Input id="site-user" value={username} autoComplete="off"
                  onChange={(e) => setUsername(e.target.value)} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="site-pass">Password</Label>
                <Input id="site-pass" type="password" value={password} autoComplete="new-password"
                  onChange={(e) => setPassword(e.target.value)} />
              </div>
            </div>
          ) : (
            <div className="space-y-1.5">
              <Label htmlFor="site-cookie">Session cookie</Label>
              <Input id="site-cookie" value={cookie} placeholder="name=value; name2=value2"
                onChange={(e) => setCookie(e.target.value)} />
              <p className="text-[11px] text-muted-foreground">
                In your browser, log in to the site, open DevTools → Application → Cookies,
                and paste the cookie string (or the specific session cookie).
              </p>
            </div>
          )}

          <Button onClick={() => void handleAdd()} disabled={busy || !provider ||
            (mode === "password" ? !username.trim() || !password : !cookie.trim())}>
            {busy ? <Loader2 className="h-4 w-4 mr-2 animate-spin" /> : <KeyRound className="h-4 w-4 mr-2" />}
            {mode === "password" ? "Log in & save" : "Save cookie"}
          </Button>
        </div>
      ) : (
        !loading && creds.length > 0 && (
          <p className="border-t pt-4 text-xs text-muted-foreground">
            All known coin sites are configured. Remove one above to switch accounts.
          </p>
        )
      )}
    </CardContent>
  );
}

function StatusIcon({ status }: { status: string }) {
  if (status === "ok") return <Check className="h-4 w-4 shrink-0 text-emerald-500" />;
  if (status === "manual_cookie") return <Check className="h-4 w-4 shrink-0 text-blue-400" />;
  if (status === "failed") return <AlertTriangle className="h-4 w-4 shrink-0 text-red-500" />;
  return <Clock className="h-4 w-4 shrink-0 text-amber-500" />;
}
