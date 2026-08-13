// sleipnir-linq — dotnet tool that generates the Sleipnir.Client.Linq service-contract interfaces
// (SleipnirContracts.g.cs) from a discovery contract (a file or a URL). A thin Exe wrapper over
// SleipnirCodegen.EmitContracts. Mirrors the exit-code convention of Sleipnir.Server.Codegen:
//   0 = ok, 1 = argument / discovery-shape error, 2 = tool (I/O) error.
using System.Text;
using Sleipnir.Codegen.Core;

namespace Sleipnir.Client.Linq.Codegen;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitArgError = 1;
    private const int ExitToolError = 2;

    private static async Task<int> Main(string[] args)
    {
        string discovery = "contract.sleipnir.json";
        string? outFile = null;
        bool toStdout = false;
        string? ns = null;
        string? baseUrl = null;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--discovery": discovery = Next(args, ref i); break;
                    case "--out": outFile = Next(args, ref i); break;
                    case "--stdout": toStdout = true; break;
                    case "--namespace": ns = Next(args, ref i); break;
                    case "--base-url": baseUrl = Next(args, ref i); break;
                    case "-h":
                    case "--help": return PrintHelp();
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return ExitArgError;
                }
            }
        }
        catch (InvalidOperationException ex) // missing value for a flag
        {
            Console.Error.WriteLine(ex.Message);
            return ExitArgError;
        }

        if (outFile is null && !toStdout)
        {
            Console.Error.WriteLine("Specify --out <path> or --stdout.");
            return ExitArgError;
        }

        string json;
        try
        {
            json = await ReadDiscoveryAsync(discovery);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read discovery from '{discovery}': {ex.Message}");
            return ExitToolError;
        }

        string source;
        try
        {
            source = SleipnirCodegen.EmitContracts(json, new EmitCsOptions { Namespace = ns, BaseUrl = baseUrl });
        }
        catch (DiscoveryShapeException ex)
        {
            Console.Error.WriteLine($"Discovery shape error: {ex.Message}");
            return ExitArgError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Emission failed: {ex.Message}");
            return ExitToolError;
        }

        if (toStdout)
        {
            await Console.Out.WriteAsync(source);
            return ExitOk;
        }

        try
        {
            var dir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            // BOM-less UTF-8 for byte parity with the source generator's output convention.
            await File.WriteAllTextAsync(outFile!, source, new UTF8Encoding(false));
            Console.Out.WriteLine($"Wrote {outFile}");
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write '{outFile}': {ex.Message}");
            return ExitToolError;
        }
    }

    /// <summary>Fetch the discovery JSON from an http(s) URL or read it from a file path.</summary>
    private static async Task<string> ReadDiscoveryAsync(string source)
    {
        if (Uri.IsWellFormedUriString(source, UriKind.Absolute)
            && (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            using var http = new HttpClient();
            return await http.GetStringAsync(source);
        }
        if (!File.Exists(source))
            throw new FileNotFoundException($"Discovery file not found: {source}", source);
        return await File.ReadAllTextAsync(source);
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new InvalidOperationException($"Missing value for {args[i]}.");
        i++;
        return args[i];
    }

    private static int PrintHelp()
    {
        Console.Out.WriteLine("sleipnir-linq — generate Sleipnir.Client.Linq service-contract interfaces.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: sleipnir-linq --discovery <url|file> [--out <path>|--stdout] [--namespace <ns>] [--base-url <url>]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("  --discovery <url|file>  Discovery source (file path or http(s) URL). Default: contract.sleipnir.json");
        Console.Out.WriteLine("  --out <path>            Write SleipnirContracts.g.cs to this path");
        Console.Out.WriteLine("  --stdout                Write the generated source to stdout");
        Console.Out.WriteLine("  --namespace <ns>        C# namespace (default Sleipnir.Linq.Contracts)");
        Console.Out.WriteLine("  --base-url <url>        Base URL hint rendered into the file header");
        return ExitOk;
    }
}