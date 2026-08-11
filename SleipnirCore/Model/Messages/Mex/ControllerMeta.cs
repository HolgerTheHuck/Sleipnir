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
    }

    /// <summary>A single enum member: the declared name and its underlying value.</summary>
    public class EnumMember
    {
        public string Name { get; set; } = string.Empty;
        public object? Value { get; set; }
    }
}