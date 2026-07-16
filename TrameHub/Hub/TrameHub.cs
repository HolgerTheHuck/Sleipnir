using TrameCore.Services;
using Microsoft.AspNetCore.SignalR;

namespace TrameHub.Hub
{

    public class TrameHub(TrameCore.Services.ITrameCore service) : Microsoft.AspNetCore.SignalR.Hub
    {
        public async Task<TrameResponse?> DoWork(TrameRequest request)
        {
            var user = Context.UserIdentifier;
            return await service.InvokeDi(request, Context.GetHttpContext(), Context.ConnectionAborted);
        }

        public async Task<IEnumerable<TrameResponse>> DoWorkMany(TrameMultiRequest? request)
        {
            if (request == null)
            {
                return new List<TrameResponse>();
            }
            if (request.Requests == null)
            {
                return new List<TrameResponse>();
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
