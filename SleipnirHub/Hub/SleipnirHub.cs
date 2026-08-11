using SleipnirCore.Services;
using Microsoft.AspNetCore.SignalR;

namespace SleipnirHub.Hub
{

    public class SleipnirHub(SleipnirCore.Services.ISleipnirCore service) : Microsoft.AspNetCore.SignalR.Hub
    {
        public async Task<SleipnirResponse?> DoWork(SleipnirRequest request)
        {
            var user = Context.UserIdentifier;
            return await service.InvokeDi(request, Context.GetHttpContext(), Context.ConnectionAborted);
        }

        public async Task<IEnumerable<SleipnirResponse>> DoWorkMany(SleipnirMultiRequest? request)
        {
            if (request == null)
            {
                return new List<SleipnirResponse>();
            }
            if (request.Requests == null)
            {
                return new List<SleipnirResponse>();
            }

            var result = await service.InvokeDi(
                request.Requests,
                Context.GetHttpContext(),
                request.Mode,
                Context.ConnectionAborted);
            return result;
        }
    }
}
