// ==============================================================================
// 03 — Batch Serial (C#-Client)
// ==============================================================================
// Mehrere Aufrufe nacheinander in einer Roundtrip (ExecutionMode.Serial). Der
// Server führt sie in Request-Reihenfolge sequenziell aus.
//
// Serial OHNE DependencyMapping/​@alias löst keine Aliase auf — es ist schlicht
// geordnete Ausführung. Wer Werte zwischen Calls weitergibt, braucht
// DependencyMapping (siehe 04). Serial ist nützlich, wenn Reihenfolge/Bandbreite
// wichtig sind oder der Roundtrip einmal bleiben soll.
// ==============================================================================

using TrameClient.Trame;
using TrameCommon.Models;

namespace Trame.Samples.CSharp;

public static class BatchSerialScenario
{
    public static async Task RunAsync(TrameRestJsonClient rest, TextWriter w)
    {
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,          // sequenziell in Request-Reihenfolge
            Requests = new List<TrameRequest>
            {
                TrameCall.Init("Customer", "GetCustomerById").With(1).Named("a").ToRequest(),
                TrameCall.Init("Customer", "GetCustomerById").With(2).Named("b").ToRequest(),
            },
        };

        var responses = (await rest.Call(multi))!.ToList();
        var a = responses[0];
        var b = responses[1];

        await w.WriteLineAsync($"  [a] GetCustomerById(1) -> code {a.Code}, data={a.Data}");
        await w.WriteLineAsync($"  [b] GetCustomerById(2) -> code {b.Code}, data={b.Data}");

        // Gotcha: sobald IRGENDEIN Request ein DependencyMapping hat, schaltet der
        // Server automatisch auf topologische Batch-Ausführung und ignoriert Mode.
        // Für reine Serial-Semantik ohne Aliase einfach kein DependencyMapping
        // setzen (wie hier).
    }
}