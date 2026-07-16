using System.Linq;
using System.Reflection;

// Lade alle SignalR-MessagePack-relevanten Assemblies aus dem NuGet-Cache.
var pkgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget", "packages", "microsoft.aspnetcore.signalr.protocols.messagepack", "8.0.13", "lib", "net8.0");
foreach (var dll in Directory.GetFiles(pkgDir, "*.dll"))
{
    try { Assembly.LoadFrom(dll); } catch { }
}

foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(x => x.GetName().Name.Contains("MessagePack") || x.GetName().Name.Contains("SignalR")))
{
    Console.WriteLine($"ASM: {a.GetName().Name} v{a.GetName().Version}");
    foreach (var t in a.GetTypes().Where(t => t.IsPublic))
    {
        if (t.Name.Contains("Options") || t.Name.Contains("Extensions"))
        {
            Console.WriteLine($"  TYPE: {t.FullName}");
            foreach (var p in t.GetProperties())
                Console.WriteLine($"    PROP {p.Name} : {p.PropertyType.FullName}");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.Contains("MessagePack")))
                Console.WriteLine($"    METH {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName))})");
        }
    }
}