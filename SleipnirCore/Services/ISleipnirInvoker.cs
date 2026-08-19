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

        /// <summary>
        /// Phase R (resume): re-runs the same authorization a fresh subscribe runs, without
        /// re-invoking the controller method. The durable-subscription store records the
        /// controller/method of the ORIGINAL subscribe; on reconnect the subscription manager
        /// calls this to re-check that the caller is still authorized (a role revoked during the
        /// disconnect gap must not silently resume). Returns <c>null</c> when authorized, or the
        /// 401/403 (or 404 if the route vanished) <see cref="SleipnirResponse"/> to return to the
        /// client — and the caller tears down the durable subscription on a non-null result.
        /// </summary>
        Task<SleipnirResponse?> AuthorizeSubscribeAsync(
            string controller,
            string method,
            HttpContext? context);

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
