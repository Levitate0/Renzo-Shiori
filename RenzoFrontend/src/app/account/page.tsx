"use client";

import React, { useRef, useState } from "react";
import { Check, KeyRound, Lock, Mail, MonitorSmartphone, Radio, UserRound, Upload } from "lucide-react";
import { toast } from "sonner";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { SettingsSectionNav } from "@/components/comp/layout/settings-section-nav";
import { useAuth } from "@/contexts/auth-context";
import { userService } from "@/lib/api/services/userService";
import { fetchGravatarBase64 } from "@/lib/gravatar";
import { SiteLoginsSection } from "@/components/comp/settings/site-logins-section";
import { DevicesSection } from "@/components/comp/settings/devices-section";
import { ScrobblerSettings } from "@/components/comp/scrobbler/scrobbler-settings";

const ACCOUNT_SECTIONS = [
  { id: "account", title: "Account", icon: UserRound },
  { id: "devices", title: "Devices", icon: MonitorSmartphone },
  { id: "site-logins", title: "Site Logins", icon: KeyRound },
  { id: "scrobbler", title: "Scrobbler", icon: Radio },
] as const;
type AccountSectionId = (typeof ACCOUNT_SECTIONS)[number]["id"];

/**
 * Per-user "Account" page — sensitive settings that belong to the signed-in
 * user rather than the whole install (unlike /settings, which is Owner-only
 * and covers app-wide config like SMTP/email). Available to every user
 * level; each section here only ever reads/writes the caller's own data
 * (site logins are scoped server-side by the authenticated user id).
 *
 * Profile picture + password/email live inline here (not behind a dialog) —
 * this is the one page a user actually wants when managing their own
 * account, so everything about "who am I" belongs on it directly.
 */
