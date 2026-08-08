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

    // Controller methods that return a TrameResponse object directly (Weg A:
    // structured domain errors instead of throwing). The invoker passes the
    // response through unchanged (TrameInvoker.ReturnResponse: result is TrameResponse).
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

    /// <summary>
    /// Fires <paramref name="count"/> events on a background task with a delay between each,
    /// so the event frames (pushed by the subscription pump task) interleave in time with
    /// concurrent call responses (pushed by the middleware thread). Used by the R6
    /// single-sender-channel regression: the synchronous <see cref="ObservableStrings"/>
    /// drains before any call traffic starts, so it cannot exercise the concurrent-send path.
    /// </summary>
    [TrameMethod("ObservableStringsOverTime")]
    public IObservable<string> ObservableStringsOverTime(int count, int delayMs)
        => new SimpleObservable<string>(observer =>
        {
            var cts = new CancellationTokenSource();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(delayMs, cts.Token);
                        observer.OnNext($"evt-{i}");
                    }
                    observer.OnCompleted();
                }
                catch (System.OperationCanceledException) { /* disposed — stop */ }
            });
            return () => cts.Cancel();
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
/// Controller with a dotted namespace, to map arbitrarily deep routing paths
/// (Customer.Address.Contact) via the Controller field.
/// </summary>
[TrameController("Customer.Address.Contact")]
public class NestedContactController
{
    [TrameMethod("Add")]
    public string Add(string name) => $"added {name}";
}

/// <summary>
/// Deterministic controller for dependency-chain tests. Each method is a pure
/// roundtrip/producer with no external state, so the @alias walking can be
/// tested in isolation and deterministically per data type — independent of
/// the stateful, slow CustomerService of the sample app.
/// </summary>
[TrameController("DepChain")]
public class DependencyChainController
{
    // --- Scalar roundtrips (producer + consumer in a single method) ---

    [TrameMethod("EchoBool")]
    public bool EchoBool(bool value) => value;

    [TrameMethod("EchoLong")]
    public long EchoLong(long value) => value;

    [TrameMethod("EchoDecimal")]
    public decimal EchoDecimal(decimal value) => value;

    [TrameMethod("EchoString")]
    public string EchoString(string value) => value;

    // --- Object (TestDto) ---

    [TrameMethod("MakeDto")]
    public TestDto MakeDto(int id, string name) => new() { Id = id, Name = name };

    /// <summary>Takes a whole TestDto as @alias and returns it unchanged.</summary>
    [TrameMethod("EchoDto")]
    public TestDto EchoDto(TestDto dto) => dto;

    /// <summary>Takes a whole TestDto as @alias and extracts its Id.</summary>
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

    /// <summary>Takes a whole List&lt;TestDto&gt; as @alias and returns it unchanged.</summary>
    [TrameMethod("EchoDtoList")]
    public List<TestDto> EchoDtoList(List<TestDto> dtos) => dtos;

    /// <summary>Takes a whole List&lt;TestDto&gt; as @alias and returns its length.</summary>
    [TrameMethod("CountDtoList")]
    public int CountDtoList(List<TestDto> dtos) => dtos.Count;

    /// <summary>Returns a fixed int list [10,20,30] as the source for array-element paths ($[1]).</summary>
    [TrameMethod("MakeIntList")]
    public List<int> MakeIntList() => new() { 10, 20, 30 };

    // --- Further primitives (roundtrip) ---

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

    // --- Whole primitive list as a dependency (not just element extraction) ---

    /// <summary>Takes a whole List&lt;int&gt; as @alias and returns it unchanged.</summary>
    [TrameMethod("EchoIntList")]
    public List<int> EchoIntList(List<int> values) => values;

    // --- Nested path ($.Inner.Id) ---

    [TrameMethod("MakeNestedDto")]
    public NestedDto MakeNestedDto(int outerId, int innerId) => new()
    {
        Id = outerId,
        Inner = new() { Id = innerId, Name = "Inner" }
    };

    // --- Nullable result (FindDto returns null for id <= 0) ---

    [TrameMethod("FindDto")]
    public TestDto? FindDto(int id) => id > 0 ? new TestDto { Id = id, Name = "Found" } : null;

    // --- Error producer: non-2xx with non-null Data (ProblemDetails) ---------------
    // Documents the status gate on exposes extraction: an expose on $.title must
    // NOT yield a value from the ProblemDetails payload on an error response.

