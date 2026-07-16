using System;

namespace Trame.Spike.LinqProvider;

/// <summary>
/// Markiert ein Service-Interface als Trame-Vertrag für einen Controller.
/// Wird vom ContractGenerator aus der Discovery auf das generierte Interface
/// gesetzt; der LINQ-Client liest hieraus den Controller-Namen für den Request.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class TrameServiceContractAttribute : Attribute
{
    public string Controller { get; }
    public TrameServiceContractAttribute(string controller) => Controller = controller;
}

/// <summary>
/// Markiert eine Vertragsmethode als Trame-Methode. Carries den serverseitigen
/// Methoden-Namen (der nicht zwingend dem C#-Methodennamen entsprechen muss).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TrameMethodContractAttribute : Attribute
{
    public string Method { get; }
    public TrameMethodContractAttribute(string method) => Method = method;
}