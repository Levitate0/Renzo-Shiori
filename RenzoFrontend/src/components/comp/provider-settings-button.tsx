"use client";

import React, { useState } from 'react';
import { Button } from "@/components/ui/button";
import { Settings } from "lucide-react";
import { ProviderPreferencesRequester } from "./provider-preferences-requester";
import { useAuth } from "@/contexts/auth-context";

interface ProviderSettingsButtonProps {
  pkgName: string;
  providerName?: string;
  variant?: "default" | "outline" | "secondary" | "ghost" | "link" | "destructive";
  size?: "default" | "sm" | "lg" | "icon";
  className?: string;
}

export function ProviderSettingsButton({
  pkgName,
  providerName,
  variant = "outline",
  size = "sm",
  className = ""
}: ProviderSettingsButtonProps) {
  const [preferencesOpen, setPreferencesOpen] = useState(false);
  // Extension settings can hold a source login: reading and saving them is Owner-only.
  const { canOwner } = useAuth();
  if (!canOwner) return null;

  return (
    <>
      <Button
        variant={variant}
        size={size}
        className={className}
        onClick={() => setPreferencesOpen(true)}
      >
        <Settings className="h-4 w-4" />
        {size !== "icon" && <span className="ml-1 hidden md:inline">Settings</span>}
      </Button>

      <ProviderPreferencesRequester
        open={preferencesOpen}
        onOpenChange={setPreferencesOpen}
        pkgName={pkgName}
        providerName={providerName}
      />
    </>
  );
}
