using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AsyncKeyedLock.Core;

internal sealed class AsyncKeyedLockDictionary<TKey> : ConcurrentDictionary<TKey, AsyncKeyedLockReleaser<TKey>>, IDisposable
    where TKey : notnull
{
    private readonly int m_maxCount = 1;

    public AsyncKeyedLockDictionary(IEqualityComparer<TKey> comparer) : base(comparer)
    {
    }

    public AsyncKeyedLockReleaser<TKey> GetOrAdd(TKey key)
    {
        if (TryGetValue(key, out AsyncKeyedLockReleaser<TKey>? releaserNoPooling) && releaserNoPooling.TryIncrement())
        {
            return releaserNoPooling;
        }

        var releaserToAddNoPooling = new AsyncKeyedLockReleaser<TKey>(key, new SemaphoreSlim(m_maxCount, m_maxCount), this);
        if (TryAdd(key, releaserToAddNoPooling))
        {
            return releaserToAddNoPooling;
        }

        while (true)
        {
            releaserNoPooling = GetOrAdd(key, releaserToAddNoPooling);
            if (ReferenceEquals(releaserNoPooling, releaserToAddNoPooling) || releaserNoPooling.TryIncrement())
            {
                return releaserNoPooling;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release(AsyncKeyedLockReleaser<TKey> releaser)
    {
        Monitor.Enter(releaser);

        if (releaser.ReferenceCount == 1)
        {
            TryRemove(releaser.Key, out _);
            releaser.IsNotInUse = true;
            Monitor.Exit(releaser);
            releaser.SemaphoreSlim.Release();
            return;
        }

        --releaser.ReferenceCount;
        Monitor.Exit(releaser);

        releaser.SemaphoreSlim.Release();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseWithoutSemaphoreRelease(AsyncKeyedLockReleaser<TKey> releaser)
    {
        Monitor.Enter(releaser);

        if (releaser.ReferenceCount == 1)
        {
            TryRemove(releaser.Key, out _);
            releaser.IsNotInUse = true;
            Monitor.Exit(releaser);
            return;
        }

        --releaser.ReferenceCount;
        Monitor.Exit(releaser);
    }

    public void Dispose()
    {
        foreach (AsyncKeyedLockReleaser<TKey> semaphore in Values)
        {
            try
            {
                semaphore.Dispose();
            }
            catch
            {
            } // do nothing
        }

        Clear();
    }
}