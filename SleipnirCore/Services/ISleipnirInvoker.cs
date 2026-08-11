using SleipnirCore.Model.Messages.Mex;
using SleipnirCommon.Models;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SleipnirCore.Services
{
    public interface ISleipnirCore
    {
        void Register<T>();
        void Register(Type handler);

        /// <summary>
        /// Nimmt mehrere Requests entgegen und ruft sie nacheinander auf.
        /// </summary>
        Task<IEnumerable<SleipnirResponse?>> InvokeDi(
            IEnumerable<SleipnirRequest> requests,
            HttpContext? context,
            ExecutionMode mode = ExecutionMode.Parallel,
            CancellationToken ct = default);

        /// <summary>
        /// Nimmt einen einzelnen Request entgegen und führt ihn aus.
        /// </summary>
        Task<SleipnirResponse?> InvokeDi(
            SleipnirRequest request,
            HttpContext? context,
            CancellationToken ct = default);

        /// <summary>
        /// Phase 3 (Events): Löst eine Subscribe-Methode auf (Auth + Parameter-Binding),
        /// führt sie aus und gibt das rohe <see cref="IObservable{T}"/> zurück (statt es
        /// zu serialisieren). Der Aufrufer (i.d.R. der WS-Subscription-Manager) subscribt
        /// darauf und pusht jedes Element als Event-Frame. Siehe
        /// <c>docs/design/phase-3-events.md</c>.
        /// </summary>
        /// <returns><see cref="SleipnirSubscribeResult"/> — Error oder Observable.</returns>
        Task<SleipnirSubscribeResult> SubscribeAsync(
            SleipnirRequest request,
            HttpContext? context,
            CancellationToken ct = default);

        DiscoveryInfo GetDiscoveryInfo();

        /// <summary>
        /// North-Bound-Default-Deny (aus <see cref="SleipnirHub.Extensions.SleipnirOptions.RequireAuthentication"/>).
        /// Transporte lesen dies, um WebSocket-Upgrade und Discovery-Endpunkt zu gate-n.
        /// Siehe <c>SECURITY.md</c>.
        /// </summary>
        bool RequireAuthentication { get; }

        /// <summary>
        /// Max Requests pro Batch (0 = unbegrenzt; aus
        /// <see cref="SleipnirHub.Extensions.SleipnirOptions.MaximumBatchSize"/>). Transporte
        /// lesen dies für das frühe 400-Batch-Cap-Gate. Siehe <c>SECURITY.md</c>.
        /// </summary>
        int MaximumBatchSize { get; }
    }
}
