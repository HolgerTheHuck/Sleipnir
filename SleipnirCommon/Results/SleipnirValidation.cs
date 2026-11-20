using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SleipnirCommon.Results;

/// <summary>
/// DataAnnotations validation that produces <see cref="SleipnirError.Errors"/>-shaped
/// dictionaries (ASP.NET Core <c>ValidationProblemDetails.errors</c>). Used by the invoker's
/// opt-in parameter validation (<c>SleipnirOptions.ValidateParameters</c>) and callable
/// directly from a controller:
/// <code>
/// var errors = SleipnirValidation.Validate(scenario, "scenario");
/// if (errors.Count &gt; 0) return SleipnirResults.ValidationProblem(errors);
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>Validator.TryValidateObject</c> it descends: into public readable properties of
/// non-framework types and into list/array elements, so <c>Steps[1].Hsn</c> is reached. It runs
/// the standard rules per object (property attributes, class-level attributes,
/// <see cref="IValidatableObject"/> — the latter only when the attributes pass, as in ASP.NET).
/// Cycles are cut by reference, depth is capped at <see cref="MaxDepth"/>; dictionaries are not
/// descended into.
/// </para>
/// <para>
/// <b>Key convention</b>: camelCase wire names (<c>[JsonPropertyName]</c> wins), <c>.</c> between
/// members, <c>[i]</c> for list indexes, rooted at <c>rootPath</c> (the parameter name for the
/// framework) — e.g. <c>scenario.steps[1].hsn</c>. The same path a TS client would write to reach
/// the value, so a form can match it to its field.
/// </para>
/// </remarks>
public static class SleipnirValidation
{
    /// <summary>Maximum object-graph depth that is descended into.</summary>
    public const int MaxDepth = 32;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>
    /// Validates <paramref name="value"/> and its nested objects/lists. Returns an empty
    /// dictionary when everything is valid (and for <c>null</c>).
    /// </summary>
    /// <param name="value">The object graph to validate.</param>
    /// <param name="rootPath">Key prefix for every error (e.g. the parameter name); <c>""</c> = none.</param>
    public static Dictionary<string, string[]> Validate(object? value, string rootPath = "")
    {
        var sink = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Walk(value, rootPath ?? string.Empty, sink, new HashSet<object>(ByReference.Instance), 0);
        return Freeze(sink);
    }

    /// <summary>
    /// Validates a single method parameter: its own validation attributes (e.g.
    /// <c>[Required]</c>, <c>[Range]</c> on the parameter) under the parameter name, then the
    /// value's object graph (<see cref="Validate"/>). Errors are added to <paramref name="errors"/>.
    /// </summary>
    public static void ValidateParameter(ParameterInfo parameter, object? value, IDictionary<string, string[]> errors)
    {
        var name = parameter.Name ?? string.Empty;
        var attributes = parameter.GetCustomAttributes<ValidationAttribute>(inherit: true).ToArray();
        if (attributes.Length > 0)
        {
            var context = new ValidationContext(value ?? new object()) { MemberName = name, DisplayName = name };
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateValue(value!, context, results, attributes))
                Add(errors, name, results.Select(r => r.ErrorMessage ?? "The value is invalid."));
        }

