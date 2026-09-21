using System.Collections.Concurrent;

namespace Mihon.ExtensionsBridge.Core.Runtime.Gatekeeper
{
    /// <summary>
    /// Serialises source calls that an extension refuses to run concurrently for
    /// the same manga.
    ///
    /// extensions-lib's threaded details/chapters path guards itself and THROWS
    /// rather than queueing:
    ///
    ///   /source/chapters failed: getMangaUpdate must not be called concurrently
    ///   for same manga
    ///
    /// Several things legitimately ask for one series at once — the scheduled
    /// GetChapters job, a library refresh, the client's own series-page poll, and
    /// the reader opening a chapter — so four fetches for one series inside a
    /// second is normal traffic, not a bug in the callers. Whichever ones lose
    /// the race get an exception, the series is recorded as having NO chapters,
    /// and the reader then answers 404 for a chapter it could have streamed.
    /// Measured at 45-58 rejections a day, every day.
    ///
    /// Keyed on source + manga url, because the constraint is per manga: two
    /// different series on one source may still run in parallel.
    ///
    /// STATIC on purpose. Interops are cached, dropped and rebuilt (see
    /// ExtensionManager's sidecar-generation handling), so a per-instance lock
    /// would let two wrappers around the same source call concurrently anyway.
    ///
    /// Lock ORDER matters: take this BEFORE the extension gate, never after. A
    /// caller holding this and waiting on the gate is fine, because waiters on
    /// this lock hold nothing.
    /// </summary>
    internal static class PerMangaLock
    {
        private sealed class Entry
        {
            public readonly SemaphoreSlim Semaphore = new(1, 1);
            public int Waiters;
        }

        private static readonly Dictionary<string, Entry> Entries = new();
        private static readonly object Sync = new();

        public static async Task<IDisposable> AcquireAsync(long sourceId, string? mangaUrl, CancellationToken token)
        {
            string key = sourceId + "|" + (mangaUrl ?? "");
            Entry entry;
            lock (Sync)
            {
                if (!Entries.TryGetValue(key, out Entry? existing))
                {
                    existing = new Entry();
                    Entries[key] = existing;
                }
                existing.Waiters++;
                entry = existing;
            }

            try
            {
                await entry.Semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                Release(key, entry, acquired: false);
                throw;
            }

            return new Releaser(key, entry);
        }

        private static void Release(string key, Entry entry, bool acquired)
        {
            if (acquired)
                entry.Semaphore.Release();
            lock (Sync)
            {
                // Only the last one out removes the entry, so the dictionary does
                // not grow with every series ever fetched.
                if (--entry.Waiters <= 0)
                    Entries.Remove(key);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly string _key;
            private readonly Entry _entry;
            private int _disposed;

            public Releaser(string key, Entry entry)
            {
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                Release(_key, _entry, acquired: true);
            }
        }
    }
}
