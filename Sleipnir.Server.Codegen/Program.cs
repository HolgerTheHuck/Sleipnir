// Server-side Sleipnir contract export + drift-check.
//
// The discovery JSON is the standard contract. A server that ships a contract must keep it in
// sync with its runtime discovery — otherwise the contract lies (the wsdl.exe trap). This tool
// regenerates the contract in-process from a built server assembly (load it, reflect the
// [SleipnirController] types, build a SleipnirInvoker, call GetDiscoveryInfo, serialize with the same
// DiscoverySerialization.Options the REST endpoint uses) and either:
//   - drift-checks it against a committed contract.sleipnir.json and fails (exit 1) on mismatch, or
//   - regenerates the committed file when --regen is passed (used by SLEIPNIR_REGEN_GOLDEN=1).
//
// It runs in its own process (invoked by the Sleipnir.Server.Codegen MSBuild target), so loading the
// server assembly and its SleipnirCore/SleipnirCommon dependencies is isolated from the MSBuild process
// — no version collisions, no locked files. The tool and the server share the same SleipnirCore build
// in this repo, so the server's [SleipnirController] references bind to the already-loaded SleipnirCore;
// an AssemblyResolve hook additionally probes the server's output directory for any deps the tool
// itself does not bring.
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SleipnirCore.Attributes;
using SleipnirCore.Model.Messages.Mex;
using SleipnirCore.Services;

