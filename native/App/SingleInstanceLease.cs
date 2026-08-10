namespace VectorAnimationEngine;

internal sealed class SingleInstanceLease : IDisposable
{
    private const string MutexName = "Local\\Vector2DAnimationEngine.Native.Instance";
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    private SingleInstanceLease(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static bool TryAcquire(TimeSpan waitTimeout, out SingleInstanceLease? lease)
        => TryAcquire(MutexName, waitTimeout, out lease);

    internal static bool TryAcquireForRegression(
        string mutexName,
        TimeSpan waitTimeout,
        out SingleInstanceLease? lease)
        => TryAcquire(mutexName, waitTimeout, out lease);

    private static bool TryAcquire(
        string mutexName,
        TimeSpan waitTimeout,
        out SingleInstanceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        if (waitTimeout < TimeSpan.Zero && waitTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        var mutex = new Mutex(initiallyOwned: false, mutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(waitTimeout);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                mutex.Dispose();
                lease = null;
                return false;
            }

            lease = new SingleInstanceLease(mutex, ownsMutex: true);
            return true;
        }
        catch
        {
            if (ownsMutex) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
