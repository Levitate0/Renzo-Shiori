namespace RenzoBackend.Utils
{

    public class AsyncLock
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly Task<IDisposable> _releaser;

        public AsyncLock()
        {
            _releaser = Task.FromResult((IDisposable)new Releaser(this));
        }

        /// <summary>
        /// Waits for the lock; throws if <paramref name="token"/> is cancelled first.
        ///
        /// It used to hand back the releaser whenever the wait task COMPLETED, and
        /// a cancelled wait completes too: the caller ran its locked section
        /// without the lock, then released a lock it never took, letting a second
        /// caller in while a third was still inside and ending in a
        /// SemaphoreFullException (logged 2026-11-03 18:33, after a Download-all
        /// request was cancelled mid-way).
        /// </summary>
        public async Task<IDisposable> LockAsync(CancellationToken token = default)
        {
            await _semaphore.WaitAsync(token).ConfigureAwait(false);
            return _releaser.Result;
        }

        private sealed class Releaser : IDisposable
        {
            private readonly AsyncLock _toRelease;
            internal Releaser(AsyncLock toRelease) { _toRelease = toRelease; }
            public void Dispose() { _toRelease._semaphore.Release(); }
        }
    }
}
