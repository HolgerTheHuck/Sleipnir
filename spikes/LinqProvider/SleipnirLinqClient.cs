using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

namespace Sleipnir.Spike.LinqProvider;

/// <summary>
/// LINQ-Provider-artiger Client: baut aus einem typsicheren Lambda
/// <c>(TService c) =&gt; c.Method(args)</c> einen <see cref="SleipnirCallSpec{T}"/>.
/// Controller-/Methoden-Namen kommen aus den Vertrags-Attriben
/// (<see cref="SleipnirServiceContractAttribute"/> / <see cref="SleipnirMethodContractAttribute"/>),
/// Parameternamen aus der Methoden-Signatur. Argumente werden JSON-serialisiert;
/// <see cref="Dep{T}"/>-Argumente werden zu <c>@alias</c>-Platzhaltern.
/// </summary>
public sealed class SleipnirLinqClient
{
    private readonly ISleipnirClient _transport;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private int _idCounter;

    public SleipnirLinqClient(ISleipnirClient transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Baut einen typisierten Call mit Rückgabewert:
    /// <c>client.Build((ICustomerService c) =&gt; c.AddCustomer("Alice"))</c> →
    /// <see cref="SleipnirCallSpec{T}"/> mit T = int.
    /// </summary>
    public SleipnirCallSpec<T> Build<TService, T>(Expression<Func<TService, Task<T>>> call)
        where TService : class
        => (SleipnirCallSpec<T>)BuildCore(typeof(T), call);

    /// <summary>
    /// Baut einen void-Call (Methode ohne Rückgabewert):
    /// <c>client.BuildVoid((ICustomerService c) =&gt; c.DeleteCustomer(5))</c>.
    /// </summary>
    public SleipnirCallSpec<object> BuildVoid<TService>(Expression<Func<TService, Task>> call)
        where TService : class
        => (SleipnirCallSpec<object>)BuildCore(typeof(object), call);

    private SleipnirCallSpec BuildCore(Type resultType, LambdaExpression call)
    {
        if (call?.Body is not MethodCallExpression mce)
            throw new ArgumentException(
                "Erwartet ein einzelner Methodenaufruf auf dem Service, z. B. c => c.Method(args).");

        var svcType = mce.Method.DeclaringType
            ?? throw new ArgumentException("Methodenaufruf ohne deklarierenden Typ.");
        var controller = svcType.GetCustomAttribute<SleipnirServiceContractAttribute>()
            ?? throw new ArgumentException(
                $"{svcType.Name} trägt kein [SleipnirServiceContract] — ist es ein generierter Vertrag?");
        var methodAttr = mce.Method.GetCustomAttribute<SleipnirMethodContractAttribute>()
            ?? throw new ArgumentException(
                $"{mce.Method.Name} trägt kein [SleipnirMethodContract].");

        // Parameter-Namen aus der Methoden-Signatur; Argumente Werte-basiert auflösen.
        // Vertrags-Parameter sind Arg<T>-Wrapper — ein Arg trägt entweder einen
        // konkreten Wert oder einen Dep<T>-Platzhalter (→ @alias).
        var paramInfos = mce.Method.GetParameters();
        var parameters = new List<SleipnirParameter>(paramInfos.Length);
        for (int i = 0; i < paramInfos.Length; i++)
        {
            object? value = EvaluateArgument(mce.Arguments[i]);
            JsonNode? data;
            if (value is IArg arg)
            {
                // Arg<T>: Dep → @alias (serverseitige Substitution, nativer String-Wert
                // mit @-Präfix), sonst nativer JSON-Wert (kein JSON-String mehr).
                data = arg.IsDep
                    ? JsonValue.Create("@" + arg.Alias)
                    : JsonSerializer.SerializeToNode(arg.Value, _jsonOpts);
            }
            else
            {
                // Fallback: nackter Wert (kein Arg<T>-Wrapper).
                data = JsonSerializer.SerializeToNode(value, _jsonOpts);
            }
            parameters.Add(new SleipnirParameter
            {
                Num = i,
                ParameterName = paramInfos[i].Name ?? $"param{i}",
                Data = data
            });
        }

        var id = $"{controller.Controller}.{methodAttr.Method}#{++_idCounter}";
        var specType = typeof(SleipnirCallSpec<>).MakeGenericType(resultType);
        return (SleipnirCallSpec)Activator.CreateInstance(
            specType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { controller.Controller, methodAttr.Method, id, JsonSerializer.SerializeToNode(parameters) },
            null)!;
    }

    /// <summary>
    /// Löst einen Argument-Expression zur Laufzeit auf. Der Spike kompiliert jedes
    /// Argument einzeln — damit sind Konstanten, erfasste Lokale (closures) und
    /// <see cref="Dep{T}"/>-Marker gleichermaßen abgedeckt. (In einem echten LINQ-
    /// Provider würde man konstante Teile zur Bauzeit einklappen.)
    /// </summary>
    private static object? EvaluateArgument(Expression arg)
    {
        // Konstante direkt — spart das Kompilieren und ist die häufigste Form.
        if (arg is ConstantExpression ce)
            return ce.Value;
        // Sonst kompilieren & ausführen (schließt erfasste Lokale/Deps ein).
        return Expression.Lambda(arg).Compile().DynamicInvoke();
    }

    /// <summary>
    /// Sendet einen einzelnen typisierten Call und deserialisiert das Resultat als T.
    /// </summary>
    public async Task<T?> SendAsync<T>(SleipnirCallSpec<T> spec, CancellationToken ct = default)
    {
        var resp = await _transport.Call(spec.ToRequest(), ct);
        return Deserialize<T>(resp);
    }

    /// <summary>
    /// Sendet einen Batch (Multi-Request) über den Transport. Liefert die rohen
    /// Responses; typisierte Extraktion via <see cref="ResultOf{T}"/>.
    /// </summary>
    public async Task<IReadOnlyList<SleipnirResponse>> SendAsync(
        SleipnirBatch batch, CancellationToken ct = default)
    {
        var multi = batch.ToMultiRequest();
        var responses = await _transport.Call(multi, ct);
        return responses?.Where(r => r != null).Cast<SleipnirResponse>().ToList()
               ?? new List<SleipnirResponse>();
    }

    /// <summary>
    /// Extrahiert das typisierte Resultat eines bestimmten Specs aus einer Batch-
    /// Response-Liste (Korrelation über die Id).
    /// </summary>
    public T? ResultOf<T>(SleipnirCallSpec<T> spec, IReadOnlyList<SleipnirResponse> responses)
    {
        var resp = responses.FirstOrDefault(r => r.Id == spec.Id);
        return Deserialize<T>(resp);
    }

    private static T? Deserialize<T>(SleipnirResponse? resp)
    {
        if (resp == null || resp.Code is < 200 or > 299) return default;
        // SleipnirResponse.Data ist seit der Response-Seiten-Migration ein JsonElement?
        // (kein JSON-String mehr) — direkt über den JsonElement-Deserializer binden.
        if (!resp.Data.HasValue || resp.Data.Value.ValueKind == JsonValueKind.Null) return default;
        try { return resp.Data.Value.Deserialize<T>(_jsonOpts); }
        catch { return default; }
    }
}

/// <summary>
/// Wurzel-Typ eines gebauten Calls. Hält den Zustand (Controller, Methode, Id,
/// serialisierte Parameter, dependencyMapping). Die generische
/// <see cref="SleipnirCallSpec{T}"/> fügt lediglich die typsicheren <c>Expose</c>-Methoden
/// hinzu; dieser Basistyp ist nicht-generisch, damit <see cref="SleipnirBatch"/> und
/// <see cref="SleipnirLinqClient.BuildVoid{TService}"/> ohne Typparameter arbeiten können.
/// </summary>
public abstract class SleipnirCallSpec
{
    public string Controller { get; protected set; }
    public string Method { get; protected set; }
    public string Id { get; protected set; }
    public JsonNode? Params { get; set; }
    public Dictionary<string, string>? DependencyMapping { get; set; }
    private int _exposeCounter;

