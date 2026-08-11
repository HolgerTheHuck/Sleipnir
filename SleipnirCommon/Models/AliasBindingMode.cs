namespace SleipnirCommon.Models;

/// <summary>
/// Controls how an extracted <c>@alias</c> fragment is bound to the consuming
/// parameter's declared CLR type. The binding pipeline (extract → inject → bind)
/// and the four runtime outcomes are specified in
/// <c>DEPENDENCY_BINDING.md</c>; this switch only affects the object→object
/// silent-default row.
/// </summary>
public enum AliasBindingMode
{
    /// <summary>
    /// Default. <see cref="System.Text.Json"/> duck-typing: overlapping properties bind,
    /// extra provider properties are ignored, <b>missing</b> properties are silently
    /// defaulted (value types → 0/false/MinValue, reference types → null). The dangerous
    /// direction (consumer declares a property the fragment lacks) returns 2xx with
    /// default-filled fields. Powerful and convenient — the subset fan-out pattern
    /// (one whole-object alias feeding several narrower consumers) works here.
    /// </summary>
    Weak,

    /// <summary>
    /// Fail-loud typing for teams that treat silent defaults as a correctness risk.
    /// The fragment must <b>fully cover</b> the consuming object type: every public
    /// read-write property the consumer declares must be present in the fragment JSON
    /// (matched case-insensitively, as <see cref="System.Text.Json"/> reads). A missing
    /// property is a <b>400</b> instead of a silent default. Cross-kind mismatches are
    /// 400 in both modes (System.Text.Json throws regardless). The subset fan-out
    /// (consumer ⊆ fragment) still binds, because nothing is missing; only the
    /// reverse direction (consumer ⊋ fragment) is rejected. Strict applies to
    /// <c>@alias</c>-sourced parameters only — literals sent deliberately by the
    /// caller are not re-checked — and checks the <b>top level only</b>: a missing
    /// property inside a nested object is still silently defaulted.
    /// </summary>
    Strict,

    /// <summary>
    /// Maximum fail-loud typing ("paranoid"). Superset of <see cref="Strict"/> that closes
    /// its two remaining gaps: (a) it checks <b>every</b> parameter — <c>@alias</c>-sourced
    /// <i>and</i> literals the caller sent — and (b) it checks <b>recursively</b>, descending
    /// into nested object properties and array elements. Every public read-write property
    /// the consumer type declares, at any depth, must be present in the fragment JSON, else
    /// <b>400</b>. Cross-kind is still 400 via System.Text.Json (paranoid does not re-check
    /// it); widening (int→long) is still accepted; the subset fan-out (consumer ⊆ fragment)
    /// still binds at every depth, because nothing is missing. Only the dangerous
    /// direction — consumer declares a property the fragment lacks, anywhere — becomes a
    /// loud 400. Use this when silent defaults at <i>any</i> depth, including in literals,
    /// are a correctness KO-criterion. It runs on every call and recurses the fragment, so
    /// it is the most expensive mode; Weak is cost-neutral.
    /// </summary>
    Paranoid,
}