// Regression: older servers keyed closed generic DTOs by their assembly-qualified CLR
// FullName ("Ns.Page`1[[Ns.TicketDto, Asm, Version=…, Culture=…, PublicKeyToken=…]]").
// The emitters split that on "." / "," and produced invalid identifiers inside
// TypedCall<…>. The key is now normalized to the flat name current servers emit
// (`Ns.PageOfTicketDto`), so both key forms yield the same client.
import { describe, it, expect } from "vitest";
import type { DiscoveryInfo } from "sleipnir-client";
import { assertDiscoveryShape } from "../../src/core/discovery.js";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";
import { emitCsClient } from "../../src/emitters/cs.js";
import { emitPyClient } from "../../src/emitters/py.js";

const AQ_KEY =
  "TicketSystem.Api.ProbeEnvelope`1[[TicketSystem.Api.TicketDto, TicketSystem.Api, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]";
const FLAT_KEY = "TicketSystem.Api.ProbeEnvelopeOfTicketDto";

function discovery(pageKey: string): DiscoveryInfo {
  return assertDiscoveryShape({
    discoveryVersion: "1",
    controllers: [
      {
        name: "Tickets",
        methods: [
          {
            methodName: "listTickets",
            returnType: { kind: "ref", ref: pageKey },
            parameters: [{ parameterName: "cursor", parameterType: { kind: "scalar", name: "string", nullable: true } }],
          },
        ],
      },
    ],
    types: {
      "TicketSystem.Api.TicketDto": {
        kind: "object",
        typeName: "TicketSystem.Api.TicketDto",
        properties: [
          { propertyName: "Id", propertyType: { kind: "scalar", name: "int" } },
          { propertyName: "Title", propertyType: { kind: "scalar", name: "string" } },
        ],
        example: null,
      },
      [pageKey]: {
        kind: "object",
        typeName: pageKey,
        properties: [
          { propertyName: "Items", propertyType: { kind: "array", element: { kind: "ref", ref: "TicketSystem.Api.TicketDto" } } },
          { propertyName: "NextCursor", propertyType: { kind: "scalar", name: "string", nullable: true } },
          { propertyName: "Total", propertyType: { kind: "scalar", name: "int" } },
        ],
        example: null,
      },
    },
  });
}

const emitTs = (key: string) => emitTsClient(buildEmitterInput(discovery(key), new NamingResolver()));

describe("closed generic type keys", () => {
  it("emits a named interface and a typed call for the flat key", () => {
    const tree = emitTs(FLAT_KEY);
    expect(tree["api/types.ts"]).toContain("export interface ProbeEnvelopeOfTicketDto");
    expect(tree["api/controllers.ts"]).toContain("TypedCall<ProbeEnvelopeOfTicketDto, ProbeEnvelopeOfTicketDtoPaths>");
  });

  it("emits the identical TS client for the legacy assembly-qualified key", () => {
    expect(emitTs(AQ_KEY)).toEqual(emitTs(FLAT_KEY));
  });

  it("leaves no CLR fragments in any emitter output", () => {
    const input = () => buildEmitterInput(discovery(AQ_KEY), new NamingResolver());
    for (const tree of [emitTsClient(input()), emitCsClient(input()), emitPyClient(input())]) {
      const all = Object.values(tree).join("\n");
      expect(all).not.toContain("ProbeEnvelope`");
      expect(all).not.toContain("Version=");
      expect(all).not.toContain("PublicKeyToken");
      expect(all).toContain("ProbeEnvelopeOfTicketDto");
    }
  });
});
