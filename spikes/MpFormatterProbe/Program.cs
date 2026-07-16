// Shared Probe-Programm: Round-Trip eines JsonElement? (und einer TrameResponse-
// artigen Hülle) durch MessagePack — gegen die je-projekt resolved MessagePack-Version.
#nullable disable

using System.Text.Json;
using System.Text.Json.Nodes;
using Trame.Spikes.MessagePack;
using MessagePack;

var opts = MessagePackSerializerOptions.Standard.WithResolver(JsonElementResolver.Instance);

var payload = JsonDocument.Parse("""{"id":7,"name":"Alice","nested":{"x":[1,2,3]},"flag":true}""").RootElement.Clone();
var resp = new ProbeResponse { Code = 200, Data = payload, Id = "req-1" };

byte[] bytes = MessagePackSerializer.Serialize(resp, opts);
var back = MessagePackSerializer.Deserialize<ProbeResponse>(bytes, opts);

Console.WriteLine($"Code={back.Code} Id={back.Id}");
Console.WriteLine($"Data.HasValue={back.Data.HasValue}");
Console.WriteLine($"Data.GetRawText()={back.Data.Value.GetRawText()}");

var orig = JsonNode.Parse(payload.GetRawText());
var round = JsonNode.Parse(back.Data.Value.GetRawText());
Console.WriteLine($"Semantically equal (envelope): {JsonNode.DeepEquals(orig, round)}");

// Null-Fall: JsonElement? = null → MessagePack nil.
var respNull = new ProbeResponse { Code = 204, Data = null, Id = "req-2" };
byte[] bytesN = MessagePackSerializer.Serialize(respNull, opts);
var backN = MessagePackSerializer.Deserialize<ProbeResponse>(bytesN, opts);
Console.WriteLine($"Null case: Data.HasValue={backN.Data.HasValue} (expected False)");

// Direkter JsonElement?-Wert (keine Hülle).
byte[] directBytes = MessagePackSerializer.Serialize<JsonElement?>(payload, opts);
var directBack = MessagePackSerializer.Deserialize<JsonElement?>(directBytes, opts);
Console.WriteLine($"Direct round-trip equal: {JsonNode.DeepEquals(JsonNode.Parse(payload.GetRawText()), JsonNode.Parse(directBack.Value.GetRawText()))}");

// Skalar-JsonElement (Zahl).
var scalar = JsonDocument.Parse("42").RootElement.Clone();
byte[] scalarBytes = MessagePackSerializer.Serialize<JsonElement?>(scalar, opts);
var scalarBack = MessagePackSerializer.Deserialize<JsonElement?>(scalarBytes, opts);
Console.WriteLine($"Scalar 42 -> {scalarBack.Value.GetRawText()}");

Console.WriteLine("SPIKE OK");

[MessagePackObject]
public sealed class ProbeResponse
{
    [Key(0)] public int Code { get; set; }
    [Key(1)] public JsonElement? Data { get; set; }
    [Key(2)] public string Id { get; set; }
}