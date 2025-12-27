namespace Zerto.PromotionWorker.Accessors.JournalCache.KeyedLock;

public sealed class AsyncKeyedLockReleaser<TKey> : IDisposable
    where TKey : notnull
{
    internal bool IsNotInUse { get; set; } = false;

    public TKey Key { get; internal set; }

    /// <summary>
    /// Gets the number of threads processing or waiting to process for the specific <see cref="Key"/>.
    /// </summary>
    public int ReferenceCount { get; internal set; } = 1;

    public readonly SemaphoreSlim SemaphoreSlim;

    private readonly AsyncKeyedLockDictionary<TKey> m_dictionary;

    internal AsyncKeyedLockReleaser(TKey key, SemaphoreSlim semaphoreSlim, AsyncKeyedLockDictionary<TKey> dictionary)
    {
        Key = key;
        SemaphoreSlim = semaphoreSlim;
        m_dictionary = dictionary;
    }

    internal bool TryIncrement()
    {
        if (Monitor.TryEnter(this))
        {
            if (IsNotInUse)
            {
                Monitor.Exit(this);
                return false;
            }

            ++ReferenceCount;
            Monitor.Exit(this);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases the <see cref="SemaphoreSlim"/> object once.
    /// </summary>
    public void Dispose()
    {
        m_dictionary.Release(this);
    }
}