namespace Sleipnir.Client.Linq;

/// <summary>
/// Declares a navigation edge on a contract DTO property so the <see cref="SleipnirQuery{TEntity}"/>
/// façade can eager-load it via the existing <c>@alias</c>/<c>dependencyMapping</c> wire (Tier 2).
/// This is the **client-side** attribute, emitted onto the contract DTO by <c>EmitContracts</c> from the
/// server-side <c>[SleipnirNavigation]</c> (in <c>SleipnirCommon</c>) through the discovery JSON — the
/// same server→wire→codegen→client split already used for <c>[SleipnirController]</c>→
/// <c>[SleipnirServiceContract]</c>. The two attributes are distinct types in distinct namespaces.
/// </summary>
/// <remarks>
/// <para>The navigation *selector* (<c>c =&gt; c.Kontakt</c>) is compile-checked — it identifies WHICH
/// property, so <c>Kontakt</c> must exist on <c>Customer</c>. <c>Fetch</c>/<c>Key</c>/<c>ChildKey</c>/
/// <c>Param</c> are strings (not C#-checked), but they are codegen-generated from the server model and
/// drift-checked against the contract at generation time, exactly as <c>[JsonPropertyName]</c> already is.
/// The split is deliberate: <b>which navigation</b> → compile-checked (selector lambda);
/// <b>how to fetch it</b> → drift-checked (codegen-generated strings).</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SleipnirNavigationAttribute : Attribute
{
    /// <summary>
    /// <c>"Controller.Method"</c> of the fetch method, e.g. <c>"Kontakt.GetByKontaktIds"</c>. The façade
    /// splits this at the last dot into controller + method for the consumer node's request.
    /// </summary>
    public string Fetch { get; set; } = string.Empty;

    /// <summary>
    /// The per-element key path on the PARENT (one element), as a **wire** name, e.g. <c>"kontaktId"</c>
    /// (a reference-navigation FK on the parent) or <c>"id"</c> (a collection-navigation parent PK). The
    /// façade composes the full result-relative JsonPath from the parent query's cardinality:
    /// collection root (Tier 2) → <c>$[*].{Key}</c>. NOT a wildcard string.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Optional: the child property (wire name) that joins back to the parent key. Convention defaults
    /// applied by the façade when omitted: <b>reference</b> navigation → child PK <c>"id"</c>;
    /// <b>collection</b> navigation → child FK <c>"{parentEntityName}Id"</c> (camelCase, e.g.
    /// <c>"kontaktId"</c>). Required only when the convention does not hold.
    /// </summary>
    public string? ChildKey { get; set; }

    /// <summary>
    /// The fetch method's parameter name that receives the key list, e.g. <c>"kontaktIds"</c>. The façade
    /// wires the parent's exported alias into this parameter as <c>@alias</c>. Required — codegen always
    /// emits it (validated at generation time against the fetch method's single collection parameter);
    /// a null/empty <c>Param</c> is a clear <c>.Build()</c> error.
    /// </summary>
    public string? Param { get; set; }
}