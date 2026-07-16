// Profiling-Spike: Wo sitzen die Bytes beim Trame-REST-GetAll?
//
// Antwortet die Frage, ob eine kompakte Draht-Codierung (MessagePack Route B =
// JSON→MessagePack 1:1, bzw. Route A = typisiert) den Single-Call-Nachteil
// gegenüber gRPC tatsächlich schließt — oder ob die Bytes woanders stecken
// (Envelope-Tax, Defaults, String-Keys).
//
// Spiegelt EXAKT die Benchmark-Bedingungen:
//  - 100 sparse Customers, wie CustomerService.AddCustomer sie anlegt
//    (nur Id/Name/OrderId gesetzt; Created=default, ResourceId=Guid.Empty,
//     Map=null, Addresses=[]).
//  - Invoker legt das Ergebnis als JSON-String in TrameResponse.Data
//    (TrameInvoker.ReturnResponse: JsonSerializer.Serialize(result) OHNE Options
//     → PascalCase). Results.Ok(response) serialisiert den Mantel mit Minimal-API-
//     Default (camelCase) → das innere JSON wird als escapetes String-Literal
//     eingebettet. Genau diese Doppel-Wicklung wird hier gemessen.
//
// Kein Server, kein HTTP — reine Serialisierungs-Messung (Draht-Größen).
using Google.Protobuf;
using Trame.Model;
using TrameCommon.Models;
using System.Text.Encodings.Web;
using MessagePack;
using MessagePack.Resolvers;
using System.Text.Json;

const int N = 100;

// 100 sparse Customers — exakt wie AddCustomer sie erzeugt.
var customers = new List<Customer>();
for (int i = 1; i <= N; i++)
    customers.Add(new Customer(i, $"Customer-{i-1}") { OrderId = i });

// Minimal-API-Default (camelCase, Web-Defaults) — Mantel-Serialisierung.
var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
// Invoker-Default (PascalCase, keine camel-Policy) — Payload-in-Data.
var invokerOptions = new JsonSerializerOptions(); // default: PascalCase, schreibt nulls

// ─── 1. Natives REST: List<Customer> direkt als JSON (camelCase) ────────────
string nativeRestBody = JsonSerializer.Serialize(customers, webOptions);
int nativeRestBytes = System.Text.Encoding.UTF8.GetByteCount(nativeRestBody);

// ─── 2. Trame: Payload-Inner (Invoker legt in Data) — PascalCase-JSON ───────
string tramePayloadJson = JsonSerializer.Serialize(customers, invokerOptions);
int tramePayloadBytes = System.Text.Encoding.UTF8.GetByteCount(tramePayloadJson);

// ─── 3. Trame: volle REST-Antwort (Mantel camelCase, Data=escapeter Payload-String) ──
var trameResponse = new TrameResponse
{
    Code = 200,
    Data = tramePayloadJson,   // inner JSON wird als String-Literal eingebettet
    Id = "req-1"
};
string trameFullJson = JsonSerializer.Serialize(trameResponse, webOptions);
int trameFullBytes = System.Text.Encoding.UTF8.GetByteCount(trameFullJson);
int envelopeTax = trameFullBytes - tramePayloadBytes; // Mantel + Escape-Overhead

// ─── 3b. JSON-Fix-Hebel (ohne neues Draht-Format!) ───────────────────────────
//  (a) Relaxed Encoder: `"` → `\"` statt `"` (6 B). Eine Options-Änderung.
var relaxedOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
string relaxedFull = JsonSerializer.Serialize(trameResponse, relaxedOptions);
int relaxedFullBytes = System.Text.Encoding.UTF8.GetByteCount(relaxedFull);
//  (b) Single-Pass: Payload strukturiert einbetten statt als JSON-String in Data —
//      kein Double-Wrapping, keine Quote-Escapes im Payload. (Entspricht einer
//      Invoker-Änderung: Ergebnis als strukturierter Wert, nicht als JSON-String.)
string singlePass = JsonSerializer.Serialize(new { code = 200, data = customers, id = "req-1" }, relaxedOptions);
int singlePassBytes = System.Text.Encoding.UTF8.GetByteCount(singlePass);

