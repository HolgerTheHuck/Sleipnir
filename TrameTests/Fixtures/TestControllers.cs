using TrameCommon.Attribute;
using TrameCommon.Models;
using TrameCommon.Results;
using TrameCore.Attributes;
using System.Threading;

namespace TrameTests.Fixtures;

[TrameController("TestInvoker")]
public class TestInvokerController
{
    [TrameMethod("Echo")]
    public string Echo(string message) => message;

    [TrameMethod("Add")]
    public int Add(int a, int b) => a + b;

    [TrameMethod("EchoAsync")]
    public async Task<string> EchoAsync(string message)
    {
        await Task.Delay(10);
        return message;
    }

    [TrameMethod("AddAsync")]
    public async Task<int> AddAsync(int a, int b)
    {
        await Task.Delay(10);
        return a + b;
    }

    [TrameMethod("VoidMethod")]
    public void VoidMethod(string data) { }

    [TrameMethod("WithCancellation")]
    public async Task<string> WithCancellation(string input, CancellationToken ct)
    {
        await Task.Delay(10, ct);
        return input;
    }

    [TrameMethod("ComplexReturn")]
    public TestDto ComplexReturn(int id) => new() { Id = id, Name = "Test" };

    [TrameMethod("NoParams")]
    public string NoParams() => "Hello World";

    // Controller-Methoden, die direkt ein TrameResponse-Objekt zurückgeben (Weg A:
    // strukturierte Domain-Fehler statt werfen). Der Invoker gibt die Response
    // unverändert durch (TrameInvoker.ReturnResponse: result is TrameResponse).
    [TrameMethod("GetOr404")]
    public TrameResponse GetOr404(int id)
        => id == 99
            ? TrameResults.NotFound($"Customer '{id}' not found.")
            : TrameResults.Ok(new TestDto { Id = id, Name = "Found" });

    [TrameMethod("ValidationProblem")]
    public TrameResponse ValidationProblem(string input)
        => string.IsNullOrWhiteSpace(input)
            ? TrameResults.BadRequest("input must not be empty.", "ParameterName=input")
            : TrameResults.Ok(input);

    [TrameMethod("Secured")]
    [TrameAuthorise]
    public string Secured(string data) => data;

    [TrameMethod("SecuredWithRole")]
    [TrameAuthorise(Role = "Admin")]
    public string SecuredWithRole(string data) => data;

    [TrameMethod("StreamNumbers")]
    public async IAsyncEnumerable<int> StreamNumbers(int count, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < count; i++)
        {
            await Task.Delay(1, ct);
            yield return i;
        }
    }

    [TrameMethod("StreamNumbersTask")]
    public async Task<IAsyncEnumerable<int>> StreamNumbersTask(int count, CancellationToken ct = default)
        => StreamNumbers(count, ct);

    [TrameMethod("ObservableStrings")]
    public IObservable<string> ObservableStrings(int count)
        => new SimpleObservable<string>(observer =>
        {
            for (int i = 0; i < count; i++)
                observer.OnNext($"evt-{i}");
            observer.OnCompleted();
            return () => { };
        });

    [TrameMethod("UploadBlob")]
    public string UploadBlob(byte[] data, string filename)
        => $"Received {data.Length} bytes for {filename}";

    [TrameMethod("DownloadBlob")]
    public byte[] DownloadBlob(string name)
        => System.Text.Encoding.UTF8.GetBytes($"Blob content for {name}");

    [TrameMethod("UploadAndProcess")]
    public async Task<int> UploadAndProcess(byte[] data, CancellationToken ct)
    {
        await Task.Delay(1, ct);
        return data.Length;
    }

    [TrameMethod("DownloadStream")]
    public System.IO.Stream DownloadStream(string name)
    {
        var content = System.Text.Encoding.UTF8.GetBytes($"Streamed content for {name}");
        return new System.IO.MemoryStream(content);
    }
}

[TrameDataContract]
public class TestDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Controller mit gepunktetem Namespace, um beliebig tiefe Routing-Pfade
/// (Customer.Address.Contact) über das Controller-Feld abzubilden.
/// </summary>
[TrameController("Customer.Address.Contact")]
public class NestedContactController
{
    [TrameMethod("Add")]
    public string Add(string name) => $"added {name}";
}

