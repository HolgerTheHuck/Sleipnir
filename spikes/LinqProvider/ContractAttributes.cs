using System;

namespace Sleipnir.Spike.LinqProvider;

/// <summary>
/// Markiert ein Service-Interface als Sleipnir-Vertrag für einen Controller.
/// Wird vom ContractGenerator aus der Discovery auf das generierte Interface
/// gesetzt; der LINQ-Client liest hieraus den Controller-Namen für den Request.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class SleipnirServiceContractAttribute : Attribute
{
    public string Controller { get; }
    public SleipnirServiceContractAttribute(string controller) => Controller = controller;
}

/// <summary>
/// Markiert eine Vertragsmethode als Sleipnir-Methode. Carries den serverseitigen
/// Methoden-Namen (der nicht zwingend dem C#-Methodennamen entsprechen muss).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SleipnirMethodContractAttribute : Attribute
{
    public string Method { get; }
    public SleipnirMethodContractAttribute(string method) => Method = method;
}