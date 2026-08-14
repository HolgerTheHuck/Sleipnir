using System;

namespace SleipnirCommon.Attribute
{
    /// <summary>
    /// Declares a navigation edge on a server DTO property so <c>SleipnirDiscoveryService</c> serializes
    /// it into the discovery JSON and the <c>sleipnir-linq</c> codegen re-emits it as the client-side
    /// <c>[SleipnirNavigation]</c> (in <c>Sleipnir.Client.Linq</c>) onto the generated contract DTO — the
    /// same server→wire→codegen→client split already used for <c>[SleipnirController]</c>→
    /// <c>[SleipnirServiceContract]</c>. The two attributes are distinct types in distinct namespaces;
    /// codegen translates server → wire JSON → client. This is the **server-side** (producer) half.
    /// </summary>
    /// <remarks>
    /// <para>The façade (<c>SleipnirQuery&lt;TEntity&gt;</c>) reads the emitted client attribute to
    /// eager-load the edge via the existing <c>@alias</c>/<c>dependencyMapping</c> wire (Tier 2).
    /// <c>Fetch</c>/<c>Key</c> are required; <c>ChildKey</c> and <c>Param</c> are optional and
    /// convention/codegen-inferred when omitted (see <c>LINQ_QUERY.md</c> §3).</para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class SleipnirNavigationAttribute : System.Attribute
    {
        /// <summary>
        /// <c>"Controller.Method"</c> of the fetch method, e.g. <c>"QueryChain.GetKontakte"</c>.
        /// The codegen splits this at the last dot into controller + method and validates the method
        /// exists with a matching collection parameter.
        /// </summary>
        public string Fetch { get; set; } = string.Empty;

        /// <summary>
        /// The per-element key path on the PARENT (one element), as a **wire** (camelCase) name, e.g.
        /// <c>"kontaktId"</c> (a reference-navigation FK on the parent) or <c>"id"</c> (a
        /// collection-navigation parent PK). The façade composes the full result-relative JsonPath from
        /// the parent query's cardinality. NOT a wildcard string.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Optional: the child property (wire name) that joins back to the parent key. Convention defaults
        /// applied by the façade when omitted: <b>reference</b> navigation → child PK <c>"id"</c>;
        /// <b>collection</b> navigation → child FK <c>"{parentEntityName}Id"</c> (camelCase). Required
        /// only when the convention does not hold.
        /// </summary>
        public string? ChildKey { get; set; }

        /// <summary>
        /// Optional: the fetch method's parameter name that receives the key list, e.g. <c>"kontaktIds"</c>.
        /// When omitted, the codegen infers it as the fetch method's single collection parameter
        /// (<c>List&lt;T&gt;</c>/<c>T[]</c>/<c>IEnumerable&lt;T&gt;</c>) and always emits a non-null
        /// <c>Param</c> on the client attribute. Required at codegen time after inference.
        /// </summary>
        public string? Param { get; set; }
    }
}