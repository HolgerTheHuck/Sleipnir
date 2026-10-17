using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SleipnirCore.Model.Messages.Mex
{
    public class DiscoveryInfo
    {
        /// <summary>Schema version (additive-only). See docs/discovery-schema.md §11.</summary>
        public string DiscoveryVersion { get; set; } = "1";

        /// <summary>
        /// Identity of this contract: <c>sha256:&lt;hex&gt;</c> over its canonical form (objects key-sorted,
        /// controllers/methods/types/properties/enum members sorted by name, parameters in signature
        /// order; <c>documentation</c>, <c>example</c> and <c>deprecated</c> excluded — they do not change
        /// the wire). Generated clients embed it and send it back (header <c>Sleipnir-Contract</c> /
        /// query <c>sleipnir-contract</c>), so the server can tell which contract each caller was built
        /// against. Additive: <c>discoveryVersion</c> stays "1". See CONTRACT_EVOLUTION.md §7.
        /// </summary>
        public string? ContractHash { get; set; }
        public List<ControllerMeta> Controllers { get; set; } = new List<ControllerMeta>();
        public Dictionary<string, TypeMeta> Types { get; set; } = new Dictionary<string, TypeMeta>(StringComparer.OrdinalIgnoreCase);
    }
}