    /// <summary>Returns a non-2xx error in ProblemDetails style with non-null Data
    ///  (title/status/detail). An expose on $.title must not expose anything despite
    ///  the path matching, because extraction is gated on success (2xx).</summary>
    [TrameMethod("FailWithProblem")]
    public TrameResponse FailWithProblem(int status)
        => TrameResults.Error(new ProblemDetails { Status = status, Title = "Invalid", Detail = "bad input" });

    // --- Binary (byte[]) — documents the chain boundary ---

    [TrameMethod("DownloadBytes")]
    public byte[] DownloadBytes() => System.Text.Encoding.UTF8.GetBytes("chain-bytes");

    [TrameMethod("EchoBytes")]
    public byte[] EchoBytes(byte[] data) => data;

    // --- Dictionary as a dependency ---

    [TrameMethod("MakeDict")]
    public Dictionary<string, int> MakeDict() => new() { { "a", 1 }, { "b", 2 }, { "c", 3 } };

    [TrameMethod("EchoDict")]
    public Dictionary<string, int> EchoDict(Dictionary<string, int> map) => map;

    // --- Alias binding matrix (provider→consumer via @alias) ---------------------
    // These methods drive the AliasBindingTests: they provide the producer and
    // consumer ends for the four runtime outcomes (compatible, cross-kind 400,
    // object→object duck-typing, unresolved) and the subset fan-out pattern.

    /// <summary>Bare scalar consumer (int) — whole object as @alias → cross-kind 400.</summary>
    [TrameMethod("EchoInt")]
    public int EchoInt(int value) => value;

    /// <summary>Producer of a narrow Id-only object (for missing-property cases).</summary>
    [TrameMethod("MakeIdOnly")]
    public IdOnlyDto MakeIdOnly(int id) => new() { Id = id };

    /// <summary>Producer of an object whose Id is a string (for kind mismatch on an overlapping property).</summary>
    [TrameMethod("MakeStringIdDto")]
    public StringIdDto MakeStringIdDto(string id) => new() { Id = id };

    /// <summary>Consumer: Id-only DTO — duck-types Id from a wider provider object.</summary>
    [TrameMethod("TakeIdOnly")]
    public int TakeIdOnly(IdOnlyDto d) => d.Id;

    /// <summary>Consumer: Name-only DTO — duck-types Name from a wider provider object.</summary>
    [TrameMethod("TakeNameOnly")]
    public string TakeNameOnly(NameOnlyDto d) => d.Name;

    /// <summary>Consumer: Id+Active DTO — observable for the silent-default case (Active=false when missing).</summary>
    [TrameMethod("TakeIdActive")]
    public string TakeIdActive(IdActiveDto d) => $"{d.Id}/{d.Active}";

    /// <summary>Consumer: whole TestDto described as a string — surfaces null when Name is missing.</summary>
    [TrameMethod("DescribeDto")]
    public string DescribeDto(TestDto d) => $"{d.Id}/{d.Name}";

    // --- Paranoid binding: nested objects + array elements -----------------
    // These methods drive the AliasBindingParanoidTests: they provide the nested
    // consumer ends for the recursive depth (Strict is shallow) and array-element coverage.

    /// <summary>Producer of a complete OrderDto (Id + Address{Street,Zip}).</summary>
    [TrameMethod("MakeOrder")]
    public OrderDto MakeOrder(int id, string street, int zip) => new()
    {
        Id = id,
        Address = new() { Street = street, Zip = zip }
    };

    /// <summary>Producer of an OrderDto without nested Zip (Dictionary → JSON without
    /// a zip key). Serves the alias path test: Paranoid must detect the missing nested
    /// Zip, Strict (shallow) must not.</summary>
    [TrameMethod("MakeOrderNoZip")]
    public Dictionary<string, object> MakeOrderNoZip(int id, string street) => new()
    {
        ["id"] = id,
        ["address"] = new Dictionary<string, object> { ["street"] = street }
    };

    /// <summary>Consumer: whole OrderDto as a string — surfaces missing nested Zip
    /// as 0 (value type, insidious). Paranoid rejects; Strict lets it through.</summary>
    [TrameMethod("TakeOrder")]
    public string TakeOrder(OrderDto o) => $"{o.Id}/{o.Address.Street}/{o.Address.Zip}";

    /// <summary>Consumer: List&lt;OrderDto&gt; — Paranoid descends into each element and
    /// covers its nested properties; Strict ignores array elements.</summary>
    [TrameMethod("TakeOrderList")]
    public int TakeOrderList(List<OrderDto> list) => list.Count;
}

/// <summary>Narrow Id-only DTO — own assembly (Weg C inference expands it). Consumer shape
///  for the subset fan-out (provider TestDto{Id,Name} → IdOnly{Id}, Name is dropped).</summary>
public class IdOnlyDto
{
    public int Id { get; set; }
}

