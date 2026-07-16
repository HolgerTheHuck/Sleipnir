using TrameCore.Model.Messages.Mex;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TrameCore.Services
{
    public interface ITrameCore
    {
        void Register<T>();
        void Register(Type handler);

        /// <summary>
        /// Nimmt mehrere Requests entgegen und ruft sie nacheinander auf.
        /// </summary>
        Task<IEnumerable<TrameResponse?>> InvokeDi(
            IEnumerable<TrameRequest> requests,
            HttpContext? context,
            ExecutionMode mode = ExecutionMode.Parallel,
            CancellationToken ct = default);

        /// <summary>
        /// Nimmt einen einzelnen Request entgegen und führt ihn aus.
        /// </summary>
        Task<TrameResponse?> InvokeDi(
            TrameRequest request,
            HttpContext? context,
            CancellationToken ct = default);

        DiscoveryInfo GetDiscoveryInfo();

        /// <summary>
        /// North-Bound-Default-Deny (aus <see cref="TrameHub.Extensions.TrameOptions.RequireAuthentication"/>).
        /// Transporte lesen dies, um WebSocket-Upgrade und Discovery-Endpunkt zu gate-n.
        /// Siehe <c>SECURITY.md</c>.
        /// </summary>
        bool RequireAuthentication { get; }

        /// <summary>
        /// Max Requests pro Batch (0 = unbegrenzt; aus
        /// <see cref="TrameHub.Extensions.TrameOptions.MaximumBatchSize"/>). Transporte
        /// lesen dies für das frühe 400-Batch-Cap-Gate. Siehe <c>SECURITY.md</c>.
        /// </summary>
        int MaximumBatchSize { get; }
    }
}
