"use client";

import { Plug } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import React, { Suspense } from 'react';

import { RibbonSlot } from "@/components/comp/layout/ribbon";
import { ExtensionVersions } from "@/components/comp/sources/extension-versions";
import { SourcesList } from "@/components/comp/sources/sources-list";
import { DefaultPriorityOrderTab } from "@/components/comp/sources/default-priority-order-tab";
import { MigrateTab } from "@/components/comp/sources/migrate-tab";
import { HealthTab } from "@/components/comp/sources/health-tab";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useSearch } from "@/contexts/search-context";

const TABS = ["sources", "default-priority", "health", "migrate"];

export default function ProvidersPage() {
  return (
    <Suspense fallback={null}>
      <ProvidersContent />
    </Suspense>
  );
}

function ProvidersContent() {
  const { searchTerm, clearSearch } = useSearch();
  // The tab is in the URL (?tab=health, ?tab=migrate&from=MangaX), so the
  // command bar's dead-source alert can link straight to it.
  const params = useSearchParams();
  const router = useRouter();
  const tabParam = params.get("tab");
  const tab = tabParam && TABS.includes(tabParam) ? tabParam : "sources";
  const from = params.get("from");
  const openTab = (next: string, fromProvider?: string) =>
    router.replace(
      next === "sources" ? "/providers/" : `/providers/?tab=${next}${fromProvider ? `&from=${encodeURIComponent(fromProvider)}` : ""}`,
      { scroll: false },
    );

  return (
    <div className="space-y-6">
      <RibbonSlot>
        <div className="flex w-full items-center gap-2">
          <Plug className="h-4 w-4 text-muted-foreground shrink-0" />
          <h2 className="truncate text-sm font-semibold text-foreground">
            Sources
          </h2>
          <span className="hidden sm:inline truncate text-xs text-muted-foreground">
            · Install, enable, and health-check Mihon extensions
          </span>
        </div>
      </RibbonSlot>

      <Tabs value={tab} onValueChange={(t) => openTab(t)}>
        <TabsList className="h-auto flex-wrap">
          <TabsTrigger value="sources">Sources</TabsTrigger>
          <TabsTrigger value="default-priority">Default priority order</TabsTrigger>
          {/* Hub twin: ui/sources/SourcesScreen.kt "Health" tab → HealthTab.kt */}
          <TabsTrigger value="health">Health</TabsTrigger>
          {/* Hub twin: ui/sources/SourcesScreen.kt "Migrate" tab → MigrateTab.kt */}
          <TabsTrigger value="migrate">Migrate</TabsTrigger>
        </TabsList>

        <TabsContent value="sources" className="space-y-6">
          <ExtensionVersions />
          <SourcesList searchTerm={searchTerm} clearSearch={clearSearch} />
        </TabsContent>

        <TabsContent value="default-priority">
          <DefaultPriorityOrderTab />
        </TabsContent>

        <TabsContent value="health">
          <HealthTab onMigrate={(provider) => openTab("migrate", provider)} />
        </TabsContent>

        <TabsContent value="migrate">
          {/* Keyed by the source so "Migrate" from Health or the alert re-picks it. */}
          <MigrateTab key={from ?? ""} initialFrom={from} />
        </TabsContent>
      </Tabs>
    </div>
  );
}
