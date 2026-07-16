using System;

namespace TrameCommon.Attribute
{
    /// <summary>
    /// Markiert einen Typ für die Discovery-Metadaten-Expansion (Property-Schema,
    /// Beispiel-Instanz, Nested-Types). Seit der Signatur-Inferenz (Weg C) **optional**:
    /// die Discovery expandiert standardmäßig jeden Klassentyp, der in einer
    /// Methodensignatur auftaucht und dessen Assembly zum Contract-Assembly-Set gehört.
    /// Das Attribute dient nur noch als gezielter Override:
    ///   - bare (<c>[TrameDataContract]</c>)  → force-expand (z. B. für Fremdtypen,
    ///     die trotzdem dokumentiert werden sollen).
    ///   - <c>[TrameDataContract(Exclude = true)]</c> → force-opaque (nur Typname,
    ///     keine Expansion — z. B. für eigene Hilfstypen, die nicht im Contract stehen).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class TrameDataContractAttribute : System.Attribute
    {
        /// <summary>
        /// Wenn <c>true</c>, wird der Typ trotz Zugehörigkeit zum Contract-Assembly-Set
        /// NICHT expandiert (force-opaque). Default <c>false</c> = force-expand.
        /// </summary>
        public bool Exclude { get; set; }
    }
}