//    Keine Schema-Änderung, keine Attribute — „JSON, nur binär". Envelope-Tax
//    entfällt (keine escapeten Quotes), aber Payload behält String-Keys + Defaults.
byte[] routeBPayload = MessagePackSerializer.ConvertFromJson(tramePayloadJson);
int routeBPayloadBytes = routeBPayload.Length;
// Route B volle Antwort: MessagePack-Envelope (Integer-Keys, [MessagePackObject])
// + Payload als binär in Content (statt JSON-String in Data).
byte[] routeBFull = MessagePackSerializer.Serialize(new TrameResponse
{
    Code = 200,
    Content = routeBPayload,
    Id = "req-1"
});
int routeBFullBytes = routeBFull.Length;

// ─── 5. MessagePack Route A: typisiert (contractless, String-Keys) ──────────
//    Customer ist NICHT [MessagePackObject] → contractless nutzt Property-Namen
//    als String-Keys (kein IDL, „Klassen sind der Contract"). Werte typisiert
//    (DateTime→int64, Guid→string), aber Defaults werden geschrieben.
var contractlessOpts = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
byte[] routeAPayload = MessagePackSerializer.Serialize(customers, contractlessOpts);
int routeAPayloadBytes = routeAPayload.Length;
byte[] routeAFull = MessagePackSerializer.Serialize(new TrameResponse
{
    Code = 200,
    Content = routeAPayload,
    Id = "req-1"
});
int routeAFullBytes = routeAFull.Length;

// ─── 6. gRPC-Protobuf: proto3 lässt Defaults weg (int-Field-Tags, keine Keys) ──
var protoList = new Trame.Grpc.CustomerList();
for (int i = 1; i <= N; i++)
{
    protoList.Customers.Add(new Trame.Grpc.Customer
    {
        Id = i,
        OrderId = i,
        Name = $"Customer-{i-1}"
        // created=0, resource_id="" → proto3 lässt Defaults weg
    });
}
int grpcBytes = protoList.ToByteArray().Length;

// ─── Ausgabe ────────────────────────────────────────────────────────────────
double KB(int b) => b / 1024.0;

// Diagnose: woher kommt die Envelope-Tax wirklich?
int quoteCount = tramePayloadJson.Count(c => c == '"');
int backslashCount = trameFullJson.Count(c => c == '\\');
// Data allein als JSON-String serialisiert = exakte escaped-Größe.
string dataAsJsonString = JsonSerializer.Serialize(tramePayloadJson, webOptions);
int dataAsJsonStringBytes = System.Text.Encoding.UTF8.GetByteCount(dataAsJsonString);
// Reiner Mantel: TrameResponse mit Data=null.
string pureEnvelope = JsonSerializer.Serialize(new TrameResponse { Code = 200, Id = "req-1" }, webOptions);
int pureEnvelopeBytes = System.Text.Encoding.UTF8.GetByteCount(pureEnvelope);
Console.WriteLine("Envelope-tax breakdown (precise):");
Console.WriteLine($"  Payload inner (raw data string) : {tramePayloadBytes:N0} B  (Quotes: {quoteCount:N0})");
Console.WriteLine($"  Data as JSON string (escaped)   : {dataAsJsonStringBytes:N0} B  -> escape overhead: {dataAsJsonStringBytes - tramePayloadBytes:N0} B");
Console.WriteLine($"  Pure envelope (TrameResponse, Data=null): {pureEnvelopeBytes:N0} B");
Console.WriteLine($"  Total (escaped data + envelope) : {dataAsJsonStringBytes + pureEnvelopeBytes:N0} B  (full measured: {trameFullBytes:N0})");
Console.WriteLine($"  Backslashes in full             : {backslashCount:N0}");
Console.WriteLine($"  Pure envelope (first 200)       : {pureEnvelope[..Math.Min(200, pureEnvelope.Length)]}");
int unicodeEscapes = System.Text.RegularExpressions.Regex.Matches(dataAsJsonString, @"\\u[0-9a-fA-F]{4}").Count;
Console.WriteLine($"  \\uXXXX-escapes in data string    : {unicodeEscapes:N0}");
Console.WriteLine($"  Data escaped (first 400)        : {dataAsJsonString[..Math.Min(400, dataAsJsonString.Length)]}");
Console.WriteLine();

