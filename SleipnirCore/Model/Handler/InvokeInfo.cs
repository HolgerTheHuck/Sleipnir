using SleipnirCore.Attributes;
using System.Reflection;

namespace SleipnirCore.Model.Handler
{
    public class InvokeInfo
    {
        public MethodInfo? MethodInfo { get; set; }

        public List<Type>? ParamTypes { get; set; } = new();

        public SleipnirAuthoriseAttribute? AuthoriseAttribute { get; set; }

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
