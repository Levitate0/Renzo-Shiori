"use client";

import React, { useState } from "react";
import { Lock } from "lucide-react";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/contexts/auth-context";
import { ChangePasswordDialog } from "@/components/comp/users/change-password-dialog";
import { SiteLoginsSection } from "@/components/comp/settings/site-logins-section";
import { ScrobblerSettings } from "@/components/comp/scrobbler/scrobbler-settings";

/**
 * Per-user "Account" page — sensitive settings that belong to the signed-in
 * user rather than the whole install (unlike /settings, which is Owner-only
 * and covers app-wide config like SMTP/email). Available to every user
 * level; each section here only ever reads/writes the caller's own data
 * (site logins are scoped server-side by the authenticated user id).
 */
export default function AccountPage() {
  const { user } = useAuth();
  const [isChangePasswordOpen, setIsChangePasswordOpen] = useState(false);

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Account</h1>
        <p className="text-sm text-muted-foreground">
          Personal settings for {user?.username ?? "your account"} — private to you.
        </p>
      </div>

      <div className="grid gap-6">
        <Card>
          <CardHeader>
            <CardTitle>Password</CardTitle>
            <CardDescription>Change your own sign-in password.</CardDescription>
          </CardHeader>
          <CardContent>
            <Button variant="outline" onClick={() => setIsChangePasswordOpen(true)}>
              <Lock className="h-4 w-4 mr-2" />
              Change password
            </Button>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Site Logins</CardTitle>
            <CardDescription>
              Log in to coin/paid sites (e.g. EzManga) so Renzō can load chapters you own — these
              credentials are yours alone and aren&apos;t visible to other users.
            </CardDescription>
          </CardHeader>
          <SiteLoginsSection />
        </Card>

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
      </div>

      <ChangePasswordDialog
        open={isChangePasswordOpen}
        onOpenChange={setIsChangePasswordOpen}
      />
    </div>
  );
}