/// <summary>
/// Deterministischer Controller für Dependency-Chain-Tests. Jede Methode ist
/// ein reiner Roundtrip/Producer ohne externe Zustände, sodass das @alias-Walking
/// pro Datentyp isoliert und deterministisch geprüft werden kann — unabhängig
/// vom zustandsbehafteten, langsamem CustomerService des Sample-Apps.
/// </summary>
[TrameController("DepChain")]
public class DependencyChainController
{
    // --- Skalare Roundtrips (Producer + Consumer in einer Methode) ---

    [TrameMethod("EchoBool")]
    public bool EchoBool(bool value) => value;

    [TrameMethod("EchoLong")]
    public long EchoLong(long value) => value;

    [TrameMethod("EchoDecimal")]
    public decimal EchoDecimal(decimal value) => value;

    [TrameMethod("EchoString")]
    public string EchoString(string value) => value;

    // --- Objekt (TestDto) ---

    [TrameMethod("MakeDto")]
    public TestDto MakeDto(int id, string name) => new() { Id = id, Name = name };

    /// <summary>Nimmt ein ganzes TestDto als @alias und gibt es unverändert zurück.</summary>
    [TrameMethod("EchoDto")]
    public TestDto EchoDto(TestDto dto) => dto;

    /// <summary>Nimmt ein ganzes TestDto als @alias und extrahiert dessen Id.</summary>
    [TrameMethod("GetDtoId")]
    public int GetDtoId(TestDto dto) => dto.Id;

    // --- Arrays / Collections ---

    [TrameMethod("MakeDtoList")]
    public List<TestDto> MakeDtoList() => new()
    {
        new() { Id = 1, Name = "One" },
        new() { Id = 2, Name = "Two" },
        new() { Id = 3, Name = "Three" }
    };

    /// <summary>Nimmt eine ganze List&lt;TestDto&gt; als @alias und gibt sie unverändert zurück.</summary>
    [TrameMethod("EchoDtoList")]
    public List<TestDto> EchoDtoList(List<TestDto> dtos) => dtos;

    /// <summary>Nimmt eine ganze List&lt;TestDto&gt; als @alias und liefert deren Länge.</summary>
    [TrameMethod("CountDtoList")]
    public int CountDtoList(List<TestDto> dtos) => dtos.Count;

    /// <summary>Liefert eine feste int-Liste [10,20,30] als Quelle für Array-Element-Pfade ($[1]).</summary>
    [TrameMethod("MakeIntList")]
    public List<int> MakeIntList() => new() { 10, 20, 30 };

    // --- Weitere Primitives (Roundtrip) ---

    [TrameMethod("EchoDouble")]
    public double EchoDouble(double value) => value;

    [TrameMethod("EchoFloat")]
    public float EchoFloat(float value) => value;

    [TrameMethod("EchoDateTime")]
    public DateTime EchoDateTime(DateTime value) => value;

    [TrameMethod("EchoGuid")]
    public Guid EchoGuid(Guid value) => value;

    [TrameMethod("EchoPriority")]
    public ChainPriority EchoPriority(ChainPriority value) => value;

    // --- Ganze primitive Liste als Dependency (nicht nur Element-Extraktion) ---

    /// <summary>Nimmt eine ganze List&lt;int&gt; als @alias und gibt sie unverändert zurück.</summary>
    [TrameMethod("EchoIntList")]
    public List<int> EchoIntList(List<int> values) => values;

    // --- Geschachtelter Pfad ($.Inner.Id) ---

    [TrameMethod("MakeNestedDto")]
    public NestedDto MakeNestedDto(int outerId, int innerId) => new()
    {
        Id = outerId,
        Inner = new() { Id = innerId, Name = "Inner" }
    };

    // --- Nullable-Result (FindDto liefert null für id <= 0) ---

    [TrameMethod("FindDto")]
    public TestDto? FindDto(int id) => id > 0 ? new TestDto { Id = id, Name = "Found" } : null;

    // --- Fehler-Producer: non-2xx mit non-null Data (ProblemDetails) ---------------
    // Dokumentiert den Status-Gate der Exposes-Extraktion: ein Expose auf $.title darf
    // bei einem Fehler-Response KEINEN Wert aus dem ProblemDetails-Payload liefern.

    /// <summary>Liefert einen non-2xx-Fehler im ProblemDetails-Stil mit non-null Data
    ///  (title/status/detail). Ein Expose auf $.title darf trotz treffendem Pfad nichts
    ///  exposen, weil die Extraktion auf Erfolg (2xx) gate-et ist.</summary>
    [TrameMethod("FailWithProblem")]
    public TrameResponse FailWithProblem(int status)
        => TrameResults.Error(new ProblemDetails { Status = status, Title = "Invalid", Detail = "bad input" });

