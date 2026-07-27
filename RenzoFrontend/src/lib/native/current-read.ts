/**
 * Tracks the chapter the reader currently has open, so the purge-on-reconnect
 * watcher can spare it (you don't lose the chapter you're mid-way through when
 * the network blips back). The reader sets this on open and clears it on close;
 * everything else is decoupled from the reader.
 */
let currentReadingChapterKey: string | undefined;

export function setCurrentReadingChapter(chapterKey: string | undefined): void {
  currentReadingChapterKey = chapterKey;
}

export function getCurrentReadingChapter(): string | undefined {
  return currentReadingChapterKey;
}
