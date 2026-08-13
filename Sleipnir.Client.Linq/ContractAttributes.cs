using System;

namespace Sleipnir.Client.Linq;

/// <summary>
/// Marks a service interface as a Sleipnir contract for a controller. The contract generator
/// (the <c>sleipnir-linq</c> tool) places this on each generated <c>I{Name}Service</c> interface;
/// <see cref="SleipnirLinqClient"/> reads the controller name from here when building a call.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class SleipnirServiceContractAttribute : Attribute
{
    /// <summary>The server-side controller name (the <c>[SleipnirController]</c> name).</summary>
    public string Controller { get; }

    public SleipnirServiceContractAttribute(string controller) => Controller = controller;
}

/// <summary>
/// Marks a contract method as a Sleipnir method, carrying the server-side method name (which need
/// not match the C# method name). The contract generator emits this on every generated interface
/// method; <see cref="SleipnirLinqClient"/> reads it to build the request.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SleipnirMethodContractAttribute : Attribute
{
    /// <summary>The server-side method name (the <c>[SleipnirMethod]</c> name).</summary>
    public string Method { get; }

    public SleipnirMethodContractAttribute(string method) => Method = method;
}