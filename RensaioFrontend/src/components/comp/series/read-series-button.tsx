"use client";

import { useMemo } from "react";
import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { BookOpen } from "lucide-react";

import { Button } from "@/components/ui/button";
import { useSettings } from "@/lib/api/hooks/useSettings";
import { readerService } from "@/lib/api/services/readerService";

/**
 * Primary entry point into the built-in reader from a series page.
 * Picks up where the user left off: the first partially-read chapter,
 * else the first unread downloaded chapter, else the first downloaded one.
 * Renders nothing when the reader is off or no chapter is downloaded.
 */
export function ReadSeriesButton({ seriesId }: { seriesId: string }) {
  const router = useRouter();
  const { data: settings } = useSettings();
  const readerEnabled = settings?.readerEnabled !== false;

  const { data } = useQuery({
    queryKey: ["reader", "chapters", seriesId],
    queryFn: () => readerService.getChapters(seriesId),
    enabled: readerEnabled && !!seriesId,
    staleTime: 30 * 1000,
  });

  const target = useMemo(() => {
    const readable = (data?.chapters ?? []).filter((c) => !!c.filename);
    if (readable.length === 0) return null;
    const inProgress = readable.find((c) => !c.isCompleted && c.progress > 0);
    if (inProgress) return { chapter: inProgress, label: "Continue" };
    const unread = readable.find((c) => !c.isCompleted);
    if (unread) {
      const anyRead = readable.some((c) => c.isCompleted);
      return { chapter: unread, label: anyRead ? "Continue" : "Read" };
    }
    return { chapter: readable[0]!, label: "Reread" };
  }, [data]);

  if (!readerEnabled || !target) return null;

  return (
    <Button
      variant="default"
      onClick={() => router.push(`/reader?seriesId=${seriesId}&chapter=${target.chapter.number}`)}
      className="px-0 w-9 sm:w-auto sm:px-4"
      title={`${target.label} — chapter ${target.chapter.number}`}
    >
      <BookOpen className="h-4 w-4 sm:mr-2" />
      <span className="hidden sm:inline">
        {target.label}
        <span className="ml-1 opacity-70">Ch. {target.chapter.number}</span>
      </span>
    </Button>
  );
}
