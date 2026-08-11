using SleipnirCommon.Attribute;
using SleipnirCommon.Models;
using SleipnirCommon.Results;
using SleipnirCore.Attributes;
using System.Threading;

namespace SleipnirTests.Fixtures;

[SleipnirController("TestInvoker")]
public class TestInvokerController
{
    [SleipnirMethod("Echo")]
    public string Echo(string message) => message;

    [SleipnirMethod("Add")]
    public int Add(int a, int b) => a + b;

    [SleipnirMethod("EchoAsync")]
    public async Task<string> EchoAsync(string message)
    {
        await Task.Delay(10);
        return message;
    }

    [SleipnirMethod("AddAsync")]
    public async Task<int> AddAsync(int a, int b)
    {
        await Task.Delay(10);
        return a + b;
    }

    [SleipnirMethod("VoidMethod")]
    public void VoidMethod(string data) { }

    [SleipnirMethod("WithCancellation")]
    public async Task<string> WithCancellation(string input, CancellationToken ct)
    {
        await Task.Delay(10, ct);
        return input;
    }

    [SleipnirMethod("ComplexReturn")]
    public TestDto ComplexReturn(int id) => new() { Id = id, Name = "Test" };

    [SleipnirMethod("NoParams")]
    public string NoParams() => "Hello World";

    // Controller methods that return a SleipnirResponse object directly (Weg A:
    // structured domain errors instead of throwing). The invoker passes the
    // response through unchanged (SleipnirInvoker.ReturnResponse: result is SleipnirResponse).
    [SleipnirMethod("GetOr404")]
    public SleipnirResponse GetOr404(int id)
        => id == 99
            ? SleipnirResults.NotFound($"Customer '{id}' not found.")
            : SleipnirResults.Ok(new TestDto { Id = id, Name = "Found" });

    [SleipnirMethod("ValidationProblem")]
    public SleipnirResponse ValidationProblem(string input)
        => string.IsNullOrWhiteSpace(input)
            ? SleipnirResults.BadRequest("input must not be empty.", "ParameterName=input")
            : SleipnirResults.Ok(input);

    [SleipnirMethod("Secured")]
    [SleipnirAuthorise]
    public string Secured(string data) => data;

    [SleipnirMethod("SecuredWithRole")]
    [SleipnirAuthorise(Role = "Admin")]
    public string SecuredWithRole(string data) => data;

    [SleipnirMethod("StreamNumbers")]
    public async IAsyncEnumerable<int> StreamNumbers(int count, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < count; i++)
        {
            await Task.Delay(1, ct);
            yield return i;
        }
    }

    [SleipnirMethod("StreamNumbersTask")]
    public async Task<IAsyncEnumerable<int>> StreamNumbersTask(int count, CancellationToken ct = default)
        => StreamNumbers(count, ct);

    [SleipnirMethod("ObservableStrings")]
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
    [SleipnirMethod("ObservableStringsOverTime")]
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

    [SleipnirMethod("UploadBlob")]
    public string UploadBlob(byte[] data, string filename)
        => $"Received {data.Length} bytes for {filename}";

    [SleipnirMethod("DownloadBlob")]
    public byte[] DownloadBlob(string name)
        => System.Text.Encoding.UTF8.GetBytes($"Blob content for {name}");

    [SleipnirMethod("UploadAndProcess")]
    public async Task<int> UploadAndProcess(byte[] data, CancellationToken ct)
    {
        await Task.Delay(1, ct);
        return data.Length;
    }

    [SleipnirMethod("DownloadStream")]
    public System.IO.Stream DownloadStream(string name)
    {
        var content = System.Text.Encoding.UTF8.GetBytes($"Streamed content for {name}");
        return new System.IO.MemoryStream(content);
    }
}

[SleipnirDataContract]
public class TestDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Controller with a dotted namespace, to map arbitrarily deep routing paths
/// (Customer.Address.Contact) via the Controller field.
/// </summary>
[SleipnirController("Customer.Address.Contact")]
public class NestedContactController
{
    [SleipnirMethod("Add")]
    public string Add(string name) => $"added {name}";
}

