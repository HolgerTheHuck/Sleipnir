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

        var regenerated = RegenerateContract(opts.AssemblyPath);
        var regeneratedJson = regenerated.Trim();

        // No committed contract yet: write it and succeed (first-time wiring).
        if (!File.Exists(opts.ContractPath))
        {
            File.WriteAllText(opts.ContractPath, regeneratedJson);
            Console.WriteLine($"[sleipnir-export] created committed contract at '{opts.ContractPath}'.");
            return ExitOk;
        }

        var committedJson = File.ReadAllText(opts.ContractPath).Trim();

        // Content comparison normalizes the incidental controllers-array order on both sides
        // (the live wire follows ConcurrentDictionary enumeration; the export sorts for
        // determinism — see below). Method/property order is metadata-stable, not normalized.
        var regenNode = NormalizeDiscovery(JsonNode.Parse(regeneratedJson)!);
        var committedNode = NormalizeDiscovery(JsonNode.Parse(committedJson)!);

        if (JsonNode.DeepEquals(regenNode, committedNode))
        {
            Console.WriteLine($"[sleipnir-export] drift-check passed: '{opts.ContractPath}' matches runtime discovery.");
            return ExitOk;
        }

        if (opts.Regen)
        {
            File.WriteAllText(opts.ContractPath, regeneratedJson);
            Console.WriteLine($"[sleipnir-export] regenerated committed contract at '{opts.ContractPath}'.");
            return ExitOk;
        }

        ReportDrift(regenNode, committedNode, opts);
        return ExitDrift;
    }

    /// <summary>Load the server assembly, reflect all [SleipnirController] types, build a SleipnirInvoker
    /// with a stub DI scope + null logger, register every controller, and serialize its discovery.
    /// Controllers are sorted by name for deterministic output (ConcurrentDictionary order is
    /// incidental and would make the committed file churn run-to-run).</summary>
    private static string RegenerateContract(string serverAssemblyPath)
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
        discovery.Controllers = discovery.Controllers.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        return JsonSerializer.Serialize(discovery, DiscoverySerialization.Options);
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

    /// <summary>Normalize a discovery payload by sorting the incidental controllers-array order,
    /// mirroring DiscoveryContractTests.NormalizeDiscovery so the export and the live-wire gate
    /// compare on equal footing.</summary>
    private static JsonNode NormalizeDiscovery(JsonNode root)
    {
        if (root is not JsonObject obj || !obj.TryGetPropertyValue("controllers", out var controllers))
            return root;
        if (controllers is not JsonArray arr) return root;
        var sorted = arr.OrderBy(c => c?["name"]?.GetValue<string>() ?? "", StringComparer.Ordinal);
        var newArr = new JsonArray();
        foreach (var c in sorted) newArr.Add(c!.DeepClone());
        obj.Remove("controllers");
        obj["controllers"] = newArr;
        return obj;
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

    private sealed record Options(string AssemblyPath, string ContractPath, bool Regen);

    private static Options? ParseArgs(string[] args)
    {
        string? assembly = null;
        string? contract = null;
        var regen = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assembly": assembly = Next(args, ref i); break;
                case "--contract": contract = Next(args, ref i); break;
                case "--regen": regen = true; break;
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
        return new Options(assembly!, contract!, regen);
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
        Console.Error.WriteLine("Exit codes: 0 = ok / regenerated, 1 = drift detected, 2 = tool error.");
    }
}