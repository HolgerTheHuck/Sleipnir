namespace SleipnirCore.Attributes
{
    /// <summary>
    /// Marks a <c>[SleipnirMethod]</c>/<c>[SleipnirEvent]</c> — or every method of a
    /// <c>[SleipnirController]</c> — as deprecated. The method keeps working; discovery reports
    /// <c>deprecated</c> (the message), generated clients mark the call (<c>@deprecated</c>,
    /// <c>[Obsolete]</c>), and the server counts its calls per client contract
    /// (<c>sleipnir.deprecated.calls</c>, <c>/observability</c> → <c>contracts.deprecatedCalls</c>)
    /// — so "can I remove it?" is answered by data: no calls for long enough. A method-level
    /// attribute overrides the controller-level one. See CONTRACT_EVOLUTION.md §7.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class)]
    public sealed class SleipnirDeprecatedAttribute : System.Attribute
    {
        public SleipnirDeprecatedAttribute(string message)
        {
            Message = message;
        }

        /// <summary>What to use instead, e.g. "Use Customer.v2.GetById".</summary>
        public string Message { get; }
    }
}
