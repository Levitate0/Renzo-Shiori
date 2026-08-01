"use client";

import React, { useState, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import { useScrobblerConfigs, useUpdateScrobblerConfig, useScrobblerAuthorize, useScrobblerCallback, useScrobblerDisconnect, useTriggerSync, useSyncStatus, useSaveComicVineApiKey, useKitsuDirectAuth, useMangaDexDirectAuth } from '@/lib/api/hooks/useScrobbler';
import { useScrobblerUnmatched, useAutoMatchAll, useConfirmMatch, useDisableLink } from '@/lib/api/hooks/useScrobbler';
import { ScrobblerProvider, type ScrobblerConfig, type OAuthCallbackRequest, type ScrobblerConfigUpdate } from '@/lib/api/types';
import { SeriesMatchDialog } from '@/components/comp/scrobbler/series-match-dialog';
import { RefreshCw, Link, Link2Off, ExternalLink, Key, ListChecks } from 'lucide-react';
import { toast } from 'sonner';
import { apiClient } from '@/lib/api/client';

const providerIcons: Record<ScrobblerProvider, string> = {
  [ScrobblerProvider.MyAnimeList]: 'MAL',
  [ScrobblerProvider.AniList]: 'AL',
  [ScrobblerProvider.ComicVine]: 'CV',
  [ScrobblerProvider.Kitsu]: 'KT',
  [ScrobblerProvider.MangaDex]: 'MD',
};

export function ScrobblerSettings() {
  const queryClient = useQueryClient();
  const { data: configs, isLoading: configsLoading } = useScrobblerConfigs();
  const { data: unmatched } = useScrobblerUnmatched();
  const updateConfig = useUpdateScrobblerConfig();
  const authorize = useScrobblerAuthorize();
  const callback = useScrobblerCallback();
  const disconnect = useScrobblerDisconnect();
  const triggerSync = useTriggerSync();
  const autoMatchAll = useAutoMatchAll();

  const kitsuAuth = useKitsuDirectAuth();
  const mangaDexAuth = useMangaDexDirectAuth();

  const [selectedSeries, setSelectedSeries] = useState<{ seriesId: string; provider: ScrobblerProvider } | null>(null);
  // Tracks the WHOLE connect flow (URL fetch → redirect → poll-for-completion,
  // which can run up to ~2 minutes) — NOT just authorize.isPending, which only
  // covers the brief initial URL fetch. Without this, the button re-enabled the
  // instant the URL request resolved, so an impatient second click during the
  // redirect/poll window fired a second independent authorize+redirect with a
  // different `state`, and whichever one didn't "win" the navigation raced with
  // the one that did — the proxy's callback then saw a state that either never
  // got visited or was superseded, and rejected it as "authorization session not
  // found" even though the flow up to that point looked fine.
  const [connectingProvider, setConnectingProvider] = useState<ScrobblerProvider | null>(null);
  const [comicVineApiKey, setComicVineApiKey] = useState('');
  const [kitsuEmail, setKitsuEmail] = useState('');
  const [kitsuPassword, setKitsuPassword] = useState('');
  const [mdUsername, setMdUsername] = useState('');
  const [mdPassword, setMdPassword] = useState('');
  const [mdClientId, setMdClientId] = useState('');
  const [mdClientSecret, setMdClientSecret] = useState('');
  const saveComicVineKey = useSaveComicVineApiKey();

  const handleToggleEnabled = useCallback((config: ScrobblerConfig) => {
    const update: ScrobblerConfigUpdate = { isEnabled: !config.isEnabled };
    updateConfig.mutate({ provider: config.provider, update });
  }, [updateConfig]);

  const handleToggleAutoSync = useCallback((config: ScrobblerConfig) => {
    const update: ScrobblerConfigUpdate = { autoSync: !config.autoSync };
    updateConfig.mutate({ provider: config.provider, update });
  }, [updateConfig]);

  // Poll the state-based callback until the backend has pulled the tokens from the
  // proxy and stored them. Reused by the popup flow AND the redirect-resume flow.
  const completePending = useCallback(async (
    providerName: string, providerNum: ScrobblerProvider, state: string, maxAttempts: number,
  ): Promise<boolean> => {
    const callbackUrl = `/api/scrobbler/callback/${providerName}?state=${state}`;
    let ok = false;
    for (let i = 0; i < maxAttempts && !ok; i++) {
      try { await apiClient.get<{ connected: boolean }>(callbackUrl); ok = true; }
      catch { await new Promise(r => setTimeout(r, 2000)); }
    }
    if (ok) {
      try { localStorage.removeItem('scrobbler_pending'); } catch { /* ignore */ }
      await queryClient.invalidateQueries({ queryKey: ['scrobbler', 'configs'] });
      try { await autoMatchAll.mutateAsync(providerNum); await triggerSync.mutateAsync(); } catch { /* best effort */ }
    }
    return ok;
  }, [queryClient, autoMatchAll, triggerSync]);

  // Resume a connection that used the full-page redirect (webviews can't do popups):
  // when the user returns to the app after authorizing, finish it. The proxy holds
  // the tokens keyed by state; we just retrieve them.
  React.useEffect(() => {
    let raw: string | null = null;
    try { raw = localStorage.getItem('scrobbler_pending'); } catch { /* ignore */ }
    if (!raw) return;
    type Pending = { providerName?: string; providerNum?: ScrobblerProvider; state?: string };
    let p: Pending | null = null;
    try { p = JSON.parse(raw) as Pending; } catch { try { localStorage.removeItem('scrobbler_pending'); } catch {} return; }
    if (!p?.providerName || !p?.state || p.providerNum === undefined) {
      try { localStorage.removeItem('scrobbler_pending'); } catch {}
      return;
    }
    void (async () => {
      const done = await completePending(p!.providerName!, p!.providerNum!, p!.state!, 6);
      if (!done) { try { localStorage.removeItem('scrobbler_pending'); } catch {} } // stale — don't retry forever
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleConnect = useCallback(async (config: ScrobblerConfig) => {
    // Re-entrancy guard for the whole flow — see connectingProvider's comment.
    if (connectingProvider != null) return;
    setConnectingProvider(config.provider);
    try {
      const providerName = ScrobblerProvider[config.provider];
      const result = await authorize.mutateAsync(providerName);

      // Persist BEFORE navigating so the redirect path can resume on return.
      try {
        localStorage.setItem('scrobbler_pending',
          JSON.stringify({ providerName, providerNum: config.provider, state: result.state }));
      } catch { /* ignore */ }

      // Always a same-window redirect — NOT window.open(). A popup sounds nicer in
      // a normal browser tab, but it doesn't hold up across our actual targets:
      // WebView2 (exe) treats window.open('about:blank', ...) as a new-window
      // request and, since about:blank isn't the Renzo host, hands it to the OS to
      // open externally — which has no real handler for a bare about:blank URI, so
      // Windows shows a "search the Microsoft Store" prompt instead of anything
      // useful. Android's WebView has its own equivalent popup quirks. A same-
      // window redirect sidesteps all of that everywhere:
      //  - Ordinary browser tab: navigates away; the on-return effect above
      //    resumes from the persisted state on the next mount.
      //  - Native shells (WPF/Android): they intercept a same-window navigation to
      //    a foreign host and hand THAT off to the system browser instead — this
      //    page never actually unloads, so poll for completion right here.
      window.location.href = result.authUrl;
      const connected = await completePending(providerName, config.provider, result.state, 60);
      if (!connected) console.warn('[Scrobbler] connect did not complete in time');
    } catch (err) {
      console.error('[Scrobbler] OAuth authorization failed:', err);
    } finally {
      setConnectingProvider(null);
    }
  }, [authorize, completePending, connectingProvider]);

  const handleDisconnect = useCallback((config: ScrobblerConfig) => {
    const providerName = ScrobblerProvider[config.provider];
    disconnect.mutate(providerName);
  }, [disconnect]);

  const handleSaveComicVineKey = useCallback(async () => {
    if (!comicVineApiKey.trim()) return;
    await saveComicVineKey.mutateAsync(comicVineApiKey);
    setComicVineApiKey('');
  }, [comicVineApiKey, saveComicVineKey]);

  if (configsLoading) {
    return <div className="p-4 text-muted-foreground">Loading scrobbler settings...</div>;
  }

  const unmatchedCount = unmatched?.filter(u => u.mappingStatus === 0).length ?? 0;

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-2xl font-bold">Scrobbler / Tracking</h2>
          <p className="text-sm text-muted-foreground">
            Connect your reading progress to external tracking services
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          {/* Track the whole library: auto-match every series against each
              connected tracker. Individual series can be toggled off from
              their own page. */}
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              const connected = (configs ?? []).filter((c) => c.isConnected);
              if (connected.length === 0) {
                toast.info('Connect a tracker below first.');
                return;
              }
              connected.forEach((c) => autoMatchAll.mutate(c.provider));
              toast.success('Matching your whole library to your trackers…', {
                description: 'New links appear under Unmatched Series as they resolve.',
              });
            }}
            disabled={autoMatchAll.isPending}
          >
            <ListChecks className={`h-4 w-4 mr-2 ${autoMatchAll.isPending ? 'animate-spin' : ''}`} />
            Track all series
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => triggerSync.mutate()}
            disabled={triggerSync.isPending}
          >
            <RefreshCw className={`h-4 w-4 mr-2 ${triggerSync.isPending ? 'animate-spin' : ''}`} />
            Sync All
          </Button>
        </div>
      </div>

      <Separator />

      {/* Provider Cards */}
      <div className="grid grid-cols-1 gap-4">
        {configs?.map((config) => (
          <Card key={config.provider} className="overflow-hidden">
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-3">
                  <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 text-sm font-bold text-primary">
                    {providerIcons[config.provider]}
                  </div>
                  <div>
                    <CardTitle className="text-lg">{config.displayName}</CardTitle>
                    <CardDescription>
                      {config.isConnected ? (
                        <Badge variant="default" className="mt-1">Connected</Badge>
                      ) : (
                        <Badge variant="secondary" className="mt-1">Disconnected</Badge>
                      )}
                    </CardDescription>
                  </div>
                </div>
              </div>
            </CardHeader>
            <CardContent>
              <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                <div className="flex flex-wrap items-center gap-x-6 gap-y-2">
                  {/* Enabled toggle */}
                  <div className="flex items-center gap-2">
                    <Switch
                      checked={config.isEnabled}
                      onCheckedChange={() => handleToggleEnabled(config)}
                      disabled={!config.isConnected}
                    />
                    <span className="text-sm">Enabled</span>
                  </div>

                  {/* Auto-sync toggle */}
                  {config.isConnected && (
                    <div className="flex items-center gap-2">
                      <Switch
                        checked={config.autoSync}
                        onCheckedChange={() => handleToggleAutoSync(config)}
                      />
                      <span className="text-sm">Auto Sync</span>
                    </div>
                  )}
                </div>

                <div className="flex flex-wrap items-center gap-2">
                  {config.isConnected ? (
                    <>
                      {config.lastSyncAt && (
                        <span className="text-xs text-muted-foreground">
                          Last sync: {new Date(config.lastSyncAt).toLocaleDateString()}
                        </span>
                      )}
                      <Button
                        variant="destructive"
                        size="sm"
                        onClick={() => handleDisconnect(config)}
                      >
                        <Link2Off className="h-4 w-4 mr-1" />
                        Disconnect
                      </Button>
                    </>
                  ) : config.supportsDirectAuth ? (
                    config.provider === ScrobblerProvider.Kitsu ? (
                      <div className="flex flex-wrap items-center gap-2">
                        <Input
                          type="email"
                          placeholder="Email"
                          value={kitsuEmail}
                          onChange={(e) => setKitsuEmail(e.target.value)}
                          className="h-8 w-full min-[420px]:w-40 text-xs"
                        />
                        <Input
                          type="password"
                          placeholder="Password"
                          value={kitsuPassword}
                          onChange={(e) => setKitsuPassword(e.target.value)}
                          className="h-8 w-full min-[420px]:w-40 text-xs"
                        />
                        <Button
                          variant="default"
                          size="sm"
                          onClick={() => {
                            kitsuAuth.mutate({ email: kitsuEmail, password: kitsuPassword });
                          }}
                          disabled={kitsuAuth.isPending || !kitsuEmail.trim() || !kitsuPassword.trim()}
                        >
                          <Link className="h-4 w-4 mr-1" />
                          {kitsuAuth.isPending ? 'Connecting...' : 'Connect'}
                        </Button>
                      </div>
                    ) : config.provider === ScrobblerProvider.MangaDex ? (
                      <div className="flex w-full flex-col gap-2 items-stretch sm:w-auto sm:items-end">
                        <a
                          href="https://mangadex.org/settings"
                          target="_blank"
                          rel="noopener noreferrer"
                          className="text-xs text-blue-500 hover:underline"
                        >
                          Create personal API client on MangaDex
                        </a>
                        <div className="flex flex-wrap items-center gap-2">
                          <Input
                            type="text"
                            placeholder="Username"
                            value={mdUsername}
                            onChange={(e) => setMdUsername(e.target.value)}
                            className="h-8 w-full min-[420px]:w-28 text-xs"
                          />
                          <Input
                            type="password"
                            placeholder="Password"
                            value={mdPassword}
                            onChange={(e) => setMdPassword(e.target.value)}
                            className="h-8 w-full min-[420px]:w-28 text-xs"
                          />
                          <Input
                            type="text"
                            placeholder="Client ID"
                            value={mdClientId}
                            onChange={(e) => setMdClientId(e.target.value)}
                            className="h-8 w-full min-[420px]:w-28 text-xs"
                          />
                          <Input
                            type="password"
                            placeholder="Client Secret"
                            value={mdClientSecret}
                            onChange={(e) => setMdClientSecret(e.target.value)}
                            className="h-8 w-full min-[420px]:w-28 text-xs"
                          />
                          <Button
                            variant="default"
                            size="sm"
                            onClick={() => {
                              mangaDexAuth.mutate({
                                username: mdUsername,
                                password: mdPassword,
                                clientId: mdClientId,
                                clientSecret: mdClientSecret,
                              });
                            }}
                            disabled={mangaDexAuth.isPending || !mdUsername.trim() || !mdPassword.trim() || !mdClientId.trim() || !mdClientSecret.trim()}
                          >
                            <Link className="h-4 w-4 mr-1" />
                            {mangaDexAuth.isPending ? 'Connecting...' : 'Connect'}
                          </Button>
                        </div>
                      </div>
                    ) : null
                  ) : config.provider === ScrobblerProvider.ComicVine ? (
                    <div className="flex flex-wrap items-center gap-2">
                      <Input
                        type="password"
                        placeholder="Enter ComicVine API key"
                        value={comicVineApiKey}
                        onChange={(e) => setComicVineApiKey(e.target.value)}
                        className="h-8 w-full min-[420px]:w-48 text-xs"
                      />
                      <Button
                        variant="default"
                        size="sm"
                        onClick={handleSaveComicVineKey}
                        disabled={saveComicVineKey.isPending || !comicVineApiKey.trim()}
                      >
                        <Key className="h-4 w-4 mr-1" />
                        Save Key
                      </Button>
                    </div>
                  ) : (
                    <Button
                      variant="default"
                      size="sm"
                      onClick={() => handleConnect(config)}
                      disabled={connectingProvider != null}
                    >
                      <Link className="h-4 w-4 mr-1" />
                      {connectingProvider === config.provider ? 'Connecting…' : 'Connect'}
                    </Button>
                  )}
                </div>
              </div>
            </CardContent>
          </Card>
        ))}
      </div>

      <Separator />

      {/* Unmatched Series Section */}
      <div className="space-y-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h3 className="text-lg font-semibold">Unmatched Series</h3>
            <p className="text-sm text-muted-foreground">
              {unmatchedCount > 0
                ? `${unmatchedCount} series need manual matching`
                : 'All series are matched'}
            </p>
          </div>
          {unmatchedCount > 0 && (
            <div className="flex gap-2">
              <Button
                variant="outline"
                size="sm"
                onClick={() => {
                  // Auto-match across all providers
                  Object.values(ScrobblerProvider).filter(v => typeof v === 'number').forEach(p => {
                    autoMatchAll.mutate(p as ScrobblerProvider);
                  });
                }}
                disabled={autoMatchAll.isPending}
              >
                <RefreshCw className={`h-4 w-4 mr-2 ${autoMatchAll.isPending ? 'animate-spin' : ''}`} />
                Auto-Match All
              </Button>
            </div>
          )}
        </div>

        {unmatched && unmatched.length > 0 && (
          <div className="rounded-md border">
            <div className="overflow-x-auto p-4">
              {/* table-fixed + colgroup so the Series title (which can run long)
                  clamps to 2 lines instead of wrapping to 5+ and squeezing the
                  Provider/Status/Actions columns down to nothing. */}
              <table className="w-full min-w-[36rem] table-fixed text-sm">
                <colgroup>
                  <col />
                  <col className="w-28" />
                  <col className="w-40" />
                  <col className="w-24" />
                </colgroup>
                <thead>
                  <tr className="border-b text-left">
                    <th className="pb-2 pr-3 font-medium">Series</th>
                    <th className="pb-2 pr-3 font-medium">Provider</th>
                    <th className="pb-2 pr-3 font-medium">Status</th>
                    <th className="pb-2 font-medium">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {unmatched.filter(s => s.mappingStatus !== 2).slice(0, 20).map((status) => (
                    <tr key={`${status.seriesId}-${status.provider}`} className="border-b last:border-0">
                      <td className="py-3 pr-3 align-top">
                        <span className="line-clamp-2" title={status.seriesTitle}>{status.seriesTitle}</span>
                      </td>
                      <td className="py-3 pr-3 align-top text-muted-foreground">{ScrobblerProvider[status.provider]}</td>
                      <td className="py-3 pr-3 align-top">
                        {status.mappingStatus === 0 && (
                          <Badge variant="secondary" className="whitespace-nowrap">Not matched</Badge>
                        )}
                        {status.mappingStatus === 1 && (
                          <Badge variant="default" className="whitespace-nowrap">
                            Auto-matched ({Math.round((status.matchScore ?? 0) * 100)}%)
                          </Badge>
                        )}
                        {status.mappingStatus === 3 && (
                          <Badge variant="secondary" className="whitespace-nowrap">Disabled</Badge>
                        )}
                      </td>
                      <td className="py-3 align-top">
                        <Button
                          variant="ghost"
                          size="sm"
                          className="whitespace-nowrap"
                          onClick={() => setSelectedSeries({ seriesId: status.seriesId, provider: status.provider })}
                        >
                          <ExternalLink className="h-4 w-4 mr-1" />
                          Match
                        </Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>

      {/* Series Match Dialog */}
      {selectedSeries && (
        <SeriesMatchDialog
          seriesId={selectedSeries.seriesId}
          provider={selectedSeries.provider}
          open={true}
          onOpenChange={(open: boolean) => {
            if (!open) setSelectedSeries(null);
          }}
        />
      )}
    </div>
  );
}