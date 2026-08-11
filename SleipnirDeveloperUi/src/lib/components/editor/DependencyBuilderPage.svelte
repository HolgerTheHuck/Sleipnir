<script lang="ts">
  // Dependency Builder — visuelle Editor-Seite für @alias-Abhängigkeitsketten.
  // baut aus DepStep[] → SleipnirRequest[] (Serial-Modus), zeigt den Graphen live,
  // generiert kopierbaren TS/C#/JSON-Code und führt den Batch aus.

  import { onDestroy } from 'svelte';
  import { tabState, type Tab, type DepStep, type DepParam } from '../../state/tabs.svelte.ts';
  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { executeBatch } from '../../api/client';
  import { formatJson } from '../../utils/json';
  import { checkSteps, methodMetaFor, type AliasProvider } from '../../utils/dependencyCheck';
  import { ExecutionMode, type SleipnirRequest, type SleipnirParameter } from 'sleipnir-client';
  import { isObjectParam, isCollectionRef, isBoolParam, isNumberParam } from '../../utils/params';
  import DependencyStep from './DependencyStep.svelte';
  import DependencyGraph from './DependencyGraph.svelte';

  let { tab }: { tab: Tab } = $props();

  let running = $state(false);
  let activeCodeTab = $state<'ts' | 'cs' | 'json'>('ts');
  let copied = $state('');
  let copyTimer = $state<ReturnType<typeof setTimeout> | null>(null);

  onDestroy(() => {
    if (copyTimer) clearTimeout(copyTimer);
  });

  // Reaktive Step-Liste aus dem Tab.
  let steps = $derived(tab.steps ?? []);

  function persist(): void {
    tabState.persist();
  }

  // --- SleipnirRequest-Transform (Kernlogik) -------------------------------------

  /** Liefert den nativen `data`-Wert für einen Parameter (kein JSON-String mehr).
   *  @alias-Platzhalter werden als native String-Werte mit @-Präfix ausgegeben.
   *  Skalare werden aus dem Literal-String coerziert (string-aware: bool-Literal
   *  „false" → false, nicht truthy); Objekte/Collections aus dem Literal-JSON geparsed. */
  function paramDataValue(p: DepParam): unknown {
    if (p.useAlias) return `@${p.aliasRef ?? ''}`;
    const ref = p.parameterType;
    const v = p.literalValue ?? '';
    // Komplexer Typ (Objekt): raw JSON-String parsen → Objekt.
    if (isObjectParam(ref, discoveryState.data)) {
      try { return JSON.parse(v || '{}'); } catch { return {}; }
    }
    // Collection (array/set/stream): raw JSON-String parsen → Array.
    if (isCollectionRef(ref)) {
      try { const a = JSON.parse(v || '[]'); return Array.isArray(a) ? a : []; } catch { return []; }
    }
    // Skalar-Coercion aus dem String-Literal.
    if (ref.kind === 'scalar') {
      if (isBoolParam(ref)) return v === 'true';
      if (isNumberParam(ref)) { const n = v === '' ? 0 : Number(v); return Number.isNaN(n) ? 0 : n; }
      if ((ref.name ?? '').toLowerCase() === 'string') return v;
    }
    // map/opaque/void/anderer Skalar: versuchen als JSON zu parsen, sonst roher String.
    try { return JSON.parse(v); } catch { return v; }
  }

  /** Baut einen SleipnirRequest aus einem DepStep (mit echten Parameter-Namen). */
  function buildRequest(step: DepStep): SleipnirRequest {
    const arr: SleipnirParameter[] = step.params.map((p, i) => ({
      parameterName: p.parameterName,
      num: i,
      data: paramDataValue(p),
    }));
    const exposes = step.exposes.filter((e) => e.alias);
    const mapping = exposes.length > 0
      ? Object.fromEntries(exposes.map((e) => [e.alias, e.jsonPath]))
      : null;
    return {
      controller: step.controller,
      method: step.method,
      id: step.id,
      params: arr,
      dependencyMapping: mapping,
    };
  }

  /** Wire-Objekt ohne leere dependencyMapping (für saubere Code-Ausgabe). */
  function toWireRequest(step: DepStep): SleipnirRequest {
    const req = buildRequest(step);
    if (!req.dependencyMapping) {
      // dependencyMapping weglassen, damit kein null im generierten Code steht.
      return {
        controller: req.controller,
        method: req.method,
        id: req.id,
        params: req.params,
      };
    }
    return req;
  }

  let requests = $derived.by(() => steps.map(buildRequest));

  // --- Validierung ------------------------------------------------------------

  let validation = $derived.by(() => {
    const errors: string[] = [];
    const seenIds = new Set<string>();
    for (let i = 0; i < steps.length; i++) {
      const s = steps[i];
      if (!s.id) {
        errors.push(`Schritt ${i + 1}: keine Id vergeben.`);
      } else if (seenIds.has(s.id)) {
        errors.push(`Schritt ${i + 1}: doppelte Id „${s.id}".`);
      }
      seenIds.add(s.id);
      if (!s.controller || !s.method) {
        errors.push(`Schritt ${i + 1} (${s.id || '?'}): Controller/Methode nicht gewählt.`);
      }
      // Alias-Referenzen müssen in früheren Schritten exposed sein.
      const priorExposed = new Set(
        steps.slice(0, i).flatMap((ss) => ss.exposes.map((e) => e.alias).filter(Boolean)),
      );
      for (const p of s.params) {
        if (p.useAlias && p.aliasRef) {
          if (!priorExposed.has(p.aliasRef)) {
            errors.push(`Schritt ${i + 1} (${s.id}): @${p.aliasRef} wird nicht von früheren Schritten exposed.`);
          }
        }
      }
      // Exposes ohne Alias sind ungültig.
      for (const e of s.exposes) {
        if (!e.alias) {
          errors.push(`Schritt ${i + 1} (${s.id}): Expose ohne Alias-Namen.`);
        }
      }
    }
    return errors;
  });

  let isValid = $derived(validation.length === 0 && steps.length > 0);

  /** Verfügbare Aliase aus allen Schritten VOR dem gegebenen Index.
   *  Dedupliziert: zwei Schritte dürfen denselben Alias exponieren (Nutzerfehler,
   *  aber kein UI-Absturz); das {#each ... (a)} in DependencyStep verträgt keine
   *  doppelten Keys (each_key_duplicate). */
  function availableAliasesFor(index: number): string[] {
    return [
      ...new Set(
        steps
          .slice(0, index)
          .flatMap((s) => s.exposes.map((e) => e.alias).filter(Boolean)),
      ),
    ];
  }

  /** alias → provider-Step (MethodMeta + JsonPath) aus Schritten *vor* `index`.
   *  Konsistent mit availableAliasesFor: ein Consumer darf nur auf früher Exponiertes
   *  verweisen. Letzter Provider gewinnt (Spiegelt Laufzeit: exposedDependencies-
   *  Map, später Schreib überschreibt). */
  function aliasProvidersFor(index: number): Record<string, AliasProvider> {
    const map: Record<string, AliasProvider> = {};
    for (const s of steps.slice(0, index)) {
      const mm = methodMetaFor(s, discoveryState.data);
      if (!mm) continue;
      for (const e of s.exposes) {
        if (e.alias) map[e.alias] = { methodMeta: mm, jsonPath: e.jsonPath };
      }
    }
    return map;
  }

  // Statische Typ-Konsistenz über alle Schritte (nicht blockierend — „Send anyway"
  // bleibt, da der Runtime-Shape vom statischen Schema abweichen kann).
  let typeIssues = $derived.by(() => checkSteps(steps, discoveryState.data));

  // --- Code-Generierung -------------------------------------------------------

  function generateTs(): string {
    if (steps.length === 0) return '// Noch keine Schritte — „Schritt hinzufügen" klicken.';
    const reqObjects = steps
      .map((s) => '    ' + JSON.stringify(toWireRequest(s), null, 2).replace(/\n/g, '\n    '))
      .join(',\n');

    // Fluent-Alternative für triviale Single-Param-Fälle (nur Kommentar).
    const fluentHints = steps
      .filter((s) => s.params.length <= 1)
      .map((s) => {
        const aliasParams = s.params.filter((p) => p.useAlias);
        if (aliasParams.length > 0) return null;
        const literal = s.params[0];
        const litStr = literal ? `${literal.parameterName}: ${literal.literalValue ?? '""'}` : '';
        const exposes = s.exposes.map((e) => `.exposes("${e.jsonPath}", "${e.alias}")`).join('');
        return `// SleipnirCall.init("${s.controller}", "${s.method}").named("${s.id}")${litStr ? `.with({ ${litStr} })` : ''}${exposes}.toRequest()`;
      })
      .filter(Boolean) as string[];

    const fluentBlock = fluentHints.length > 0
      ? '\n/* Fluent-Alternative (nur triviale Fälle):\n' + fluentHints.map((h) => `   ${h}`).join('\n') + '\n*/\n'
      : '';

    return `${fluentBlock}import { ExecutionMode, type SleipnirMultiRequest } from "sleipnir-client";

// Batch mit @alias-Abhängigkeitskettung — Serial-Modus zwingend.
const batch: SleipnirMultiRequest = {
  mode: ExecutionMode.Serial,
  requests: [
${reqObjects}
  ],
};

const results = await rest.callBatch(batch.requests, batch.mode);`;
  }

  function csEscape(s: string): string {
    return s.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
  }

  function generateCs(): string {
    if (steps.length === 0) return '// Noch keine Schritte — „Schritt hinzufügen" klicken.';
    const reqBlocks = steps
      .map((s) => {
        const wire = toWireRequest(s);
        const mappingLine = wire.dependencyMapping
          ? `\n            DependencyMapping = new Dictionary<string, string> { ${Object.entries(wire.dependencyMapping).map(([k, v]) => `["${k}"] = "${v}"`).join(', ')} },`
          : '';
        // Params als nativer JsonNode-Literal (data ist nativer JSON-Wert, kein
        // doppelt-kodierter String mehr). Selbstcontained ohne manuelles Escaping.
        const paramsJson = JSON.stringify(wire.params ?? []);
        return `        new SleipnirRequest
        {
            Controller = "${wire.controller}",
            Method = "${wire.method}",
            Id = "${wire.id}",
            Params = JsonNode.Parse(${csStringLiteral(paramsJson)}),${mappingLine}
        }`;
      })
      .join(',\n');

    // Fluent-Kommentar für triviale Fälle.
    const fluentHints = steps
      .filter((s) => s.params.filter((p) => p.useAlias).length === 0)
      .map((s) => {
        const exposes = s.exposes.map((e) => `.Exposes("${e.jsonPath}", "${e.alias}")`).join('');
        const lit = s.params[0];
        const litStr = lit ? `.Param("${lit.parameterName}", ${lit.literalValue ?? '""'})` : '';
        return `// SleipnirCall.Init("${s.controller}", "${s.method}").Named("${s.id}")${litStr}${exposes}.ToRequest()`;
      });
    const fluentBlock = fluentHints.length > 0
      ? '\n// Fluent (nur wenn @alias der einzige Parameter ist):\n' + fluentHints.map((h) => `// ${h}`).join('\n') + '\n'
      : '';

    return `using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;
using System.Collections.Generic;
using System.Text.Json.Nodes;
${fluentBlock}
var multi = new SleipnirMultiRequest
{
    Mode = ExecutionMode.Serial,
    Requests = new List<SleipnirRequest>
    {
${reqBlocks}
    }
};

var responses = await client.Call(multi);`;
  }

  function generateJson(): string {
    const wire = {
      mode: 1, // ExecutionMode.Serial
      requests: steps.map(toWireRequest),
    };
    return JSON.stringify(wire, null, 2);
  }

  let tsCode = $derived(generateTs());
  let csCode = $derived(generateCs());
  let jsonCode = $derived(generateJson());
  let activeCode = $derived(
    activeCodeTab === 'ts' ? tsCode : activeCodeTab === 'cs' ? csCode : jsonCode,
  );

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(activeCode);
      copied = activeCodeTab;
      if (copyTimer) clearTimeout(copyTimer);
      copyTimer = setTimeout(() => {
        copied = '';
        copyTimer = null;
      }, 1500);
    } catch {
      /* ignore */
    }
  }

  // --- Ausführen --------------------------------------------------------------

  async function run(): Promise<void> {
    if (!isValid) return;
    running = true;
    const start = performance.now();
    try {
      const responses = await executeBatch({ requests, mode: ExecutionMode.Serial });
      const duration = `${Math.round(performance.now() - start)} ms`;
      tabState.applyResult(
        tab,
        { id: null },
        duration,
        formatJson(responses),
        'Batch OK',
      );
    } catch (err) {
      const duration = `${Math.round(performance.now() - start)} ms`;
      tabState.applyResult(
        tab,
        { id: null },
        duration,
        tab.responseText,
        'Error',
        err instanceof Error ? err.message : String(err),
      );
    } finally {
      running = false;
    }
  }

  // --- Step-Operationen -------------------------------------------------------

  function addStep(): void {
    if (!tab.steps) tab.steps = [];
    const n = tab.steps.length;
    tab.steps.push({
      id: `step${n + 1}`,
      controller: '',
      method: '',
      params: [],
      exposes: [],
    });
    persist();
  }

  function removeStep(index: number): void {
    if (!tab.steps) return;
    tab.steps.splice(index, 1);
    persist();
  }

  let hasResult = $derived(tab.responseText && tab.responseText !== '{}');
