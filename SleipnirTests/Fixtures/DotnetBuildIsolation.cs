using System.Diagnostics;

namespace SleipnirTests.Fixtures;

/// <summary>
/// For tests that spawn <c>dotnet build</c> with redirected stdout/stderr. By default the build
/// leaves long-lived helpers behind — reusable MSBuild worker nodes (idle timeout 15 min), the
/// MSBuild server and the shared Roslyn compiler server (VBCSCompiler). They inherit the redirected
/// pipe handles, so <c>ReadToEndAsync</c> on the child's output does not finish when the build
/// exits but only when those helpers time out — every such test took ~15 minutes.
/// Switching them off for the spawned process tree keeps the pipes owned by the build alone.
/// </summary>
internal static class DotnetBuildIsolation
{
    public static ProcessStartInfo Apply(ProcessStartInfo psi)
    {
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";   // no lingering worker nodes
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"; // no MSBuild server
        psi.Environment["UseSharedCompilation"] = "false";  // no VBCSCompiler server (MSBuild property via env)
        return psi;
    }
}
