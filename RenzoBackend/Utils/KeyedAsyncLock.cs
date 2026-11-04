using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RenzoBackend.Utils
{
    /// <summary>
    /// One async lock per key, created on first use and dropped when nobody
    /// holds or waits for it.
    ///
    /// Entries are reference-counted. The previous version removed and disposed
    /// a key's semaphore as soon as its count read 1 after a release, which a
    /// caller that had already looked the semaphore up, but not yet waited on it,
    /// could not see: it then waited on a disposed semaphore, or got one nobody
    /// else would ever see while a third caller made a fresh one for the same
    /// key, and both "held" the lock at once.
    /// </summary>
    public class KeyedAsyncLock
    {
        private sealed class Entry
        {
            public readonly SemaphoreSlim Semaphore = new(1, 1);
            public int Refs;
        }

        private readonly Dictionary<string, Entry> _locks = new();

        public async Task<IDisposable> LockAsync(string key, CancellationToken token = default)
        {
            Entry entry;
            lock (_locks)
            {
                if (!_locks.TryGetValue(key, out entry!))
                    _locks[key] = entry = new Entry();
                entry.Refs++;
            }
            try
            {
                await entry.Semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                Drop(key, entry);
                throw;
            }
            return new Releaser(this, key, entry);
        }

        private void Drop(string key, Entry entry)
        {
            lock (_locks)
            {
                if (--entry.Refs == 0)
                {
                    _locks.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly KeyedAsyncLock _parent;
            private readonly string _key;
            private readonly Entry _entry;
            private int _disposed;

            public Releaser(KeyedAsyncLock parent, string key, Entry entry)
            {
                _parent = parent;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _entry.Semaphore.Release();
                _parent.Drop(_key, _entry);
            }
        }
    }
}
