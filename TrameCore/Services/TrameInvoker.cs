using System.Collections.Concurrent;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Encodings.Web;
using TrameCore.Attributes;
using System.Net;
using System.Text.Json.Nodes;
using TrameCore.Model.Messages.Mex;
using TrameCore.Services.Helper;
using System.Diagnostics;
using TrameCore.Tracing;
using Microsoft.Extensions.Logging; // Für JsonPath.Net
using TrameCommon.Results;
using TrameCommon.Models;

namespace TrameCore.Services
{
    public class TrameInvoker : ITrameCore
    {
        private readonly ConcurrentDictionary<string, Type> _routeHandlers = new();
        private readonly ConcurrentDictionary<string, InvokeInfo> _invokeCache = new();

        private readonly TrameDiscoveryService _discoveryService;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        private readonly ILogger<TrameInvoker> _logger;
        private readonly List<ITrameInterceptor> _interceptors;

        /// <summary>
        /// Wenn gesetzt, werden in Fehler-Responses die echten Exception-Details
        /// (TrameError.Details) befüllt. In Produktion auf false belassen, um keine
        /// internen Informationen an Clients preiszugeben. Wird durch AddTrame anhand
        /// von TrameOptions.EnableDetailedErrors bzw. IHostEnvironment.IsDevelopment() gesetzt.
        /// </summary>
        public bool EnableDetailedErrors { get; set; }

        /// <summary>
        /// Maximale Elementzahl eines Top-Level-Array-/Collection-Parameters (Default 1000,
        /// 0 = unbegrenzt). Schützt den Server vor Kardinalitäts-Sprengung, insb. beim
        /// @alias-Whole-Collection-Passthrough (server-seitig erzeugte Arrays — Body-Size-Limits
        /// greifen nicht). string/byte[] ausgenommen. Wird über AddTrame aus
        /// TrameOptions gesetzt; der Invoker-Default schützt auch einen nackten `new TrameInvoker()`.
        /// </summary>
        public int MaxParameterArrayLength { get; set; } = 1000;

        /// <summary>
        /// Maximale Elementzahl eines Top-Level-Array-/Collection-Rückgabewerts (Default 10000,
        /// 0 = unbegrenzt). Verhindert, dass ein Einzelergebnis den Server in Memory treibt.
        /// Greift materialisierte Collections (via ReturnResponse) und IAsyncEnumerable-Streams
        /// (Early-Stop in ConsumeAsyncEnumerable). string/byte[] ausgenommen.
        /// </summary>
        public int MaxResultElementCount { get; set; } = 10000;

        /// <summary>
        /// Wie ein extrahiertes @alias-Fragment an den Consumer-Parametertyp gebunden wird
        /// (Default Weak). Weak = STJ-Duck-Typing mit stillen Defaults (mächtig; das
        /// Subset-Fan-out funktioniert). Strict = das Fragment muss den Consumer-Typ
        /// vollständig decken (jede public read-write Eigenschaft muss im Fragment
        /// vorhanden sein), sonst 400 — schaltet nur die object→object-silent-default-
        /// Zeile um; cross-kind ist in beiden Modi 400. Greift nur @alias-sourced
        /// Parameter. Siehe DEPENDENCY_BINDING.md. Wird über AddTrame aus TrameOptions
        /// gesetzt; der Invoker-Default (Weak) gilt auch für einen nackten new TrameInvoker().
        /// </summary>
        public AliasBindingMode AliasBindingMode { get; set; } = AliasBindingMode.Weak;

        /// <summary>
        /// North-Bound-Default-Deny. Wenn <c>true</c>, verlangt jede Methode ohne
        /// <c>[TrameAnonymous]</c> einen authentifizierten User; <c>[TrameAuthorise]</c>
        /// prüft weiterhin Rolle/Authentication. Default <c>false</c> (South-Bound,
        /// nicht breaking). Wird über AddTrame aus TrameOptions gesetzt. Siehe
        /// SECURITY.md.
        /// </summary>
        public bool RequireAuthentication { get; set; }

        /// <summary>
        /// Maximale Anzahl Requests pro Batch (Default 0 = unbegrenzt, nicht
        /// breaking). Backstop am Batch-Einstieg; die Transport-Multi-Endpunkte
        /// gate-n früher mit 400. Siehe SECURITY.md.
        /// </summary>
        public int MaximumBatchSize { get; set; }

        /// <summary>
        /// Maximale Länge eines client-kontrollierten dependencyMapping-JsonPath
        /// (Default 256, 0 = unbegrenzt). Wird an DependencyResolver.ExtractValue
        /// durchgereicht; ein zu langer Pfad wird vor der Evaluation verworfen.
        /// </summary>
        public int MaxDependencyPathLength { get; set; } = 256;

        /// <summary>
        /// Wenn <c>false</c>, werden <c>$..</c>-Pfade in dependencyMapping
        /// abgelehnt (vor der Evaluation). Default <c>true</c> (nicht breaking).
        /// </summary>
        public bool AllowRecursiveDescent { get; set; } = true;

        // camelCase + relaxed Encoder: Der Payload wird jetzt strukturiert (JsonElement)
        // in Data abgelegt und von den Transporten in einem Pass roh serialisiert —
        // kein Double-Wrapping mehr. UnsafeRelaxed verhindert dennoch `"`-Escaping im
        // Mantel (z. B. in ExposedDependencies-Strings oder Fehlermeldungen).
        private readonly JsonSerializerOptions _jsonSerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };


        public TrameInvoker(IServiceScopeFactory serviceScopeFactory, ILogger<TrameInvoker> logger, IEnumerable<ITrameInterceptor>? interceptors = null)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
            _discoveryService = new TrameDiscoveryService(_routeHandlers);
            _interceptors = interceptors?.ToList() ?? new List<ITrameInterceptor>();
        }


        #region Registrierung von Controllern

        public void Register<T>()
        {
            Register(typeof(T));
        }

        // Registrierung läuft typischerweise einmal beim Start. Da wir bei
        // Namens-Kollisionen jetzt hart fehlschlagen (statt still TryAdd), sichern
        // wir die Prüf-und-Einfüge-Sequenz ab — sonst könnte eine konkurrente
        // Registrierung die Kollision schlucken, bevor wir sie bemerken.
        private readonly object _registrationLock = new();

        public void Register(Type controllerType)
        {
            var controllerAttr = GetAttribute<TrameControllerAttribute>(controllerType);
            if (controllerAttr == null) return;

            lock (_registrationLock)
            {
                // Controller-Name darf nicht von einem anderen Typ belegt sein.
                // Erneute Registrierung desselben Typs bleibt idempotent (z. B. bei
                // mehrfachem Register<T>() oder wiederholtem UseTrame()).
                if (_routeHandlers.TryGetValue(controllerAttr.Name, out var existingController)
                    && existingController != controllerType)
                {
                    throw new InvalidOperationException(
                        $"A Trame controller named '{controllerAttr.Name}' is already registered " +
                        $"on type '{existingController.FullName}'. Controller names must be unique. " +
                        $"Either rename one [TrameController] or remove the duplicate.");
                }

                _routeHandlers.TryAdd(controllerAttr.Name, controllerType);

                // A registration after the first GetDiscoveryInfo() would otherwise stay
                // invisible until app restart — invalidate the cached DiscoveryInfo so the
                // newly registered controller/methods appear on the next discovery call.
                // (TrameDiscoveryService derives from _routeHandlers, so the cache only ever
                // holds a stale snapshot; this is the seam that keeps it current.)
                _discoveryService.InvalidateCache();

                foreach (var methodInfo in controllerType.GetMethods())
                {
                    var methodAttr = GetAttribute<TrameMethodAttribute>(methodInfo);
                    if (methodAttr == null) continue;

                    string key = $"{controllerAttr.Name}_{methodAttr.Name}";

                    // Gleichnamige Trame-Methoden sind nicht erlaubt — Trame hat keine
                    // parameterbasierte Überladungsauflösung, der Dispatch-Key ist rein
                    // namensbasiert. Eine stille TryAdd-First-wins-Doppelung wäre ein
                    // nicht-deterministischer Bug, also werfen wir laut zur Registrierungszeit.
                    // Erneute Registrierung derselben MethodInfo bleibt idempotent.
                    if (_invokeCache.TryGetValue(key, out var existing)
                        && existing.MethodInfo != methodInfo)
                    {
                        throw new InvalidOperationException(
                            $"Trame method '{controllerAttr.Name}.{methodAttr.Name}' is already registered " +
                            $"on '{existing.MethodInfo.DeclaringType?.FullName}.{existing.MethodInfo.Name}'. " +
                            $"Method names within a controller must be unique. Trame does not resolve " +
                            $"overloads by parameters — give each method a distinct [TrameMethod] name.");
                    }

                    var invokeInfo = new InvokeInfo()
                    {
                        MethodInfo = methodInfo,
                        // Methoden-Level schlägt Controller-Level (Default); beides null
                        // → im RequireAuthentication-Modus verlangt CheckAuthorisation
                        // mindestens IsAuthenticated, sonst default-allow (South-Bound).
                        AuthoriseAttribute = GetAttribute<TrameAuthoriseAttribute>(methodInfo)
                            ?? GetAttribute<TrameAuthoriseAttribute>(controllerType),
                        AnonymousAttribute = GetAttribute<TrameAnonymousAttribute>(methodInfo),
                        CompiledInvocation = CompileInvocation(controllerType, methodInfo),
                        IsAsync = typeof(Task).IsAssignableFrom(methodInfo.ReturnType),
                        HasResult = GetHasResult(methodInfo)
                    };
                    _invokeCache.TryAdd(key, invokeInfo);
                }
            }
        }

        #endregion

        public DiscoveryInfo GetDiscoveryInfo()
        {
            var discovery = _discoveryService.GetDiscoveryInfo();
            return discovery;
        }

        #region Haupt-Invoke-Methoden

        public async Task<IEnumerable<TrameResponse?>> InvokeDi(
            IEnumerable<TrameRequest> requests,
            HttpContext? context,
            ExecutionMode mode = ExecutionMode.Parallel,
            CancellationToken ct = default)
        {
            var requestList = requests.ToList();

            // Batch-Cap-Backstop: die Transport-Multi-Endpunkte gate-n früher mit 400;
            // dieser Check schützt direkte In-Process-Aufrufer (Tests, ITrameCore-Konsumenten).
            if (MaximumBatchSize > 0 && requestList.Count > MaximumBatchSize)
                throw new InvalidOperationException(
                    $"Batch exceeds MaximumBatchSize ({MaximumBatchSize}): {requestList.Count} requests.");

            // Batch-Parent-Activity (rpc.system + trame.batch.mode/count). Null ohne Listener.
            using var batchActivity = TrameTracing.StartBatch(requestList, mode);

            // Auto-detect: if requests have DependencyMappings, use batch-based
            // topological execution regardless of the specified mode.
            if (requestList.Any(r => r.DependencyMapping != null && r.DependencyMapping.Count > 0))
            {
                batchActivity?.SetTag("trame.batch.mode", "DependencyBatches");
                return await ExecuteInDependencyBatches(requestList, context, ct);
            }

            switch (mode)
            {
                case ExecutionMode.Parallel:
                    return await ExecuteInParallel(requestList, context, ct);

                case ExecutionMode.Serial:
                    return await ExecuteSequentially(requestList, context, ct);

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public async Task<TrameResponse?> InvokeDi(TrameRequest request, HttpContext? context, CancellationToken ct = default)
        {
            // Build the interceptor pipeline wrapping the actual execution.
            // Phase 1: Pipeline trägt TrameInvocationContext (HttpContext, später InvokeInfo/Activity).
            TrameInvocationDelegate pipeline = ctx => ExecuteSingleInvocationSimple(ctx.Request, ctx.HttpContext, ctx.CancellationToken);

            // Wrap interceptors in reverse order (last interceptor runs first).
            for (int i = _interceptors.Count - 1; i >= 0; i--)
            {
                var interceptor = _interceptors[i];
                var next = pipeline;
                pipeline = ctx => interceptor.InvokeAsync(ctx, next);
            }

            var stopwatch = Stopwatch.StartNew();
            // Call-Activity umschließt die ganze Interceptor-Pipeline — ein künftiger
            // Tracing-Interceptor würde zum Kind-Span, kein Double-Count. Null ohne Listener.
            using var activity = TrameTracing.StartCall(request);
            _logger.LogTrace("Starting RPC call {Controller}.{Method} with request ID {RequestId}", request.Controller, request.Method, request.Id);

            var invocationContext = new TrameInvocationContext
            {
                Request = request,
                HttpContext = context,
                CancellationToken = ct,
                Activity = activity,
            };

            TrameResponse? response;
            try
            {
                response = await pipeline(invocationContext);
                invocationContext.Response = response;
                TrameTracing.SetCallStatus(activity, response);
                _logger.LogDebug("RPC call {Controller}.{Method} completed. Status Code: {Code}", request.Controller, request.Method, response?.Code);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RPC call {Controller}.{Method} failed.", request.Controller, request.Method);
                response = InternalServerError("An internal error occurred while processing the request.", ex);
                invocationContext.Response = response;
                TrameTracing.RecordException(activity, ex);
                TrameTracing.SetCallStatus(activity, response);
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogTrace("RPC call {Controller}.{Method} took {Duration} ms", request.Controller, request.Method, stopwatch.ElapsedMilliseconds);
            }
            // RequestId auf allen Pfaden durchreichen (auch Fehlerpfade).
            if (response != null && string.IsNullOrEmpty(response.Id))
                response.Id = request?.Id ?? string.Empty;
            return response;
        }


        #endregion

        #region Ausführungs-Strategien

        private async Task<TrameResponse?> ExecuteSingleInvocationSimple(
            TrameRequest request,
            HttpContext? context,
            CancellationToken ct)
        {
            try
            {
                var controllerType = GetControllerType(request.Controller);
                if (controllerType == null)
                    return BadRequest($"Controller '{request.Controller}' not found.", HttpStatusCode.NotFound);

                string key = $"{request.Controller}_{request.Method}";
                if (!_invokeCache.TryGetValue(key, out var invokeInfo))
                    return BadRequest($"Method '{request.Method}' not found on controller '{request.Controller}'.");

                try
                {
                    await CheckAuthorisation(invokeInfo, context);

                }
                catch (UnauthorizedAccessException)
                {
                    return Unauthorized();
                }

                // Im Single-Call ohne Dependencies brauchen wir keine Alias-Auflösung:
                var parameters = BuildParameters(request?.Params, invokeInfo.MethodInfo!.GetParameters(), ct);
                if (parameters.Items == null) return parameters.Response;

                // Inject binary data for byte[] parameters
                InjectBinaryParameters(parameters.Items!, invokeInfo.MethodInfo.GetParameters(), request?.BinaryData);

                var result = await ExecuteMethod(invokeInfo, controllerType, parameters.Items, ct);
                if (result != null)
                    result.Id = request?.Id ?? string.Empty;
                return result;
            }
            catch (Exception ex)
            {
                return InternalServerError("An internal error occurred while processing the request.", ex);
            }
        }


        private async Task<IEnumerable<TrameResponse?>> ExecuteInParallel(
            IEnumerable<TrameRequest> requests,
            HttpContext? context,
            CancellationToken ct)
        {
            var requestList = requests.ToList();

            // Serialer Auth-Pre-Pass: Lookup + Autorisierung pro Request. Das ist die einzige
            // Stelle, die den HttpContext berührt — bewusst serial, bevor der Fan-out per
            // Task.WhenAll die Ausführung parallelisiert. HttpContext ist nicht threadsicher;
            // durch den Pre-Pass erreicht die parallele Region den Context nie (OnAuthorization-
            // Overrides dürfen ebenfalls nicht concurrent darauf schreiben — s. CLAUDE.md).
            var decisions = new AuthDecision[requestList.Count];
            for (int i = 0; i < requestList.Count; i++)
                decisions[i] = await ResolveAndAuthorizeAsync(requestList[i], context, ct);

            // Fan-out (OHNE Context): nur ExecuteAuthorized läuft parallel. Es berührt weder den
            // Context noch teilt es veränderbares State — ExecuteMethod erzeugt eigenen DI-Scope.
            var results = await Task.WhenAll(
                requestList.Select((request, i) =>
                    decisions[i].IsError
                        ? Task.FromResult(decisions[i].Error)
                        : ExecuteAuthorized(request, decisions[i].Info!, decisions[i].ControllerType!, ct)));

            // RequestId durchreichen (Erfolgspfade setzen die Id bereits, Fehlerpfade nicht).
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] != null && string.IsNullOrEmpty(results[i]!.Id))
                    results[i]!.Id = requestList[i].Id ?? string.Empty;
            }
            return results;
        }

        private async Task<IEnumerable<TrameResponse?>> ExecuteSequentially(
            IEnumerable<TrameRequest> requests,
            HttpContext? context,
            CancellationToken ct)
        {
            var requestList = requests.ToList();
            // Lookup nach Id für die @alias-Auflösung innerhalb der Sequenz.
            var responses = new ConcurrentDictionary<string, TrameResponse?>();
            // Ergebnis in Request-Reihenfolge sammeln — ConcurrentDictionary.Values
            // bewahrt KEINE Einfügereihenfolge, weshalb Batch-Clients, die über das
            // erste Array-Element korrelieren (WebSocket/SignalR: root[0].Id), ohne
            // diese Ordnung keinen Match finden und bis ins Timeout hängen. Der
            // Parallel-Pfad (ExecuteInParallel) gibt results bereits index-genau in
            // Request-Reihenfolge zurück — dieses Verhalten muss Serial ebenfalls halten.
            var orderedResults = new List<TrameResponse?>(requestList.Count);

            foreach (var current in requestList)
            {
                // Verwende den neuen Mechanismus zum Auflösen von Abhängigkeiten
                var resolution = ResolveParameterValues(current, responses);
                if (resolution.Error != null)
                {
                    // Error-Response muss die Request-Id tragen, damit Clients den
                    // fehlerhaften Schritt korrelieren können — analog zum topologischen
                    // Pfad (ExecuteDependentRequestAsync setzt err.Id ebenfalls).
                    if (string.IsNullOrEmpty(resolution.Error.Id))
                        resolution.Error.Id = current?.Id ?? string.Empty;
                    responses.TryAdd(current?.Id ?? String.Empty, resolution.Error);
                    orderedResults.Add(resolution.Error);
                    continue;
                }

                var effectiveRequest = new TrameRequest()
                {
                    Id = current.Id,
                    Controller = current.Controller,
                    Method = current.Method,
                    Params = resolution.ResolvedParams,
                    DependencyMapping = current.DependencyMapping
                };

                // Serial-Pfad: Lookup + Auth (ResolveAndAuthorizeAsync) und Ausführung
                // (ExecuteAuthorized) nacheinander — serial, daher kein Context-Race. Die
                // Aufteilung dient der Code-Teilung mit den Parallel-/Topologie-Pfaden.
                var decision = await ResolveAndAuthorizeAsync(effectiveRequest, context, ct);
                TrameResponse? result = decision.IsError
                    ? decision.Error
                    : await ExecuteAuthorized(effectiveRequest, decision.Info!, decision.ControllerType!, ct);

                if (result != null && string.IsNullOrEmpty(result.Id))
                    result.Id = current?.Id ?? string.Empty;
                responses.TryAdd(current?.Id ?? String.Empty, result);
                orderedResults.Add(result);
            }

            return orderedResults;
        }


        /// <summary>
        /// Führt Requests in topologischen Batches aus: unabhängige Requests laufen parallel
        /// (Task.WhenAll pro Batch), abhängige Requests warten auf ihre Provider aus früheren
        /// Batches. Der HttpContext wird <b>serial im Auth-Pre-Pass pro Batch</b> berührt —
        /// die parallele Ausführung greift nie concurrent darauf zu. Fehlgeschlagene Provider
        /// (401/Fehler/nicht-exportiert) werden propagiert: ihre Dependents laufen nicht, sondern
        /// bekommen eine erklärende 400 (siehe <see cref="ExplainUnavailability"/>), statt erst
        /// zur Laufzeit am fehlenden Alias mit nichtssagendem „Unresolved dependencies" zu
        /// scheitern. Die Transitivität fällt natürlich heraus: ein übersprungener Provider
        /// hat keine ExposedDependencies → seine Dependents werden im nächsten Batch ebenfalls
        /// als unavailable erkannt usw.
        /// </summary>
        private async Task<IEnumerable<TrameResponse?>> ExecuteInDependencyBatches(
            List<TrameRequest> requests,
            HttpContext? context,
            CancellationToken ct)
        {
            List<List<TrameRequest>> batches;
            try
            {
                batches = DependencyGraphBuilder.SortByDependencyBatches(requests);
            }
            catch (InvalidOperationException ex)
            {
                // Cycle detected – return error for all requests
                _logger.LogWarning(ex, "Circular dependency detected in request batch.");
                return requests.Select(r => new TrameResponse
                {
                    Code = (int)HttpStatusCode.BadRequest,
                    Id = r.Id,
                    Error = new TrameError { Code = 400, Message = "Circular dependency detected in request batch.", RequestId = r.Id }
                });
            }

            // alias → Graph-Key des Providers (statisch aus den DependencyMapping-Keys). Muss
            // die gleiche Key-Auflösung wie DependencyGraphBuilder nutzen, damit die Propagierung
            // den Provider in priorResponses findet (siehe GraphKey).
            var aliasToProvider = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in requests)
            {
                if (r.DependencyMapping != null)
                {
                    foreach (var kvp in r.DependencyMapping)
                        aliasToProvider[kvp.Key] = GraphKey(r);
                }
            }

            var allResponses = new List<TrameResponse?>();

            // Aufgetürmte Responses VORHERIGER Batches — abhängige Requests lesen hieraus ihre
            // @alias-Platzhalter (analog zu ExecuteSequentially) UND die Verfügbarkeits-
            // Propagierung schlägt hier nach, ob ein Provider fehlgeschlagen ist. Innerhalb
            // eines Batches sind die Requests per Kahn unabhängig, daher darf ein Batch nur
            // die Responses früherer Batches sehen (die Writes erfolgen erst nach Task.WhenAll,
            // also ohne Race innerhalb des Batches). Gekeyt nach GraphKey (nicht nach response.Id,
            // damit auch Request-Ids konsistent mit dem GraphBuilder aufgelöst werden).
            var priorResponses = new ConcurrentDictionary<string, TrameResponse?>();

            foreach (var batch in batches)
            {
                // Serialer Auth-Pre-Pass pro Batch (Context nur hier, serial): Lookup +
                // Autorisierung je Request, bevor der Fan-out die Ausführung parallelisiert.
                var decisions = new AuthDecision[batch.Count];
                for (int i = 0; i < batch.Count; i++)
                    decisions[i] = await ResolveAndAuthorizeAsync(batch[i], context, ct);

                // Fan-out (OHNE Context): Auth-Fehler → gemerkte Response; sonst Verfügbarkeits-
                // Check gegen priorResponses, dann @alias-Auflösung + ExecuteAuthorized.
                var batchResponses = await Task.WhenAll(batch.Select((request, i) =>
                    ExecuteDependentRequestAsync(request, decisions[i], priorResponses, aliasToProvider, ct)));

                for (int i = 0; i < batchResponses.Length; i++)
                {
                    var response = batchResponses[i];
                    allResponses.Add(response);
                    if (response != null)
                        priorResponses[GraphKey(batch[i])] = response;
                }
            }

            return allResponses;
        }

        /// <summary>
        /// Graph-Key eines Requests — identisch zur Auflösung in
        /// <see cref="DependencyGraphBuilder.SortByDependencyBatches"/>: Request-Id, oder
        /// Fallback <c>Controller.Method</c> bei leerer Id. Gehäkelt mit dem GraphBuilder, damit
        /// die Verfügbarkeits-Propagierung den Provider in priorResponses findet.
        /// </summary>
        private static string GraphKey(TrameRequest r)
        {
            var id = r.Id ?? string.Empty;
            return !string.IsNullOrEmpty(id) ? id : $"{r.Controller}.{r.Method}";
        }

        /// <summary>
        /// Führt einen einzelnen abhängigen Request im topologischen Pfad aus (parallel im Fan-out,
        /// KEIN HttpContext). Reihenfolge: Auth-Fehler aus dem Pre-Pass → Verfügbarkeits-
        /// Propagierung gegen priorResponses → @alias-Auflösung → ExecuteAuthorized.
        /// </summary>
        private async Task<TrameResponse?> ExecuteDependentRequestAsync(
            TrameRequest request,
            AuthDecision decision,
            ConcurrentDictionary<string, TrameResponse?> priorResponses,
            Dictionary<string, string> aliasToProvider,
            CancellationToken ct)
        {
            // Auth-Fehler aus dem Pre-Pass → bereits getracete Response.
            if (decision.IsError)
            {
                var err = decision.Error!;
                if (string.IsNullOrEmpty(err.Id)) err.Id = request?.Id ?? string.Empty;
                return err;
            }

            // Verfügbarkeits-Propagierung: ist einer der konsumierten Aliase von einem
            // fehlgeschlagenen Provider nicht bedient? Dann läuft dieser Request nicht, sondern
            // bekommt eine erklärende 400 — spart die verschwendete Ausführung und benennt die
            // Ursache (statt nichtssagendem „Unresolved dependencies" weiter unten).
            var unavailable = ExplainUnavailability(request!, aliasToProvider, priorResponses);
            if (unavailable != null)
            {
                if (string.IsNullOrEmpty(unavailable.Id)) unavailable.Id = request?.Id ?? string.Empty;
                return unavailable;
            }

            // @alias-Auflösung gegen priorResponses (bestehend).
            TrameRequest effective = request!;
            if (request?.Params != null)
            {
                var resolution = ResolveParameterValues(request, priorResponses);
                if (resolution.Error != null)
                {
                    var err = resolution.Error;
                    if (string.IsNullOrEmpty(err.Id)) err.Id = request?.Id ?? string.Empty;
                    return err;
                }
                effective = new TrameRequest()
                {
                    Id = request!.Id,
                    Controller = request.Controller,
                    Method = request.Method,
                    Params = resolution.ResolvedParams,
                    DependencyMapping = request.DependencyMapping,
                };
            }

            var response = await ExecuteAuthorized(effective, decision.Info!, decision.ControllerType!, ct);
            if (response != null && string.IsNullOrEmpty(response.Id))
                response.Id = effective?.Id ?? string.Empty;
            return response;
        }

        /// <summary>
        /// Prüft, ob alle konsumierten @alias-Platzhalter eines Requests durch erfolgreiche
        /// Provider aus <paramref name="priorResponses"/> bedient sind. Liefert eine getracete
        /// 400-Response mit erklärender Meldung, sobald ein Alias nicht verfügbar ist — andernfalls
        /// null (verfügbar). Ursachen: kein Provider; ProviderResponse ist Fehler (401/4xx/5xx);
        /// Provider hat den Alias deklariert aber nicht exportiert (Pfad matchte nichts / void).
        /// </summary>
        private TrameResponse? ExplainUnavailability(
            TrameRequest request,
            Dictionary<string, string> aliasToProvider,
            ConcurrentDictionary<string, TrameResponse?> priorResponses)
        {
            if (request.Params == null)
                return null;

            var consumed = DependencyGraphBuilder.ExtractAliases(request.Params);
            if (consumed.Count == 0)
                return null;

            foreach (var alias in consumed)
            {
                if (!aliasToProvider.TryGetValue(alias, out var providerKey))
                    return TraceCallError(request,
                        BadRequest($"Dependency '@{alias}' unavailable: no provider exposes '@{alias}'."));

                if (!priorResponses.TryGetValue(providerKey, out var providerResponse) || providerResponse == null)
                    return TraceCallError(request,
                        BadRequest($"Dependency '@{alias}' unavailable: provider '{providerKey}' produced no result."));

                if (providerResponse.Code < 200 || providerResponse.Code >= 300)
                {
                    var reason = providerResponse.Code == (int)HttpStatusCode.Unauthorized
                        ? "was unauthorized (401)"
                        : $"returned HTTP {providerResponse.Code}";
                    return TraceCallError(request,
                        BadRequest($"Dependency '@{alias}' unavailable: provider '{providerKey}' {reason}."));
                }

                if (providerResponse.ExposedDependencies == null ||
                    !providerResponse.ExposedDependencies.ContainsKey(alias))
                    return TraceCallError(request,
                        BadRequest($"Dependency '@{alias}' unavailable: provider '{providerKey}' did not expose '@{alias}'."));
            }

            return null;
        }


        /// <summary>
        /// Löst Alias-Platzhalter (@alias) in den Parametern eines Requests anhand der
        /// ExposedDependencies vorheriger Responses auf. Gibt BadRequest zurück, wenn
        /// ein Alias nicht auflösbar ist. Arbeitiert direkt auf dem nativen
        /// <see cref="TrameRequest.Params"/>-<see cref="JsonNode"/> (DeepClone, damit das
        /// DTO unverändert bleibt) und reicht den aufgelösten Knoten weiter — kein
        /// String-Roundtrip mehr.
        /// </summary>
        private (JsonNode? ResolvedParams, TrameResponse? Error) ResolveParameterValues(
            TrameRequest current,
            ConcurrentDictionary<string, TrameResponse?>? previousResponses)
        {
            var paramsNode = current.Params;
            if (paramsNode == null)
                return (null, null);

            // DeepClone: die Substitution mutiert den Knotenbaum — das DTO darf dabei
            // unverändert bleiben (ein Request-Objekt wird nicht doppelt aufgelöst,
            // aber der Clone bewahrt die Trennung sauber).
            JsonNode? parameterNode;
            try
            {
                parameterNode = paramsNode.DeepClone();
            }
            catch (Exception)
            {
                return (null, BadRequest("Invalid parameter JSON format."));
            }

            if (!ContainsAlias(parameterNode))
            {
                // Kein Alias → Strict hat nichts zu prüfen (nur @alias-sourced). Paranoid
                // prüft auch Literale: hier sind es ausschließlich Literale.
                if (AliasBindingMode == AliasBindingMode.Paranoid)
                {
                    var paranoidError = ParanoidBindingCheck(current, parameterNode);
                    if (paranoidError != null)
                        return (null, paranoidError);
                }
                return (parameterNode, null);
            }

            // Alle ExposedDependencies aus den bisherigen Responses zusammenführen.
            // ExposedDependencies ist Dictionary<string,string>: die Werte sind die
            // JSON-Form des extrahierten Fragments (ToJsonString), das in
            // ReplaceDependencyByAliasCore per JsonNode.Parse zu einem nativen Knoten wird.
            var mergedDependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (previousResponses != null)
            {
                foreach (var response in previousResponses.Values)
                {
                    if (response?.ExposedDependencies != null)
                    {
                        foreach (var kv in response.ExposedDependencies)
                        {
                            mergedDependencies[kv.Key] = kv.Value;
                        }
                    }
                }
            }

            // Führe den Ersetzungsvorgang durch (und protokolliere die Ersetzungen für
            // den optionalen Strict-Binding-Check):
            var replacements = new List<AliasReplacement>();
            var unresolved = ReplaceDependencyByAlias(parameterNode, mergedDependencies, replacements);
            if (unresolved.Count > 0)
            {
                return (null, BadRequest($"Unresolved dependencies: {string.Join(", ", unresolved)}"));
            }

            // Strict-Modus: das Fragment muss den Consumer-Typ vollständig decken. Nur
            // @alias-sourced Parameter (replacements); Literale sind bewusst gesendet.
            if (AliasBindingMode == AliasBindingMode.Strict && replacements.Count > 0)
            {
                var strictError = StrictBindingCheck(current, replacements);
                if (strictError != null)
                    return (null, strictError);
            }
            else if (AliasBindingMode == AliasBindingMode.Paranoid)
            {
                // Paranoid: wie Strict, aber für ALLE Parameter (incl. Literale) und
                // rekursiv in verschachtelte Objekte/Array-Elemente. Liest direkt aus dem
                // aufgelösten parameterNode (Alias-Daten sind inzwischen injiziert).
                var paranoidError = ParanoidBindingCheck(current, parameterNode);
                if (paranoidError != null)
                    return (null, paranoidError);
            }

            return (parameterNode, null);
        }

        /// <summary>
        /// Strict-Binding-Check: für jede @alias-Ersetzung prüfen, ob das injizierte
        /// Fragment den deklarierten Consumer-Parametertyp vollständig deckt. Jede public
        /// read-write Eigenschaft des Consumer-Typs muss im Fragment-JSON (case-insensitiv,
        /// wie STJ liest) vorhanden sein — fehlt eine, würde STJ sie im Weak-Modus still
        /// defaulten; im Strict-Modus wird das zu einem harten 400. Skalare/Collection-/
        /// Dictionary-/object-Consumer haben keine prüfbaren Eigenschaften → übersprungen.
        /// Cross-kind wird hier nicht geprüft (STJ wirft ohnehin in beiden Modi).
        /// </summary>
        private TrameResponse? StrictBindingCheck(TrameRequest current, List<AliasReplacement> replacements)
        {
            // Consumer-Methoden-Signatur aus dem Route-Cache holen (gleicher Key wie beim Dispatch).
            if (!_invokeCache.TryGetValue($"{current.Controller}_{current.Method}", out var invokeInfo)
                || invokeInfo.MethodInfo == null)
                return null; // unbekannte Methode → der normale Ausführungspfad liefert die 404

            var byName = new Dictionary<string, ParameterInfo>(StringComparer.Ordinal);
            foreach (var p in invokeInfo.MethodInfo.GetParameters())
            {
                if (!string.IsNullOrEmpty(p.Name))
                    byName[p.Name] = p;
            }

            foreach (var rep in replacements)
            {
                if (!byName.TryGetValue(rep.ParamName, out var param))
                    continue; // Parameter unbekannt → BuildParameters liefert den Default/Fehler

                var required = RequiredPropertyNames(param.ParameterType);
                if (required.Count == 0)
                    continue; // Skalar/Collection/Dictionary/object — nichts zu decken.

                // Fragment als JsonObject parsen. Kein Object → STJ wird ohnehin 400 liefern
                // (cross-kind object→scalar bzw. scalar→object); Strict muss nicht vorwegnehmen.
                JsonObject? fragment;
                try { fragment = JsonNode.Parse(rep.FragmentJson) as JsonObject; }
                catch { continue; } // unparsebar → überlasse es STJ.
                if (fragment == null) continue;

                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in fragment) present.Add(kvp.Key);

                var missing = required.Where(r => !present.Contains(r)).ToList();
                if (missing.Count > 0)
                {
                    var list = string.Join(", ", missing.Select(m => $"'{m}'"));
                    return BadRequest(
                        $"Strict alias binding: parameter '{rep.ParamName}' ({param.ParameterType.Name}) " +
                        $"requires property {list}, which is absent from the '@{rep.Alias}' fragment. " +
                        $"In weak mode this would be silently defaulted; in strict mode it is rejected.");
                }
            }
            return null;
        }

        /// <summary>Die public read-write Eigenschaftsnamen eines Typs, die STJ beim
        ///  Deserialisieren bindet (und die im Strict-Modus im Fragment vorhanden sein
        ///  müssen). Indexer, get-only und nicht-public Setter ausgenommen. Liefert leer
        ///  für Skalare/Strings/Arrays/Dictionarys/object — diese haben keine deckbaren
        ///  Eigenschaften und werden vom Strict-Check übersprungen.</summary>
        private static List<string> RequiredPropertyNames(Type type)
        {
            var names = new List<string>();
            // Nullable<T> und Enums haben keine bindbaren Eigenschaften; string ebenso.
            if (Nullable.GetUnderlyingType(type) != null || type.IsEnum || type == typeof(string))
                return names;
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;       // Indexer
                if (!p.CanWrite) continue;                              // get-only (computed)
                if (p.GetSetMethod(nonPublic: false) == null) continue; // kein public Setter
                names.Add(p.Name);
            }
            return names;
        }

        /// <summary>
        /// Paranoid-Binding-Check — die fail-lauteste Variante. Wie <see cref="StrictBindingCheck"/>,
        /// aber mit zwei Erweiterungen: (a) er prüft <b>alle</b> Parameter — <c>@alias</c>-sourced
        /// <i>und</i> Literale, die der Aufrufer bewusst gesendet hat — und (b) er prüft
        /// <b>rekursiv</b>: steigt in verschachtelte Objekte und Array-Elemente ab. Jede public
        /// read-write Eigenschaft des Consumer-Typs, in jeder Tiefe, muss im Fragment vorhanden
        /// sein, sonst 400. Cross-kind wird nicht geprüft (STJ wirft ohnehin in allen Modi);
        /// Widening (int→long) bleibt erlaubt; Subset-Fan-out (consumer ⊆ fragment) bindet auch
        /// hier, da nichts fehlt. Läuft in <see cref="ResolveParameterValues"/> für jeden Request.
        /// </summary>
        private TrameResponse? ParanoidBindingCheck(TrameRequest current, JsonNode parameterNode)
        {
            // Consumer-Methoden-Signatur aus dem Route-Cache (gleicher Key wie beim Dispatch).
            if (!_invokeCache.TryGetValue($"{current.Controller}_{current.Method}", out var invokeInfo)
                || invokeInfo.MethodInfo == null)
                return null; // unbekannte Methode → der normale Ausführungspfad liefert die 404

            var byName = new Dictionary<string, ParameterInfo>(StringComparer.Ordinal);
            foreach (var p in invokeInfo.MethodInfo.GetParameters())
            {
                if (!string.IsNullOrEmpty(p.Name))
                    byName[p.Name] = p;
            }

            // parameterNode ist ein JsonArray von {"parameterName","data"}-Einträgen. data ist
            // ein nativer JSON-Wert (Object/Array/Skalar); Skalare haben keine deckbaren
            // Eigenschaften und werden über ExtractFragment übersprungen.
            if (parameterNode is not JsonArray paramArray)
                return null;

            foreach (var entry in paramArray)
            {
                if (entry is not JsonObject entryObj)
                    continue;

                var paramName = entryObj["parameterName"]?.GetValue<string>()
                                ?? entryObj["ParameterName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(paramName))
                    continue;
                if (!byName.TryGetValue(paramName, out var param))
                    continue; // Parameter unbekannt → BuildParameters liefert den Fehler

                // Das Fragment/der Literal-Wert aus dem data-Feld (Object oder Array; Skalar/null
                // hat keine deckbaren Eigenschaften und wird übersprungen).
                var fragment = ExtractFragment(entryObj);
                if (fragment == null)
                    continue;

                var paramType = param.ParameterType;
                var missing = new List<string>();

                if (fragment is JsonObject fragObj)
                {
                    if (RequiredPropertyNames(paramType).Count > 0)
                        CollectMissing(paramType, fragObj, paramName, missing);
                }
                else if (fragment is JsonArray fragArr)
                {
                    // Top-Level-Array-Parameter (List<T>/T[]): in jedes Element absteigen,
                    // dessen Typ coverable Eigenschaften hat. CollectMissing selbst steigt
                    // in verschachtelte Arrays innerhalb von Elementen ab.
                    var elemType = GetCollectionElementType(paramType);
                    if (elemType != null && RequiredPropertyNames(elemType).Count > 0)
                    {
                        var i = 0;
                        foreach (var elem in fragArr)
                        {
                            if (elem is JsonObject elemObj)
                                CollectMissing(elemType, elemObj, $"{paramName}[{i}]", missing);
                            i++;
                        }
                    }
                }

                if (missing.Count > 0)
                {
                    var list = string.Join(", ", missing.Select(m => $"'{m}'"));
                    return BadRequest(
                        $"Paranoid binding: parameter '{paramName}' ({paramType.Name}) " +
                        $"is not fully covered by its fragment. Missing: {list}. In weak mode these " +
                        $"would be silently defaulted; in strict mode the top-level check would pass " +
                        $"(it checks only @alias parameters and does not recurse); paranoid mode " +
                        $"enforces full coverage of every parameter — including literals — at every depth.");
                }
            }
            return null;
        }

        /// <summary>
        /// Liest das native <c>data</c>-Feld eines TrameParameter-Eintrags und liefert es als
        /// <see cref="JsonNode"/> — ein <see cref="JsonObject"/> oder <see cref="JsonArray"/>,
        /// je nachdem, was der Literal/das Fragment ist; für Skalare/null <c>null</c> (nichts
        /// zu decken). <c>data</c> ist seit der Wire-Vereinfachung ein nativer JSON-Wert (kein
        /// JSON-String mehr), daher entfällt das frühere <c>JsonNode.Parse</c> des String-Inhalts.
        /// </summary>
        private static JsonNode? ExtractFragment(JsonObject entryObj)
        {
            var dataNode = entryObj["data"] ?? entryObj["Data"];
            if (dataNode is JsonObject directObj)
                return directObj;
            if (dataNode is JsonArray directArr)
                return directArr;
            // Skalare (Zahl/String/Bool/null) haben keine deckbaren Eigenschaften → null.
            return null;
        }

        /// <summary>
        /// Sammelt fehlende deckungspflichtige Eigenschaften rekursiv. Für jede public read-write
        /// Eigenschaft des Consumer-Typs, die im Fragment fehlt (case-insensitiv, wie STJ liest),
        /// wird der dotted Pfad in <paramref name="missing"/> notiert. Für vorhandene Eigenschaften,
        /// deren deklarierter Typ ein coverable Object ist und deren Fragment-Wert ein nicht-null
        /// JsonObject ist, steigt der Check ab (Tiefe). Für Collection-Typen (List&lt;T&gt;/T[]/
        /// IEnumerable&lt;T&gt;) steigt er in jedes JsonArray-Element ab. Die Rekursion folgt der
        /// Fragment-Struktur (ein Baum) → endet von selbst, kein Zyklus-Risiko.
        /// </summary>
        private static void CollectMissing(
            Type consumerType, JsonObject fragment, string path, List<string> missing)
        {
            var required = RequiredPropertyNames(consumerType);
            if (required.Count == 0)
                return;

            // Vorhandene Fragment-Schlüssel (case-insensitiv, wie STJ liest).
            var present = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in fragment)
                present[kvp.Key] = kvp.Value;

            foreach (var propName in required)
            {
                var childPath = string.IsNullOrEmpty(path) ? propName : $"{path}.{propName}";

                if (!present.ContainsKey(propName))
                {
                    missing.Add(childPath);
                    continue;
                }

                // Vorhanden → ggf. in den deklarierten Typ absteigen, wenn das Fragment dort
                // ein Objekt ist (Objekt-Eigenschaft) oder ein Array (Collection-Eigenschaft).
                var propType = consumerType.GetProperty(propName)?.PropertyType;
                if (propType == null)
                    continue;

                switch (present[propName])
                {
                    case JsonObject nestedFrag:
                        if (RequiredPropertyNames(propType).Count > 0)
                            CollectMissing(propType, nestedFrag, childPath, missing);
                        break;
                    case JsonArray arrFrag:
                        var elemType = GetCollectionElementType(propType);
                        if (elemType != null && RequiredPropertyNames(elemType).Count > 0)
                        {
                            var i = 0;
                            foreach (var elem in arrFrag)
                            {
                                if (elem is JsonObject elemObj)
                                    CollectMissing(elemType, elemObj, $"{childPath}[{i}]", missing);
                                i++;
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Für einen Collection-Typ (Array oder generische Sequenz wie List&lt;T&gt;/
        /// IEnumerable&lt;T&gt;/ICollection&lt;T&gt;) der Element-Typ; sonst <c>null</c>.
        /// Dictionarys sind bewusst ausgenommen (offene Schlüsselmenge, keine
        /// "fehlende Eigenschaft"-Semantik auf der Werteseite).
        /// </summary>
        private static Type? GetCollectionElementType(Type type)
        {
            if (type.IsArray)
                return type.GetElementType();
            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>)
                    || def == typeof(ICollection<>) || def == typeof(IReadOnlyList<>)
                    || def == typeof(IReadOnlyCollection<>) || def == typeof(HashSet<>)
                    || def == typeof(ISet<>))
                    return type.GetGenericArguments()[0];
            }
            // IEnumerable<T>-Schnittstellen am konkreten Typ (z. B. custom Collections).
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    && iface.GetGenericArguments()[0] != typeof(object))
                    return iface.GetGenericArguments()[0];
            }
            return null;
        }

        /// <summary>
        /// Durchläuft den JsonNode rekursiv und gibt zurück, ob ein String-Wert gefunden wurde, der mit '@' beginnt.
        /// </summary>
        private bool ContainsAlias(JsonNode? node)
        {
            if (node == null) return false;
            if (node is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<string>(out var strValue))
                {
                    return strValue.Trim().StartsWith("@"); //|| strValue.Trim().StartsWith("\"@");
                }
            }
            else if (node is JsonObject jsonObj)
            {
                foreach (var kvp in jsonObj)
                {
                    if (ContainsAlias(kvp.Value))
                        return true;
                }
            }
            else if (node is JsonArray jsonArr)
            {
                foreach (var item in jsonArr)
                {
                    if (ContainsAlias(item))
                        return true;
                }
            }
            return false;
        }


        #endregion

        #region Hilfsmethoden für die Ausführung

        private HashSet<string> ReplaceDependencyByAlias(
            JsonNode? node,
            Dictionary<string, string> exposedDependencies,
            List<AliasReplacement>? replacements = null)
        {
            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            ReplaceDependencyByAliasCore(node, exposedDependencies, unresolved, replacements);
            return unresolved;
        }

        private void ReplaceDependencyByAliasCore(
            JsonNode? node,
            Dictionary<string, string> exposedDependencies,
            HashSet<string> unresolved,
            List<AliasReplacement>? replacements)
        {
            if (node == null) return;
            if (node is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<string>(out var strValue) && strValue.StartsWith("@"))
                {
                    string alias = strValue.Substring(1); // z. B. "firstId"
                    if (exposedDependencies.TryGetValue(alias, out var actualValue))
                    {
                        // actualValue ist die JSON-Form des extrahierten Werts (siehe
                        // exposed[alias] = extracted.ToJsonString()) — z. B. "2" (int),
                        // "\"hi\"" (string) oder "{...}" (Objekt). Da TrameParameter.Data
                        // nun ein nativer JsonNode-Wert ist, parsen wir actualValue in
                        // seinen nativen Typ (Zahl/String/Object) und injizieren ihn direkt.
                        // Das frühere JsonValue.Create (das einen String-Knoten erzeugte,
                        // damit `data` ein String blieb) entfällt — BuildParameters bindet
                        // den nativen Knoten per JsonSerializer.Deserialize(JsonNode, Type).
                        // Strict-Check-Buchhaltung: protokolliere die Ersetzung, wenn der
                        // Alias direkt der Wert eines TrameParameter-Objekts ist (Eltern-
                        // JsonObject hat einen parameterName-/ParameterName-Eintrag). Für
                        // in Literalen verschachtelte Aliase wird nicht gebucht — der Strict-
                        // Check gilt dem Top-Level-Transfer (siehe ResolveParameterValues).
                        if (replacements != null && node.Parent is JsonObject repParent)
                        {
                            var paramName = repParent["parameterName"]?.GetValue<string>()
                                            ?? repParent["ParameterName"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(paramName))
                                replacements.Add(new AliasReplacement(paramName, alias, actualValue));
                        }
                        var newNode = JsonNode.Parse(actualValue);
                        ReplaceInParent(node, newNode);
                    }
                    else
                    {
                        unresolved.Add(alias);
                    }
                }
            }
            else if (node is JsonObject jsonObj)
            {
                foreach (var property in jsonObj.ToList())
                {
                    if (property.Value != null)
                    {
                        ReplaceDependencyByAliasCore(property.Value, exposedDependencies, unresolved, replacements);
                    }
                }
            }
            else if (node is JsonArray jsonArr)
            {
                foreach (var t in jsonArr)
                {
                    ReplaceDependencyByAliasCore(t, exposedDependencies, unresolved, replacements);
                }
            }
        }

        /// <summary>Protokoll einer @alias-Ersetzung für den Strict-Binding-Check:
        ///  welcher Consumer-Parameter hat welchen Alias durch welches Fragment erhalten?</summary>
        private readonly record struct AliasReplacement(string ParamName, string Alias, string FragmentJson);

        private static void ReplaceInParent(JsonNode oldNode, JsonNode newNode)
        {
            // Erzeuge einen Klon des neuen Knotens, damit dieser keinen Elternbezug hat.
            var newNodeClone = JsonNode.Parse(newNode.ToJsonString());
            var parent = oldNode.Parent;
            if (parent is JsonObject parentObj)
            {
                // Durchlaufe alle Properties des Eltern-Objekts.
                foreach (var kvp in parentObj.ToList())
                {
                    if (kvp.Value == oldNode)
                    {
                        // Ersetze den alten Knoten durch den neuen Knoten.
                        parentObj[kvp.Key] = newNodeClone;
                        break;
                    }
                }
            }
            else if (parent is JsonArray parentArr)
            {
                // Durchlaufe das Array und ersetze den alten Knoten.
                for (int i = 0; i < parentArr.Count; i++)
                {
                    if (parentArr[i] == oldNode)
                    {
                        parentArr[i] = newNodeClone;
                        break;
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Knoten ohne Eltern können nicht ersetzt werden.");
            }
        }


        /// <summary>
        /// Ergebnis des seriellen Auth-Pre-Pass: entweder auflösbare Route + autorisiert
        /// (<see cref="IsError"/> = false) oder eine getracete Fehler-Response.
        /// </summary>
        private readonly struct AuthDecision
        {
            public readonly InvokeInfo? Info;
            public readonly Type? ControllerType;
            public readonly TrameResponse? Error;
            public AuthDecision(InvokeInfo? info, Type? type, TrameResponse? error)
            { Info = info; ControllerType = type; Error = error; }
            public bool IsError => Error != null;
        }

        /// <summary>
        /// Phase 1 (serial-sicher): löst Controller &amp; Methode auf und führt die Autorisierung
        /// durch — das ist die <b>einzige</b> Stelle, die im Batch-Pfad den <see cref="HttpContext"/>
        /// berührt (über <see cref="CheckAuthorisation"/> → <c>OnAuthorization(context)</c>).
        /// Wird bewusst <b>serial im Pre-Pass</b> aufgerufen, bevor die parallele Ausführung
        /// (Phase 2 = <see cref="ExecuteAuthorized"/>) per <c>Task.WhenAll</c> fächert, sodass
        /// der Context nie concurrent erreicht wird. Pre-Execution-Fehler (Controller/Method
        /// nicht gefunden, unauthorized) werden mit einem eigenen <c>TrameCall</c>-Span getraced,
        /// damit auch 4xx/401 im Telemetry sichtbar bleiben.
        /// </summary>
        private async Task<AuthDecision> ResolveAndAuthorizeAsync(
            TrameRequest request, HttpContext? context, CancellationToken ct)
        {
            var controllerType = GetControllerType(request.Controller);
            if (controllerType == null)
                return new AuthDecision(null, null, TraceCallError(request,
                    BadRequest($"Controller '{request.Controller}' not found.", HttpStatusCode.NotFound)));

            string key = $"{request.Controller}_{request.Method}";
            if (!_invokeCache.TryGetValue(key, out var invokeInfo))
                return new AuthDecision(null, null, TraceCallError(request,
                    BadRequest($"Method '{request.Method}' not found on controller '{request.Controller}'.")));

            try
            {
                await CheckAuthorisation(invokeInfo, context);
            }
            catch (UnauthorizedAccessException)
            {
                return new AuthDecision(null, null, TraceCallError(request, Unauthorized()));
            }

            return new AuthDecision(invokeInfo, controllerType, null);
        }

        /// <summary>
        /// Phase 2 (parallel-sicher, KEIN <see cref="HttpContext"/>): übernimmt Parameter-Bau,
        /// Methoden-Ausführung und Exposes-Extraktion aus dem ehemaligen ExecuteSingleInvocation.
        /// <see cref="ExecuteMethod"/> erzeugt seinen eigenen DI-Scope pro Call und ist von sich
        /// aus parallel-safe. Der per-Request <c>TrameCall</c>-Span wird hier geöffnet und
        /// <c>Activity.Current</c> darauf gesetzt, damit User-Code-Spans während ExecuteMethod
        /// korrekt unter dem Call-Span schachteln (wie beim ehemaligen ExecuteSingleInvocation).
        /// Kein <see cref="InjectBinaryParameters"/> — die Binary-Asymmetrie zum Single-Call-Pfad
        /// bleibt unverändert.
        /// </summary>
        private async Task<TrameResponse?> ExecuteAuthorized(
            TrameRequest request,
            InvokeInfo invokeInfo,
            Type controllerType,
            CancellationToken ct)
        {
            using var activity = TrameTracing.StartCall(request);
            var previous = Activity.Current;
            Activity.Current = activity;
            try
            {
                var parameters = BuildParameters(request.Params, invokeInfo.MethodInfo!.GetParameters(), ct);
                // BuildParameters liefert bei Fehler (Cap überschritten, Duplikat-Name,
                // falscher Typ, ungültiges JSON) (Items: null, Response: BadRequest). Der
                // Vergleich `== default` ((null,null)) fängt (null, BadRequest) NICHT ab —
                // dann fiel der Fehler durch zu ExecuteMethod(null-Args) → NRE → 500, und
                // die saubere 400 (z. B. „überschreitet MaxParameterArrayLength") ging
                // verloren. Auf Items==null prüfen (wie der Single-Call-Pfad).
                if (parameters.Items == null) return Status(parameters.Response);

                var result = await ExecuteMethod(invokeInfo, controllerType, parameters.Items!, ct);
                if (result != null)
                    result.Id = request.Id;

                // Exposes-Extraktion nur bei Erfolg (2xx). Ein Fehler-Response exposiert
                // nichts — auch nicht, wenn es (wie TrameResults.Error(ProblemDetails)) ein
                // non-null Data mit Pfad-tragenden Feldern (title/status/detail) trägt. Sonst
                // würden Werte aus einem Fehler-Payload als ExposedDependencies geliefert und
                // an Dependents weitergereicht. Dependents sehen über die Propagierung (s.
                // ExplainUnavailability: „returned HTTP <code>") ohnehin den Fehlverlauf;
                // dieser Gate verhindert zusätzlich den Datenleck aus dem Fehler-Payload.
                if (request.DependencyMapping != null && result != null && result.IsSuccess && result.Data.HasValue)
                {
                    var exposed = new Dictionary<string, string>();
                    // DependencyResolver extrahiert Werte aus dem strukturierten Data (JsonElement).
                    // byte[]-Returns liegen nicht mehr in Data (sondern in Content) → HasValue ist
                    // dort false und der Block wird sauber übersprungen (kein 500 durch unparsebares
                    // Base64 mehr — der alte Sonderfall entfällt mit dem Single-Pass-Modell).
                    foreach (var kv in request.DependencyMapping)
                    {
                        string alias = kv.Key;
                        string jsonPath = kv.Value;
                        try
                        {
                            var extracted = DependencyResolver.ExtractValue(
                                result.Data.Value, jsonPath, MaxDependencyPathLength, AllowRecursiveDescent);
                            if (extracted != null)
                                // ToJsonString (nicht ToString!) bewahrt den JSON-Typ: Zahl/Bool/Object
                                // bleiben korrekt kodiert, damit die @alias-Substitution weiter unten
                                // typgetreu injiziert wird (siehe ReplaceDependencyByAlias).
                                exposed[alias] = extracted.ToJsonString();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to extract dependency for alias {Alias} (path {Path}) — result data could not be resolved.",
                                alias, jsonPath);
                        }
                    }
                    result.ExposedDependencies = exposed;
                }

                return Status(result);
            }
            catch (Exception ex)
            {
                TrameTracing.RecordException(activity, ex);
                return Status(InternalServerError("An internal error occurred while processing the request.", ex));
            }
            finally
            {
                Activity.Current = previous;
            }

            // Setzt den OTel-Status aus der Response und reicht sie durch (ein Aufruf pro Return-Pfad).
            TrameResponse? Status(TrameResponse? response)
            {
                TrameTracing.SetCallStatus(activity, response);
                return response;
            }
        }

        /// <summary>
        /// Öffnet einen kurzen <c>TrameCall</c>-Span für einen Pre-Execution-Fehler (Auth/Lookup
        /// aus <see cref="ResolveAndAuthorizeAsync"/> sowie Verfügbarkeits-/Auflösungs-Fehler aus
        /// dem topologischen Pfad), setzt den OTel-Status aus der Response und disposed. Sicherstellt,
        /// dass jeder Request genau einen Span erhält — Fehlerpfade hier, Ausführung in
        /// <see cref="ExecuteAuthorized"/>.
        /// </summary>
        private TrameResponse? TraceCallError(TrameRequest request, TrameResponse? response)
        {
            using var activity = TrameTracing.StartCall(request);
            TrameTracing.SetCallStatus(activity, response);
            return response;
        }

        #endregion

        #region Parameter-Bau und Validierung

        private (object?[]? Items, TrameResponse? Response) BuildParameters(
            JsonNode? paramsNode,
            ParameterInfo[] methodParams,
            CancellationToken ct)
        {
            try
            {
                // Parameter-Liste aus dem nativen Params-JsonArray (Einträge mit
                // parameterName/num/data, data ist nativer JsonNode). Direkter Durchlauf
                // statt Deserialize<List<TrameParameter>>(string) — kein String-Roundtrip.
                var entries = new List<(string Name, int Num, JsonNode? Data)>();
                if (paramsNode is JsonArray arr)
                {
                    foreach (var entry in arr)
                    {
                        if (entry is not JsonObject eo)
                            continue;
                        var name = eo["parameterName"]?.GetValue<string>()
                                   ?? eo["ParameterName"]?.GetValue<string>()
                                   ?? string.Empty;
                        int num = 0;
                        var numNode = eo["num"] ?? eo["Num"];
                        if (numNode is JsonValue nv && nv.TryGetValue<int>(out var ni))
                            num = ni;
                        var data = eo["data"] ?? eo["Data"];
                        entries.Add((name, num, data));
                    }
                }

                // Nach Parameternamen indizieren. Doppelte Namen als BadRequest melden,
                // statt über ToDictionary eine unbehandelte Exception auszulösen.
                var byName = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                foreach (var (name, _, data) in entries)
                {
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (byName.ContainsKey(name))
                        return (null, BadRequest($"Duplicate parameter name '{name}'."));
                    byName[name] = data;
                }

                // Parameter ohne CancellationToken in der Reihenfolge der Methodensignatur.
                // Der clientseitige TrameCall-Builder vergibt Num ohnehin ohne Kenntnis der
                // CancellationToken-Parameter, daher wird hier an der bereinigten Folge gebunden.
                var nonTokenParams = methodParams
                    .Where(p => p.ParameterType != typeof(CancellationToken))
                    .ToArray();

                var parameters = new object?[methodParams.Length];
                foreach (var param in methodParams)
                {
                    if (param.ParameterType == typeof(CancellationToken))
                    {
                        parameters[param.Position] = ct;
                        continue;
                    }

                    string name = param.Name ?? $"param{param.Position}";

                    // 1. Bindung nach Parameternamen (bevorzugt).
                    bool matched = byName.TryGetValue(name, out var jsonValue);

                    // 2. Positions-Fallback: über Num, falls kein Name gepasst hat.
                    //    byte[]-Parameter werden hier ausgespart, da sie nachträglich über
                    //    InjectBinaryParameters aus TrameRequest.BinaryData gefüllt werden.
                    if (!matched && param.ParameterType != typeof(byte[]))
                    {
                        int positionalIndex = Array.IndexOf(nonTokenParams, param);
                        foreach (var e in entries)
                        {
                            if (e.Num == positionalIndex)
                            {
                                jsonValue = e.Data;
                                matched = true;
                                break;
                            }
                        }
                    }

                    if (matched)
                    {
                        try
                        {
                            // jsonValue ist ein nativer JsonNode (oder null bei fehlendem
                            // data-Feld). Ein JSON-null-Wert ist ein nicht-null JsonNode und
                            // wird hier deserialisiert (→ null für Ref-Typen, 400 für nicht-
                            // nullable Werttypen); ein fehlendes data-Feld ist C#-null → Default.
                            if (jsonValue != null)
                            {
                                parameters[param.Position] =
                                    JsonSerializer.Deserialize(jsonValue, param.ParameterType,
                                        _jsonSerializerOptions);
                            }
                        }
                        catch (Exception)
                        {
                            return (null, BadRequest($"Parameter '{name}' cannot be converted to type '{param.ParameterType.Name}'."));
                        }

                        // Kardinalitäts-Cap auf Collection-Parametern (Top-Level). Schützt
                        // vor Riesen-Arrays, die insb. beim @alias-Whole-Collection-Passthrough
                        // serverseitig erzeugt werden (Body-Size-Limits greifen dort nicht).
                        // string ausnehmen (ist IEnumerable<char>); byte[] wird via
                        // InjectBinaryParameters gefüllt und ist keine Kardinalitäts-Frage.
                        if (MaxParameterArrayLength > 0
                            && parameters[param.Position] is not string and not byte[]
                            && parameters[param.Position] is ICollection colParam
                            && colParam.Count > MaxParameterArrayLength)
                        {
                            return (null, BadRequest(
                                $"Parameter '{name}' überschreitet MaxParameterArrayLength " +
                                $"({MaxParameterArrayLength}; Ist {colParam.Count}). " +
                                "Paginieren oder Cap erhöhen (0 = unbegrenzt)."));
                        }
                    }
                    else
                    {
                        // Falls der Parameter fehlt, den Default-Wert verwenden
                        parameters[param.Position] = GetDefault(param.ParameterType);
                    }
                }
                return (parameters, null);
            }
            catch (Exception)
            {
                return (null, BadRequest("Error processing request parameters."));
            }
        }


        /// <summary>
        /// Injects binary data from TrameRequest.BinaryData into byte[] parameters.
        /// If a method has a byte[] parameter, it receives the raw binary payload
        /// instead of a Base64-encoded JSON string.
        /// </summary>
        private static void InjectBinaryParameters(object?[] parameters, ParameterInfo[] methodParams, byte[]? binaryData)
        {
            if (binaryData == null || binaryData.Length == 0) return;

            // Find the first byte[] parameter and inject the binary data
            for (int i = 0; i < methodParams.Length; i++)
            {
                if (methodParams[i].ParameterType == typeof(byte[]))
                {
                    parameters[i] = binaryData;
                    break; // Only inject into the first byte[] parameter
                }
            }
        }
        private object? GetDefault(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }


        #endregion

        #region Methoden-Ausführung und Reflection-Handling

        private static bool GetHasResult(MethodInfo methodInfo)
        {
            var returnType = methodInfo.ReturnType;

            if (returnType == typeof(void))
                return false;

            // IAsyncEnumerable<T> always has results
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                return true;

            if (typeof(Task).IsAssignableFrom(returnType))
            {
                return returnType.IsGenericType;
            }

            return true;
        }

        /// <summary>
        /// Returns true if the type is IAsyncEnumerable&lt;T&gt;.
        /// </summary>
        private static bool IsAsyncEnumerable(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                return true;

            // Check if the type implements IAsyncEnumerable<T> (e.g. compiler-generated state machines)
            return type.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        }

        /// <summary>
        /// Returns the element type of an IAsyncEnumerable&lt;T&gt;, or null if not applicable.
        /// </summary>
        private static Type? GetAsyncEnumerableElementType(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                return type.GetGenericArguments()[0];

            // Check interfaces for compiler-generated types
            var iface = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
            if (iface != null)
                return iface.GetGenericArguments()[0];

            // Also handle Task<IAsyncEnumerable<T>>
            if (typeof(Task).IsAssignableFrom(type) && type.IsGenericType)
            {
                var taskResultType = type.GetGenericArguments()[0];
                if (IsAsyncEnumerable(taskResultType))
                    return GetAsyncEnumerableElementType(taskResultType);
            }

            return null;
        }

        private static Func<object, object?[], object?> CompileInvocation(Type controllerType, MethodInfo methodInfo)
        {
            // Parameter: (object instance, object?[] args)
            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var argsParam = Expression.Parameter(typeof(object?[]), "args");

            var parameters = methodInfo.GetParameters();
            var argExpressions = new Expression[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var paramInfo = parameters[i];
                // args[i]
                var indexed = Expression.ArrayIndex(argsParam, Expression.Constant(i));

                Expression converted;
                if (paramInfo.ParameterType == typeof(CancellationToken))
                {
                    // CancellationToken wird zur Laufzeit vom Invoker eingesetzt.
                    converted = Expression.Default(typeof(CancellationToken));
                }
                else
                {
                    converted = Expression.Convert(indexed, paramInfo.ParameterType);
                }

                argExpressions[i] = converted;
            }

            // ((ControllerType)instance).MethodName(...)
            var castInstance = Expression.Convert(instanceParam, controllerType);
            var call = Expression.Call(castInstance, methodInfo, argExpressions);

            Expression body;
            if (methodInfo.ReturnType == typeof(void))
            {
                body = Expression.Block(call, Expression.Constant(null, typeof(object)));
            }
            else if (methodInfo.ReturnType.IsValueType)
            {
                body = Expression.Convert(call, typeof(object));
            }
            else
            {
                body = Expression.TypeAs(call, typeof(object));
            }

            var lambda = Expression.Lambda<Func<object, object?[], object?>>(body, instanceParam, argsParam);
            return lambda.Compile();
        }

        private async Task<TrameResponse?> ExecuteMethod(
            InvokeInfo invokeInfo,
            Type? controllerType,
            object?[]? parameters,
            CancellationToken ct)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            if (controllerType != null)
            {
                var instance = scope.ServiceProvider.GetService(controllerType);

                if (instance == null)
                    return InternalServerError($"Failed to resolve controller '{controllerType.Name}' from the DI container.");

                try
                {
                    var compiled = invokeInfo.CompiledInvocation;
                    if (compiled == null)
                    {
                        return InternalServerError($"Method '{invokeInfo.MethodInfo?.Name}' is not compiled.");
                    }

                    // Ersetze ggf. CancellationToken-Platzhalter durch den realen Wert
                    var methodParams = invokeInfo.MethodInfo!.GetParameters();
                    for (int i = 0; i < methodParams.Length; i++)
                    {
                        if (methodParams[i].ParameterType == typeof(CancellationToken))
                        {
                            parameters![i] = ct;
                        }
                    }

                    var result = compiled(instance, parameters!);

                    // Handle IAsyncEnumerable<T> directly (non-Task)
                    if (IsAsyncEnumerable(invokeInfo.MethodInfo!.ReturnType) && result != null)
                    {
                        var elementType = GetAsyncEnumerableElementType(invokeInfo.MethodInfo.ReturnType)!;
                        object list;
                        try
                        {
                            list = await ConsumeAsyncEnumerable(result, elementType, ct, MaxResultElementCount);
                        }
                        catch (ResultCardinalityExceededException ex)
                        {
                            return BadRequest(ex.Message, HttpStatusCode.RequestEntityTooLarge);
                        }
                        return Ok(JsonSerializer.SerializeToUtf8Bytes(list, _jsonSerializerOptions));
                    }

                    // Handle Task<IAsyncEnumerable<T>>
                    if (invokeInfo.IsAsync && result is Task taskResult)
                    {
                        await taskResult;

                        if (invokeInfo.HasResult)
                        {
                            var resultValue = GetTaskResult(taskResult);

                            // Check if the Task result is IAsyncEnumerable<T>
                            if (resultValue != null && IsAsyncEnumerable(resultValue.GetType()))
                            {
                                var elementType = GetAsyncEnumerableElementType(resultValue.GetType())!;
                                object list;
                                try
                                {
                                    list = await ConsumeAsyncEnumerable(resultValue, elementType, ct, MaxResultElementCount);
                                }
                                catch (ResultCardinalityExceededException ex)
                                {
                                    return BadRequest(ex.Message, HttpStatusCode.RequestEntityTooLarge);
                                }
                                return Ok(JsonSerializer.SerializeToUtf8Bytes(list, _jsonSerializerOptions));
                            }

                            return ReturnResponse(resultValue, instance?.GetType());
                        }

                        // Task ohne Ergebnis (async void-ähnlich) → 204 No Content.
                        return NoContent();
                    }

                    // Synchrone void-Methoden → 204 No Content.
                    if (!invokeInfo.HasResult)
                        return NoContent();

                    return ReturnResponse(result, controllerType);
                }
                catch (TargetInvocationException tie)
                {
                    return InternalServerError("An internal error occurred while processing the request.", tie.InnerException);
                }
            }
            else
            {
                return BadRequest("No Controller", HttpStatusCode.BadRequest);
            }
        }

        private object? GetTaskResult(Task taskResult)
        {
            if (taskResult is null)
                return null;

            // Für Task&lt;T&gt; greifen wir direkt über die generische Eigenschaft auf das Result zu.
            // Dies spart Reflection (GetProperty) pro Request.
            if (taskResult.GetType().IsGenericType)
            {
                return ((dynamic)taskResult).Result;
            }

            return null;
        }
        /// <summary>
        /// Consumes an IAsyncEnumerable<T> and collects all elements into a List<T>.
        /// Uses a generic helper method to avoid reflection issues with compiler-generated state machines.
        /// </summary>
        private static async Task<object> ConsumeAsyncEnumerable(
            object asyncEnumerable, Type elementType, CancellationToken ct, int maxElements)
        {
            var helperType = typeof(AsyncEnumerableConsumer<>).MakeGenericType(elementType);
            var method = helperType.GetMethod("Consume")!;
            var task = (Task)method.Invoke(null, new[] { asyncEnumerable, ct, maxElements })!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        /// <summary>
        /// Signalisiert, dass ein IAsyncEnumerable-Resultat die MaxResultElementCount
        /// überschritten hat. Wird im Streaming-Pfad geworfen, damit der Konsum früh
        /// abgebrochen wird (bevor eine Riesen-Liste allokiert ist) statt erst nach
        /// vollständiger Materialisierung.
        /// </summary>
        private sealed class ResultCardinalityExceededException : Exception
        {
            public ResultCardinalityExceededException(int cap, int count)
                : base($"Stream überschreitet MaxResultElementCount ({cap}; bei Element {count}). Paginieren oder Cap erhöhen (0 = unbegrenzt).")
            { }
        }

        /// <summary>
        /// Generic helper class for consuming IAsyncEnumerable<T> without reflection.
        /// </summary>
        private static class AsyncEnumerableConsumer<T>
        {
            public static async Task<List<T>> Consume(IAsyncEnumerable<T> source, CancellationToken ct, int maxElements)
            {
                var list = new List<T>();
                await foreach (var item in source.WithCancellation(ct))
                {
                    // Early-Stop: nicht erst die komplette Riesen-Sequenz allokieren.
                    if (maxElements > 0 && list.Count >= maxElements)
                        throw new ResultCardinalityExceededException(maxElements, list.Count);
                    list.Add(item);
                }
                return list;
            }
        }
        #endregion

        #region Hilfsmethoden & Response-Fabriken

        private static T? GetAttribute<T>(MemberInfo member) where T : Attribute
            => (T?)member.GetCustomAttributes(typeof(T), false).FirstOrDefault();

        private TrameResponse ReturnResponse(object? result, Type? instanceType)
        {
            // null-Ergebnis → 200 mit leerem Data (kein 204 — die Methode hat erfolgreich
            // kein Ergebnis geliefert, das ist ein anderer Zustand als void/Task).
            if (result is null) return Ok((byte[]?)null);

            if (result is TrameResponse trameResp)
                return trameResp;

            // Binary return: Bytes landen ausschließlich in Content (kein Base64-String
            // mehr in Data → keine doppelte Belegung, kein Parse-Problem im Dep-Pfad).
            if (result is byte[] bytes)
                return new TrameResponse { Code = (int)HttpStatusCode.OK, Content = bytes };

            // Kardinalitäts-Cap auf Top-Level-Collection-Resultaten (List/Array/Dictionary).
            // string ist IEnumerable<char> → ausgenommen. Streaming-Returns werden vorher
            // konsumiert und in ConsumeAsyncEnumerable frühzeitig gestoppt (siehe unten),
            // landen aber als materialisierte List<T> auch hier — dieser Check ist der
            // einheitliche Rückfall für Task<T>/Sync-Returns.
            if (MaxResultElementCount > 0
                && result is not string
                && result is ICollection colResult
                && colResult.Count > MaxResultElementCount)
            {
                return BadRequest(
                    $"Rückgabewert überschreitet MaxResultElementCount " +
                    $"({MaxResultElementCount}; Ist {colResult.Count}). " +
                    "Paginieren oder Cap erhöhen (0 = unbegrenzt).",
                    HttpStatusCode.RequestEntityTooLarge);
            }

            // Single-Pass: object → rohe UTF-8-Bytes (kein JsonDocument-Baum). Der
            // Transport-Converter schreibt DataBytes via WriteRawValue direkt in den
            // Wire. Data bleibt null und wird nur bei Dep-Chaining lazy gelesen.
            try { return Ok(JsonSerializer.SerializeToUtf8Bytes(result, _jsonSerializerOptions)); }
            catch
            {
                _logger.LogWarning("Failed to serialize result of type {ResultType} from controller {ControllerType}",
                    result.GetType().Name, instanceType?.FullName);
                return InternalServerError("Failed to serialize the response.");
            }
        }

        private Type? GetControllerType(string controllerName)
            => _routeHandlers.GetValueOrDefault(controllerName);

        private async Task CheckAuthorisation(InvokeInfo invokeInfo, HttpContext? context)
        {
            // [TrameAnonymous] → immer erlaubt, auch im RequireAuthentication-Modus
            // (bewusst öffentlicher Endpunkt, z.B. Health/Ping). Greift nur am
            // REST-Transport (per-Request-Gate im Invoker).
            if (invokeInfo.AnonymousAttribute != null) return;

            // Explizite [TrameAuthorise] (Method- oder Controller-Level) →
            // Role/Authentication-Check wie bisher.
            if (invokeInfo.AuthoriseAttribute != null)
            {
                if (!await invokeInfo.AuthoriseAttribute.OnAuthorization(context))
                    throw new UnauthorizedAccessException();
                return;
            }

            // Unbestückte Methode:
            //  - RequireAuthentication=false (South-Bound-Default): erlaubt (default-allow).
            //  - RequireAuthentication=true  (North-Bound): IsAuthenticated verlangen.
            if (RequireAuthentication)
            {
                var authenticated = context?.User?.Identity?.IsAuthenticated ?? false;
                if (!authenticated)
                    throw new UnauthorizedAccessException();
            }
        }

        #endregion

        #region Response-Generierung

        private TrameResponse BadRequest(string message, HttpStatusCode code = HttpStatusCode.BadRequest)
            => CreateError((int)code, message, CategoryFor((int)code));

        // Erfolg: strukturierter Ergebniswert in Data (roh, ein Pass — kein Double-Wrapping).
        // Der Bulk-Pfad nutzt Ok(byte[]) mit rohen UTF-8-Bytes (DataBytes); Data bleibt
        // null und wird erst lazy materialisiert, wenn ein Reader (Dep-Chaining, Tests)
        // darauf zugreift. Ok(JsonElement?) bleibt für den Legacy/Spezial-Pfad.
        private TrameResponse Ok(JsonElement? data)
            => new() { Code = TrameErrorCodes.Ok, Data = data };

        private TrameResponse Ok(byte[]? dataBytes)
            => new() { Code = TrameErrorCodes.Ok, DataBytes = dataBytes };

        private TrameResponse NoContent()
            => new() { Code = TrameErrorCodes.NoContent };

        private TrameResponse Unauthorized()
            => CreateError(TrameErrorCodes.Unauthorized, "Unauthorized.", TrameErrorCategory.Unauthenticated);

        private TrameResponse InternalServerError(string message)
            => CreateError(TrameErrorCodes.InternalServerError, message, TrameErrorCategory.Internal);

        /// <summary>
        /// Erzeugt eine 500-Response. Die Message bleibt generisch (kein Leak in Produktion);
        /// die echten Exception-Details werden nur bei aktiviertem EnableDetailedErrors
        /// in TrameError.Details abgelegt.
        /// </summary>
        private TrameResponse InternalServerError(string message, Exception? ex)
        {
            var response = CreateError(TrameErrorCodes.InternalServerError, message, TrameErrorCategory.Internal);
            if (ex != null && EnableDetailedErrors && response.Error != null)
                response.Error.Details = ex.ToString();
            return response;
        }

        /// <summary>
        /// Fehler-Response: Data bleibt null (Fehler tragen nur Code + Error-Objekt),
        /// die Message wohnt in Error.Message. Kein String-Payload in Data mehr.
        /// Die semantische Kategorie wird aus dem Code abgeleitet (siehe <see cref="CategoryFor"/>),
        /// falls der Caller keine explizite angibt — so bleiben die bestehenden Aufrufstellen
        /// ohne Änderung kategorisiert.
        /// </summary>
        private TrameResponse CreateError(int code, string? message, TrameErrorCategory category = TrameErrorCategory.None)
        {
            return new TrameResponse
            {
                Code = code,
                Data = null,
                Error = new TrameError
                {
                    Code = code,
                    Message = message ?? "Unknown error",
                    Category = category == TrameErrorCategory.None ? CategoryFor(code) : category,
                }
            };
        }

        /// <summary>
        /// Leitet die semantische <see cref="TrameErrorCategory"/> aus einem numerischen
        /// Code ab (Default-Kategorisierung für bestehende Aufrufstellen, die keine
        /// explizite Kategorie übergeben). Hält die Kategorisierung an einer Stelle
        /// zentral, statt sie in jeder Fabrik zu duplizieren.
        /// </summary>
        private static TrameErrorCategory CategoryFor(int code) => code switch
        {
            TrameErrorCodes.BadRequest or 422 => TrameErrorCategory.InvalidArgument,
            TrameErrorCodes.Unauthorized => TrameErrorCategory.Unauthenticated,
            TrameErrorCodes.Forbidden => TrameErrorCategory.PermissionDenied,
            TrameErrorCodes.NotFound => TrameErrorCategory.NotFound,
            TrameErrorCodes.Conflict => TrameErrorCategory.Conflict,
            TrameErrorCodes.RequestEntityTooLarge or 429 => TrameErrorCategory.ResourceExhausted,
            TrameErrorCodes.InternalServerError => TrameErrorCategory.Internal,
            TrameErrorCodes.ServiceUnavailable => TrameErrorCategory.Unavailable,
            TrameErrorCodes.ClientClosedRequest => TrameErrorCategory.Cancelled,
            _ => TrameErrorCategory.None,
        };

        #endregion

        public class InvokeInfo
        {
            public MethodInfo MethodInfo { get; set; } = null!;
            public TrameAuthoriseAttribute? AuthoriseAttribute { get; set; }

            /// <summary>
            /// Methoden-Level [TrameAnonymous] → Opt-out vom RequireAuthentication-
            /// Default-Deny (Health/Ping). Null, wenn nicht vorhanden.
            /// </summary>
            public TrameAnonymousAttribute? AnonymousAttribute { get; set; }

            /// <summary>
            /// Kompilierter Delegate für den Methodenaufruf.
            /// Signatur: (object controllerInstance, object?[] args) => object?
            /// Falls die Methode async ist, wird der zurückgegebene Task noch nicht awaited.
            /// </summary>
            public Func<object, object?[], object?>? CompiledInvocation { get; set; }

            /// <summary>
            /// True, wenn die Methode asynchron (Task/Task&lt;T&gt;) ist.
            /// </summary>
            public bool IsAsync { get; set; }

            /// <summary>
            /// True, wenn die Methode einen Rückgabewert liefert (Task&lt;T&gt; oder synchroner Wert).
            /// </summary>
            public bool HasResult { get; set; }
        }
    }
}