    protected SleipnirCallSpec(string controller, string method, string id, JsonNode? paramsNode)
    {
        Controller = controller;
        Method = method;
        Id = id;
        Params = paramsNode;
    }

    /// <summary>
    /// Stellt einen JsonPath-Pfad aus dem Resultat als Alias bereit und registriert
    /// das zugehörige <c>dependencyMapping</c> an diesem Call. Wird von den
    /// generischen <c>Expose</c>-Methoden in <see cref="SleipnirCallSpec{T}"/> gerufen.
    /// </summary>
    protected Dep<TProp> ExposePath<TProp>(string path)
    {
        // Alias muss für den serverseitigen DependencyGraphBuilder rein
        // alphanumerisch + '_' sein (ExtractAliases bricht an '.', '#' u. ä. ab).
        // Sonst würde die Kante nicht erkannt und der Folge-Call im falschen
        // Batch landen — der @alias-Platzhalter bliebe unaufgelöst.
        var safeId = new string(Id.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var alias = $"{safeId}__dep{++_exposeCounter}";
        (DependencyMapping ??= new Dictionary<string, string>())[alias] = path;
        return new Dep<TProp>(alias);
    }

    public SleipnirRequest ToRequest() => new()
    {
        Controller = Controller,
        Method = Method,
        Id = Id,
        Params = Params,
        DependencyMapping = DependencyMapping
    };
}