    // --- Binär (byte[]) — dokumentiert die Chain-Grenze ---

    [TrameMethod("DownloadBytes")]
    public byte[] DownloadBytes() => System.Text.Encoding.UTF8.GetBytes("chain-bytes");

    [TrameMethod("EchoBytes")]
    public byte[] EchoBytes(byte[] data) => data;

    // --- Dictionary als Dependency ---

    [TrameMethod("MakeDict")]
    public Dictionary<string, int> MakeDict() => new() { { "a", 1 }, { "b", 2 }, { "c", 3 } };

    [TrameMethod("EchoDict")]
    public Dictionary<string, int> EchoDict(Dictionary<string, int> map) => map;

    // --- Alias-Binding-Matrix (Provider→Consumer per @alias) ---------------------
    // Diese Methoden treiben die AliasBindingTests: sie stellen die Producer- und
    // Consumer-Enden für die vier Runtime-Ergebnisse (kompatibel, cross-kind 400,
    // object→object-Duck-Typing, unresolved) sowie das Subset-Fan-out-Muster.

    /// <summary>Nackter Skalar-Consumer (int) — ganzes Objekt als @alias → cross-kind 400.</summary>
    [TrameMethod("EchoInt")]
    public int EchoInt(int value) => value;

    /// <summary>Producer eines schmalen Nur-Id-Objekts (für missing-Property-Fälle).</summary>
    [TrameMethod("MakeIdOnly")]
    public IdOnlyDto MakeIdOnly(int id) => new() { Id = id };

    /// <summary>Producer eines Objekts, dessen Id ein String ist (für Kind-Mismatch auf Überlappung).</summary>
    [TrameMethod("MakeStringIdDto")]
    public StringIdDto MakeStringIdDto(string id) => new() { Id = id };

    /// <summary>Consumer: Nur-Id-DTO — duck-typet Id aus einem breiteren Provider-Objekt.</summary>
    [TrameMethod("TakeIdOnly")]
    public int TakeIdOnly(IdOnlyDto d) => d.Id;

    /// <summary>Consumer: Nur-Name-DTO — duck-typet Name aus einem breiteren Provider-Objekt.</summary>
    [TrameMethod("TakeNameOnly")]
    public string TakeNameOnly(NameOnlyDto d) => d.Name;

    /// <summary>Consumer: Id+Active-DTO — beobachtbar für silent-default (Active=false bei Fehlen).</summary>
    [TrameMethod("TakeIdActive")]
    public string TakeIdActive(IdActiveDto d) => $"{d.Id}/{d.Active}";

    /// <summary>Consumer: ganzes TestDto als String beschrieben — macht null bei fehlendem Name sichtbar.</summary>
    [TrameMethod("DescribeDto")]
    public string DescribeDto(TestDto d) => $"{d.Id}/{d.Name}";

    // --- Paranoid-Binding: verschachtelte Objekte + Array-Elemente -----------------
    // Diese Methoden treiben die AliasBindingParanoidTests: sie stellen die verschachtelten
    // Consumer-Enden für die rekursive Tiefe (Strict ist flach) und die Array-Element-Deckung.

    /// <summary>Producer eines vollständigen OrderDto (Id + Address{Street,Zip}).</summary>
    [TrameMethod("MakeOrder")]
    public OrderDto MakeOrder(int id, string street, int zip) => new()
    {
        Id = id,
        Address = new() { Street = street, Zip = zip }
    };

    /// <summary>Producer eines OrderDto ohne verschachteltes Zip (Dictionary → JSON ohne
    /// zip-Schlüssel). Dient dem Alias-Pfad-Test: Paranoid muß das fehlende verschachtelte
    /// Zip erkennen, Strict (flach) nicht.</summary>
    [TrameMethod("MakeOrderNoZip")]
    public Dictionary<string, object> MakeOrderNoZip(int id, string street) => new()
    {
        ["id"] = id,
        ["address"] = new Dictionary<string, object> { ["street"] = street }
    };

    /// <summary>Consumer: ganzes OrderDto als String — macht fehlendes verschachteltes Zip
    /// als 0 sichtbar (Werttyp, heimtückisch). Paranoid lehnt ab; Strict läßt es durch.</summary>
    [TrameMethod("TakeOrder")]
    public string TakeOrder(OrderDto o) => $"{o.Id}/{o.Address.Street}/{o.Address.Zip}";

