using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TrameCore.Model.Messages.Mex
{
    public class DiscoveryInfo
    {
        /// <summary>Schema version (additive-only). See docs/discovery-schema.md §11.</summary>
        public string DiscoveryVersion { get; set; } = "1";
        public List<ControllerMeta> Controllers { get; set; } = new List<ControllerMeta>();
        public Dictionary<string, TypeMeta> Types { get; set; } = new Dictionary<string, TypeMeta>(StringComparer.OrdinalIgnoreCase);
    }
}
