using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrameClient.Trame
{
    public interface ITrameClient
    {
        Task<TrameResponse?> Call(TrameRequest request, CancellationToken ct = default);
        Task<T?> Call<T>(TrameRequest? request, CancellationToken ct = default);
        Task<IEnumerable<TrameResponse?>?> Call(TrameMultiRequest? request, CancellationToken ct = default);
        /// <summary>
        /// Sendet einen TrameRequest und liefert das binäre <c>Content</c>-Feld
        /// (z. B. für <c>byte[]</c>-Rückgaben). Wirft <c>TrameException</c> bei
        /// nicht-erfolgreichem Call.
        /// </summary>
        Task<byte[]?> CallBinary(TrameRequest? request, CancellationToken ct = default);
    }
}
