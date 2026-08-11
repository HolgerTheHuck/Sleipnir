namespace SleipnirCore.Attributes
{

    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class SleipnirControllerAttribute : System.Attribute
    {
        public SleipnirControllerAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>
        /// Steuert die Auto-Discovery: Wenn <c>true</c> (Default), wird der Controller
        /// vom Attribut-Scan in <c>AddSleipnir</c>/<c>UseSleipnir</c> bzw.
        /// <c>SleipnirControllerBuilder.FromAssemblies</c> automatisch gefunden und
        /// registriert. Setze auf <c>false</c>, um ihn aus dem Bulk-Scan
        /// auszunehmen — z. B. für Test-Fixtures, die bewusst invalid sind und nur
        /// explizit per <c>Register&lt;T&gt;()</c> / <c>SleipnirControllerBuilder.Add&lt;T&gt;()</c>
        /// registriert werden sollen. Explizite Registrierung ignoriert dieses Flag.
        /// </summary>
        public bool AutoDiscover { get; set; } = true;
    }
}
