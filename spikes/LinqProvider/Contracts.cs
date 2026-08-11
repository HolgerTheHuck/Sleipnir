using Sleipnir.Model;

namespace Sleipnir.Spike.LinqProvider;

// =====================================================================
// AUTO-GENERIERT (Prototyp). Diese Datei spiegelt den Output des
// ContractGenerator (siehe ContractGenerator.cs) wider, der die Discovery
// (/api/sleipnir/discovery) in typsichere C#-Verträge übersetzt. Im Prototyp
// ist sie der Einfachheit halber handgeführt und gegen den Customer-
// Controller der Sample-App gemodelt. Ein echter Generator würde für jede
// Discovery-Datei eine solche Datei erzeugen.
//
// Konvention: Rückgaben sind entpackt — Task<T> -> T, Task -> void,
// CancellationToken entfällt. Parametertypen sind Arg<T>-Wrapper, damit ein
// Parameter sowohl konkrete Werte als auch Dep<T>-Platzhalter (für Dependency-
// Chaining) annehmen kann — typgeprüft vom Compiler.
// =====================================================================

/// <summary>Vertrag für den Customer-Controller der Sample-App.</summary>
[SleipnirServiceContract("Customer")]
public interface ICustomerService
{
    [SleipnirMethodContract("AddCustomer")]
    Task<int> AddCustomer(Arg<string> name);

    [SleipnirMethodContract("GetCustomerById")]
    Task<Customer?> GetCustomerById(Arg<int> id);

    [SleipnirMethodContract("GetAllCustomers")]
    Task<List<Customer>> GetAllCustomers();

    [SleipnirMethodContract("UpdateCustomerName")]
    Task UpdateCustomerName(Arg<int> customerId, Arg<string> newName);

    [SleipnirMethodContract("DeleteCustomer")]
    Task DeleteCustomer(Arg<int> id);
}