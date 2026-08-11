using System.Text.Json;
using System.Text.Json.Nodes;

namespace SleipnirClient.Sleipnir
{
    public class SleipnirCall
    {
        private readonly string _controller;
        private readonly string _method;
        private string _name;
        private readonly List<SleipnirParameter> _parameters = new();
        private int _num = 0;
        // Hier speichern wir die Abhängigkeiten, die dieser Aufruf "exposed" (bereitstellt)
        private readonly Dictionary<string, string> _exposedDependencies = new();

        private SleipnirCall(string controller, string method)
        {
            _controller = controller;
            _method = method;
        }

        public static SleipnirCall Init(string controller, string method)
        {
            return new SleipnirCall(controller, method);
        }

        public SleipnirCall Named(string name)
        {
            _name = name;
            return this;
        }

        /// <summary>
        /// Gibt an, dass aus dem Ergebnis dieser Methode der Wert, der unter dem angegebenen JSON‑Path zu finden ist,
        /// unter dem Alias zur Verfügung gestellt wird.
        /// Beispiel: Exposes("$[0].Id", "firstId")
        ///
        /// Der JSON-Path ist **ergebnisrelativ** — die Wurzel ($) ist das Serialisierte
        /// Resultat (z. B. ein Customer-Objekt oder ein int), NICHT der Response-Umschlag.
        /// Es gibt also kein "data"-Knoten-Ebene. Verwende "$" für das ganze Resultat
        /// (z. B. ein zurückgegebener int), "$.Id"/"$.Name" für Eigenschaften eines
        /// Objekts oder "$[0].Id" für das erste Listenelement. Ein Pfad wie "$.data"
        /// trifft daher nie (außer das Resultat hat selbst eine "data"-Eigenschaft).
        /// </summary>
        public SleipnirCall Exposes(string jsonPath, string alias)
        {
            _exposedDependencies[alias] = jsonPath;
            return this;
        }

        /// <summary>
        /// Fügt einen Parameter mit einem Dependency-Platzhalter hinzu.
        /// Beispiel: WithAlias("@firstId")
        /// Der Platzhalter (z. B. "@firstId") wird unter dem abgeleiteten Parameternamen
        /// (ohne führendes @) hinterlegt. Die Server-Logik ersetzt diesen Platzhalter
        /// anhand der zuvor exposed Dependencies. Ist ein Alias nicht auflösbar, schlägt
        /// der Aufruf fehlt (kein impliziter Fallback in v1).
        /// </summary>
        public SleipnirCall WithAlias(string dependencyPlaceholder)
        {
            // Store the alias placeholder with a parameter name derived from the alias.
            // The server resolves @alias references before invoking the method.
            var aliasName = dependencyPlaceholder.StartsWith('@')
                ? dependencyPlaceholder.Substring(1)
                : dependencyPlaceholder;
            _parameters.Add(new SleipnirParameter
            {
                Num = _num,
                ParameterName = aliasName,
                Data = JsonValue.Create(dependencyPlaceholder) // e.g. "@firstId" als nativer String-Wert
            });
            _num++;
            return this;
        }

        /// <summary>
        /// Fügt einen benannten Parameter hinzu. Der Name muss mit einem Parameter der
        /// Zielmethode übereinstimmen (sichere, positionsunabhängige Bindung).
        /// </summary>
        public SleipnirCall Param(string parameterName, object? value)
        {
            _parameters.Add(new SleipnirParameter
            {
                Num = _num,
                ParameterName = parameterName,
                Data = value == null ? null : JsonSerializer.SerializeToNode(value)
            });
            _num++;
            return this;
        }

        /// <summary>
        /// Fügt einen oder mehrere Parameter hinzu.
        /// </summary>
        public SleipnirCall With(params object?[] args)
        {
            foreach (var arg in args)
                Add(arg);
            return this;
        }

        /// <summary>
        /// Fügt einen einzelnen Parameter hinzu.
        /// </summary>
        public SleipnirCall Add(object? p)
        {
            _parameters.Add(new SleipnirParameter()
            {
                Num = _num,
                ParameterName = $"param{_num}",
                Data = p == null ? null : JsonSerializer.SerializeToNode(p)
            });
            _num++;
            return this;
        }

        /// <summary>
        /// Wandelt den SleipnirCall in einen SleipnirRequest um.
        /// Optional könnte hier auch die Dependency-Mapping-Information mit übertragen werden.
        /// </summary>
        public SleipnirRequest ToRequest()
        {
            if (string.IsNullOrEmpty(_name))
                _name = $"{_controller}.{_method}";

            return new SleipnirRequest
            {
                Controller = _controller,
                Method = _method,
                Params = JsonSerializer.SerializeToNode(_parameters),
                Id = _name,
                DependencyMapping = _exposedDependencies.Count > 0 ? _exposedDependencies : null
            };
        }
    }
}
