using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SleipnirCore.Model.Messages.Mex
{
    public class ControllerMeta
    {
        public string Name { get; set; } = string.Empty;
        public List<MethodMeta> Methods { get; set; } = new();
    }

    public class MethodMeta
    {
        public string MethodName { get; set; } = string.Empty;
        // Language-neutral type reference (replaces the prior .NET type-name string).
        public TypeRef ReturnType { get; set; } = new TypeRef { Kind = "void" };
        public List<ParameterMeta> Parameters { get; set; } = new();
        public string? Documentation { get; set; }
    }

    public class ParameterMeta
    {
        public string ParameterName { get; set; } = string.Empty;
        public TypeRef ParameterType { get; set; } = new TypeRef { Kind = "opaque" };
        /// <summary>C# default parameter value (compile-time constant), or null when none.</summary>
        public object? DefaultValue { get; set; }
        public string? Documentation { get; set; }
    }

    public class TypeMeta
    {
        /// <summary>"object" | "enum". Determines whether Properties or Members is populated.</summary>
        public string Kind { get; set; } = "object";
        // The opaque registry key (identity, not type syntax). Doubles as the DiscoveryInfo.Types key.
        public string TypeName { get; set; } = string.Empty;
        public List<PropertyMeta> Properties { get; set; } = new();
        /// <summary>Enum members, populated when Kind == "enum".</summary>
        public List<EnumMember>? Members { get; set; }

        public object? Example { get; set; }
    }

    public class PropertyMeta
    {
        public string PropertyName { get; set; } = string.Empty;
        public TypeRef PropertyType { get; set; } = new TypeRef { Kind = "opaque" };
        /// <summary>
        /// Optional navigation edge (from <c>[SleipnirNavigation]</c> on the server DTO property).
        /// <c>null</c> when no attribute is present → omitted from the wire by
        /// <c>DiscoverySerialization.Options</c> (<c>WhenWritingNull</c>). Only populated for properties
        /// of expanded contract types (Weg C boundary enforced by <c>EnsureRegistered</c>).
        /// </summary>
        public NavigationMeta? Navigation { get; set; }
    }

    /// <summary>
    /// A navigation edge serialized from <c>[SleipnirNavigation]</c>; consumed by the
    /// <c>sleipnir-linq</c> codegen to emit the client-side <c>[SleipnirNavigation]</c> onto the
    /// contract DTO. Wire shape (camelCase): <c>{ fetch, key, childKey?, param? }</c>.
    /// </summary>
    public class NavigationMeta
    {
        /// <summary><c>"Controller.Method"</c> of the fetch method.</summary>
        public string Fetch { get; set; } = string.Empty;
        /// <summary>Per-element key path on the parent, as a wire (camelCase) name.</summary>
        public string Key { get; set; } = string.Empty;
        /// <summary>Optional child join property (wire name); convention-inferred when null.</summary>
        public string? ChildKey { get; set; }
        /// <summary>Optional fetch-method collection parameter name; codegen-inferred when null.</summary>
        public string? Param { get; set; }
    }

    /// <summary>A single enum member: the declared name and its underlying value.</summary>
    public class EnumMember
    {
        public string Name { get; set; } = string.Empty;
        public object? Value { get; set; }
    }
}