    /// <summary>Consumer: List&lt;OrderDto&gt; — Paranoid steigt in jedes Element ab und
    /// deckt dessen verschachtelte Eigenschaften; Strict ignoriert Array-Elemente.</summary>
    [TrameMethod("TakeOrderList")]
    public int TakeOrderList(List<OrderDto> list) => list.Count;
}

/// <summary>Schmales Nur-Id-DTO — Own-Assembly (Weg-C-Inferenz expandiert es). Consumer-Shape
///  für das Subset-Fan-out (Provider TestDto{Id,Name} → IdOnly{Id}, Name fällt weg).</summary>
public class IdOnlyDto
{
    public int Id { get; set; }
}

/// <summary>Schmales Nur-Name-DTO — Consumer-Shape für Subset-Fan-out (Name übernommen, Id fällt weg).</summary>
public class NameOnlyDto
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Id+Active-DTO — beobachtet den heimtückischen Fall: fehlendes Active (Werttyp)
///  wird zur Laufzeit still auf false gesetzt, kein 400.</summary>
public class IdActiveDto
{
    public int Id { get; set; }
    public bool Active { get; set; }
}

/// <summary>DTO mit String-Id — Provider-Shape für Kind-Mismatch auf einer überlappenden
///  Eigenschaft (Provider Id:string → Consumer Id:int → 400).</summary>
public class StringIdDto
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>Verschachteltes OrderDto für die Paranoid-Tiefenprüfung: Address ist ein
///  coverable Object mit eigener deckungspflichtigen Eigenschaft Zip (Werttyp). Ein
///  Fragment, das Address liefert aber ohne Zip, bindet in Weak/Strict still (Zip=0),
///  wird in Paranoid rekursiv abgelehnt.</summary>
public class OrderDto
{
    public int Id { get; set; }
    public AddressDto Address { get; set; } = new();
}

/// <summary>Adresse mit Street (Referenz) und Zip (Werttyp) — Zip ist der heimtückische
///  verschachtelte Fall: fehlt es, wird es still auf 0 gesetzt.</summary>
public class AddressDto
{
    public string Street { get; set; } = string.Empty;
    public int Zip { get; set; }
}

/// <summary>Enum für den Enum-Roundtrip durch eine Chain (default-Serialisierung als Zahl).</summary>
public enum ChainPriority { Low = 0, Medium = 1, High = 2 }

/// <summary>
/// Verschachtelter DataContract für Multi-Level-JsonPath-Tests ($.Inner.Id) in einer Chain.
/// </summary>
[TrameDataContract]
public class NestedDto
{
    public int Id { get; set; }
    public TestDto Inner { get; set; } = new();
}

// --- Fixtures für die Signatur-Inferenz-Tests (Weg C) ----------------------------
// Separate Controller, damit die 18-Methoden-Assertion für TestInvokerController
// unangetastet bleibt. Diese Controller üben die Expansion-Heuristik:
//   Regel 4: UnmarkedDto (own Assembly, kein Attribute) → expandiert.
//   Regel 5: TrameResponse (fremde Assembly, kein Override) → opaque.
//   Regel 2: ExcludedDto (own Assembly, Exclude=true)     → force-opaque.

/// <summary>
/// Controller für die Weg-C-Discovery-Inferenz-Tests. Jede Methode zielt auf genau
/// eine Heuristik-Regel ab.
/// </summary>
[TrameController("DiscoveryInference")]
public class DiscoveryInferenceController
{
    /// <summary>Regel 4: unmarkierter Own-Assembly-Typ als Rückgabewert → muss expandieren.</summary>
    [TrameMethod("ReturnUnmarked")]
    public UnmarkedDto ReturnUnmarked(int id) => new() { Id = id, Name = "inferred" };

    /// <summary>Regel 5: Framework-Envelope aus TrameCommon (fremde Assembly) → muss opaque bleiben.</summary>
    [TrameMethod("ReturnFrameworkType")]
    public TrameResponse ReturnFrameworkType(int id)
        => TrameResults.Ok(new UnmarkedDto { Id = id, Name = "envelope" });

    /// <summary>Regel 2: Own-Assembly-Typ mit [TrameDataContract(Exclude = true)] → force-opaque.</summary>
    [TrameMethod("TakeExcluded")]
    public int TakeExcluded(ExcludedDto d) => d.X;
}