/// <summary>
/// Deterministic controller for dependency-chain tests. Each method is a pure
/// roundtrip/producer with no external state, so the @alias walking can be
/// tested in isolation and deterministically per data type — independent of
/// the stateful, slow CustomerService of the sample app.
/// </summary>
[SleipnirController("DepChain")]
public class DependencyChainController
{
    // --- Scalar roundtrips (producer + consumer in a single method) ---

    [SleipnirMethod("EchoBool")]
    public bool EchoBool(bool value) => value;

    [SleipnirMethod("EchoLong")]
    public long EchoLong(long value) => value;

    [SleipnirMethod("EchoDecimal")]
    public decimal EchoDecimal(decimal value) => value;

    [SleipnirMethod("EchoString")]
    public string EchoString(string value) => value;

    // --- Object (TestDto) ---

    [SleipnirMethod("MakeDto")]
    public TestDto MakeDto(int id, string name) => new() { Id = id, Name = name };

    /// <summary>Takes a whole TestDto as @alias and returns it unchanged.</summary>
    [SleipnirMethod("EchoDto")]
    public TestDto EchoDto(TestDto dto) => dto;

    /// <summary>Takes a whole TestDto as @alias and extracts its Id.</summary>
    [SleipnirMethod("GetDtoId")]
    public int GetDtoId(TestDto dto) => dto.Id;

    // --- Arrays / Collections ---

    [SleipnirMethod("MakeDtoList")]
    public List<TestDto> MakeDtoList() => new()
    {
        new() { Id = 1, Name = "One" },
        new() { Id = 2, Name = "Two" },
        new() { Id = 3, Name = "Three" }
    };

    /// <summary>Takes a whole List&lt;TestDto&gt; as @alias and returns it unchanged.</summary>
    [SleipnirMethod("EchoDtoList")]
    public List<TestDto> EchoDtoList(List<TestDto> dtos) => dtos;

    /// <summary>Takes a whole List&lt;TestDto&gt; as @alias and returns its length.</summary>
    [SleipnirMethod("CountDtoList")]
    public int CountDtoList(List<TestDto> dtos) => dtos.Count;

    /// <summary>Returns a fixed int list [10,20,30] as the source for array-element paths ($[1]).</summary>
    [SleipnirMethod("MakeIntList")]
    public List<int> MakeIntList() => new() { 10, 20, 30 };

    // --- Further primitives (roundtrip) ---

    [SleipnirMethod("EchoDouble")]
    public double EchoDouble(double value) => value;

    [SleipnirMethod("EchoFloat")]
    public float EchoFloat(float value) => value;

    [SleipnirMethod("EchoDateTime")]
    public DateTime EchoDateTime(DateTime value) => value;

    [SleipnirMethod("EchoGuid")]
    public Guid EchoGuid(Guid value) => value;

    [SleipnirMethod("EchoPriority")]
    public ChainPriority EchoPriority(ChainPriority value) => value;

    // --- Whole primitive list as a dependency (not just element extraction) ---

    /// <summary>Takes a whole List&lt;int&gt; as @alias and returns it unchanged.</summary>
    [SleipnirMethod("EchoIntList")]
    public List<int> EchoIntList(List<int> values) => values;

    // --- Nested path ($.Inner.Id) ---

    [SleipnirMethod("MakeNestedDto")]
    public NestedDto MakeNestedDto(int outerId, int innerId) => new()
    {
        Id = outerId,
        Inner = new() { Id = innerId, Name = "Inner" }
    };

    // --- Nullable result (FindDto returns null for id <= 0) ---

    [SleipnirMethod("FindDto")]
    public TestDto? FindDto(int id) => id > 0 ? new TestDto { Id = id, Name = "Found" } : null;

    // --- Error producer: non-2xx with non-null Data (ProblemDetails) ---------------
    // Documents the status gate on exposes extraction: an expose on $.title must
    // NOT yield a value from the ProblemDetails payload on an error response.

    /// <summary>Returns a non-2xx error in ProblemDetails style with non-null Data
    ///  (title/status/detail). An expose on $.title must not expose anything despite
    ///  the path matching, because extraction is gated on success (2xx).</summary>
    [SleipnirMethod("FailWithProblem")]
    public SleipnirResponse FailWithProblem(int status)
        => SleipnirResults.Error(new ProblemDetails { Status = status, Title = "Invalid", Detail = "bad input" });

    // --- Binary (byte[]) — documents the chain boundary ---

