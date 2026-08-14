using SleipnirCommon.Attribute;
using SleipnirCore.Attributes;
using SleipnirCore.Model.Messages.Mex;
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
            // forces expansion. Computed once: the controller map does not change during a build.
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

                // Collect every method decorated with [SleipnirMethod].
                var methods = controllerType.GetMethods()
                    .Where(m => m.GetCustomAttributes(typeof(SleipnirMethodAttribute), false).Any());

                foreach (var method in methods)
                {
                    var methodAttr = method.GetCustomAttribute<SleipnirMethodAttribute>();

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
                        MethodName = methodAttr?.Name ?? method.Name,
                        ReturnType = BuildTypeRef(effectiveReturnType, returnNullable, ctx),
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

            // byte[] -> binary scalar (before array detection).
            if (type == typeof(byte[]))
                return new TypeRef { Kind = "scalar", Name = "bytes", Nullable = nullable };

            // Enum -> register with members, reference by key.
            if (type.IsEnum)
            {
                EnsureRegistered(type, ctx);
                return new TypeRef { Kind = "ref", Ref = TypeKey(type), Nullable = nullable };
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
                return new TypeRef { Kind = "ref", Ref = TypeKey(type), Nullable = nullable };
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
            string key = TypeKey(type);
            if (ctx.Types.ContainsKey(key)) return;

            if (type.IsEnum)
            {
                ctx.Types[key] = BuildEnumTypeMeta(type);
                return;
            }

            if (!IsExpandableType(type, ctx.ContractAssemblies)) return;

            // Placeholder first so self-referential properties resolve to this entry (cycle-safe).
            var meta = new TypeMeta { Kind = "object", TypeName = key };
            ctx.Types[key] = meta;
            PopulateObjectMeta(meta, type, ctx);
        }

        private static TypeMeta BuildEnumTypeMeta(Type type)
        {
            var meta = new TypeMeta { Kind = "enum", TypeName = TypeKey(type), Example = null };
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

        private static string TypeKey(Type type) => type.FullName ?? type.Name;

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
            public BuildCtx(Dictionary<string, TypeMeta> types, HashSet<Assembly> contractAssemblies)
            {
                Types = types;
                ContractAssemblies = contractAssemblies;
            }
        }
    }
}