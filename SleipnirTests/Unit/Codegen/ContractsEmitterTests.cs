// Tests for the EmitContracts emission mode of Sleipnir.Codegen.Core — the C# LINQ-contract emitter
// (SleipnirContracts.g.cs: POCO DTOs + [SleipnirServiceContract] interfaces). This is the structural
// fix for the spike's flat-string ContractGenerator, which could not handle the real TypeRef IR
// ({kind:"ref"/"array"/...}) and parsed the legacy discovery shape. Here the Story-01 golden
// discovery (the real TypeRef IR) is fed to EmitContracts and the emitted interfaces/POCOs are
// asserted — exactly where the spike's generator would have choked.
using System.IO;
using FluentAssertions;
using Sleipnir.Codegen.Core;
using Xunit;

namespace SleipnirTests.Unit.Codegen;

public class ContractsEmitterTests
{
    private static DirectoryInfo ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "stories"))
                && Directory.Exists(Path.Combine(dir.FullName, "clients"))
                && File.Exists(Path.Combine(dir.FullName, "Sleipnir.sln")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string Story01Fixture()
    {
        var repo = ResolveRepoRoot();
        return File.ReadAllText(Path.Combine(repo.FullName, "clients", "codegen", "test", "fixtures", "story01-discovery.json"));
    }

    private static string Emit() => SleipnirCodegen.EmitContracts(Story01Fixture());

    [Fact]
    public void Emits_usings_and_default_contract_namespace()
    {
        var cs = Emit();
        cs.Should().Contain("using Sleipnir.Client.Linq;");
        cs.Should().Contain("using System.Text.Json.Serialization;");
        cs.Should().Contain("using System.Threading.Tasks;");
        cs.Should().Contain("namespace Sleipnir.Linq.Contracts");
    }

    [Fact]
    public void Emits_one_service_interface_per_controller_with_contract_attributes()
    {
        var cs = Emit();
        // Story-01 exposes an Order controller with a GetById(int):Order method.
        cs.Should().Contain("[SleipnirServiceContract(\"Order\")]");
        cs.Should().Contain("public interface IOrderService");
        cs.Should().Contain("[SleipnirMethodContract(\"GetById\")]");
        // The method signature: Task<Order?> GetById(Arg<int> id) — Arg<T> param, nullable ref return.
        cs.Should().Contain("Task<Order?> GetById(Arg<int> id)");
    }

    [Fact]
    public void Emits_ref_return_as_nullable_task_of_poco()
    {
        // Customer.GetById(customerId:int):Customer — a ref return becomes Task<Customer?>.
        var cs = Emit();
        cs.Should().Contain("[SleipnirServiceContract(\"Customer\")]");
        cs.Should().Contain("Task<Customer?> GetById(Arg<int> customerId)");
    }

    [Fact]
    public void Emits_array_return_as_task_of_list()
    {
        // Stock.GetByArticles(articleIds:int[]):StockInfo[] → Task<List<StockInfo>?> GetByArticles(Arg<List<int>> articleIds).
        // This is the case the spike's flat MapType could not handle (TypeRef kind:array element:ref).
        var cs = Emit();
        cs.Should().Contain("[SleipnirServiceContract(\"Stock\")]");
        cs.Should().Contain("Task<List<StockInfo>?> GetByArticles(Arg<List<int>> articleIds)");
    }

    [Fact]
    public void Emits_poco_dtos_with_jsonpropertyname_wire_mapping()
    {
        // The Order POCO: [JsonPropertyName("customerId")] public int? CustomerId.
        var cs = Emit();
        cs.Should().Contain("public class Order");
        cs.Should().Contain("[JsonPropertyName(\"customerId\")]");
        cs.Should().Contain("public int? CustomerId { get; set; }");
    }

    [Fact]
    public void Custom_namespace_is_honoured()
    {
        var cs = SleipnirCodegen.EmitContracts(Story01Fixture(), new EmitCsOptions { Namespace = "My.App.Contracts" });
        cs.Should().Contain("namespace My.App.Contracts");
        cs.Should().NotContain("namespace Sleipnir.Linq.Contracts");
    }
}