/// <summary>Unmarkierter DTO in der Test-Assembly (Contract-Assembly-Set) — Regel 4.</summary>
public class UnmarkedDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Own-Assembly-Typ mit Exclude-Override — Regel 2 (force-opaque).</summary>
[TrameDataContract(Exclude = true)]
public class ExcludedDto
{
    public int X { get; set; }
}

// --- Fixtures für die Parallel-Auth + Dependent-Propagierung --------------------
// Deterministischer Controller mit Aufruf-Zählern (static, weil der DI-Container
// pro Call eine frische Instanz erzeugt — instance-Counter würden pro Call wieder
// bei 0 stehen). Die Tests serialisieren sich über die xUnit-Collection
// "auth-propagation" und resetten die Counter im Konstruktor, sodass nebenläufige
// Klassen-Instanzen sich nicht in die Quere kommen. Andere Test-Klassen greifen
// nicht auf diesen Controller zu.
[TrameController("AuthProp")]
public class AuthPropagationController
{
    [TrameMethod("Echo")]
    public string Echo(string value)
    {
        Interlocked.Increment(ref EchoCalls);
        return value;
    }

    [TrameMethod("SecuredEcho")]
    [TrameAuthorise]
    public string SecuredEcho(string value)
    {
        Interlocked.Increment(ref SecuredCalls);
        return value;
    }

    public static int EchoCalls;
    public static int SecuredCalls;

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref EchoCalls, 0);
        Interlocked.Exchange(ref SecuredCalls, 0);
    }
}

/// <summary>
/// Fixture für die Auth-Postur-Matrix (North-Bound-Default-Deny): jede Methode trägt eine
/// andere Bestückung, damit RequireAuthentication {off,on} × Methode × User durchgespielt
/// werden kann. Static-Counter isolieren die Aufrufe wie <see cref="AuthPropagationController"/>.
/// </summary>
[TrameController("AuthPosture")]
public class AuthPostureController
{
    [TrameMethod("Open")]
    public string Open() { Interlocked.Increment(ref OpenCalls); return "open"; }

    [TrameMethod("Locked")]
    [TrameAuthorise]
    public string Locked() { Interlocked.Increment(ref LockedCalls); return "locked"; }

    [TrameMethod("AdminOnly")]
    [TrameAuthorise(Role = "Admin")]
    public string AdminOnly() { Interlocked.Increment(ref AdminCalls); return "admin"; }

    [TrameMethod("Public")]
    [TrameAnonymous]
    public string Public() { Interlocked.Increment(ref PublicCalls); return "public"; }

    public static int OpenCalls, LockedCalls, AdminCalls, PublicCalls;

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref OpenCalls, 0);
        Interlocked.Exchange(ref LockedCalls, 0);
        Interlocked.Exchange(ref AdminCalls, 0);
        Interlocked.Exchange(ref PublicCalls, 0);
    }
}

/// <summary>
/// Controller mit KLASSEN-LEVEL-[TrameAuthorise] — gilt als Default für alle Methoden
/// des Controllers (North-Bound nutzt das: ein bestückter Controller schützt alles).
/// </summary>
[TrameController("AuthPostureClass")]
[TrameAuthorise]
public class AuthPostureClassLevelController
{
    [TrameMethod("Inherited")]
    public string Inherited() { return "inherited"; }

    // Methoden-Level-Opt-out schlägt den Klassen-Default.
    [TrameMethod("Opened")]
    [TrameAnonymous]
    public string Opened() { return "opened"; }
}

// --- Fixtures für die strukturellen TypeRef-Edge-Cases (Discovery-Schema) ----------
// Ein dedizierter Controller, der jeden TypeRef-Zweig isoliert, der in den
// Basis-Discovery-Tests nicht getroffen wird: set, scalar "any", Nullable<T>-Unwrap,
// native Arrays, verschachtelte Collections, Default-Wert vorhanden, Enum mit
// Byte-Underlying, [TrameExample]-Belegung, selbstreferenzieller Typ (zyklussicher)
// und bare Task -> void.

[TrameController("DiscoveryEdge")]
public class DiscoveryEdgeCasesController
{
    // --- set-Kinds -----------------------------------------------------------

    [TrameMethod("EchoHashSet")]
    public HashSet<string> EchoHashSet(HashSet<string> values) => values;

    [TrameMethod("EchoSortedSet")]
    public SortedSet<int> EchoSortedSet(SortedSet<int> values) => values;

