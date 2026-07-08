namespace VectorAnimationEngine;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--bench", StringComparison.OrdinalIgnoreCase))
        {
            Benchmark.RunDefaultStress();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new AppHost());
    }
}