/// <summary>Narrow Name-only DTO — consumer shape for the subset fan-out (Name taken, Id dropped).</summary>
public class NameOnlyDto
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Id+Active DTO — observes the insidious case: missing Active (value type)
///  is silently set to false at runtime, no 400.</summary>
public class IdActiveDto
{
    public int Id { get; set; }
    public bool Active { get; set; }
}

/// <summary>DTO with a string Id — provider shape for kind mismatch on an overlapping
///  property (provider Id:string → consumer Id:int → 400).</summary>
public class StringIdDto
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>Nested OrderDto for the Paranoid depth check: Address is a coverable object
///  with its own coverable property Zip (value type). A fragment that supplies Address
///  but without Zip binds silently in Weak/Strict (Zip=0), and is rejected recursively
///  in Paranoid.</summary>
public class OrderDto
{
    public int Id { get; set; }
    public AddressDto Address { get; set; } = new();
}

/// <summary>Address with Street (reference) and Zip (value type) — Zip is the insidious
///  nested case: when missing, it is silently set to 0.</summary>
public class AddressDto
{
    public string Street { get; set; } = string.Empty;
    public int Zip { get; set; }
}

/// <summary>Enum for the enum roundtrip through a chain (default serialization as a number).</summary>
public enum ChainPriority { Low = 0, Medium = 1, High = 2 }

/// <summary>
/// Nested DataContract for multi-level JsonPath tests ($.Inner.Id) in a chain.
/// </summary>
[TrameDataContract]
public class NestedDto
{
    public int Id { get; set; }
    public TestDto Inner { get; set; } = new();
}

// --- Fixtures for the signature-inference tests (Weg C) ----------------------------
// Separate controller, so the 18-method assertion for TestInvokerController
// stays untouched. These controllers exercise the expansion heuristics:
//   Rule 4: UnmarkedDto (own assembly, no attribute) → expanded.
//   Rule 5: TrameResponse (foreign assembly, no override) → opaque.
//   Rule 2: ExcludedDto (own assembly, Exclude=true)    → force-opaque.

/// <summary>
/// Controller for the Weg C discovery-inference tests. Each method targets exactly
/// one heuristic rule.
/// </summary>
[TrameController("DiscoveryInference")]
public class DiscoveryInferenceController
{
    /// <summary>Rule 4: unmarked own-assembly type as a return value → must expand.</summary>
    [TrameMethod("ReturnUnmarked")]
    public UnmarkedDto ReturnUnmarked(int id) => new() { Id = id, Name = "inferred" };

    /// <summary>Rule 5: framework envelope from TrameCommon (foreign assembly) → must stay opaque.</summary>
    [TrameMethod("ReturnFrameworkType")]
    public TrameResponse ReturnFrameworkType(int id)
        => TrameResults.Ok(new UnmarkedDto { Id = id, Name = "envelope" });

    /// <summary>Rule 2: own-assembly type with [TrameDataContract(Exclude = true)] → force-opaque.</summary>
    [TrameMethod("TakeExcluded")]
    public int TakeExcluded(ExcludedDto d) => d.X;
}

/// <summary>Unmarked DTO in the test assembly (contract-assembly set) — Rule 4.</summary>
public class UnmarkedDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Own-assembly type with an Exclude override — Rule 2 (force-opaque).</summary>
[TrameDataContract(Exclude = true)]
public class ExcludedDto
{
    public int X { get; set; }
}

// --- Fixtures for parallel auth + dependent propagation --------------------
// Deterministic controller with invocation counters (static, because the DI
// container creates a fresh instance per call — instance counters would reset
// to 0 on each call). The tests serialize via the xUnit collection
// "auth-propagation" and reset the counters in the constructor, so concurrent
// class instances do not interfere with each other. Other test classes do not
// access this controller.
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
/// Fixture for the auth-posture matrix (North-Bound-Default-Deny): each method carries a
/// different configuration, so RequireAuthentication {off,on} × method × user can be played
/// through. Static counters isolate the invocations like <see cref="AuthPropagationController"/>.
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
/// Controller with a CLASS-LEVEL [TrameAuthorise] — applies as the default for all
/// methods of the controller (North-Bound uses this: an attributed controller
/// protects everything).
/// </summary>
[TrameController("AuthPostureClass")]
[TrameAuthorise]
public class AuthPostureClassLevelController
{
    [TrameMethod("Inherited")]
    public string Inherited() { return "inherited"; }

