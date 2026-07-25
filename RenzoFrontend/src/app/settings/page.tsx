"use client";

import React from 'react';
import { SettingsManager } from "@/components/comp/settings-manager";

export default function SettingsPage() {
  return (
    <div className="space-y-8">
      <SettingsManager
        showHeader={true}
        showSaveButton={true}
        title="Settings"
        description="Configure your Renzo Shiori application settings"
      />
      {/* Scrobbler moved to the per-user Account page (/account) — each user
          links their own trackers privately; it's no longer Owner-gated here. */}
    </div>
  );
}
