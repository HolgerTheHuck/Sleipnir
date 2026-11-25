// Regression: older servers keyed closed generic DTOs by their assembly-qualified CLR FullName
// ("Ns.Page`1[[Ns.TicketDto, Asm, Version=…]]"). Sleipnir.Codegen.Core normalizes that key to the
// flat name current servers emit (Ns.PageOfTicketDto) — same rule as normalizeTypeKey in
// clients/codegen/src/core/casing.ts (covered there by generic-keys.test.ts).
using FluentAssertions;
using Sleipnir.Codegen.Core;
using Xunit;

namespace SleipnirTests.Unit.Codegen;

public class GenericTypeKeyCodegenTests
{
    private const string AqKey =
        "TicketSystem.Api.ProbeEnvelope`1[[TicketSystem.Api.TicketDto, TicketSystem.Api, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]";
    private const string FlatKey = "TicketSystem.Api.ProbeEnvelopeOfTicketDto";

    private static string Discovery(string pageKey) => $$"""
    {
      "discoveryVersion": "1",
      "controllers": [{
        "name": "Tickets",
        "methods": [{
          "methodName": "listTickets",
          "returnType": { "kind": "ref", "ref": "{{pageKey}}" },
          "parameters": []
        }]
      }],
      "types": {
        "TicketSystem.Api.TicketDto": {
          "kind": "object", "typeName": "TicketSystem.Api.TicketDto",
          "properties": [{ "propertyName": "Id", "propertyType": { "kind": "scalar", "name": "int" } }],
          "example": null
        },
        "{{pageKey}}": {
          "kind": "object", "typeName": "{{pageKey}}",
          "properties": [
            { "propertyName": "Items", "propertyType": { "kind": "array", "element": { "kind": "ref", "ref": "TicketSystem.Api.TicketDto" } } },
            { "propertyName": "Total", "propertyType": { "kind": "scalar", "name": "int" } }
          ],
          "example": null
        }
      }
    }
    """;

    [Fact]
    public void LegacyAssemblyQualifiedKey_EmitsSameContractsAsFlatKey()
    {
        var legacy = SleipnirCodegen.EmitContracts(Discovery(AqKey));
        var flat = SleipnirCodegen.EmitContracts(Discovery(FlatKey));

        flat.Should().Contain("class ProbeEnvelopeOfTicketDto");
        flat.Should().Contain("Task<ProbeEnvelopeOfTicketDto?> listTickets(");
        legacy.Should().Be(flat);
        legacy.Should().NotContain("Version=").And.NotContain("PublicKeyToken").And.NotContain("ProbeEnvelope`");
    }
}
