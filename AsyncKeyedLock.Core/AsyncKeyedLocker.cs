namespace AsyncKeyedLock.Core;

/// <summary>
/// Represents a thread-safe keyed locker that allows you to lock based on a key (keyed semaphores), only allowing a specified number of concurrent threads that share the same key.
/// Code copied from the https://github.com/MarkCiliaVincenti/AsyncKeyedLock and simplified
/// </summary>
public class AsyncKeyedLocker : IDisposable
{
    private readonly AsyncKeyedLockDictionary<string> m_dictionary;

    public AsyncKeyedLocker()
    {
        m_dictionary = new AsyncKeyedLockDictionary<string>(StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        AsyncKeyedLockReleaser<string> releaser = GetOrAdd(key);
        try
        {
            await releaser.SemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            m_dictionary.ReleaseWithoutSemaphoreRelease(releaser);
            throw;
        }

        return releaser;
    }

    private AsyncKeyedLockReleaser<string> GetOrAdd(string key) => m_dictionary.GetOrAdd(key);

    public bool IsInUse(string key)
    {
        if (!m_dictionary.TryGetValue(key, out var result))
        {
            return false;
        }


        Monitor.Enter(result);
        if (result.IsNotInUse)
        {
            Monitor.Exit(result);
            return false;
        }
        Monitor.Exit(result);
        return true;
    }

    public void Dispose() => m_dictionary.Dispose();
}