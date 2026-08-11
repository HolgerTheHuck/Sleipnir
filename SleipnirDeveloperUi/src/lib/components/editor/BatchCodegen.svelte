<script lang="ts">
  // Code-Generator für einen konfigurierten Batch — erzeugt kopierbaren
  // TypeScript-/C#-/JSON-Code für die im BatchEditor zusammengestellten Zeilen.
  //
  // Vorbild: DependencyBuilderPage (generateTs/Cs/Json). Im Gegensatz zum
  // Dependency Builder (Serial-locked) nutzt dieser Generator den tatsächlichen
  // Modus (Parallel/Serial) der übergebenen `mode`-Prop. Null-`dependencyMapping`
  // wird weggelassen, damit kein null im generierten Code steht (saubere Wire-Form).

  import { onDestroy } from 'svelte';
  import { ExecutionMode, type SleipnirRequest } from 'sleipnir-client';

  let {
    requests = [],
    mode = ExecutionMode.Parallel,
  }: {
    requests: SleipnirRequest[];
    mode: ExecutionMode;
  } = $props();

  let activeLang = $state<'ts' | 'cs' | 'json'>('ts');
  let copied = $state('');
  let copyTimer = $state<ReturnType<typeof setTimeout> | null>(null);

  onDestroy(() => {
    if (copyTimer) clearTimeout(copyTimer);
  });

  const MODE_NAMES: Record<number, string> = {
    [ExecutionMode.Parallel]: 'Parallel',
    [ExecutionMode.Serial]: 'Serial',
  };

  /** Wire-Form eines Requests: null/leeres dependencyMapping wird weggelassen
   *  (kein null im generierten Code). Spiegelt DependencyBuilderPage.toWireRequest. */
  function toWire(req: SleipnirRequest): SleipnirRequest {
    if (!req.dependencyMapping || Object.keys(req.dependencyMapping).length === 0) {
      const { dependencyMapping: _omit, ...rest } = req;
      return rest as SleipnirRequest;
    }
    return req;
  }

  let wireRequests = $derived.by(() => requests.map(toWire));

  const emptyMsg = '// Noch keine Batch-Zeilen — im Editor „+ Add" klicken.';

  // --- Hilfsfunktionen: params → Fluent-Builder -----------------------------
  // Der BatchEditor legt die Parameter als natives Array in `params` ab (jeder
  // Eintrag: { parameterName, num, data }, `data` ist der native JSON-Wert ODER
  // ein "@alias"-Marker-String). Für den Codegen kehren wir das zurück in einzelne
  // .Param/.withAlias-Aufrufe, damit der Entwickler lesbaren Code bekommt statt
  // handgeschriebener JSON-Strings.

  interface ParsedParam {
    parameterName: string;
    data: unknown;
  }

  /** Liest `req.params` (natives Array) in die Parameter-Liste, oder null wenn
   *  das Feld fehlt (z. B. manuelle Fallback-Eingabe ohne parsebare Form). */
  function parseParams(req: SleipnirRequest): ParsedParam[] | null {
    if (!req.params) return [];
    if (!Array.isArray(req.params)) return null;
    return req.params.map((p) => ({
      parameterName: p.parameterName ?? '',
      data: p.data,
    }));
  }

  function csEscape(s: string): string {
    return s.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/\r/g, '').replace(/\n/g, '\\n');
  }

  /** Nativer JSON-Wert → C#-Literal. Skalare direkt, Objekte/Arrays
   *  über JsonNode.Parse mit C#-11-Raw-String (kein manuelles Escaping). */
  function csLiteral(data: unknown): { code: string; usesJsonNode: boolean } {
    if (data === null || data === undefined) return { code: 'null', usesJsonNode: false };
    if (typeof data === 'boolean') return { code: data ? 'true' : 'false', usesJsonNode: false };
    if (typeof data === 'number') return { code: `${data}`, usesJsonNode: false };
    if (typeof data === 'string') return { code: csStringLiteral(data), usesJsonNode: false };
    // Objekt/Array → JsonNode.Parse mit Raw-String (C# 11, .NET 8).
    const json = JSON.stringify(data, null, 2);
    return { code: `JsonNode.Parse("""\n${json}\n""")!`, usesJsonNode: true };
  }

  function csStringLiteral(s: string): string {
    return '"' + csEscape(s) + '"';
  }

  /** Nativer JSON-Wert → JS/TS-Literal (JSON ist eine Teilmenge von JS für Daten). */
  function tsLiteral(data: unknown): string {
    return JSON.stringify(data);
  }

  // --- TypeScript ---------------------------------------------------------------

  function generateTs(): string {
    if (requests.length === 0) return emptyMsg;
    const modeName = MODE_NAMES[mode] ?? 'Serial';
    const serialNote = mode === ExecutionMode.Serial ? ' — @alias-Auflösung aktiv' : '';

    const builders = wireRequests.map((r, idx) => {
      const parts: string[] = [`SleipnirCall.init("${r.controller}", "${r.method}")`];
      if (r.id) parts.push(`  .named("${r.id}")`);
      const params = parseParams(r);
      if (params === null) {
        parts.push(`  // params nicht parsebar — manuell anpassen:`);
        parts.push(`  // params: ${JSON.stringify(r.params ?? [])}`);
      } else {
        for (const p of params) {
          if (typeof p.data === 'string' && p.data.startsWith('@')) {
            parts.push(`  .withAlias("${p.data}")`);
          } else {
            parts.push(`  .param("${p.parameterName}", ${tsLiteral(p.data)})`);
          }
        }
      }
      if (r.dependencyMapping) {
        for (const [alias, path] of Object.entries(r.dependencyMapping)) {
          parts.push(`  .exposes("${path}", "${alias}")`);
        }
      }
      parts.push(`  .toRequest()`);
      return `const req${idx + 1} = ${parts.join('\n')};`;
    });

    const reqVars = builders.map((_, idx) => `req${idx + 1}`).join(', ');

    return `import { ExecutionMode, SleipnirCall, type SleipnirMultiRequest } from "sleipnir-client";

// Batch-Ausführung (Mode: ${modeName}${serialNote})
${builders.join('\n\n')}

const batch = SleipnirCall.batch([${reqVars}], ExecutionMode.${modeName});
const results = await rest.callBatch(batch.requests, batch.mode);`;
  }

  // --- C# -----------------------------------------------------------------------

  function generateCs(): string {
    if (requests.length === 0) return emptyMsg;
    const modeName = MODE_NAMES[mode] ?? 'Serial';
    const serialNote = mode === ExecutionMode.Serial ? ' — @alias-Auflösung aktiv' : '';
    let usesJsonNode = false;

    const builders = wireRequests.map((r, idx) => {
      const parts: string[] = [`SleipnirCall.Init("${r.controller}", "${r.method}")`];
      if (r.id) parts.push(`    .Named("${csEscape(r.id)}")`);
      const params = parseParams(r);
      if (params === null) {
        parts.push(`    // Params nicht parsebar — manuell anpassen:`);
        parts.push(`    // Params = ${csStringLiteral(JSON.stringify(r.params ?? []))}`);
      } else {
        for (const p of params) {
          if (typeof p.data === 'string' && p.data.startsWith('@')) {
            parts.push(`    .WithAlias("${csEscape(p.data)}")`);
          } else {
            const lit = csLiteral(p.data);
            if (lit.usesJsonNode) usesJsonNode = true;
            parts.push(`    .Param("${csEscape(p.parameterName)}", ${lit.code})`);
          }
        }
      }
      if (r.dependencyMapping) {
        for (const [alias, path] of Object.entries(r.dependencyMapping)) {
          parts.push(`    .Exposes("${csEscape(path)}", "${csEscape(alias)}")`);
        }
      }
      parts.push(`    .ToRequest()`);
      return `var req${idx + 1} =\n${parts.join('\n')};`;
    });

    const reqVars = builders.map((_, idx) => `req${idx + 1}`).join(', ');
    const jsonNodeUsing = usesJsonNode ? 'using System.Text.Json.Nodes;\n' : '';

    return `using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;
using System.Collections.Generic;
${jsonNodeUsing}
// Batch-Ausführung (Mode: ${modeName}${serialNote})
${builders.join('\n\n')}

var multi = new SleipnirMultiRequest
{
    Mode = ExecutionMode.${modeName},
    Requests = new List<SleipnirRequest> { ${reqVars} },
};

var responses = await client.Call(multi);`;
  }

  // --- JSON ---------------------------------------------------------------------

  function generateJson(): string {
    if (requests.length === 0) return emptyMsg;
    const wire = {
      mode, // ExecutionMode: Parallel=0, Serial=1
      requests: wireRequests,
    };
    return JSON.stringify(wire, null, 2);
  }

  let code = $derived(
    activeLang === 'ts' ? generateTs() : activeLang === 'cs' ? generateCs() : generateJson(),
  );

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(code);
      copied = activeLang;
      if (copyTimer) clearTimeout(copyTimer);
      copyTimer = setTimeout(() => {
        copied = '';
        copyTimer = null;
      }, 1500);
    } catch {
      /* ignore */
    }
  }
