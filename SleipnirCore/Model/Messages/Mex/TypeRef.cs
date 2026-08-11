using System;

namespace SleipnirCore.Model.Messages.Mex
{
    /// <summary>
    /// Language-neutral type reference carried on the discovery wire. Replaces the prior
    /// .NET-flavored type-name strings (e.g. "List&lt;Order&gt;", "Dictionary&lt;string,int&gt;")
    /// with a structured, discriminated type model any client generator or non-C# server can
    /// consume. See docs/discovery-schema.md for the authoritative specification.
    /// </summary>
    public class TypeRef
    {
        /// <summary>
        /// The discriminator. One of: scalar, array, set, map, stream, event, ref, opaque, void.
        /// (event = Phase 3: <c>IObservable&lt;T&gt;</c>, server-pushed subscription.)
        /// </summary>
        public string Kind { get; set; } = "opaque";

        /// <summary>Scalar only: a name from the fixed scalar table (docs/discovery-schema.md §3).</summary>
        public string? Name { get; set; }

        /// <summary>array | set | stream: the element's TypeRef.</summary>
        public TypeRef? Element { get; set; }

        /// <summary>map: the key's TypeRef.</summary>
        public TypeRef? Key { get; set; }

        /// <summary>map: the value's TypeRef.</summary>
        public TypeRef? Value { get; set; }

        /// <summary>ref: the opaque key into DiscoveryInfo.Types this usage resolves to.</summary>
        public string? Ref { get; set; }

        /// <summary>opaque: diagnostic hint of the unmodelled framework/BCL type. Never identity.</summary>
        public string? NativeName { get; set; }

        /// <summary>
        /// Occurrence-level nullability from C# nullable reference types. Absent (null) means
        /// not-nullable. stream and void are never nullable.
        /// </summary>
        public bool? Nullable { get; set; }
    }
}