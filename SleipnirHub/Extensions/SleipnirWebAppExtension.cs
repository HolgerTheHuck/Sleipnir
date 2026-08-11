
namespace SleipnirHub.Extensions
{
    public static class SleipnirWebAppExtension
    {
        public static HubEndpointConventionBuilder AddSleipnir(
            this IEndpointRouteBuilder endpoints)
        {
            if (endpoints == null)
                throw new ArgumentNullException(nameof(endpoints));

            return endpoints.MapHub<Hub.SleipnirHub>("/sleipnirhub");
        }
    }
}
