"use client";

import { Plug } from "lucide-react";
import React from 'react';

import { RibbonSlot } from "@/components/comp/layout/ribbon";
import { ExtensionVersions } from "@/components/comp/sources/extension-versions";
import { SourcesList } from "@/components/comp/sources/sources-list";
import { DefaultPriorityOrderTab } from "@/components/comp/sources/default-priority-order-tab";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useSearch } from "@/contexts/search-context";

export default function ProvidersPage() {
  const { searchTerm, clearSearch } = useSearch();

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

      <Tabs defaultValue="sources">
        <TabsList>
          <TabsTrigger value="sources">Sources</TabsTrigger>
          <TabsTrigger value="default-priority">Default priority order</TabsTrigger>
        </TabsList>

        <TabsContent value="sources" className="space-y-6">
          <ExtensionVersions />
          <SourcesList searchTerm={searchTerm} clearSearch={clearSearch} />
        </TabsContent>

        <TabsContent value="default-priority">
          <DefaultPriorityOrderTab />
        </TabsContent>
      </Tabs>
    </div>
  );
}
