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
    }
}
