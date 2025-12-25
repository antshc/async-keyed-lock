using System.Collections.Concurrent;
using System.Reflection;
using Xunit.Abstractions;

namespace AsyncKeyedLock.Tests
{
    public sealed class KeyedSemaphoreTests
    {
        [Fact]
        public async Task AcquireAsync_SameKey_IsMutuallyExclusive()
        {
            var keyed = new KeyedSemaphore();
            var key = "A";

            var entered1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var t1 = Task.Run(async () =>
            {
                using (await keyed.AcquireAsync(key))
                {
                    entered1.TrySetResult();
                    await release1.Task; // hold the lock
                }
            });

            await entered1.Task; // ensure T1 is holding the lock

            var t2 = Task.Run(async () =>
            {
                using (await keyed.AcquireAsync(key))
                {
                    entered2.TrySetResult();
                }
            });

            // T2 must NOT enter while T1 holds the lock
            var completed = await Task.WhenAny(entered2.Task, Task.Delay(150));
            Assert.NotSame(entered2.Task, completed);

            // Release T1 -> now T2 should enter
            release1.TrySetResult();
            await entered2.Task;

            await Task.WhenAll(t1, t2);
        }

        [Fact]
        public async Task AcquireAsync_DifferentKeys_CanRunConcurrently()
        {
            var keyed = new KeyedSemaphore();

            var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tA = Task.Run(async () =>
            {
                using (await keyed.AcquireAsync("A"))
                {
                    enteredA.TrySetResult();
                    await release.Task;
                }
            });

            var tB = Task.Run(async () =>
            {
                using (await keyed.AcquireAsync("B"))
                {
                    enteredB.TrySetResult();
                    await release.Task;
                }
            });

            // Both should enter quickly (not blocked by each other)
            var bothEntered = Task.WhenAll(enteredA.Task, enteredB.Task);
            var completed = await Task.WhenAny(bothEntered, Task.Delay(300));
            Assert.Same(bothEntered, completed);

            release.TrySetResult();
            await Task.WhenAll(tA, tB);
        }

        [Fact]
        public async Task Dispose_IsIdempotent_SecondDisposeDoesNothing()
        {
            var keyed = new KeyedSemaphore();

            var releaser = await keyed.AcquireAsync("A");
            releaser.Dispose();

            // Should not throw
            releaser.Dispose();
        }

        [Fact]
        public async Task AcquireAsync_WhenCancelledWhileWaiting_ThrowsAndDoesNotLeakEntry()
        {
            var keyed = new KeyedSemaphore();
            var key = "A";

            // Hold the lock so the next AcquireAsync will wait
            var hold = await keyed.AcquireAsync(key);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(100);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await keyed.AcquireAsync(key, cts.Token);
            });

            // Release the holder
            hold.Dispose();

