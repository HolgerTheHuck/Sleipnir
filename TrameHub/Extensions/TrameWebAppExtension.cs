
namespace TrameHub.Extensions
{
    public static class TrameWebAppExtension
    {
        public static HubEndpointConventionBuilder AddTrame(
            this IEndpointRouteBuilder endpoints)
        {
            if (endpoints == null)
                throw new ArgumentNullException(nameof(endpoints));

            return endpoints.MapHub<Hub.TrameHub>("/tramehub");
        }
    }
}
