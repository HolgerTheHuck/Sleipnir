using SleipnirCommon.Attribute;
using SleipnirCore.Attributes;
using SleipnirCore.Model.Messages.Mex;
using SleipnirCore.Services.Helper;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SleipnirCore.Services
{
    /// <summary>
    /// Builds the language-neutral <see cref="DiscoveryInfo"/> contract from the registered
    /// controllers. Types are emitted as structured <see cref="TypeRef"/> (see
    /// docs/discovery-schema.md), not .NET type-name strings. The result is cached and rebuilt
    /// on <see cref="InvalidateCache"/> (wired by SleipnirInvoker.Register).
    /// </summary>
    public class SleipnirDiscoveryService
    {
        private readonly ConcurrentDictionary<string, Type> _routeHandlers;
        private DiscoveryInfo? _cachedDiscovery;
        private readonly object _cacheLock = new();

        public SleipnirDiscoveryService(ConcurrentDictionary<string, Type> routeHandlers)
        {
            _routeHandlers = routeHandlers;
        }

        public DiscoveryInfo GetDiscoveryInfo()
        {
            if (_cachedDiscovery != null)
                return _cachedDiscovery;

            lock (_cacheLock)
            {
                return _cachedDiscovery ??= BuildDiscoveryInfo();
            }
        }

        public void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedDiscovery = null;
            }
        }

        // --- Neutral scalar / "any" tables (docs/discovery-schema.md §3) ---------------------

        private static readonly Dictionary<string, string> ScalarByFullName = new()
        {
            ["System.String"] = "string",
            ["System.Char"] = "char",
            ["System.Boolean"] = "bool",
            ["System.SByte"] = "int",
            ["System.Byte"] = "int",
            ["System.Int16"] = "int",
            ["System.UInt16"] = "int",
            ["System.Int32"] = "int",
            ["System.UInt32"] = "int",
            ["System.Int64"] = "long",
            ["System.UInt64"] = "long",
            ["System.IntPtr"] = "long",
            ["System.UIntPtr"] = "long",
            ["System.Single"] = "float",
            ["System.Double"] = "double",
            ["System.Decimal"] = "decimal",
            ["System.DateTime"] = "datetime",
            ["System.DateTimeOffset"] = "datetimeoffset",
            ["System.DateOnly"] = "dateonly",
            ["System.TimeOnly"] = "timeonly",
            ["System.TimeSpan"] = "timespan",
            ["System.Guid"] = "guid",
            ["System.Uri"] = "uri",
            ["System.Version"] = "version",
        };

        private static readonly HashSet<string> AnyByFullName = new()
        {
            "System.Object",
            "System.Text.Json.JsonElement",
            "System.Text.Json.Nodes.JsonNode",
            "System.Text.Json.Nodes.JsonObject",
            "System.Text.Json.Nodes.JsonArray",
            "System.Text.Json.Nodes.JsonValue",
            "System.Text.Json.Nodes.JsonDocument",
            "System.Dynamic.ExpandoObject",
        };

        // --- Collection definitions ------------------------------------------------------------

        private static readonly HashSet<Type> ArrayDefinitions = new()
        {
            typeof(List<>), typeof(IList<>), typeof(IReadOnlyList<>),
            typeof(ICollection<>), typeof(IReadOnlyCollection<>), typeof(IEnumerable<>),
            typeof(Collection<>),
        };
        private static readonly HashSet<Type> SetDefinitions = new()
        {
            typeof(HashSet<>), typeof(ISet<>), typeof(SortedSet<>),
        };
        private static readonly HashSet<Type> MapDefinitions = new()
        {
            typeof(Dictionary<,>), typeof(IDictionary<,>), typeof(IReadOnlyDictionary<,>),
            typeof(SortedDictionary<,>), typeof(SortedList<,>),
        };

        private DiscoveryInfo BuildDiscoveryInfo()
        {
            var discovery = new DiscoveryInfo();

            // Contract-assembly set = assemblies of all registered controllers. Types from these
            // assemblies are expanded by signature inference (Weg C); types from other assemblies
            // (BCL, Sleipnir framework envelopes, third-party) stay opaque unless [SleipnirDataContract]
            // forces expansion. Exception: the generic typed envelope SleipnirResponse<T> unwraps
            // to its payload in BuildTypeRef (see there); only the non-generic envelope hits rule 5.
            // Computed once: the controller map does not change during a build.
            var contractAssemblies = _routeHandlers.Values
                .Select(t => t.Assembly)
                .Distinct()
                .ToHashSet();

            var ctx = new BuildCtx(discovery.Types, contractAssemblies);

            foreach (var kvp in _routeHandlers)
            {
                string controllerName = kvp.Key;
                Type controllerType = kvp.Value;

                var controllerMeta = new ControllerMeta { Name = controllerName };

                // Collect every method decorated with [SleipnirMethod] (a call) or [SleipnirEvent]
                // (a server-push event). The two are mutually exclusive (enforced at registration),
                // and both share the {Controller}_{name} dispatch namespace.
                var methods = controllerType.GetMethods()
                    .Where(m => m.GetCustomAttributes(typeof(SleipnirMethodAttribute), false).Any()
                             || m.GetCustomAttributes(typeof(SleipnirEventAttribute), false).Any());

                foreach (var method in methods)
                {
                    var methodAttr = method.GetCustomAttribute<SleipnirMethodAttribute>();
                    var eventAttr = method.GetCustomAttribute<SleipnirEventAttribute>();

                    // Effective return type (Task / Task<T> unwrapped; bare Task -> void).
                    Type effectiveReturnType = method.ReturnType;
                    bool returnIsTask = typeof(Task).IsAssignableFrom(effectiveReturnType);
                    if (returnIsTask)
                    {
                        if (effectiveReturnType.IsGenericType)
                            effectiveReturnType = effectiveReturnType.GenericTypeArguments[0];
                        else
                            effectiveReturnType = typeof(void);
                    }

                    var methodDoc = method.GetCustomAttribute<SleipnirDocumentationAttribute>()?.Summary;

                    // Return nullability: readable only for non-Task reference returns (the NRT of T
                    // inside Task<T> is not exposed by NullabilityInfoContext). Absent => not-nullable.
                    bool? returnNullable = null;
                    if (!returnIsTask && !effectiveReturnType.IsValueType)
                    {
                        returnNullable = ReadNullable(() => ctx.NrtCtx.Create(method.ReturnParameter).ReadState);
                    }

                    var methodMeta = new MethodMeta
                    {
                        Documentation = methodDoc,
                        MethodName = methodAttr?.Name ?? eventAttr?.Name ?? method.Name,
                        ReturnType = BuildTypeRef(effectiveReturnType, returnNullable, ctx),
                        // Method-level [SleipnirDeprecated] overrides the controller-level one.
                        Deprecated = (method.GetCustomAttribute<SleipnirDeprecatedAttribute>()
                                      ?? controllerType.GetCustomAttribute<SleipnirDeprecatedAttribute>())?.Message,
                    };

                    var paramDoc = method.GetCustomAttribute<SleipnirDocumentationAttribute>()?.Summary;

                    // Parameters (CancellationToken is injected by the framework — dropped here).
                    var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken));

                    foreach (var param in parameters)
                    {
                        bool? paramNullable = ReadNullable(() => ctx.NrtCtx.Create(param).ReadState);
                        var paramMeta = new ParameterMeta
                        {
                            Documentation = paramDoc,
                            ParameterName = param.Name ?? string.Empty,
                            ParameterType = BuildTypeRef(param.ParameterType, paramNullable, ctx),
                            DefaultValue = ReadDefaultValue(param),
                        };

                        methodMeta.Parameters.Add(paramMeta);
                    }

                    controllerMeta.Methods.Add(methodMeta);
                }

                discovery.Controllers.Add(controllerMeta);
            }

            discovery.ContractHash = ContractHash.Compute(discovery);
            return discovery;
        }

        // --- The neutral type builder ---------------------------------------------------------

        /// <summary>
        /// Builds a <see cref="TypeRef"/> for a usage site (return / parameter / property).
        /// Expandable object types and enums are registered in <c>discovery.types</c> and
        /// referenced by <c>ref</c>; scalars/collections/streams/opaque are inline.
        /// </summary>
        private static TypeRef BuildTypeRef(Type type, bool? nullable, BuildCtx ctx)
        {
            if (type == null || type == typeof(void))
                return new TypeRef { Kind = "void" };

            // Nullable<T> value type -> unwrap, force nullable.
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
                return BuildTypeRef(underlying, true, ctx);

            // Generic typed envelope SleipnirResponse<T> (and Task<SleipnirResponse<T>> via the
            // caller-side Task unwrap) -> unwrap to the payload. Analogue of Nullable<T> above:
            // one place, applies to returns, parameters and nested properties. The wire data slot
            // holds the payload (errors keep data:null and carry code + error object) — the
            // non-2xx branch is modelled CLIENT-side (TS `TypedResponse<T> = SleipnirResponse &
            // { data: T | null }`, C# contracts `Task<T?>`), not by discovery nullability, so the
            // payload ref keeps this occurrence's nullable (absent for Task returns, same as T
            // inside Task<T> today). The NON-generic SleipnirResponse deliberately stays opaque
            // (rule 5 below, back-compat).
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SleipnirCommon.Models.SleipnirResponse<>))
                return BuildTypeRef(type.GetGenericArguments()[0], nullable, ctx);

            // byte[] -> binary scalar (before array detection).
            if (type == typeof(byte[]))
                return new TypeRef { Kind = "scalar", Name = "bytes", Nullable = nullable };

            // Enum -> register with members, reference by key.
            if (type.IsEnum)
            {
                EnsureRegistered(type, ctx);
                return new TypeRef { Kind = "ref", Ref = TypeKey(type, ctx), Nullable = nullable };
            }

            // Scalar from the fixed table (primitives + BCL value types + string).
            string? fullName = type.FullName;
            if (fullName != null && ScalarByFullName.TryGetValue(fullName, out var scalarName))
                return new TypeRef { Kind = "scalar", Name = scalarName, Nullable = nullable };

            // Dynamic/object/JSON-dom -> "any".
            if (fullName != null && AnyByFullName.Contains(fullName))
                return new TypeRef { Kind = "scalar", Name = "any", Nullable = nullable };

            // Collections (explicit kind: array / set / map / stream).
            if (TryCollection(type, out var collKind, out var element, out var mapKey, out var mapValue))
            {
                if (collKind == "map")
                    return new TypeRef { Kind = "map", Key = BuildTypeRef(mapKey!, null, ctx), Value = BuildTypeRef(mapValue!, null, ctx), Nullable = nullable };
                if (collKind == "stream")
                    return new TypeRef { Kind = "stream", Element = BuildTypeRef(element!, null, ctx) };
                if (collKind == "event")
                    return new TypeRef { Kind = "event", Element = BuildTypeRef(element!, null, ctx) };
                return new TypeRef { Kind = collKind, Element = BuildTypeRef(element!, null, ctx), Nullable = nullable };
            }

            // Expandable contract object -> register, reference by key.
            if (IsExpandableType(type, ctx.ContractAssemblies))
            {
                EnsureRegistered(type, ctx);
                return new TypeRef { Kind = "ref", Ref = TypeKey(type, ctx), Nullable = nullable };
            }

            // Everything else (framework envelopes, BCL opaque, third-party) -> opaque with a hint.
            return new TypeRef { Kind = "opaque", NativeName = type.Name, Nullable = nullable };
        }

        private static bool TryCollection(Type type, out string kind, out Type? element, out Type? key, out Type? value)
        {
            kind = string.Empty;
            element = key = value = null;

            if (type.IsArray)
            {
                element = type.GetElementType();
                kind = "array";
                return true;
            }

            if (!type.IsGenericType) return false;
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();

            if (def == typeof(IAsyncEnumerable<>)) { element = args[0]; kind = "stream"; return true; }
            if (def == typeof(IObservable<>)) { element = args[0]; kind = "event"; return true; }
            if (ArrayDefinitions.Contains(def)) { element = args[0]; kind = "array"; return true; }
            if (SetDefinitions.Contains(def)) { element = args[0]; kind = "set"; return true; }
            if (MapDefinitions.Contains(def)) { key = args[0]; value = args[1]; kind = "map"; return true; }
            return false;
        }

        // --- Registry (object / enum) ---------------------------------------------------------

        /// <summary>Idempotently registers an expandable object or enum type in discovery.types.</summary>
        private static void EnsureRegistered(Type type, BuildCtx ctx)
        {
            string key = TypeKey(type, ctx);
            if (ctx.Types.ContainsKey(key)) return;

            if (type.IsEnum)
            {
                ctx.Types[key] = BuildEnumTypeMeta(type, key);
                return;
            }

            if (!IsExpandableType(type, ctx.ContractAssemblies)) return;

            // Placeholder first so self-referential properties resolve to this entry (cycle-safe).
            var meta = new TypeMeta { Kind = "object", TypeName = key };
            ctx.Types[key] = meta;
            PopulateObjectMeta(meta, type, ctx);
        }

        private static TypeMeta BuildEnumTypeMeta(Type type, string key)
        {
            var meta = new TypeMeta { Kind = "enum", TypeName = key, Example = null };
            var underlying = Enum.GetUnderlyingType(type);
            var members = new List<EnumMember>();
            foreach (var name in Enum.GetNames(type))
            {
                object? value;
                try { value = Convert.ChangeType(Enum.Parse(type, name), underlying); }
                catch { value = Enum.Parse(type, name); }
                members.Add(new EnumMember { Name = name, Value = value });
            }
            meta.Members = members;
            return meta;
        }

        private static void PopulateObjectMeta(TypeMeta meta, Type type, BuildCtx ctx)
        {
            // Example: [SleipnirExample] JSON, else a parameterless-ctor default instance, else null.
            var exampleAttr = type.GetCustomAttribute<SleipnirExampleAttribute>();
            if (exampleAttr != null)
            {
                try
                {
                    meta.Example = JsonSerializer.Deserialize(exampleAttr.ExampleJson, type,
                        new JsonSerializerOptions { WriteIndented = true });
                }
                catch { meta.Example = null; }
            }
            else if (type.GetConstructor(Type.EmptyTypes) != null)
            {
                try { meta.Example = Activator.CreateInstance(type); }
                catch { meta.Example = null; }
            }
            else
            {
                meta.Example = null;
            }

            // Properties: each property type is a TypeRef with occurrence-level nullability.
            // A [SleipnirNavigation] on the property (server-side producer attribute) is read here and
            // serialized as `navigation` — the first property-level attribute→discovery flow; discovery
            // just reports it, the codegen re-emits it as the client-side [SleipnirNavigation].
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                bool? propNullable = ReadNullable(() => ctx.NrtCtx.Create(prop).ReadState);
                var navAttr = prop.GetCustomAttribute<SleipnirNavigationAttribute>();
                meta.Properties.Add(new PropertyMeta
                {
                    PropertyName = prop.Name,
                    PropertyType = BuildTypeRef(prop.PropertyType, propNullable, ctx),
                    Navigation = navAttr is null ? null : new NavigationMeta
                    {
                        Fetch = navAttr.Fetch,
                        Key = navAttr.Key,
                        ChildKey = navAttr.ChildKey,
                        Param = navAttr.Param,
                    },
                });
            }
        }

        /// <summary>
        /// Decides whether a type is fully expanded in discovery (property schema, example) or
        /// appears only as an opaque <c>TypeRef</c>. The Weg-C heuristic:
        ///   1. primitive/enum/string -> opaque (handled as scalar/ref, not expanded here).
        ///   2. [SleipnirDataContract(Exclude = true)] -> force-opaque.
        ///   3. [SleipnirDataContract] (bare) -> force-expand.
        ///   4. assembly in the contract-assembly set -> expand.
        ///   5. otherwise (foreign/BCL/Sleipnir envelope) -> opaque.
        /// (The generic typed envelope SleipnirResponse&lt;T&gt; never reaches this classifier —
        /// it is unwrapped to its payload earlier in BuildTypeRef.)
        /// </summary>
        private static bool IsExpandableType(Type type, HashSet<Assembly> contractAssemblies)
        {
            if (type == null) return false;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string)) return false;

            var attr = GetDataContractAttribute(type);
            if (attr != null)
            {
                if (attr.Exclude) return false;
                return true;
            }

            return contractAssemblies.Contains(type.Assembly);
        }

        private static SleipnirDataContractAttribute? GetDataContractAttribute(Type type)
            => type.GetCustomAttributes(typeof(SleipnirDataContractAttribute), false)
                   .OfType<SleipnirDataContractAttribute>()
                   .FirstOrDefault();

        // --- Helpers --------------------------------------------------------------------------

        /// <summary>
        /// The <c>discovery.types</c> key of a registered type. Non-generic types keep their CLR
        /// <c>FullName</c> (<c>MyApp.Order</c>). A closed generic's <c>FullName</c> is the
        /// assembly-qualified CLR form (<c>MyApp.Page`1[[MyApp.TicketDto, MyApp, Version=…]]</c>),
        /// which is not a usable contract name — it is flattened instead to
        /// <c>{Namespace}.{Name}Of{Arg1}And{Arg2}</c> (<c>MyApp.PageOfTicketDto</c>), see
        /// <see cref="MangledName"/>. Arguments use their short CLR name; should two distinct
        /// closed types flatten to the same key (<c>Page&lt;A.Dto&gt;</c> + <c>Page&lt;B.Dto&gt;</c>),
        /// the later one qualifies its arguments with their namespace (<c>PageOfBDto</c>).
        /// </summary>
        private static string TypeKey(Type type, BuildCtx ctx)
        {
            if (ctx.KeyByType.TryGetValue(type, out var cached)) return cached;
            if (!type.IsGenericType)
            {
                // Recorded so a later generic that flattens onto a real type name
                // (Page<TicketDto> vs. a declared PageOfTicketDto) takes the qualified form.
                string plain = type.FullName ?? type.Name;
                ctx.KeyByType[type] = plain;
                ctx.TypeByKey.TryAdd(plain, type);
                return plain;
            }

            string prefix = KeyPrefix(type);
            string key = prefix + MangledName(type, qualifyArgs: false, isArg: false);
            if (ctx.TypeByKey.TryGetValue(key, out var owner) && owner != type)
                key = prefix + MangledName(type, qualifyArgs: true, isArg: false);

            ctx.KeyByType[type] = key;
            ctx.TypeByKey[key] = type;
            return key;
        }

        /// <summary>Namespace + enclosing types (<c>MyApp.</c>, <c>MyApp.Outer+</c>), mirroring FullName.</summary>
        private static string KeyPrefix(Type type)
        {
            string prefix = string.IsNullOrEmpty(type.Namespace) ? string.Empty : type.Namespace + ".";
            var enclosing = new List<string>();
            for (var d = type.DeclaringType; d != null; d = d.DeclaringType)
                enclosing.Insert(0, StripArity(d.Name));
            foreach (var e in enclosing) prefix += e + "+";
            return prefix;
        }

        /// <summary>
        /// Flat identifier for a type used inside a contract key: <c>Page&lt;TicketDto&gt;</c> →
        /// <c>PageOfTicketDto</c>, <c>Pair&lt;A, List&lt;B&gt;&gt;</c> → <c>PairOfAAndListOfB</c>,
        /// <c>T[]</c> → <c>TArray</c>, scalars by CLR name (<c>Int32</c>, <c>String</c>). The argument
        /// order is the declaration order of the generic parameters. The codegen applies the same
        /// rule defensively to AQ names from older servers (<c>casing.ts</c> / <c>Casing.cs</c>).
        /// </summary>
        private static string MangledName(Type type, bool qualifyArgs, bool isArg)
        {
            if (type.IsArray)
                return MangledName(type.GetElementType()!, qualifyArgs, isArg) + "Array";
            string name = StripArity(type.Name);
            string head = qualifyArgs && isArg ? Qualifier(type) + name : name;
            if (!type.IsGenericType) return head;

            // Generic args of a nested type include the enclosing type's args; only the own ones
            // belong to this name segment.
            var args = type.GetGenericArguments();
            int own = type.GetGenericTypeDefinition().GetGenericArguments().Length
                      - (type.DeclaringType?.GetGenericArguments().Length ?? 0);
            var ownArgs = args.Skip(args.Length - Math.Max(own, 0)).ToArray();
            if (ownArgs.Length == 0) return head;
            return head + "Of" + string.Join("And", ownArgs.Select(a => MangledName(a, qualifyArgs, isArg: true)));
        }

        private static string Qualifier(Type type)
            => string.Concat((type.Namespace ?? string.Empty)
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(seg => char.ToUpperInvariant(seg[0]) + seg.Substring(1)));

        private static string StripArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        /// <summary>Maps an NRT state to the wire form: only Nullable -> true (absent otherwise).</summary>
        private static bool? ReadNullable(Func<NullabilityState> read)
        {
            try
            {
                var state = read();
                return state == NullabilityState.Nullable ? true : (bool?)null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a C# default parameter value (compile-time constant). Non-constant defaults and
        /// null-valued defaults are reported as absent (no <c>defaultValue</c> on the wire).
        /// </summary>
        private static object? ReadDefaultValue(ParameterInfo param)
        {
            try
            {
                if (!param.HasDefaultValue) return null;
                var dv = param.DefaultValue;
                if (dv == null || dv == DBNull.Value) return null;
                return dv;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Per-build context: the type registry, contract-assembly set, and NRT reader.</summary>
        private sealed class BuildCtx
        {
            public readonly Dictionary<string, TypeMeta> Types;
            public readonly HashSet<Assembly> ContractAssemblies;
            public readonly NullabilityInfoContext NrtCtx = new();
            /// <summary>Closed generic → flattened key, and its reverse (collision detection).</summary>
            public readonly Dictionary<Type, string> KeyByType = new();
            public readonly Dictionary<string, Type> TypeByKey = new(StringComparer.OrdinalIgnoreCase); // same comparer as discovery.types
            public BuildCtx(Dictionary<string, TypeMeta> types, HashSet<Assembly> contractAssemblies)
            {
                Types = types;
                ContractAssemblies = contractAssemblies;
            }
        }
    }
}