            // After everything is done, the key should be cleaned up
            Assert.False(MapContainsKey(keyed, key));
        }

        [Fact]
        public async Task Cleanup_RemovesKeyAfterLastRelease()
        {
            var keyed = new KeyedSemaphore();
            var key = "cleanup-key";

            Assert.False(MapContainsKey(keyed, key));

            using (await keyed.AcquireAsync(key))
            {
                Assert.True(MapContainsKey(keyed, key));
            }

            Assert.False(MapContainsKey(keyed, key));
            Assert.Equal(0, MapCount(keyed));
        }

        [Fact]
        public async Task Cleanup_DoesNotRemoveWhileAnotherWaiterExists()
        {
            var keyed = new KeyedSemaphore();
            var key = "A";

            // T1 holds lock
            var t1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var t1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var t1 = Task.Run(async () =>
            {
                using (await keyed.AcquireAsync(key))
                {
                    t1Entered.TrySetResult();
                    await t1Release.Task;
                }
            });

            await t1Entered.Task;
            Assert.True(MapContainsKey(keyed, key));

            // T2 waits
            var t2WaitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var t2Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var t2 = Task.Run(async () =>
            {
                t2WaitingStarted.TrySetResult();
                using (await keyed.AcquireAsync(key))
                {
                    t2Entered.TrySetResult();
                }
            });

            await t2WaitingStarted.Task;

            // While T1 still holds, key must remain present
            Assert.True(MapContainsKey(keyed, key));

            // Release T1; T2 should enter and then exit
            t1Release.TrySetResult();
            await t2Entered.Task;

            await Task.WhenAll(t1, t2);

            // After both complete, cleanup should remove it
            Assert.False(MapContainsKey(keyed, key));
        }

        // ---------------- helpers: reflection to inspect private _map ----------------

        private static object GetMap(KeyedSemaphore keyed)
        {
            var f = typeof(KeyedSemaphore).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Could not find _map field via reflection.");
            return f.GetValue(keyed) ?? throw new InvalidOperationException("_map is null.");
        }

        private static int MapCount(KeyedSemaphore keyed)
        {
            var map = GetMap(keyed);
            var p = map.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new InvalidOperationException("Could not find Count property on map.");
            return (int)(p.GetValue(map) ?? 0);
        }

        private static bool MapContainsKey(KeyedSemaphore keyed, string key)
        {
            var map = GetMap(keyed);

            // ConcurrentDictionary<,>.ContainsKey(string)
            var m = map.GetType().GetMethod("ContainsKey", BindingFlags.Instance | BindingFlags.Public, binder: null, types: new[] { typeof(string) }, modifiers: null)
                    ?? throw new InvalidOperationException("Could not find ContainsKey(string) method on map.");

            return (bool)(m.Invoke(map, new object[] { key }) ?? false);
        }
    } 
        public sealed class KeyedSemaphore
        {
            private sealed class RefCountedSemaphore
            {
                public readonly SemaphoreSlim Semaphore = new(initialCount: 1, maxCount: 1);
                public int RefCount;
            }

            private readonly ConcurrentDictionary<string, RefCountedSemaphore> _map =
                new(StringComparer.Ordinal); // or OrdinalIgnoreCase if you want case-insensitive keys

            public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
            {
                if (key is null) throw new ArgumentNullException(nameof(key));

                // Get/create the per-key semaphore holder and increment refcount atomically
                var holder = _map.GetOrAdd(key, _ => new RefCountedSemaphore());
                Interlocked.Increment(ref holder.RefCount);

                try
                {
                    await holder.Semaphore.WaitAsync(ct).ConfigureAwait(false);
                    return new Releaser(this, key, holder);
                }
                catch
                {
                    // If we failed to acquire (canceled/exception), undo refcount
                    ReleaseRef(key, holder);
                    throw;
                }
            }

            private void ReleaseRef(string key, RefCountedSemaphore holder)
            {
                if (Interlocked.Decrement(ref holder.RefCount) == 0)
                {
                    // Best-effort cleanup: remove only if the value is the same instance
                    _map.TryRemove(new KeyValuePair<string, RefCountedSemaphore>(key, holder));
                    // Note: we do NOT dispose the SemaphoreSlim here to avoid races with late waiters.
                    // If you want disposal, you need stronger coordination.
                }
            }

            private sealed class Releaser : IDisposable
            {
                private readonly KeyedSemaphore _owner;
                private readonly string _key;
                private RefCountedSemaphore? _holder;

                public Releaser(KeyedSemaphore owner, string key, RefCountedSemaphore holder)
                {
                    _owner = owner;
                    _key = key;
                    _holder = holder;
                }

                public void Dispose()
                {
                    var holder = Interlocked.Exchange(ref _holder, null);
                    if (holder is null) return;

                    holder.Semaphore.Release();
                    _owner.ReleaseRef(_key, holder);
                }
            }
        }
}

//// -------------------- usage example --------------------
//public static class Example
//{
//    private static readonly KeyedSemaphore Locks = new();

//    public static async Task DemoAsync()
//    {
//        // "A" operations are serialized; "B" can run concurrently with "A"
//        var t1 = DoWork("A", 1);
//        var t2 = DoWork("A", 2);
//        var t3 = DoWork("B", 3);

//        await Task.WhenAll(t1, t2, t3);
//    }

//    private static async Task DoWork(string key, int id)
//    {
//        using (await Locks.AcquireAsync(key))
//        {
//            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] start  id={id} key={key}");
//            await Task.Delay(250);
//            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] finish id={id} key={key}");
//        }
//    }
//}
