# Chapter 2 — A Blazor Pflege-Backend with a generated typed C# client

> **Goal:** build tier 2 — `Story.Admin`, a Blazor Server admin backend — that calls the
> API through a **generated, typed C# client**. Same `Market.GetQuote`, but now
> `client.Market.GetQuote("BTC")` with a compile-time `Quote` type.

Chapter 1 ended with `curl` and the DevUI — no client SDK. This chapter introduces the
first *typed* client: the `Sleipnir.Generator` Roslyn source generator reads the server's
`contract.sleipnir.json` and emits a `SleipnirGeneratedClient` (plus the `Quote` type and a
`Market` controller accessor) **into the Blazor project's compilation**. A wrong method
name or a missing property is now a build error, not a runtime `400`.

```
guide/admin/  (Story.Admin, Blazor Server, port 5011)
  Story.Admin.csproj       SleipnirClient + the source generator + linked contract
  Program.cs               registers one SleipnirGeneratedClient for the app
  Components/Pages/MarketQuote.razor   the typed quote call
```

## The contract loop (one source of truth)

The contract lives in exactly one place: the server. Two tools move it:

1. **Server export** (`Sleipnir.Server.Codegen`, wired in `guide/server/Story.Api.csproj`)
   regenerates `guide/server/contract.sleipnir.json` on every server build and **fails the
   build if it has drifted** from the runtime discovery.
2. **Client generation** (`Sleipnir.Generator`, wired in `guide/admin/Story.Admin.csproj`)
   reads that same file and emits `SleipnirGenerated.cs` into the admin's compilation.

The admin **links** the server's contract — it does not copy it:

```xml
<!-- guide/admin/Story.Admin.csproj -->
<AdditionalFiles Include="..\server\contract.sleipnir.json" Link="contract.sleipnir.json" />
```

So the loop is one command per tier:

```
change a controller → rebuild server (contract regenerates) → rebuild admin (stubs update)
```

Touch the contract anywhere else and it drifts; the server build catches it. This is the
gRPC-style compile-time contract boundary, without a second protocol.

## Wiring the generator

```xml
<!-- guide/admin/Story.Admin.csproj -->
<ItemGroup>
  <!-- The runtime the generated stubs call into. -->
  <ProjectReference Include="..\..\SleipnirClient\SleipnirClient.csproj" />
  <!-- The generator, wired as an analyzer so it runs during this build. In a published setup
       this is <PackageReference Include="Sleipnir.Generator" PrivateAssets="all" />. -->
  <ProjectReference Include="..\..\Sleipnir.SourceGenerator\Sleipnir.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>

<ItemGroup>
  <AdditionalFiles Include="..\server\contract.sleipnir.json" Link="contract.sleipnir.json" />
</ItemGroup>
```

In-repo `ProjectReference`s mean clone-and-build works with no NuGet restore of Sleipnir
packages. The generator runs as an analyzer; a contract shape violation surfaces as
diagnostic `SLEIPNIR001`, an emit failure as `SLEIPNIR002`.

## Register the client

One generated client for the whole admin app (a Pflege-Backend has one server-side
session, so a singleton is fine). The default constructor wraps a `SleipnirTransportRouter`
with capability `all` — `auto` probes WebSocket first and falls back to REST+SSE:

```csharp
// guide/admin/Program.cs
using Sleipnir.Generated;
using Sleipnir.Guide.Admin.Components;

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(_ => new SleipnirGeneratedClient("https://localhost:5010"));
builder.WebHost.UseUrls("https://localhost:5011");
```

> **Why Blazor Server?** The admin session runs on the server, so the call to the API is
> server-to-server — the admin's network, the admin's secrets. Chapter 7 puts the admin
> **bearer** here, server-side, where a customer never sees it. (See the WASM variant
> note at the end if you'd rather run the admin in the browser.)

## The typed call

```razor
<!-- guide/admin/Components/Pages/MarketQuote.razor -->
@page "/quote"
@inject SleipnirGeneratedClient Sleipnir

@code {
    private string symbol = "BTC";
    private Quote? quote;

    private async Task GetQuoteAsync()
    {
        var call = Sleipnir.Market.GetQuote(symbol);   // typed Call, no strings
        quote = await Sleipnir.Call<Quote>(call);       // -> Quote? 
    }
}
```

`client.Market.GetQuote(symbol)` builds a `Call` (the controller accessor + method are
generated; `symbol` binds to the `Arg<string>` parameter via an implicit conversion).
`client.Call<Quote>(call)` executes it and deserializes the response `data` into the
generated `Quote` — a POCO with `[JsonPropertyName]` mapping the camelCase wire to
PascalCase properties (`symbol` → `Symbol`, `price` → `Price`, …).

Type the wrong method (`Sleipnir.Market.GetQoute`) or read a missing field (`quote.Bids`)
and the build fails. That is the whole point of this chapter.

> **Gotcha — page class names shadow types.** A Blazor page `Quote.razor` generates a
> class named `Quote`. Inside that file the unqualified `Quote` resolves to the *page
> class*, shadowing `Sleipnir.Generated.Quote` — so `quote.Symbol` errors with "no
> definition for Symbol". This page is named `MarketQuote.razor` to avoid the clash.
> Pick page names that don't collide with your contract types.

## See the generated code

The generated `SleipnirGenerated.cs` lives in the compilation (in-memory by default). To
inspect it during development, add `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>`
to the admin's `<PropertyGroup>` and rebuild — it appears under
`obj/Debug/net10.0/generated/Sleipnir.SourceGenerator/.../SleipnirGenerated.cs`. The DevUI
Codegen tab (`https://localhost:5010/Sleipnir`) shows the same output from the discovery
payload without a build.

## Try it

Two terminals:

```bash
# terminal 1 — the API
dotnet run --project guide/server

# terminal 2 — the admin
dotnet run --project guide/admin
```

Open `https://localhost:5011/quote`, type `BTC`, click **Get quote**. You should see the
typed quote card (price, change, time). Try `NOPE` — the card shows "No market for symbol
'NOPE'" (the `null` return from chapter 1).

### The 5-line console alternative

You do not need Blazor to use the generated client. The same `Story.Admin.csproj` generator
wiring works in a console app — the whole point of the source generator is that it runs in
*any* .NET compilation:

```csharp
using Sleipnir.Generated;
var client = new SleipnirGeneratedClient("https://localhost:5010");
var q = await client.Call<Quote>(client.Market.GetQuote("BTC"));
Console.WriteLine($"{q!.Symbol}: {q.Price}");
```

Reference `SleipnirClient` + the generator analyzer + the contract `AdditionalFiles`, same
as the admin. If a browser admin is more than you need, start here.

### Blazor WASM variant

To run the admin in the browser instead of on the server: switch the template to
`InteractiveWebAssembly`, move the `SleipnirGeneratedClient` construction client-side, and
**enable CORS on the API** (already open in this guide's dev config) since the browser will
call the API directly. The admin bearer then lives in the browser — acceptable for an
internal tool behind auth, but for the guide we keep it server-side (chapter 7).

---

**Next:** [Chapter 3 — a plain HTML/JS page with the generated JS client](03-html-js.md).
Same quote, zero build step, REST + SSE only — the thinnest possible client, and a love
letter to `curl`-friendly REST.