namespace Sleipnir.Server.Codegen;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitDrift = 1;
    private const int ExitError = 2;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // Tool failure (not a drift) — distinct exit code so the MSBuild target can report it
            // as a tool error rather than a contract drift.
            Console.Error.WriteLine($"[sleipnir-export] error: {ex}");
            return ExitError;
        }
    }

    private static int Run(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return ExitError;

        var discovery = BuildDiscovery(opts.AssemblyPath);

        // Spike (P3.1): optional OpenAPI 3.1 side-export. Emitted whenever --openapi is passed,
        // independent of the drift verdict — tooling interop must not depend on contract state.
        if (opts.OpenApiPath is not null)
        {
            var openApiJson = OpenApiExporter.Export(discovery, opts.OpenApi);
            File.WriteAllText(opts.OpenApiPath, openApiJson);
            Console.WriteLine($"[sleipnir-export] wrote OpenAPI 3.1 document to '{opts.OpenApiPath}'.");
        }

        var regeneratedJson = JsonSerializer.Serialize(discovery, DiscoverySerialization.Options).Trim();

        // No committed contract yet: write it and succeed (first-time wiring).
        if (!File.Exists(opts.ContractPath))
        {
            File.WriteAllText(opts.ContractPath, regeneratedJson);
            Console.WriteLine($"[sleipnir-export] created committed contract at '{opts.ContractPath}'.");
            return ExitOk;
        }

        var committedJson = File.ReadAllText(opts.ContractPath).Trim();

        // Content comparison normalizes every order-incidental array on both sides: controllers
        // (live wire follows ConcurrentDictionary enumeration), methods, and contract-type
        // properties (reflection order is metadata-stable only per toolchain). The export sorts
        // these for determinism too — see RegenerateContract. Parameter order is signature order
        // and is NOT normalized (positional `num` binding resolves by index).
        var regenNode = NormalizeDiscovery(JsonNode.Parse(regeneratedJson)!);
        var committedNode = NormalizeDiscovery(JsonNode.Parse(committedJson)!);

        if (JsonNode.DeepEquals(regenNode, committedNode) && !opts.Regen)
        {
            Console.WriteLine($"[sleipnir-export] drift-check passed: '{opts.ContractPath}' matches runtime discovery.");
            return ExitOk;
        }

        if (opts.Regen)
        {
            // --regen always rewrites — even when the comparison passes, e.g. an order-only
            // difference between an older (unsorted) committed file and the current sorted
            // canonical form. Content-equal is not shape-equal: regen migrates the file.
            File.WriteAllText(opts.ContractPath, regeneratedJson);
            Console.WriteLine($"[sleipnir-export] regenerated committed contract at '{opts.ContractPath}'.");
            return ExitOk;
        }

        ReportDrift(regenNode, committedNode, opts);
        return ExitDrift;
    }

    /// <summary>Load the server assembly, reflect all [SleipnirController] types, build a SleipnirInvoker
    /// with a stub DI scope + null logger, register every controller, and return the discovery.
    /// All order-incidental collections (controllers, methods, contract-type properties, enum
    /// members) are sorted by name for deterministic output — see the comment in the method body.</summary>
    private static DiscoveryInfo BuildDiscovery(string serverAssemblyPath)
    {
        var serverDir = Path.GetDirectoryName(serverAssemblyPath)
            ?? throw new DirectoryNotFoundException("Could not resolve server assembly directory.");
        var serverAsm = Assembly.LoadFrom(serverAssemblyPath);
        // Probe the server's output directory for any dependency the tool did not already load
        // (the shared SleipnirCore/SleipnirCommon are already loaded from the tool's own build; this
        // catches server-only deps).
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => ResolveFromDir(e.Name, serverDir);

        var controllerTypes = DiscoverControllers(serverAsm, serverDir);

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SleipnirInvoker>.Instance);

        foreach (var t in controllerTypes)
            invoker.Register(t);

        var discovery = invoker.GetDiscoveryInfo();

        // Deterministic file output. Arrays whose order carries no meaning are sorted by name so
        // the committed contract.sleipnir.json only ever churns on REAL contract changes — a moved
        // C# member (or a reflection-order shift across toolchain upgrades) must not produce an
        // unreadable "everything moved, nothing changed" git diff. Exception: parameters keep
        // SIGNATURE order — positional binding (JSON-RPC `num`) resolves by index, so their
        // sequence is contract-relevant and must not be sorted.
        //   - controllers: by name (ConcurrentDictionary enumeration is incidental).
        //   - methods: by method name (reflection order is metadata-stable only per toolchain).
        //   - types / properties / enum members: keys and arrays sorted by name.
        discovery.Controllers = discovery.Controllers.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        foreach (var controller in discovery.Controllers)
            controller.Methods.Sort((a, b) => string.CompareOrdinal(a.MethodName, b.MethodName));
        discovery.Types = new Dictionary<string, TypeMeta>(
            discovery.Types.OrderBy(kvp => kvp.Key, StringComparer.Ordinal),
            discovery.Types.Comparer);
        foreach (var type in discovery.Types.Values)
        {
            type.Properties.Sort((a, b) => string.CompareOrdinal(a.PropertyName, b.PropertyName));
            if (type.Members is not null)
                type.Members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        // Vacuous-green guard: a server that ships a contract.sleipnir.json is expected to expose
        // controllers. An EMPTY discovery is almost always a tool/load failure (e.g. the export tool
        // running on a runtime that cannot reflect the server assembly — the 1.1.2 net10 regression,
        // where a net8-pinned tool loaded a net10 server assembly and silently found nothing), not an
        // intentional empty contract. Without this guard the drift-check passes vacuously
        // (empty == empty) and the broken contract ships unnoticed. Fail loudly as a tool error (exit 2)
        // so the build breaks instead of going green on an empty contract.
        if (discovery.Controllers.Count == 0)
        {
            throw new InvalidOperationException(
                $"Regenerated contract has 0 [SleipnirController] types from '{serverAssemblyPath}'. " +
                "A server that ships a contract.sleipnir.json is expected to expose controllers; an empty " +
                "discovery is almost certainly a tool/load failure (e.g. the export tool running on a " +
                "runtime that cannot reflect the server assembly), not an intentional empty contract. " +
                "Aborting so the drift-check fails loudly instead of passing vacuously (empty == empty).");
        }

        return discovery;
    }

    private static List<Type> DiscoverControllers(Assembly serverAsm, string serverDir)
    {
        var found = new Dictionary<string, Type>(StringComparer.Ordinal);
        // Scan the server assembly plus every other assembly in its output directory that defines
        // [SleipnirController] types. Scoped to the server dir (NOT AppDomain-wide) so that an MSBuild
        // run exporting multiple servers never cross-pollinates controllers — the collision lesson
        // from DiscoveryContractTests (two [SleipnirController("Customer")] in one AppDomain).
        foreach (var asm in LoadAssembliesIn(serverAsm, serverDir))
        {
            foreach (var t in SafeGetTypes(asm))
            {
                if (!t.IsClass || t.IsAbstract) continue;
                if (t.GetCustomAttribute<SleipnirControllerAttribute>() is null) continue;
                // AutoDiscover=false controllers are excluded from the bulk auto-discovery scans in
                // AddSleipnir/UseSleipnir/FromAssemblies; a server that exposes them does so via an
                // explicit Register<T>(). The export cannot know which opt-outs the server actually
                // registered, so it registers every [SleipnirController] type it can see — matching a
                // server that auto-discovers AND explicitly registers its opt-outs. (The
                // out-of-process DiscoveryContractTests gate catches any mismatch vs the live wire.)
                found[t.FullName!] = t;
            }
        }
        return found.Values.ToList();
    }

    private static IEnumerable<Assembly> LoadAssembliesIn(Assembly serverAsm, string serverDir)
    {
        yield return serverAsm;
        foreach (var dll in Directory.EnumerateFiles(serverDir, "*.dll"))
        {
            if (Path.GetFileName(dll).Equals(Path.GetFileName(serverAsm.Location), StringComparison.OrdinalIgnoreCase))
                continue;
            Assembly? asm = null;
            try { asm = Assembly.LoadFrom(dll); }
            catch { /* native, framework-signed we cannot load, or already loaded under a different identity — skip */ }
            if (asm is not null) yield return asm;
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types fail to load when a transitive dep is missing; keep the ones that did.
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static Assembly? ResolveFromDir(string? requestedName, string serverDir)
    {
        if (string.IsNullOrEmpty(requestedName)) return null;
        var an = new AssemblyName(requestedName);
        var candidate = Path.Combine(serverDir, an.Name + ".dll");
        if (!File.Exists(candidate)) return null;
        try { return Assembly.LoadFrom(candidate); }
        catch { return null; }
    }

    /// <summary>Normalize a discovery payload by sorting the order-incidental arrays —
    /// <c>controllers</c> by name, each controller's <c>methods</c> by method name, and each
    /// contract type's <c>properties</c> by property name — mirroring
    /// DiscoveryContractTests.NormalizeDiscovery so the export and the live-wire gate
    /// compare on equal footing.</summary>
    private static JsonNode NormalizeDiscovery(JsonNode root)
    {
        if (root is not JsonObject obj)
            return root;

        if (obj["controllers"] is JsonArray controllers)
        {
            SortArrayBy(controllers, c => c?["name"]?.GetValue<string>() ?? "");
            foreach (var controller in controllers)
            {
                if (controller is JsonObject co && co["methods"] is JsonArray methods)
                    SortArrayBy(methods, m => m?["methodName"]?.GetValue<string>() ?? "");
            }
        }

        if (obj["types"] is JsonObject types)
        {
            foreach (var type in types)
            {
                if (type.Value is JsonObject to && to["properties"] is JsonArray properties)
                    SortArrayBy(properties, p => p?["propertyName"]?.GetValue<string>() ?? "");
            }
        }

        return obj;
    }

    /// <summary>Sort a JSON array of objects by a string key, in place. The sort key extraction
    /// never throws: nodes without the key sort as <c>""</c> (first), matching the pre-existing
    /// normalization behavior.</summary>
    private static void SortArrayBy(JsonArray array, Func<JsonNode?, string> key)
    {
        var sorted = array.OrderBy(key, StringComparer.Ordinal).ToArray();
        array.Clear();
        foreach (var node in sorted) array.Add(node!);
    }

    private static void ReportDrift(JsonNode regenNode, JsonNode committedNode, Options opts)
    {
        // All output goes to stdout so the MSBuild <Exec ConsoleToMSBuild> captures it into the
        // ConsoleOutput property and surfaces it in the build error (stderr is not captured by
        // ConsoleToMSBuild and would be lost).
        Console.WriteLine($"[sleipnir-export] contract drift detected for '{opts.ContractPath}'.");
        Console.WriteLine("The committed contract.sleipnir.json does not match the server's runtime discovery.");
        Console.WriteLine("Either update the server's [SleipnirController]/[SleipnirMethod] declarations to match the");
        Console.WriteLine("committed contract, or regenerate the contract if the change is intentional:");
        Console.WriteLine($"    SLEIPNIR_REGEN_GOLDEN=1 dotnet build  (regenerates {opts.ContractPath})");
        Console.WriteLine();
        Console.WriteLine("--- regenerated (normalized, pretty) ---");
        Console.WriteLine(regenNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("--- committed (normalized, pretty) ---");
        Console.WriteLine(committedNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Options(string AssemblyPath, string ContractPath, bool Regen)
    {
        /// <summary>When set, an OpenAPI 3.1 document is exported alongside the drift-check (P3.1 spike).</summary>
        public string? OpenApiPath { get; init; }
        public OpenApiExporter.Options OpenApi { get; init; } = new();
    }

    private static Options? ParseArgs(string[] args)
    {
        string? assembly = null;
        string? contract = null;
        string? openApi = null;
        string? openApiTitle = null;
        string? openApiServer = null;
        var regen = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assembly": assembly = Next(args, ref i); break;
                case "--contract": contract = Next(args, ref i); break;
                case "--regen": regen = true; break;
                case "--openapi": openApi = Next(args, ref i); break;
                case "--openapi-title": openApiTitle = Next(args, ref i); break;
                case "--openapi-server": openApiServer = Next(args, ref i); break;
                case var h when h == "-h" || h == "--help" || h == "/?":
                    PrintHelp();
                    return null;
            }
        }
        if (string.IsNullOrEmpty(assembly) || string.IsNullOrEmpty(contract))
        {
            Console.Error.WriteLine("[sleipnir-export] missing required --assembly <path> and/or --contract <path>.");
            PrintHelp();
            return null;
        }
        return new Options(assembly!, contract!, regen)
        {
            OpenApiPath = openApi,
            OpenApi = new OpenApiExporter.Options
            {
                Title = openApiTitle ?? "Sleipnir API",
                ServerUrl = openApiServer,
            },
        };
    }

    private static string? Next(string[] args, ref int i)
        => i + 1 < args.Length ? args[++i] : null;

    private static void PrintHelp()
    {
        Console.Error.WriteLine("Sleipnir contract export + drift-check.");
        Console.Error.WriteLine("Usage: Sleipnir.Server.Codegen --assembly <server.dll> --contract <contract.sleipnir.json> [--regen]");
        Console.Error.WriteLine("  --assembly  Path to the built server assembly (its output dir supplies deps).");
        Console.Error.WriteLine("  --contract  Path to the committed contract.sleipnir.json to drift-check / regenerate.");
        Console.Error.WriteLine("  --regen     Overwrite the committed contract instead of failing on drift.");
        Console.Error.WriteLine("  --openapi <path>         Also export an OpenAPI 3.1 document (P3.1 spike).");
        Console.Error.WriteLine("  --openapi-title <text>   Info title for the OpenAPI document (default: Sleipnir API).");
        Console.Error.WriteLine("  --openapi-server <url>   Server base URL recorded in the OpenAPI document.");
        Console.Error.WriteLine("Exit codes: 0 = ok / regenerated, 1 = drift detected, 2 = tool error.");
    }
}