    [SleipnirMethod("DownloadBytes")]
    public byte[] DownloadBytes() => System.Text.Encoding.UTF8.GetBytes("chain-bytes");

    [SleipnirMethod("EchoBytes")]
    public byte[] EchoBytes(byte[] data) => data;

    // --- Dictionary as a dependency ---

    [SleipnirMethod("MakeDict")]
    public Dictionary<string, int> MakeDict() => new() { { "a", 1 }, { "b", 2 }, { "c", 3 } };

    [SleipnirMethod("EchoDict")]
    public Dictionary<string, int> EchoDict(Dictionary<string, int> map) => map;

    // --- Alias binding matrix (provider→consumer via @alias) ---------------------
    // These methods drive the AliasBindingTests: they provide the producer and
    // consumer ends for the four runtime outcomes (compatible, cross-kind 400,
    // object→object duck-typing, unresolved) and the subset fan-out pattern.

    /// <summary>Bare scalar consumer (int) — whole object as @alias → cross-kind 400.</summary>
    [SleipnirMethod("EchoInt")]
    public int EchoInt(int value) => value;

    /// <summary>Producer of a narrow Id-only object (for missing-property cases).</summary>
    [SleipnirMethod("MakeIdOnly")]
    public IdOnlyDto MakeIdOnly(int id) => new() { Id = id };

    /// <summary>Producer of an object whose Id is a string (for kind mismatch on an overlapping property).</summary>
    [SleipnirMethod("MakeStringIdDto")]
    public StringIdDto MakeStringIdDto(string id) => new() { Id = id };

    /// <summary>Consumer: Id-only DTO — duck-types Id from a wider provider object.</summary>
    [SleipnirMethod("TakeIdOnly")]
    public int TakeIdOnly(IdOnlyDto d) => d.Id;

    /// <summary>Consumer: Name-only DTO — duck-types Name from a wider provider object.</summary>
    [SleipnirMethod("TakeNameOnly")]
    public string TakeNameOnly(NameOnlyDto d) => d.Name;

    /// <summary>Consumer: Id+Active DTO — observable for the silent-default case (Active=false when missing).</summary>
    [SleipnirMethod("TakeIdActive")]
    public string TakeIdActive(IdActiveDto d) => $"{d.Id}/{d.Active}";

    /// <summary>Consumer: whole TestDto described as a string — surfaces null when Name is missing.</summary>
    [SleipnirMethod("DescribeDto")]
    public string DescribeDto(TestDto d) => $"{d.Id}/{d.Name}";

    // --- Paranoid binding: nested objects + array elements -----------------
    // These methods drive the AliasBindingParanoidTests: they provide the nested
    // consumer ends for the recursive depth (Strict is shallow) and array-element coverage.

    /// <summary>Producer of a complete OrderDto (Id + Address{Street,Zip}).</summary>
    [SleipnirMethod("MakeOrder")]
    public OrderDto MakeOrder(int id, string street, int zip) => new()
    {
        Id = id,
        Address = new() { Street = street, Zip = zip }
    };

    /// <summary>Producer of an OrderDto without nested Zip (Dictionary → JSON without
    /// a zip key). Serves the alias path test: Paranoid must detect the missing nested
    /// Zip, Strict (shallow) must not.</summary>
    [SleipnirMethod("MakeOrderNoZip")]
    public Dictionary<string, object> MakeOrderNoZip(int id, string street) => new()
    {
        ["id"] = id,
        ["address"] = new Dictionary<string, object> { ["street"] = street }
    };

    /// <summary>Consumer: whole OrderDto as a string — surfaces missing nested Zip
    /// as 0 (value type, insidious). Paranoid rejects; Strict lets it through.</summary>
    [SleipnirMethod("TakeOrder")]
    public string TakeOrder(OrderDto o) => $"{o.Id}/{o.Address.Street}/{o.Address.Zip}";

