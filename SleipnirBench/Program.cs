// Sleipnir-Benchmark-Einstiegspunkt. BenchmarkDotNet übernimmt die Statistik
// (Mean, StdErr, StdDev, Median, Gen0/1/2, Allocated) und schreibt Reports
// nach BenchmarkDotNet.Artifacts/results/. Aufruf:
//   dotnet run -c Release --project SleipnirBench\SleipnirBench.csproj -- single
//   dotnet run -c Release --project SleipnirBench\SleipnirBench.csproj -- batch
//   dotnet run -c Release --project SleipnirBench\SleipnirBench.csproj -- dependency
//   dotnet run -c Release --project SleipnirBench\SleipnirBench.csproj            # alle
// BDn-Argumente direkt durchreichen, z.B. -- --filter "*Ws*"
using BenchmarkDotNet.Running;
using SleipnirBench;

Console.WriteLine("Sleipnir Performance Benchmarks (BenchmarkDotNet)");
Console.WriteLine("=============================================");
Console.WriteLine();

// Environment.GetCommandLineArgs()[0] ist der App-Pfad; alles danach sind die
// App-Argumente (dotnet run reicht alles nach '--' an die App weiter).
var appArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

var switcher = BenchmarkSwitcher.FromTypes(new[]
{
    typeof(SingleCallBenchmarks),
    typeof(BatchCallBenchmarks),
    typeof(DependencyChainBenchmarks)
});

if (appArgs.Any(a => a.StartsWith("-")))
{
    // BDn-Argumente (z.B. --filter "*Ws*") direkt durchreichen.
    switcher.Run(appArgs);
}
else
{
    var benchArg = appArgs.FirstOrDefault(a => !a.Contains("SleipnirBench") && !a.Contains("dotnet") && !a.Contains("run") && !a.Contains("Release"));
    switch (benchArg?.ToLowerInvariant())
    {
        case "single":
            switcher.Run(new[] { "--filter", "*SingleCallBenchmarks*" });
            break;
        case "batch":
            switcher.Run(new[] { "--filter", "*BatchCallBenchmarks*" });
            break;
        case "dependency":
        case "chain":
            switcher.Run(new[] { "--filter", "*DependencyChainBenchmarks*" });
            break;
        default:
            BenchmarkRunner.Run<SingleCallBenchmarks>();
            BenchmarkRunner.Run<BatchCallBenchmarks>();
            BenchmarkRunner.Run<DependencyChainBenchmarks>();
            break;
    }
}

Console.WriteLine();
Console.WriteLine("Done! Detailed reports: BenchmarkDotNet.Artifacts/results/");