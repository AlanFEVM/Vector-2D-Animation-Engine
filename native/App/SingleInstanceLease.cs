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

    public static bool TryAcquire(out SingleInstanceLease? lease)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            lease = null;
            return false;
        }

        lease = new SingleInstanceLease(mutex, ownsMutex: true);
        return true;
    }

    public void Dispose()
    {
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
