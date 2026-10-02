using System.Runtime.CompilerServices;

namespace CombatSolver;

internal static class GcManualMemoryReleaseChecks
{
    internal static void Run()
    {
        PolicyCheck.Run("manual decommit waits for search ownership and preserves live data", () =>
        {
            SearchGcPolicy.ReclaimIfPendingAsync("manual_release_setup", true).GetAwaiter().GetResult();
            byte[] live = new byte[256 * 1024];
            live[0] = 17;
            live[^1] = 29;
            WeakReference garbage = AllocateTransientHeap();
            long committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
            SearchGcLifecycleSnapshot before = SearchGcPolicy.CaptureLifecycle();
            ISearchGcScope scope = SearchGcPolicy.EnterSearchScope(
                false, 1, new SearchMemoryPressureSignal(), CancellationToken.None);
            Task release;
            Task? second = null;
            try
            {
                release = SearchGcPolicy.ForceManualProcessMemoryRelease();
                PolicyCheck.Require(!release.IsCompleted, "Manual release waits while search owns its graph.");
                PolicyCheck.Require(SearchGcPolicy.CaptureLifecycle().DeltaFrom(before).ForcedCollections == 0,
                    "A pending manual request must not collect behind the active search.");
                second = SearchGcPolicy.ForceManualProcessMemoryRelease();
                PolicyCheck.Require(!second.IsCompleted, "A second click also waits for the safe release boundary.");
            }
            finally
            {
                scope.Dispose();
            }
            Task.WhenAll(release, second!).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            PolicyCheck.Require(!garbage.IsAlive && live[0] == 17 && live[^1] == 29,
                "Reclaim unreachable arrays while retaining live game-equivalent data.");
            PolicyCheck.Require(SearchGcPolicy.CaptureLifecycle().DeltaFrom(before).ForcedCollections == 1,
                "One joined manual request performs one full collection.");
            long committedAfter = GC.GetGCMemoryInfo().TotalCommittedBytes;
            PolicyCheck.Require(committedAfter < committedBefore,
                "Aggressive collection must return the released transient heap's unused committed pages.");
            Console.WriteLine($"MANUAL_DECOMMIT committed_before={committedBefore} committed_after={committedAfter}");
            GC.KeepAlive(live);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateTransientHeap()
    {
        byte[][] arrays = Enumerable.Range(0, 64).Select(_ => new byte[1024 * 1024]).ToArray();
        // Complete a GC while the transient graph is alive, establishing committed-page evidence.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        WeakReference result = new(arrays);
        GC.KeepAlive(arrays);
        return result;
    }
}