    // A method-level opt-out overrides the class default.
    [TrameMethod("Opened")]
    [TrameAnonymous]
    public string Opened() { return "opened"; }
}

/// <summary>
/// Fixture for the batch-path policy fix (1.1.1): <c>[TrameAuthorise(Policy=...)]</c>
/// evaluated in the serial auth pre-pass via the invoker's <c>PolicyEvaluator</c> delegate.
/// Static counters isolate the invocations like <see cref="AuthPropagationController"/>.
/// </summary>
[TrameController("PolicyAuth")]
public class PolicyAuthController
{
    [TrameMethod("Open")]
    public string Open() { Interlocked.Increment(ref OpenCalls); return "open"; }

    [TrameMethod("AllowedPolicy")]
    [TrameAuthorise(Policy = "allowed")]
    public string AllowedPolicy() { Interlocked.Increment(ref AllowedCalls); return "allowed"; }

    [TrameMethod("DeniedPolicy")]
    [TrameAuthorise(Policy = "denied")]
    public string DeniedPolicy() { Interlocked.Increment(ref DeniedCalls); return "denied"; }

    public static int OpenCalls, AllowedCalls, DeniedCalls;

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref OpenCalls, 0);
        Interlocked.Exchange(ref AllowedCalls, 0);
        Interlocked.Exchange(ref DeniedCalls, 0);
    }
}

// --- Fixtures for the structural TypeRef edge cases (discovery schema) ----------
// A dedicated controller that isolates each TypeRef branch not covered by the
// base discovery tests: set, scalar "any", Nullable<T> unwrap, native arrays,
// nested collections, default value present, enum with a byte underlying type,
// [TrameExample] population, self-referencing type (cycle-safe), and bare Task -> void.

[TrameController("DiscoveryEdge")]
public class DiscoveryEdgeCasesController
{
    // --- set kinds -----------------------------------------------------------

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

    // --- Nullable<T> value-type unwrap ------------------------------------------

    [TrameMethod("EchoNullableInt")]
    public int? EchoNullableInt(int? value) => value;

    [TrameMethod("EchoNullableGuid")]
    public Guid? EchoNullableGuid(Guid? value) => value;

    // --- native arrays -------------------------------------------------------

    [TrameMethod("EchoLongArray")]
    public long[] EchoLongArray(long[] values) => values;

    [TrameMethod("EchoDtoArray")]
    public TestDto[] EchoDtoArray(TestDto[] values) => values;

    // --- nested collections ------------------------------------------

    [TrameMethod("MakeNestedList")]
    public List<List<int>> MakeNestedList() => new();

    [TrameMethod("MakeMapOfLists")]
    public Dictionary<string, List<int>> MakeMapOfLists() => new();

    [TrameMethod("MakeSetOfArrays")]
    public HashSet<string[]> MakeSetOfArrays() => new();

    // --- default value present (counterpart to the absent-default test) ---------

    [TrameMethod("EchoWithDefault")]
    public int EchoWithDefault(int x = 42) => x;

    [TrameMethod("EchoStringDefault")]
    public string EchoStringDefault(string s = "hi") => s;

    // --- enum with a byte underlying type (Convert.ChangeType path) ------------------

    [TrameMethod("EchoByteFlag")]
    public ByteFlag EchoByteFlag(ByteFlag value) => value;

    // --- [TrameExample] population ---------------------------------------------

    [TrameMethod("MakeExampled")]
    public ExampledDto MakeExampled() => new();

    // --- self-referencing type (cycle-safe placeholder) ---------------

    [TrameMethod("MakeNode")]
    public TreeNode MakeNode(int v) => new() { Value = v };

    // --- bare Task -> void (Task without <T>) ------------------------------------

    [TrameMethod("Fire")]
    public async Task Fire() { await Task.Delay(1); }

    // --- further scalar names (direct name assertion) ---------------------

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

/// <summary>Enum with a byte underlying type — exercises the Convert.ChangeType path in BuildEnumTypeMeta.</summary>
public enum ByteFlag : byte { None = 0, A = 1, B = 2 }

/// <summary>Type with [TrameExample] — discovery populates Example from the JSON string.</summary>
[TrameExample("""{"Id":7,"Name":"sample"}""")]
public class ExampledDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Self-referencing type: Next points to its own type. Serves the test that
/// EnsureRegistered sets a placeholder before the properties are resolved
/// (cycle-safe), and that Next is emitted as ref + nullable.
/// </summary>
public class TreeNode
{
    public int Value { get; set; }
    public TreeNode? Next { get; set; }
}