        foreach (var kv in Validate(value, name))
            Add(errors, kv.Key, kv.Value);
    }

    /// <summary>
    /// Converts DataAnnotations results into an errors dictionary. Each result is filed under
    /// each of its member names (camelCased, prefixed with <paramref name="prefix"/>); a result
    /// without member names goes under <paramref name="prefix"/> (or <c>""</c>).
    /// </summary>
    public static Dictionary<string, string[]> ToErrors(IEnumerable<ValidationResult> results, string? prefix = null)
    {
        if (results is null) throw new ArgumentNullException(nameof(results));
        var sink = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in results)
            File(sink, prefix ?? string.Empty, null, r);
        return Freeze(sink);
    }

    private static void Walk(object? value, string path, Dictionary<string, List<string>> sink,
        HashSet<object> visited, int depth)
    {
        if (value is null || depth > MaxDepth) return;
        var type = value.GetType();
        if (IsLeaf(type) || value is IDictionary) return;
        if (!visited.Add(value)) return;

        if (value is IEnumerable sequence)
        {
            var i = 0;
            foreach (var item in sequence)
                Walk(item, $"{path}[{i++}]", sink, visited, depth + 1);
            return;
        }

        if (IsFrameworkType(type)) return;

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        foreach (var r in results)
            File(sink, path, type, r);

        foreach (var prop in Properties(type))
        {
            if (IsLeaf(prop.PropertyType)) continue;
            object? child;
            try { child = prop.GetValue(value); }
            catch { continue; } // a throwing getter is not a validation error
            Walk(child, Join(path, WireName(prop)), sink, visited, depth + 1);
        }
    }

    private static void File(Dictionary<string, List<string>> sink, string path, Type? type, ValidationResult r)
    {
        var message = r.ErrorMessage ?? "The value is invalid.";
        var any = false;
        foreach (var member in r.MemberNames)
        {
            if (string.IsNullOrEmpty(member)) continue;
            any = true;
            Add(sink, Join(path, MemberPath(type, member)), message);
        }
        if (!any) Add(sink, path, message);
    }

    /// <summary>
    /// Wire name for a member name from a <see cref="ValidationResult"/>: the property's
    /// <c>[JsonPropertyName]</c> or camelCase name; a dotted/indexed member path
    /// (<c>Steps[1].Hsn</c>) is camelCased per segment.
    /// </summary>
    private static string MemberPath(Type? type, string member)
    {
        if (type != null && type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance) is { } p)
            return WireName(p);
        var sb = new StringBuilder(member.Length);
        var segmentStart = true;
        foreach (var c in member)
        {
            sb.Append(segmentStart && char.IsUpper(c) ? char.ToLowerInvariant(c) : c);
            segmentStart = c == '.';
        }
        return sb.ToString();
    }

    private static string WireName(PropertyInfo p)
        => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);

    private static string Join(string path, string member)
        => path.Length == 0 ? member : $"{path}.{member}";

    private static PropertyInfo[] Properties(Type type) => PropertyCache.GetOrAdd(type, t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetMethod!.IsPublic)
            .ToArray());

    private static bool IsLeaf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type.IsPointer
               || type == typeof(string) || type == typeof(decimal) || type == typeof(byte[])
               || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
               || type == typeof(Guid) || type == typeof(Uri) || type == typeof(JsonElement)
               || typeof(Delegate).IsAssignableFrom(type)
               || typeof(System.Threading.CancellationToken) == type
#if NET6_0_OR_GREATER
               || type == typeof(DateOnly) || type == typeof(TimeOnly)
#endif
               || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type);
    }

    /// <summary>BCL/framework types carry no user validation rules — not descended into.</summary>
    private static bool IsFrameworkType(Type type)
    {
        var ns = type.Namespace;
        return ns != null && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
                              || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    private static void Add(Dictionary<string, List<string>> sink, string key, string message)
    {
        if (!sink.TryGetValue(key, out var list)) sink[key] = list = new List<string>();
        if (!list.Contains(message)) list.Add(message);
    }

    private static void Add(IDictionary<string, string[]> errors, string key, IEnumerable<string> messages)
    {
        var merged = errors.TryGetValue(key, out var existing) ? existing.ToList() : new List<string>();
        foreach (var m in messages)
            if (!merged.Contains(m)) merged.Add(m);
        errors[key] = merged.ToArray();
    }

    private static Dictionary<string, string[]> Freeze(Dictionary<string, List<string>> sink)
    {
        var result = new Dictionary<string, string[]>(sink.Count, StringComparer.Ordinal);
        foreach (var kv in sink) result[kv.Key] = kv.Value.ToArray();
        return result;
    }

    // ReferenceEqualityComparer is .NET 5+; this library also targets netstandard2.1.
    private sealed class ByReference : IEqualityComparer<object>
    {
        public static readonly ByReference Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
