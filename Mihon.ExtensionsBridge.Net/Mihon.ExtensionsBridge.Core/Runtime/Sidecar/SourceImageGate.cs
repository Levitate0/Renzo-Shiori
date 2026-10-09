namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>
    /// Marks image fetches made on behalf of someone LOOKING at the screen —
    /// the reader — as opposed to background work like the download queue.
    ///
    /// An AsyncLocal, so it follows the call down through SourceTimeout's
    /// Task.Run and the gatekeeper into <see cref="SourceImageGate"/> without
    /// every interface in between growing a parameter. Setting it inside an
    /// async method is scoped to that method: the runtime restores the caller's
    /// value when it returns.
    /// </summary>
    public static class ImageFetchPriority
    {
        private static readonly AsyncLocal<bool> Interactive = new();

        public static bool IsInteractive => Interactive.Value;

        /// <summary>Call at the top of an async method that serves the reader.</summary>
        public static void MarkInteractive() => Interactive.Value = true;
    }

    /// <summary>
    /// Per-HOST admission to the sidecar for image fetches, with the reader
    /// ahead of background work.
    ///
    /// History. A plain 5-slot semaphore per source fixed "continuous reading
    /// loads three chapters then stalls" (abandoned reader requests used to
    /// pile up inside the uncancellable sidecar), but it made the reader
    /// compete with the download queue: a running download fetches 5 pages in
    /// parallel (PagesInParallelPerChapter), one chapter per source at a time,
    /// and with thousands queued there is always one running — so it held all
    /// 5 slots continuously and the reader waited behind it.
    ///
    /// Keyed per image HOST, not per source, because that is what OkHttp's
    /// limit is (Dispatcher.maxRequestsPerHost, shared by every extension
    /// client). Keying per source cut multi-host sources to 5 in total: MangaK
    /// spreads one 17-page chapter over 10 CDN hosts, so it went from all 17
    /// in parallel to 5 at a time — "it loads 4 images at a time now".
    ///
    /// Rules:
    ///  - A request waiting here is withdrawn the instant its caller cancels
    ///    (the original fix: the sidecar only holds work someone wants).
    ///  - When a slot frees, a waiting INTERACTIVE request gets it before any
    ///    background one. No slot is reserved for the reader: measured, a
    ///    reserve bought the reader ~0.1s and cost downloads a fifth of their
    ///    throughput, because in a fair line the reader already waits at most
    ///    one image behind a download.
    /// </summary>
    public sealed class SourceImageGate
    {
        private readonly Func<int> _capacity;
        private readonly object _sync = new();
        private readonly LinkedList<TaskCompletionSource<bool>> _interactive = new();
        private readonly LinkedList<TaskCompletionSource<bool>> _background = new();
        private int _inUse;

        /// <param name="capacity">Read on every admission, so a settings change
        /// widens or narrows existing gates without rebuilding them.</param>
        public SourceImageGate(Func<int> capacity)
        {
            _capacity = () => Math.Max(1, capacity());
        }

        /// <summary>Slots currently held (for diagnostics and tests).</summary>
        public int InUse { get { lock (_sync) return _inUse; } }

        public async Task AcquireAsync(bool interactive, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            TaskCompletionSource<bool> waiter;
            LinkedListNode<TaskCompletionSource<bool>> node;
            lock (_sync)
            {
                if (CanAdmit(interactive))
                {
                    _inUse++;
                    return;
                }
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                node = (interactive ? _interactive : _background).AddLast(waiter);
            }

            using (token.Register(() =>
            {
                bool removed;
                lock (_sync)
                {
                    removed = node.List != null;
                    if (removed) node.List!.Remove(node);
                }
                // Only cancel if we pulled it out before a slot was handed over;
                // otherwise the slot is ours and the caller releases it.
                if (removed) waiter.TrySetCanceled(token);
            }))
            {
                await waiter.Task.ConfigureAwait(false);
            }
        }

        public void Release()
        {
            // Give the slot back, then admit waiters — reader first — while there
            // is room under the CURRENT capacity. A loop rather than a one-for-one
            // handover because the capacity is live: raising MaxRequestsPerHost
            // should let the queue fill the new room at once, and lowering it must
            // stop over-capacity holders from passing their slots on.
            List<TaskCompletionSource<bool>>? wake = null;
            lock (_sync)
            {
                _inUse--;
                while (_inUse < _capacity())
                {
                    LinkedList<TaskCompletionSource<bool>> from =
                        _interactive.First != null ? _interactive :
                        _background.First != null ? _background : null!;
                    if (from == null)
                        break;
                    TaskCompletionSource<bool> next = from.First!.Value;
                    from.RemoveFirst();
                    _inUse++;
                    (wake ??= new()).Add(next);
                }
            }
            if (wake != null)
                foreach (TaskCompletionSource<bool> w in wake)
                    w.TrySetResult(true);
        }

        // Both may use every slot; background yields while a reader request is queued.
        private bool CanAdmit(bool interactive) =>
            _inUse < _capacity() && (interactive || _interactive.Count == 0);
    }
}
