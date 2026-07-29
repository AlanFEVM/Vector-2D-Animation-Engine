namespace VectorAnimationEngine;

internal static class ParallelBatch
{
    public const int DefaultMinItemsPerWorker = 4096;

    public static int MaximumWorkerCount
    {
        get
        {
            var processorCount = Environment.ProcessorCount;
            if (processorCount <= 1) return 1;
            return Math.Min(8, Math.Max(2, processorCount - 1));
        }
    }

    public static int WorkerCount(int itemCount, int minItemsPerWorker = DefaultMinItemsPerWorker, int? maxWorkers = null)
    {
        if (itemCount <= 0) return 1;
        minItemsPerWorker = Math.Max(1, minItemsPerWorker);
        var usefulWorkers = Math.Max(1, (itemCount + minItemsPerWorker - 1) / minItemsPerWorker);
        return Math.Min(usefulWorkers, Math.Max(1, maxWorkers ?? MaximumWorkerCount));
    }

    public static void For(
        int itemCount,
        int minItemsPerWorker,
        Action<int, int, int> processRange,
        int? maxWorkers = null)
    {
        ArgumentNullException.ThrowIfNull(processRange);
        var workers = WorkerCount(itemCount, minItemsPerWorker, maxWorkers);
        if (workers == 1)
        {
            processRange(0, 0, Math.Max(0, itemCount));
            return;
        }

        Parallel.For(0, workers, worker =>
        {
            var start = (int)((long)itemCount * worker / workers);
            var end = (int)((long)itemCount * (worker + 1) / workers);
            processRange(worker, start, end);
        });
    }
}
