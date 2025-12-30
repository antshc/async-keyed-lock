using AsyncKeyedLock.Core;
using FluentAssertions;
using System.Collections.Concurrent;
using System.Reflection;
using Xunit.Abstractions;

namespace AsyncKeyedLock.Tests
{
    public sealed class AsyncKeyedLockerTests
    {
        [Fact]
        public async Task LockAsync_WhenCancelled_ShouldReleaseKeyedSemaphoreAndThrowOperationCanceledException()
        {
            // Arrange
            var dictionary = new AsyncKeyedLocker();
            var cancelledCancellationToken = new CancellationToken(true);

            // Act
            Func<Task> action = async () =>
            {
                using var _ = await dictionary.LockAsync("test", cancelledCancellationToken);
            };
            await action.Should().ThrowAsync<OperationCanceledException>();

            // Assert
            dictionary.IsInUse("test").Should().BeFalse();
        }

        [Fact]
        public async Task LockAsync_WhenNotCancelled_ShouldReturnDisposable()
        {
            // Arrange
            var dictionary = new AsyncKeyedLocker();
            var cancellationToken = default(CancellationToken);

            // Act
            var releaser = await dictionary.LockAsync("test", cancellationToken);

            // Assert
            dictionary.IsInUse("test").Should().BeTrue();
            releaser.Dispose();
            dictionary.IsInUse("test").Should().BeFalse();
        }

        [Fact]
        public async Task LockAsync_SameKey_IsMutuallyExclusive()
        {
            var keyed = new AsyncKeyedLocker();
            var key = "A";

            var entered1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var t1 = Task.Run(async () =>
            {
                using (await keyed.LockAsync(key, CancellationToken.None))
                {
                    entered1.TrySetResult();
                    await release1.Task; // hold the lock
                }
            });

            await entered1.Task; // ensure T1 is holding the lock

            var t2 = Task.Run(async () =>
            {
                using (await keyed.LockAsync(key, CancellationToken.None))
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
        public async Task LockAsync_DifferentKeys_CanRunConcurrently()
        {
            var keyed = new AsyncKeyedLocker();

            var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tA = Task.Run(async () =>
            {
                using (await keyed.LockAsync("A", CancellationToken.None))
                {
                    enteredA.TrySetResult();
                    await release.Task;
                }
            });

            var tB = Task.Run(async () =>
            {
                using (await keyed.LockAsync("B", CancellationToken.None))
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
            var keyed = new AsyncKeyedLocker();

            var releaser = await keyed.LockAsync("A", CancellationToken.None);
            releaser.Dispose();

            // Should not throw
            releaser.Dispose();
        }

        [Fact]
        public async Task LockAsync_WhenCancelledWhileWaiting_ThrowsAndDoesNotLeakEntry()
        {
            var keyed = new AsyncKeyedLocker();
            var key = "A";

            // Hold the lock so the next LockAsync will wait
            var hold = await keyed.LockAsync(key);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(100);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await keyed.LockAsync(key, cts.Token);
            });

            // Release the holder
            hold.Dispose();

            // After everything is done, the key should be cleaned up
            Assert.False(MapContainsKey(keyed, key));
        }

        [Fact]
        public async Task Cleanup_RemovesKeyAfterLastRelease()
        {
            var keyed = new AsyncKeyedLocker();
            var key = "cleanup-key";

            Assert.False(MapContainsKey(keyed, key));

            using (await keyed.LockAsync(key))
            {
                Assert.True(MapContainsKey(keyed, key));
            }

            Assert.False(MapContainsKey(keyed, key));
            Assert.Equal(0, MapCount(keyed));
        }

        [Fact]
        public async Task Cleanup_DoesNotRemoveWhileAnotherWaiterExists()
        {
            var keyed = new AsyncKeyedLocker();
            var key = "A";

            // T1 holds lock
            var t1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var t1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var t1 = Task.Run(async () =>
            {
                using (await keyed.LockAsync(key))
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
                using (await keyed.LockAsync(key))
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

        private static object GetMap(AsyncKeyedLocker keyed)
        {
            var f = typeof(AsyncKeyedLocker).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Could not find _map field via reflection.");
            return f.GetValue(keyed) ?? throw new InvalidOperationException("_map is null.");
        }

        private static int MapCount(AsyncKeyedLocker keyed)
        {
            var map = GetMap(keyed);
            var p = map.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new InvalidOperationException("Could not find Count property on map.");
            return (int)(p.GetValue(map) ?? 0);
        }

        private static bool MapContainsKey(AsyncKeyedLocker keyed, string key)
        {
            var map = GetMap(keyed);

            // ConcurrentDictionary<,>.ContainsKey(string)
            var m = map.GetType().GetMethod("ContainsKey", BindingFlags.Instance | BindingFlags.Public, binder: null, types: new[] { typeof(string) }, modifiers: null)
                    ?? throw new InvalidOperationException("Could not find ContainsKey(string) method on map.");

            return (bool)(m.Invoke(map, new object[] { key }) ?? false);
        }
    } 
}