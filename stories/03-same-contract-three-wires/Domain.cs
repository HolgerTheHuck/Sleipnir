using SleipnirCore.Attributes;

namespace SleipnirStories.Story03;

// === Story 03 Domain — "The Same Contract, Three Wires" ===========================
// Eine winzige Domain, bewusst klein, damit der Punkt der Transport-Unabhängigkeit
// im Vordergrund steht: die Klassen SIND der Vertrag; REST/WebSocket/SignalR exponieren
// dieselben Controller simultan. Ein Aufruf über drei Wires liefert dasselbe Resultat.

public sealed class Greeting
{
    public string Message { get; set; } = "";
    public int Count { get; set; }
}

internal static class GreetingStore
{
    public static int CallCount;
}

[SleipnirController("Greeter")]
public class GreeterController
{
    // Ein einfacher Call, der über alle drei Wires identisch läuft.
    [SleipnirMethod("Greet")]
    public Greeting Greet(string name)
    {
        var n = Interlocked.Increment(ref GreetingStore.CallCount);
        return new Greeting { Message = $"Hello, {name}!", Count = n };
    }

    // Ein 2er-Batch-Fall: Add liefert eine Summe; Echo gibt sie unverändert zurück.
    // Beweist, dass auch die Batch-Topologie transport-unabhängig ist.
    [SleipnirMethod("Add")]
    public int Add(int a, int b) => a + b;

    [SleipnirMethod("Echo")]
    public int Echo(int value) => value;
}