    [TrameMethod("EchoDtoSet")]
    public HashSet<TestDto> EchoDtoSet(HashSet<TestDto> values) => values;

    // --- scalar "any" (object / JSON-DOM) ------------------------------------

    [TrameMethod("EchoObject")]
    public object EchoObject(object value) => value;

    [TrameMethod("EchoJsonElement")]
    public System.Text.Json.JsonElement EchoJsonElement(System.Text.Json.JsonElement el) => el;

    [TrameMethod("EchoJsonNode")]
    public System.Text.Json.Nodes.JsonNode EchoJsonNode(System.Text.Json.Nodes.JsonNode node) => node;

    // --- Nullable<T>-Werttyp-Unwrap ------------------------------------------

    [TrameMethod("EchoNullableInt")]
    public int? EchoNullableInt(int? value) => value;

    [TrameMethod("EchoNullableGuid")]
    public Guid? EchoNullableGuid(Guid? value) => value;

    // --- native Arrays -------------------------------------------------------

    [TrameMethod("EchoLongArray")]
    public long[] EchoLongArray(long[] values) => values;

    [TrameMethod("EchoDtoArray")]
    public TestDto[] EchoDtoArray(TestDto[] values) => values;

    // --- verschachtelte Collections ------------------------------------------

    [TrameMethod("MakeNestedList")]
    public List<List<int>> MakeNestedList() => new();

    [TrameMethod("MakeMapOfLists")]
    public Dictionary<string, List<int>> MakeMapOfLists() => new();

    [TrameMethod("MakeSetOfArrays")]
    public HashSet<string[]> MakeSetOfArrays() => new();

    // --- Default-Wert vorhanden (Gegenstück zum absent-Default-Test) ---------

    [TrameMethod("EchoWithDefault")]
    public int EchoWithDefault(int x = 42) => x;

    [TrameMethod("EchoStringDefault")]
    public string EchoStringDefault(string s = "hi") => s;

    // --- Enum mit Byte-Underlying (Convert.ChangeType-Pfad) ------------------

    [TrameMethod("EchoByteFlag")]
    public ByteFlag EchoByteFlag(ByteFlag value) => value;

    // --- [TrameExample]-Belegung ---------------------------------------------

    [TrameMethod("MakeExampled")]
    public ExampledDto MakeExampled() => new();

    // --- selbstreferenzieller Typ (zyklussicherer Placeholder) ---------------

    [TrameMethod("MakeNode")]
    public TreeNode MakeNode(int v) => new() { Value = v };

    // --- bare Task -> void (Task ohne<T>) ------------------------------------

    [TrameMethod("Fire")]
    public async Task Fire() { await Task.Delay(1); }

    // --- weitere Skalar-Namen (direkte Namens-Assertion) ---------------------

    [TrameMethod("EchoLong")]
    public long EchoLong(long v) => v;

    [TrameMethod("EchoBool")]
    public bool EchoBool(bool v) => v;

    [TrameMethod("EchoDouble")]
    public double EchoDouble(double v) => v;

    [TrameMethod("EchoDecimal")]
    public decimal EchoDecimal(decimal v) => v;

    [TrameMethod("EchoTimeSpan")]
    public TimeSpan EchoTimeSpan(TimeSpan v) => v;

    [TrameMethod("EchoDateTimeOffset")]
    public DateTimeOffset EchoDateTimeOffset(DateTimeOffset v) => v;

    [TrameMethod("EchoDateTime")]
    public DateTime EchoDateTime(DateTime v) => v;

    [TrameMethod("EchoGuid")]
    public Guid EchoGuid(Guid v) => v;
}

/// <summary>Enum mit Byte-Underlying — übt den Convert.ChangeType-Pfad in BuildEnumTypeMeta.</summary>
public enum ByteFlag : byte { None = 0, A = 1, B = 2 }

/// <summary>Typ mit [TrameExample] — die Discovery belegt Example aus dem JSON-String.</summary>
[TrameExample("""{"Id":7,"Name":"sample"}""")]
public class ExampledDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Selbstreferenzieller Typ: Next verweist auf den eigenen Typ. Dient dem Test,
/// dass EnsureRegistered einen Placeholder setzt, bevor die Properties aufgelöst
/// werden (zyklussicher), und dass Next als ref + nullable emitet wird.
/// </summary>
public class TreeNode
{
    public int Value { get; set; }
    public TreeNode? Next { get; set; }
}