</script>

<div class="dep-builder">
  <div class="dep-toolbar">
    <span class="pill accent">Dependency Builder</span>
    <span class="dep-hint">@alias-Ketten visuell zusammenstellen → Code + Ausführung</span>
    <span class="pill" title="Serial-Modus zwingend für @alias-Auflösung">Mode: Serial (locked)</span>
    <span class="pill">{steps.length} Schritt{steps.length === 1 ? '' : 'e'}</span>
    <span class="pill">{tab.duration}</span>
    <div class="toolbar-actions">
      <button class="ghost small" onclick={addStep} title="Schritt hinzufügen">+ Schritt hinzufügen</button>
      <button class="primary small" onclick={run} disabled={!isValid || running}>
        {running ? 'Running…' : 'Ausführen'}
      </button>
    </div>
  </div>

  {#if !discoveryState.data || discoveryState.data.controllers.length === 0}
    <div class="dep-empty">
      <p>Discovery nicht geladen — bitte Refresh klicken oder Endpoint prüfen.</p>
    </div>
  {:else}
    <div class="dep-scroll">
      <!-- Schritt-Liste -->
      <div class="step-list">
        {#each steps as step, i (i)}
          <DependencyStep
            {step}
            index={i}
            availableAliases={availableAliasesFor(i)}
            aliasProviders={aliasProvidersFor(i)}
            onremove={() => removeStep(i)}
            onchange={persist}
          />
        {/each}
        {#if steps.length === 0}
          <div class="dep-empty thin">
            <p>Noch keine Schritte. „Schritt hinzufügen" klicken, um zu starten.</p>
          </div>
        {/if}
      </div>

      <!-- Validierung (blockierend — strukturelle Fehler) -->
      {#if validation.length > 0}
        <div class="validation-box">
          <span class="block-label error-label">Validierung</span>
          <ul>
            {#each validation as msg (msg)}
              <li>{msg}</li>
            {/each}
          </ul>
        </div>
      {/if}

      <!-- Typ-Check (nicht blockierend — statische Konsistenz gegen Discovery-Schemas,
           siehe utils/dependencyCheck.ts. „Ausführen" bleibt erlaubt, da der Runtime-Shape
           vom statischen Schema abweichen kann.) -->
      {#if typeIssues.length > 0}
        <div class="typecheck-box">
          <span class="block-label typecheck-label">Typ-Check (nicht blockierend)</span>
          <ul>
            {#each typeIssues as iss (iss.where + iss.message)}
              <li class:err={iss.severity === 'error'} class:warn={iss.severity === 'warn'}>
                <span class="iss-where">{iss.where}</span> — {iss.message}
              </li>
            {/each}
          </ul>
        </div>
      {/if}

      <!-- Live Dependency Graph -->
      {#if steps.length > 0}
        <DependencyGraph {requests} />
      {/if}

      <!-- Ergebnis -->
      {#if hasResult}
        <div class="result-box">
          <div class="result-header">
            <span class="block-label">Ergebnis</span>
            <span class="pill" class:success={tab.status === 'Batch OK'} class:error={tab.status === 'Error'}>{tab.status}</span>
          </div>
          <pre class="code result-pre"><code>{tab.responseText}</code></pre>
        </div>
      {/if}

      <!-- Code-Ausgabe -->
      <div class="codegen-section">
        <div class="codegen-header">
          <div class="lang-tabs">
            <button class="ghost small" class:active={activeCodeTab === 'ts'} onclick={() => (activeCodeTab = 'ts')}>TypeScript</button>
            <button class="ghost small" class:active={activeCodeTab === 'cs'} onclick={() => (activeCodeTab = 'cs')}>C#</button>
            <button class="ghost small" class:active={activeCodeTab === 'json'} onclick={() => (activeCodeTab = 'json')}>JSON</button>
          </div>
          <button class="primary small" onclick={copy}>
            {copied === activeCodeTab ? 'Kopiert!' : 'Code kopieren'}
          </button>
        </div>
        <pre class="code codegen-output"><code>{activeCode}</code></pre>
      </div>
    </div>
  {/if}
</div>

<style>
  .dep-builder {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    overflow: hidden;
  }
  .dep-toolbar {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
    margin-bottom: 8px;
    flex-shrink: 0;
  }
  .dep-hint {
    font-size: 0.78rem;
    color: var(--text-muted);
  }
  .toolbar-actions {
    display: flex;
    align-items: center;
    gap: 4px;
    margin-left: auto;
  }

  .dep-scroll {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  .step-list {
    display: flex;
    flex-direction: column;
    gap: 8px;
    flex-shrink: 0;
  }

  .validation-box {
    border: 1px solid var(--error);
    border-radius: var(--radius-sm);
    background: rgba(248, 81, 73, 0.08);
    padding: 8px 12px;
    flex-shrink: 0;
  }
  .error-label {
    color: var(--error);
    margin-bottom: 4px;
  }
  .validation-box ul {
    margin: 0;
    padding-left: 18px;
    font-size: 0.8rem;
    color: var(--text);
  }
  .validation-box li {
    margin: 2px 0;
  }

  /* Typ-Check-Box: nicht blockierend, daher amber statt rot — error-Items innerhalb
     bekommen roten Punkt, warn-Items amber. */
  .typecheck-box {
    border: 1px solid var(--warning, #e0a800);
    border-radius: var(--radius-sm);
    background: rgba(224, 168, 0, 0.06);
    padding: 8px 12px;
    flex-shrink: 0;
  }
  .typecheck-label {
    color: var(--warning, #e0a800);
    margin-bottom: 4px;
  }
  .typecheck-box ul {
    margin: 0;
    padding-left: 18px;
    font-size: 0.8rem;
    color: var(--text);
  }
  .typecheck-box li {
    margin: 2px 0;
    list-style: disc;
  }
  .typecheck-box li.err::marker {
    color: var(--error);
  }
  .typecheck-box li.warn::marker {
    color: var(--warning, #e0a800);
  }
  .typecheck-box .iss-where {
    font-family: var(--font-mono);
    font-size: 0.75rem;
    color: var(--text-dim);
  }

  .result-box {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    padding: 8px;
    flex-shrink: 0;
  }
  .result-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 4px;
  }
  .result-pre {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-all;
    font-size: 0.8rem;
    max-height: 240px;
    overflow-y: auto;
  }
  .result-pre code {
    font-family: var(--font-mono);
    color: var(--code-text);
  }

  .codegen-section {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-elevated);
    padding: 8px;
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .codegen-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-shrink: 0;
  }
  .lang-tabs {
    display: flex;
    gap: 2px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    overflow: hidden;
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
    max-height: 320px;
    overflow-y: auto;
  }
  .codegen-output code {
    font-family: var(--font-mono);
  }

  .dep-empty {
    padding: 24px;
    text-align: center;
    color: var(--text-muted);
    font-size: 0.9rem;
    border: 1px dashed var(--border);
    border-radius: var(--radius-sm);
  }
  .dep-empty.thin {
    padding: 16px;
  }
  .dep-empty p {
    margin: 0;
  }

  /* Pill-Status-Farben für Ergebnis */
  .pill.success {
    color: var(--success);
    border-color: var(--success);
    background: rgba(63, 185, 80, 0.1);
  }
  .pill.error {
    color: var(--error);
    border-color: var(--error);
    background: rgba(248, 81, 73, 0.1);
  }
</style>