export default function AccountPage() {
  const { user, refreshAuth, changePassword } = useAuth();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [activeSectionId, setActiveSectionId] = useState<AccountSectionId>("account");

  // ── Profile picture ──────────────────────────────────────────────────
  const [avatarBase64, setAvatarBase64] = useState<string | undefined>(undefined);
  const [avatarContentType, setAvatarContentType] = useState<string | undefined>(undefined);
  const [avatarPreview, setAvatarPreview] = useState<string | null>(
    user?.avatarBase64 ? `data:${user.avatarContentType || "image/png"};base64,${user.avatarBase64}` : null,
  );
  const [gravatarEmail, setGravatarEmail] = useState("");
  const [avatarError, setAvatarError] = useState("");
  const [savingAvatar, setSavingAvatar] = useState(false);
  const avatarDirty = avatarBase64 !== undefined;

  const handleFileUpload = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    if (file.size > 2 * 1024 * 1024) {
      setAvatarError("Image must be less than 2MB");
      return;
    }
    const validTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];
    if (!validTypes.includes(file.type)) {
      setAvatarError("Only PNG, JPEG, GIF, and WebP images are allowed");
      return;
    }
    setAvatarError("");
    const reader = new FileReader();
    reader.onload = () => {
      const base64 = (reader.result as string).split(",")[1];
      setAvatarBase64(base64);
      setAvatarContentType(file.type);
      setAvatarPreview(reader.result as string);
    };
    reader.readAsDataURL(file);
  };

  const handleGravatarFetch = async () => {
    if (!gravatarEmail.trim()) return;
    setAvatarError("");
    try {
      const { base64, contentType } = await fetchGravatarBase64(gravatarEmail);
      setAvatarBase64(base64);
      setAvatarContentType(contentType);
      setAvatarPreview(`data:${contentType};base64,${base64}`);
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : "Gravatar error");
    }
  };

  const handleSaveAvatar = async () => {
    if (!avatarDirty) return;
    setSavingAvatar(true);
    setAvatarError("");
    try {
      await userService.updateMe({ avatarBase64, avatarContentType });
      await refreshAuth();
      setAvatarBase64(undefined);
      setAvatarContentType(undefined);
      setGravatarEmail("");
      toast.success("Avatar updated");
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : "Failed to save avatar");
    } finally {
      setSavingAvatar(false);
    }
  };

  const handleRemoveAvatar = async () => {
    setSavingAvatar(true);
    setAvatarError("");
    try {
      await userService.updateMe({ removeAvatar: true });
      await refreshAuth();
      setAvatarBase64(undefined);
      setAvatarContentType(undefined);
      setAvatarPreview(null);
      toast.success("Avatar removed");
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : "Failed to remove avatar");
    } finally {
      setSavingAvatar(false);
    }
  };

  // ── Email (password-reset delivery) ──────────────────────────────────
  const [email, setEmail] = useState(user?.email ?? "");
  const [savingEmail, setSavingEmail] = useState(false);
  const emailDirty = email.trim() !== (user?.email ?? "");

  const handleSaveEmail = async () => {
    setSavingEmail(true);
    try {
      await userService.updateMe({ email: email.trim() });
      await refreshAuth();
      toast.success("Email updated");
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to update email");
    } finally {
      setSavingEmail(false);
    }
  };

  // ── Password ──────────────────────────────────────────────────────────
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordError, setPasswordError] = useState("");
  const [savingPassword, setSavingPassword] = useState(false);

  const handleUpdatePassword = async (e: React.FormEvent) => {
    e.preventDefault();
    setPasswordError("");
    if (newPassword.length < 8) {
      setPasswordError("New password must be at least 8 characters");
      return;
    }
    if (newPassword !== confirmPassword) {
      setPasswordError("Passwords do not match");
      return;
    }
    setSavingPassword(true);
    try {
      await changePassword(currentPassword, newPassword);
      setCurrentPassword("");
      setNewPassword("");
      setConfirmPassword("");
      toast.success("Password changed");
    } catch (err) {
      setPasswordError(err instanceof Error ? err.message : "Failed to change password");
    } finally {
      setSavingPassword(false);
    }
  };

  const initials = user?.username.slice(0, 2).toUpperCase() ?? "?";

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Account</h1>
        <p className="text-sm text-muted-foreground">
          Personal settings for {user?.username ?? "your account"} — private to you.
        </p>
      </div>

      {/* Section nav — a sidebar list on wider screens, a full drawer (same
          pattern as the app's main nav) on narrow ones. Profile picture and
          Security live under the same "Account" section (they're both "who
          am I" for this account) — only Devices, Site Logins and Scrobbler get
          their own nav entries, since those are substantial blocks on their
          own. */}
      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[200px_1fr] lg:items-start">
        <SettingsSectionNav
          sections={ACCOUNT_SECTIONS}
          activeId={activeSectionId}
          onChange={(id) => setActiveSectionId(id as AccountSectionId)}
          drawerTitle="Account"
        />

        <div className="min-w-0 space-y-5">
        {activeSectionId === "account" && (
        <Card>
          <CardHeader className="pb-4">
            <CardTitle>Profile picture</CardTitle>
            <CardDescription>Shown on the account menu and the users list.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            {avatarError && (
              <div className="p-3 text-sm text-red-500 bg-red-50 dark:bg-red-950 rounded-md">
                {avatarError}
              </div>
            )}
            <div className="flex flex-wrap items-center gap-3">
              <div className="h-16 w-16 shrink-0 rounded-full bg-primary/20 border border-primary/30 flex items-center justify-center overflow-hidden">
                {avatarPreview ? (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img src={avatarPreview} alt="Avatar preview" className="h-full w-full object-cover" />
                ) : (
                  <span className="text-base font-semibold text-primary">{initials}</span>
                )}
              </div>
              <div className="min-w-0 flex-1 space-y-2">
                <div className="flex flex-wrap items-center gap-2">
                  <Button type="button" variant="outline" size="sm" onClick={() => fileInputRef.current?.click()}>
                    <Upload className="h-4 w-4 mr-2" />
                    Upload image
                  </Button>
                  {(avatarPreview || user?.avatarBase64) && (
                    <Button type="button" variant="ghost" size="sm" onClick={handleRemoveAvatar} disabled={savingAvatar}>
                      Remove
                    </Button>
                  )}
                  <input
                    ref={fileInputRef}
                    type="file"
                    accept=".png,.jpg,.jpeg,.gif,.webp"
                    className="hidden"
                    onChange={handleFileUpload}
                  />
                </div>
                <p className="text-xs text-muted-foreground">
                  Any image works — it&apos;s center-cropped and resized in your browser before upload.
                </p>
              </div>
            </div>

            <div className="space-y-2">
              <Label htmlFor="account-gravatar">Use Gravatar</Label>
              <div className="flex flex-wrap gap-2">
                <Input
                  id="account-gravatar"
                  type="email"
                  placeholder="you@example.com"
                  value={gravatarEmail}
                  onChange={(e) => setGravatarEmail(e.target.value)}
                  className="min-w-0 flex-1 sm:w-72 sm:flex-none"
                />
                <Button type="button" variant="secondary" onClick={handleGravatarFetch}>
                  Fetch
                </Button>
              </div>
              <p className="text-xs text-muted-foreground">
                Looks up that address&apos;s Gravatar as a preview — nothing changes until you save. The
                email itself is never sent to the server.
              </p>
            </div>

            <Button onClick={handleSaveAvatar} disabled={!avatarDirty || savingAvatar}>
              {savingAvatar ? "Saving…" : "Save avatar"}
            </Button>
          </CardContent>
        </Card>
        )}

        {/* Devices — the remembered sign-ins on this account */}
        {activeSectionId === "devices" && (
        <Card>
          <CardHeader>
            <CardTitle>Devices</CardTitle>
            <CardDescription>
              Browsers and televisions that stay signed in to your account. Sign out anything you
              don&apos;t recognise — or any TV you no longer use.
            </CardDescription>
          </CardHeader>
          <DevicesSection />
        </Card>
        )}

        {/* Site Logins */}
        {activeSectionId === "site-logins" && (
        <Card>
          <CardHeader>
            <CardTitle>Site Logins</CardTitle>
            <CardDescription>
              Log in to coin/paid sites (e.g. EzManga) so Renzo Shiori can load chapters you own — these
              credentials are yours alone and aren&apos;t visible to other users.
            </CardDescription>
          </CardHeader>
          <SiteLoginsSection />
        </Card>
        )}

        {/* Scrobbler */}
        {activeSectionId === "scrobbler" && (
        <Card>
          <CardHeader>
            <CardTitle>Scrobbler</CardTitle>
            <CardDescription>
              Link external trackers (AniList, MyAnimeList, Kitsu, MangaDex) to sync your reading
              progress. Your connections are private to your account — no other user can see them.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <ScrobblerSettings />
          </CardContent>
        </Card>
        )}

        {/* Security — email + password, inline (no dialog hop for something this common) */}
        {activeSectionId === "account" && (
        <Card>
          <CardHeader className="pb-4">
            <CardTitle>Security</CardTitle>
            <CardDescription>Changing your password signs out all your other sessions.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-5">
            <div className="space-y-2">
              <Label htmlFor="account-email">
                <span className="inline-flex items-center gap-1.5">
                  <Mail className="h-3.5 w-3.5" />
                  Email
                </span>
              </Label>
              <div className="flex flex-wrap gap-2">
                <Input
                  id="account-email"
                  type="email"
                  placeholder="Optional — used for password reset"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className="min-w-0 flex-1 sm:w-72 sm:flex-none"
                />
                <Button type="button" onClick={handleSaveEmail} disabled={!emailDirty || savingEmail}>
                  {savingEmail ? "Saving…" : "Save"}
                </Button>
              </div>
              <p className="text-xs text-muted-foreground">
                Password-reset links are sent here. Leave empty to disable email reset for this account.
              </p>
            </div>

            <form onSubmit={handleUpdatePassword} className="space-y-3 border-t pt-5">
              <div className="flex items-center gap-1.5 text-sm font-medium">
                <Lock className="h-3.5 w-3.5" />
                Password
              </div>
              {passwordError && (
                <div className="p-3 text-sm text-red-500 bg-red-50 dark:bg-red-950 rounded-md">
                  {passwordError}
                </div>
              )}
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <div className="space-y-2">
                  <Label htmlFor="currentPassword">Current password</Label>
                  <Input
                    id="currentPassword"
                    type="password"
                    autoComplete="current-password"
                    value={currentPassword}
                    onChange={(e) => setCurrentPassword(e.target.value)}
                    required
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="newPassword">New password</Label>
                  <Input
                    id="newPassword"
                    type="password"
                    autoComplete="new-password"
                    placeholder="At least 8 characters"
                    value={newPassword}
                    onChange={(e) => setNewPassword(e.target.value)}
                    required
                    minLength={8}
                  />
                </div>
              </div>
              <div className="space-y-2 sm:w-1/2 sm:pr-1.5">
                <Label htmlFor="confirmNewPassword">Confirm new password</Label>
                <Input
                  id="confirmNewPassword"
                  type="password"
                  autoComplete="new-password"
                  value={confirmPassword}
                  onChange={(e) => setConfirmPassword(e.target.value)}
                  required
                  minLength={8}
                />
              </div>
              <Button type="submit" disabled={savingPassword}>
                {savingPassword ? (
                  "Changing…"
                ) : (
                  <>
                    <Check className="h-4 w-4 mr-2" />
                    Update password
                  </>
                )}
              </Button>
            </form>
          </CardContent>
        </Card>
        )}
        </div>
      </div>
    </div>
  );
}
