// ==============================================================================
// Sleipnir C#-Client-Samples — Runner.
//
// Startet die Szenarien 1–4 gegen den laufenden Sample-Server
// (samples/server). Aufruf:
//
//   dotnet run --project samples/csharp -- 1        # nur Szenario 1
//   dotnet run --project samples/csharp -- 2        # nur Szenario 2
//   dotnet run --project samples/csharp -- all      # alle nacheinander (Default)
//   dotnet run --project samples/csharp --          # = all
//
// Voraussetzung: der Sample-Server läuft auf https://localhost:5001
//   dotnet run --project samples/server/SampleServer.csproj
// ==============================================================================

using Sleipnir.Samples.CSharp;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

const string baseUrl = "https://localhost:5001";

var which = args.Length > 0 ? args[0].Trim() : "all";

// Szenario-Tabelle: Nummer → (Titel, Runner). Runner bekommt einen frischen
// REST-Client, sodass jedes Szenario isoliert ist.
var scenarios = new Dictionary<string, (string Titel, Func<SleipnirRestJsonClient, TextWriter, Task> Run)>
{
    ["1"] = ("01 — Single Call",        SingleCallScenario.RunAsync),
    ["2"] = ("02 — Batch Parallel",     BatchParallelScenario.RunAsync),
    ["3"] = ("03 — Batch Serial",       BatchSerialScenario.RunAsync),
    ["4"] = ("04 — Dependency Chain",   DependencyChainScenario.RunAsync),
};

var toRun = which.Equals("all", StringComparison.OrdinalIgnoreCase)
    ? scenarios.OrderBy(kv => kv.Key).Select(kv => kv.Key).ToArray()
    : new[] { which };

foreach (var key in toRun)
{
    if (!scenarios.TryGetValue(key, out var s))
    {
        Console.WriteLine($"Unknown scenario '{key}'. Allowed: 1, 2, 3, 4, all.");
        return;
    }

    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"  {s.Titel}");
    Console.WriteLine(new string('=', 78));

    using var client = new SleipnirRestJsonClient(baseUrl);
    try
    {
        await s.Run(client, Console.Out);
        Console.WriteLine($"  -> Scenario '{key}' OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  -> Scenario '{key}' ERROR: {ex.Message}");
    }
}