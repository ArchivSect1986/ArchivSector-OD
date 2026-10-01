using System.Linq;

namespace ArchivSector_OD
{
    // Ported from the Python app's _claim_or_queue_slot() /
    // _resume_if_queued() -- the shared gate both Multi-Drive Batch
    // Sync's concurrent-rip cap and Unattended Mode's auto-start go
    // through. Deliberately shared, not two separate mechanisms:
    // Unattended must never silently bypass a concurrency cap the
    // user explicitly turned on -- they're independent settings, and
    // this is the one place that enforces both correctly together.
    //
    // A static class (not per-card state) since the whole point is
    // coordinating across every DriveBayCard instance, none of which
    // individually owns "how many rips are running right now."
    public static class BatchSyncService
    {
        private static readonly object Lock = new();
        private static int activeCount = 0;
        private static readonly List<(string DriveLetter, Func<Task> Resume)> queued = new();

        public static event Action? StateChanged;

        public static int ActiveCount { get { lock (Lock) return activeCount; } }
        public static int QueuedCount { get { lock (Lock) return queued.Count; } }

        // Returns true if a slot was claimed immediately -- the caller
        // should run its pipeline right away and call ReleaseSlot()
        // when done. Returns false if every slot was busy: the caller
        // has been queued, and `resume` (which itself must call
        // ReleaseSlot() when it finishes) will be invoked automatically
        // once a slot frees up -- the caller does nothing further.
        public static bool ClaimOrQueue(string driveLetter, int maxConcurrent, Func<Task> resume)
        {
            bool claimed;
            lock (Lock)
            {
                claimed = activeCount < Math.Max(1, maxConcurrent);
                if (claimed) activeCount++;
                else queued.Add((driveLetter, resume));
            }
            StateChanged?.Invoke();
            return claimed;
        }

        public static void ReleaseSlot()
        {
            (string DriveLetter, Func<Task> Resume)? next = null;
            lock (Lock)
            {
                activeCount--;
                if (queued.Count > 0)
                {
                    next = queued[0];
                    queued.RemoveAt(0);
                    activeCount++; // claimed immediately on the queued item's behalf
                }
            }
            StateChanged?.Invoke();
            if (next is not null)
            {
                _ = next.Value.Resume();
            }
        }

        // Removes a drive's queued entry -- used if its disc is
        // ejected or the operation is otherwise abandoned before its
        // turn came up, so it doesn't auto-start later against a disc
        // that's no longer there.
        public static void RemoveQueued(string driveLetter)
        {
            lock (Lock) { queued.RemoveAll(q => q.DriveLetter == driveLetter); }
            StateChanged?.Invoke();
        }

        public static bool IsQueued(string driveLetter)
        {
            lock (Lock) return queued.Any(q => q.DriveLetter == driveLetter);
        }
    }
}