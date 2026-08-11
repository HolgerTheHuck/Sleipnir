using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SleipnirDeveloperUi;

/// <summary>
/// Einzeiler-Einstiegspunkt für die Sleipnir-Developer-UI aus einem ASP.NET-Host.
/// Mappt einen Redirect (Default <c>/Sleipnir</c>) auf die SPA-Index unter
/// <c>/developer-static/developer/index.html</c>.
/// </summary>
/// <remarks>
/// Die Assets werden über den Static-Web-Assets-Mechanismus des Razor-SDK
/// ausgeliefert (<c>StaticWebAssetBasePath=developer-static</c>); der Host muss
/// <c>UseStaticWebAssets()</c> (Development) bzw. den Publish-Manifest-Pfad und
/// <c>UseStaticFiles()</c> aktiviert haben. Ein Nachbarverzeichnis-Hack
/// (PhysicalFileProvider auf ../SleipnirDeveloperUi/wwwroot) ist NICHT nötig —
/// das ist nur bei ProjectReference-Dev-Setups als Workaround entstanden und
/// funktioniert bei NuGet-Konsum nicht.
/// </remarks>
public static class DeveloperUiEndpointExtensions
{
    /// <param name="entryPath">Öffentliche Einstiegs-URL, die auf die SPA weiterleitet.</param>
    public static IEndpointRouteBuilder MapSleipnirDeveloperUi(this IEndpointRouteBuilder endpoints, string entryPath = "/Sleipnir")
    {
        endpoints.MapGet(entryPath, context =>
        {
            context.Response.Redirect("/developer-static/developer/index.html");
            return Task.CompletedTask;
        });
        return endpoints;
    }
}