    /// <summary>Consumer: List&lt;OrderDto&gt; — Paranoid descends into each element and
    /// covers its nested properties; Strict ignores array elements.</summary>
    [SleipnirMethod("TakeOrderList")]
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
[SleipnirDataContract]
public class NestedDto
{
    public int Id { get; set; }
    public TestDto Inner { get; set; } = new();
}

// --- Fixtures for the signature-inference tests (Weg C) ----------------------------
// Separate controller, so the 18-method assertion for TestInvokerController
// stays untouched. These controllers exercise the expansion heuristics:
//   Rule 4: UnmarkedDto (own assembly, no attribute) → expanded.
//   Rule 5: SleipnirResponse (foreign assembly, no override) → opaque.
//   Rule 2: ExcludedDto (own assembly, Exclude=true)    → force-opaque.

/// <summary>
/// Controller for the Weg C discovery-inference tests. Each method targets exactly
/// one heuristic rule.
/// </summary>
[SleipnirController("DiscoveryInference")]
public class DiscoveryInferenceController
{
    /// <summary>Rule 4: unmarked own-assembly type as a return value → must expand.</summary>
    [SleipnirMethod("ReturnUnmarked")]
    public UnmarkedDto ReturnUnmarked(int id) => new() { Id = id, Name = "inferred" };

    /// <summary>Rule 5: framework envelope from SleipnirCommon (foreign assembly) → must stay opaque.</summary>
    [SleipnirMethod("ReturnFrameworkType")]
    public SleipnirResponse ReturnFrameworkType(int id)
        => SleipnirResults.Ok(new UnmarkedDto { Id = id, Name = "envelope" });

    /// <summary>Rule 2: own-assembly type with [SleipnirDataContract(Exclude = true)] → force-opaque.</summary>
    [SleipnirMethod("TakeExcluded")]
    public int TakeExcluded(ExcludedDto d) => d.X;
}

/// <summary>Unmarked DTO in the test assembly (contract-assembly set) — Rule 4.</summary>
public class UnmarkedDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Own-assembly type with an Exclude override — Rule 2 (force-opaque).</summary>
[SleipnirDataContract(Exclude = true)]
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
[SleipnirController("AuthProp")]
public class AuthPropagationController
{
    [SleipnirMethod("Echo")]
    public string Echo(string value)
    {
        Interlocked.Increment(ref EchoCalls);
        return value;
    }

    [SleipnirMethod("SecuredEcho")]
    [SleipnirAuthorise]
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
[SleipnirController("AuthPosture")]
public class AuthPostureController
{
    [SleipnirMethod("Open")]
    public string Open() { Interlocked.Increment(ref OpenCalls); return "open"; }

    [SleipnirMethod("Locked")]
    [SleipnirAuthorise]
    public string Locked() { Interlocked.Increment(ref LockedCalls); return "locked"; }

    [SleipnirMethod("AdminOnly")]
    [SleipnirAuthorise(Role = "Admin")]
    public string AdminOnly() { Interlocked.Increment(ref AdminCalls); return "admin"; }

    [SleipnirMethod("Public")]
    [SleipnirAnonymous]
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
/// Controller with a CLASS-LEVEL [SleipnirAuthorise] — applies as the default for all
/// methods of the controller (North-Bound uses this: an attributed controller
/// protects everything).
/// </summary>
[SleipnirController("AuthPostureClass")]
[SleipnirAuthorise]
public class AuthPostureClassLevelController
{
    [SleipnirMethod("Inherited")]
    public string Inherited() { return "inherited"; }

    // A method-level opt-out overrides the class default.
    [SleipnirMethod("Opened")]
    [SleipnirAnonymous]
    public string Opened() { return "opened"; }
}

/// <summary>
/// Fixture for the batch-path policy fix (1.1.1): <c>[SleipnirAuthorise(Policy=...)]</c>
/// evaluated in the serial auth pre-pass via the invoker's <c>PolicyEvaluator</c> delegate.
/// Static counters isolate the invocations like <see cref="AuthPropagationController"/>.
/// </summary>
[SleipnirController("PolicyAuth")]
public class PolicyAuthController
{
    [SleipnirMethod("Open")]
    public string Open() { Interlocked.Increment(ref OpenCalls); return "open"; }

    [SleipnirMethod("AllowedPolicy")]
    [SleipnirAuthorise(Policy = "allowed")]
    public string AllowedPolicy() { Interlocked.Increment(ref AllowedCalls); return "allowed"; }

    [SleipnirMethod("DeniedPolicy")]
    [SleipnirAuthorise(Policy = "denied")]
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
// [SleipnirExample] population, self-referencing type (cycle-safe), and bare Task -> void.

[SleipnirController("DiscoveryEdge")]
public class DiscoveryEdgeCasesController
{
    // --- set kinds -----------------------------------------------------------