</script>

<div class="batch-codegen">
  <div class="codegen-header">
    <span class="block-label">Code</span>
    <div class="lang-tabs">
      <button class="ghost small" class:active={activeLang === 'ts'} onclick={() => (activeLang = 'ts')}>TypeScript</button>
      <button class="ghost small" class:active={activeLang === 'cs'} onclick={() => (activeLang = 'cs')}>C#</button>
      <button class="ghost small" class:active={activeLang === 'json'} onclick={() => (activeLang = 'json')}>JSON</button>
    </div>
    <button class="primary small" onclick={copy}>
      {copied === activeLang ? 'Kopiert!' : 'Code kopieren'}
    </button>
  </div>
  <pre class="code codegen-output"><code>{code}</code></pre>
</div>

<style>
  .batch-codegen {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-elevated);
    padding: 8px;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .codegen-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    flex-shrink: 0;
  }
  .block-label {
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--text-muted);
  }
  .lang-tabs {
    display: flex;
    gap: 2px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    overflow: hidden;
    margin-left: auto;
  }
  .lang-tabs button {
    border: none;
    border-radius: 0;
    padding: 3px 10px;
    font-size: 0.8rem;
  }
  .lang-tabs button.active {
    background: var(--accent-secondary);
    color: #fff;
  }
  .codegen-output {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-all;
    padding: 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--code-text);
    font-family: var(--font-mono);
    font-size: 0.8rem;
    line-height: 1.6;
    max-height: 300px;
    overflow-y: auto;
  }
  .codegen-output code {
    font-family: var(--font-mono);
  }
</style>