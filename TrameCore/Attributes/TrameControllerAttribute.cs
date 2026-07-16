namespace TrameCore.Attributes
{

    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class TrameControllerAttribute : System.Attribute
    {
        public TrameControllerAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>
        /// Steuert die Auto-Discovery: Wenn <c>true</c> (Default), wird der Controller
        /// vom Attribut-Scan in <c>AddTrame</c>/<c>UseTrame</c> bzw.
        /// <c>TrameControllerBuilder.FromAssemblies</c> automatisch gefunden und
        /// registriert. Setze auf <c>false</c>, um ihn aus dem Bulk-Scan
        /// auszunehmen — z. B. für Test-Fixtures, die bewusst invalid sind und nur
        /// explizit per <c>Register&lt;T&gt;()</c> / <c>TrameControllerBuilder.Add&lt;T&gt;()</c>
        /// registriert werden sollen. Explizite Registrierung ignoriert dieses Flag.
        /// </summary>
        public bool AutoDiscover { get; set; } = true;
    }
}