    [SleipnirMethod("EchoHashSet")]
    public HashSet<string> EchoHashSet(HashSet<string> values) => values;

    [SleipnirMethod("EchoSortedSet")]
    public SortedSet<int> EchoSortedSet(SortedSet<int> values) => values;

    [SleipnirMethod("EchoDtoSet")]
    public HashSet<TestDto> EchoDtoSet(HashSet<TestDto> values) => values;

    // --- scalar "any" (object / JSON-DOM) ------------------------------------

    [SleipnirMethod("EchoObject")]
    public object EchoObject(object value) => value;

    [SleipnirMethod("EchoJsonElement")]
    public System.Text.Json.JsonElement EchoJsonElement(System.Text.Json.JsonElement el) => el;

    [SleipnirMethod("EchoJsonNode")]
    public System.Text.Json.Nodes.JsonNode EchoJsonNode(System.Text.Json.Nodes.JsonNode node) => node;

    // --- Nullable<T> value-type unwrap ------------------------------------------

    [SleipnirMethod("EchoNullableInt")]
    public int? EchoNullableInt(int? value) => value;

    [SleipnirMethod("EchoNullableGuid")]
    public Guid? EchoNullableGuid(Guid? value) => value;

    // --- native arrays -------------------------------------------------------

    [SleipnirMethod("EchoLongArray")]
    public long[] EchoLongArray(long[] values) => values;

    [SleipnirMethod("EchoDtoArray")]
    public TestDto[] EchoDtoArray(TestDto[] values) => values;

    // --- nested collections ------------------------------------------

    [SleipnirMethod("MakeNestedList")]
    public List<List<int>> MakeNestedList() => new();

    [SleipnirMethod("MakeMapOfLists")]
    public Dictionary<string, List<int>> MakeMapOfLists() => new();

    [SleipnirMethod("MakeSetOfArrays")]
    public HashSet<string[]> MakeSetOfArrays() => new();

    // --- default value present (counterpart to the absent-default test) ---------

    [SleipnirMethod("EchoWithDefault")]
    public int EchoWithDefault(int x = 42) => x;

    [SleipnirMethod("EchoStringDefault")]
    public string EchoStringDefault(string s = "hi") => s;

    // --- enum with a byte underlying type (Convert.ChangeType path) ------------------

    [SleipnirMethod("EchoByteFlag")]
    public ByteFlag EchoByteFlag(ByteFlag value) => value;

    // --- [SleipnirExample] population ---------------------------------------------

    [SleipnirMethod("MakeExampled")]
    public ExampledDto MakeExampled() => new();

    // --- self-referencing type (cycle-safe placeholder) ---------------

    [SleipnirMethod("MakeNode")]
    public TreeNode MakeNode(int v) => new() { Value = v };

    // --- bare Task -> void (Task without <T>) ------------------------------------

    [SleipnirMethod("Fire")]
    public async Task Fire() { await Task.Delay(1); }

    // --- further scalar names (direct name assertion) ---------------------

    [SleipnirMethod("EchoLong")]
    public long EchoLong(long v) => v;

    [SleipnirMethod("EchoBool")]
    public bool EchoBool(bool v) => v;

    [SleipnirMethod("EchoDouble")]
    public double EchoDouble(double v) => v;

    [SleipnirMethod("EchoDecimal")]
    public decimal EchoDecimal(decimal v) => v;

    [SleipnirMethod("EchoTimeSpan")]
    public TimeSpan EchoTimeSpan(TimeSpan v) => v;

    [SleipnirMethod("EchoDateTimeOffset")]
    public DateTimeOffset EchoDateTimeOffset(DateTimeOffset v) => v;

    [SleipnirMethod("EchoDateTime")]
    public DateTime EchoDateTime(DateTime v) => v;

    [SleipnirMethod("EchoGuid")]
    public Guid EchoGuid(Guid v) => v;
}

/// <summary>Enum with a byte underlying type — exercises the Convert.ChangeType path in BuildEnumTypeMeta.</summary>
public enum ByteFlag : byte { None = 0, A = 1, B = 2 }

/// <summary>Type with [SleipnirExample] — discovery populates Example from the JSON string.</summary>
[SleipnirExample("""{"Id":7,"Name":"sample"}""")]
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