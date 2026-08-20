using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SleipnirClient.Sleipnir
{
    public interface ISleipnirClient
    {
        Task<SleipnirResponse?> Call(SleipnirRequest request, CancellationToken ct = default);
        Task<T?> Call<T>(SleipnirRequest? request, CancellationToken ct = default);
        Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default);
        /// <summary>
        /// Sendet einen SleipnirRequest und liefert das binäre <c>Content</c>-Feld
        /// (z. B. für <c>byte[]</c>-Rückgaben). Wirft <c>SleipnirException</c> bei
        /// nicht-erfolgreichem Call.
        /// </summary>
        Task<byte[]?> CallBinary(SleipnirRequest? request, CancellationToken ct = default);

        /// <summary>
        /// Subscribes to a server-push event (<c>[SleipnirEvent]</c>) and returns an
        /// <see cref="SleipnirSubscription{T}"/> (an <c>IObservable&lt;T&gt;</c> + <c>IDisposable</c>).
        /// The <paramref name="request"/> carries controller/method/params (built via
        /// <see cref="SleipnirCall"/>); the active event backend unpacks it. A non-event backend
        /// (e.g. REST, which is calls-only) throws <see cref="NotImplementedException"/>.
        /// <para>
        /// <b>Cross-transport resume.</b> A durable <c>subscriptionId</c> + <c>lastEventId</c> obtained
        /// on one transport resume live on another (the server-side store is process-wide). Use
        /// <see cref="ResumeAsync{T}"/> to resume after a transport switch.
        /// </para>
        /// </summary>
        Task<SleipnirSubscription<T>> SubscribeAsync<T>(SleipnirRequest? request, ResumePolicy? resumePolicy = null, CancellationToken ct = default);

        /// <summary>
        /// Resumes a durable event subscription by <paramref name="subscriptionId"/> + the
        /// <paramref name="lastEventId"/> cursor — the server replays the gap from its disconnect
        /// buffer, then continues live. Cross-transport: a <c>subscriptionId</c> created over
        /// WebSocket (or another SSE/SignalR stream) is resumable here. Not every backend supports
        /// resuming INTO it (e.g. WebSocket needs the original controller/method); such backends
        /// throw — switch to a backend that does (SSE / SignalR).
        /// </summary>
        Task<SleipnirSubscription<T>> ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy? resumePolicy = null, CancellationToken ct = default);
    }
}
