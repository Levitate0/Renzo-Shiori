"use client";

import "@/styles/globals.css";
import React from "react";

import { GeistSans } from "geist/font/sans";
import { Fraunces, JetBrains_Mono } from "next/font/google";
import { Toaster } from "sonner";

const fraunces = Fraunces({
  subsets: ["latin"],
  display: "swap",
  variable: "--font-fraunces",
  axes: ["opsz"],
});

const jetbrainsMono = JetBrains_Mono({
  subsets: ["latin"],
  weight: ["400", "500", "600"],
  display: "swap",
  variable: "--font-jetbrains-mono",
});

import { ThemeProvider } from "@/components/theme/theme-provider";
import { TooltipProvider } from "@/components/ui/tooltip";
import QueryProvider from "@/components/providers/query-provider";
import { AuthProvider } from "@/contexts/auth-context";
import { SetupWizardProvider } from "@/components/providers/setup-wizard-provider";
import { ImportWizardProvider } from "@/components/providers/import-wizard-provider";
import { ClientSideSetupWizard } from "@/components/comp/setup-wizard/client-wrapper";
import { ImportProgressPill } from "@/components/comp/setup-wizard/import-progress-pill";
import { ImportWizard } from "@/components/comp/import-wizard";
import { FontLoader } from "@/components/ui/font-loader";
import { SearchProvider } from "@/contexts/search-context";
import { ServiceWorkerRegistrar } from "@/components/comp/service-worker-registrar";

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en" className={`${GeistSans.variable} ${fraunces.variable} ${jetbrainsMono.variable}`} suppressHydrationWarning>
      <head>
        <title>Renzō</title>
        <meta name="description" content="Series Downloader" />
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover"/>
        <meta name="theme-color" content="#0a0a0a"/>
        <meta name="mobile-web-app-capable" content="yes"/>
        <meta name="apple-mobile-web-app-capable" content="yes"/>
        <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent"/>
        <meta name="apple-mobile-web-app-title" content="Renzō"/>
        {/* Cache-busted (?v=2) — browsers cache favicons very aggressively
            (often ignoring normal Cache-Control), so a same-URL swap of the
            underlying PNG doesn't reliably reach an open tab. Bump the
            version string whenever the icon art changes again. */}
        <link rel="icon" href="/favicon.ico?v=2" sizes="any"/>
        <link rel="icon" type="image/png" sizes="32x32" href="/favicon-32x32.png?v=2"/>
        <link rel="icon" type="image/png" sizes="16x16" href="/favicon-16x16.png?v=2"/>
        <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png?v=2"/>
        <link rel="manifest" href="/site.webmanifest?v=2"/>
        <script
          dangerouslySetInnerHTML={{
            __html: `
              (function() {
                try {
                  var storageKey = 'renzo-theme';
                  var theme = localStorage.getItem(storageKey);
                  var systemTheme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
                  
                  if (theme === 'dark' || (theme === 'system' && systemTheme === 'dark') || (!theme && systemTheme === 'dark')) {
                    document.documentElement.classList.add('dark');
                    document.documentElement.style.colorScheme = 'dark';
                  } else {
                    document.documentElement.classList.remove('dark');
                    document.documentElement.style.colorScheme = 'light';
                  }
                } catch (_) {
                  // Fallback to system preference
                  if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
                    document.documentElement.classList.add('dark');
                    document.documentElement.style.colorScheme = 'dark';
                  }
                }

                try {
                  // Accent color theme — see src/lib/utils/accent-theme.ts.
                  // "rose" is the default and needs no attribute (it's the
                  // fallback CSS values), so only non-default accents apply one.
                  var accent = localStorage.getItem('renzo-accent');
                  var known = ['blue', 'green', 'purple', 'orange', 'slate'];
                  if (accent && known.indexOf(accent) !== -1) {
                    document.documentElement.setAttribute('data-accent', accent);
                  }
                } catch (_) {
                  // Fall back to the default rose accent.
                }
              })();
            `,
          }}
        />
        <style dangerouslySetInnerHTML={{
          __html: `
            /* Prevent any flash by setting initial colors immediately */
            html { background: white; }
            html.dark { background: hsl(20, 14.3%, 4.1%); }
            @media (prefers-color-scheme: dark) {
              html:not(.light) { background: hsl(20, 14.3%, 4.1%); }
            }
          `
        }} />
      </head>
      <body suppressHydrationWarning>        <ThemeProvider
          attribute="class"
          defaultTheme="system"
          enableSystem
          disableTransitionOnChange
          storageKey="renzo-theme"
        >
          <TooltipProvider>
            <QueryProvider>
              <AuthProvider>
                <SetupWizardProvider>
                  <ImportWizardProvider>
                    <SearchProvider>
                      <FontLoader />
                      <ServiceWorkerRegistrar />
                      <ClientSideSetupWizard />
                      <ImportProgressPill />
                      <ImportWizard />
                      {children}
                      <Toaster position="top-center" richColors />
                    </SearchProvider>
                  </ImportWizardProvider>
                </SetupWizardProvider>
              </AuthProvider>
            </QueryProvider>
          </TooltipProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