Console.WriteLine($"Wire-size probe — {N} sparse Customers (GetAll)");
Console.WriteLine(new string('=', 70));
Console.WriteLine();
Console.WriteLine($"{"Encoding",-50} {"Bytes",10} {"KB",8}");
Console.WriteLine(new string('-', 70));
Console.WriteLine($"{"REST native (camelCase JSON)",-50} {nativeRestBytes,10:N0} {KB(nativeRestBytes),8:N2}");
Console.WriteLine($"{"Trame payload inner (PascalCase JSON)",-50} {tramePayloadBytes,10:N0} {KB(tramePayloadBytes),8:N2}");
Console.WriteLine($"{"Trame REST full (envelope + escaped payload)",-50} {trameFullBytes,10:N0} {KB(trameFullBytes),8:N2}");
Console.WriteLine($"{"  of which envelope tax (envelope+escape)",-50} {envelopeTax,10:N0} {KB(envelopeTax),8:N2}");
Console.WriteLine($"{"Trame REST + relaxed encoder (\" instead of \\u0022)",-50} {relaxedFullBytes,10:N0} {KB(relaxedFullBytes),8:N2}");
Console.WriteLine($"{"Trame REST single-pass (structured, no wrap)",-50} {singlePassBytes,10:N0} {KB(singlePassBytes),8:N2}");
Console.WriteLine();
Console.WriteLine($"{"MessagePack Route B — payload (JSON->MP 1:1)",-50} {routeBPayloadBytes,10:N0} {KB(routeBPayloadBytes),8:N2}");
Console.WriteLine($"{"MessagePack Route B — full (MP envelope+binary)",-50} {routeBFullBytes,10:N0} {KB(routeBFullBytes),8:N2}");
Console.WriteLine($"{"MessagePack Route A — payload (contractless)",-50} {routeAPayloadBytes,10:N0} {KB(routeAPayloadBytes),8:N2}");
Console.WriteLine($"{"MessagePack Route A — full (MP envelope+binary)",-50} {routeAFullBytes,10:N0} {KB(routeAFullBytes),8:N2}");
Console.WriteLine($"{"gRPC-Protobuf (proto3, defaults omitted)",-50} {grpcBytes,10:N0} {KB(grpcBytes),8:N2}");
Console.WriteLine();
Console.WriteLine("Ratios (smaller = better):");
Console.WriteLine(new string('-', 70));
double Ref(int b) => Math.Round((double)b / grpcBytes, 2);
Console.WriteLine($"  gRPC-Protobuf              = {Ref(grpcBytes)}×  (reference)");
Console.WriteLine($"  Trame REST full (JSON)      = {Ref(trameFullBytes)}×  vs gRPC");
Console.WriteLine($"  Trame REST + relaxed encoder= {Ref(relaxedFullBytes)}×  vs gRPC");
Console.WriteLine($"  Trame REST single-pass      = {Ref(singlePassBytes)}×  vs gRPC");
Console.WriteLine($"  Trame payload inner (JSON)  = {Ref(tramePayloadBytes)}×  vs gRPC");
Console.WriteLine($"  MessagePack Route B full    = {Ref(routeBFullBytes)}×  vs gRPC");
Console.WriteLine($"  MessagePack Route A full    = {Ref(routeAFullBytes)}×  vs gRPC");
Console.WriteLine();
Console.WriteLine("Where the Trame-REST bytes sit (vs native-REST)?");
Console.WriteLine($"  Native-REST body           : {nativeRestBytes:N0} B");
Console.WriteLine($"  Trame envelope tax (envelope): {envelopeTax:N0} B  ({envelopeTax*100.0/trameFullBytes:0.0}% of Trame body)");
Console.WriteLine($"  -> Envelope tax alone makes Trame {envelopeTax:n0} B larger than necessary.");