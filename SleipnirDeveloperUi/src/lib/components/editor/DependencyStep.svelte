<script lang="ts">
  // Ein einzelner Schritt im Dependency Builder: Controller/Methode wählen,
  // Parameter als Literal oder @alias-Referenz setzen, Exposes (dependencyMapping)
  // definieren. Mutiert das step-Objekt direkt (reaktiv via $state-Proxy im Parent)
  // und meldet Änderungen über onchange → Parent persistiert.

  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { jsonPathSuggestions, displayType, defaultLiteralValue, isObjectParam, isBoolParam, objectPropertyCount } from '../../utils/params';
  import { checkExpose, checkAliasBinding, type AliasProvider, type CheckIssue } from '../../utils/dependencyCheck';
  import type { DepStep, DepParam, DepExpose } from '../../state/tabs.svelte.ts';

  let {
    step,
    index,
    availableAliases = [],
    aliasProviders = {},
    onremove,
    onchange,
  }: {
    step: DepStep;
    index: number;
    availableAliases?: string[];
    /** alias → provider-Step (MethodMeta + JsonPath) aus *früheren* Schritten.
     *  Wird im Parent pro Step-Index gebaut (slice(0, index)) — verbraucht nur
     *  frühere Provider, konsistent mit availableAliases. */
    aliasProviders?: Record<string, AliasProvider>;
    onremove?: () => void;
    onchange?: () => void;
  } = $props();

  let controllers = $derived(discoveryState.data?.controllers ?? []);
  let selectedControllerMeta = $derived(controllers.find((c) => c.name === step.controller) ?? null);
  let methods = $derived(selectedControllerMeta?.methods ?? []);
  let methodMeta = $derived(methods.find((m) => m.methodName === step.method) ?? null);

  // JsonPath-Schnellauswahlen aus dem Return-Typ. camelCase + passender Präfix
  // ($[0].prop bei Listen, sonst $.prop) — JsonPath ist case-sensitiv gegen den
  // camelCase-Server-Output, daher camelCase-Vorschläge (utils/params.ts).
  let jsonPathOptions = $derived.by(() =>
    // Dedup: gleiche JsonPath-Vorschläge würden im {#each ... (opt)} each_key_duplicate
    // auslösen.
    [...new Set(jsonPathSuggestions(methodMeta?.returnType, discoveryState.data))],
  );

  // --- Handler ---------------------------------------------------------------

  function onControllerChange(e: Event): void {
    step.controller = (e.target as HTMLSelectElement).value;
    step.method = '';
    step.params = [];
    step.exposes = [];
    onchange?.();
  }

  function onMethodChange(e: Event): void {
    const methodName = (e.target as HTMLSelectElement).value;
    step.method = methodName;
    const m = methods.find((mm) => mm.methodName === methodName);
    if (m) {
      step.params = m.parameters.map((p) => ({
        parameterName: p.parameterName,
        parameterType: p.parameterType,
        useAlias: false,
        aliasRef: undefined,
        literalValue: defaultLiteralValue(p.parameterType, discoveryState.data),
      }));
    } else {
      step.params = [];
    }
    step.exposes = [];
    onchange?.();
  }

  function onParamToggle(p: DepParam, useAlias: boolean): void {
    p.useAlias = useAlias;
    if (useAlias) {
      p.aliasRef = availableAliases[0] ?? '';
    } else {
      p.aliasRef = undefined;
    }
    onchange?.();
  }

  function onAliasRefChange(p: DepParam, e: Event): void {
    p.aliasRef = (e.target as HTMLSelectElement).value;
    onchange?.();
  }

  function onLiteralChange(p: DepParam, e: Event): void {
    p.literalValue = (e.target as HTMLInputElement | HTMLTextAreaElement).value;
    onchange?.();
  }

  function onIdChange(e: Event): void {
    step.id = (e.target as HTMLInputElement).value;
    onchange?.();
  }

  // --- Exposes ---------------------------------------------------------------

  function addExpose(): void {
    step.exposes.push({ alias: '', jsonPath: '$' });
    onchange?.();
  }

  function removeExpose(i: number): void {
    step.exposes.splice(i, 1);
    onchange?.();
  }

  function onExposeAliasChange(ex: DepExpose, e: Event): void {
    ex.alias = (e.target as HTMLInputElement).value;
    onchange?.();
  }

  function onExposeJsonPathChange(ex: DepExpose, e: Event): void {
    ex.jsonPath = (e.target as HTMLInputElement).value;
    onchange?.();
  }

  // --- Render-Heuristik (spiegelt ParamEditor) -------------------------------

  function isComplex(p: DepParam): boolean {
    return isObjectParam(p.parameterType, discoveryState.data);
  }

  function isBool(p: DepParam): boolean {
    return isBoolParam(p.parameterType);
  }

  function aliasUnavailable(p: DepParam): boolean {
    return p.useAlias && availableAliases.length === 0;
  }

  // --- Typ-Konsistenz-Checks (statisch, gegen die Discovery-Schemas) ---------
  // Siehe utils/dependencyCheck.ts: prüft Expose-Pfade gegen das Return-Schema und
  // @alias-Bindungen gegen den provider-Expose. Liefert null, wenn kein Befund.

  function exposeIssue(ex: DepExpose): CheckIssue | null {
    if (!ex.alias || !ex.jsonPath) return null;
    return checkExpose(index, step.id, methodMeta, ex.jsonPath, discoveryState.data);
  }

  function aliasIssue(p: DepParam): CheckIssue | null {
    if (!p.useAlias || !p.aliasRef) return null;
    const provider = aliasProviders[p.aliasRef];
    if (!provider) return null; // strukturell schon gemeldet (alias ohne Provider)
    return checkAliasBinding(index, step.id, provider.methodMeta, provider.jsonPath, p, discoveryState.data);
  }
</script>

<div class="dep-step">
  <div class="step-header">
    <span class="step-badge" title="Schritt-Reihenfolge">{index + 1}</span>
    <input
      class="step-id-input"
      value={step.id}
      oninput={onIdChange}
      spellcheck={false}
      placeholder="stepId"
      title="Schritt-Id (zugleich SleipnirRequest.id)"
    />
    <select
      class="ctrl-select"
      value={step.controller}
      onchange={onControllerChange}
      title="Controller wählen"
    >
      <option value="" disabled>Controller…</option>
      {#each controllers as c (c.name)}
        <option value={c.name}>{c.name}</option>
      {/each}
    </select>
    <select
      class="method-select"
      value={step.method}
      onchange={onMethodChange}
      disabled={!step.controller}
      title="Methode wählen"
    >
      <option value="" disabled>Methode…</option>
      {#each methods as m (m.methodName)}
        <option value={m.methodName}>{m.methodName}</option>
      {/each}
    </select>
    {#if methodMeta}
      <span class="return-type" title="Rückgabetyp">{displayType(methodMeta.returnType)}</span>
    {/if}
    <button class="ghost small icon step-remove" onclick={() => onremove?.()} title="Schritt entfernen">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <line x1="18" y1="6" x2="6" y2="18"></line>
        <line x1="6" y1="6" x2="18" y2="18"></line>
      </svg>
    </button>
  </div>

  <!-- Parameter ------------------------------------------------------------- -->
  {#if step.params.length > 0}
    <div class="param-block">
      <span class="block-label">Parameter</span>
      {#each step.params as p, pi (p.parameterName + pi)}
        {@const aiss = aliasIssue(p)}
        <div class="param-row">
          <span class="param-name" title={displayType(p.parameterType)}>{p.parameterName}</span>
          <span class="param-type">{displayType(p.parameterType)}</span>
          <div class="param-toggle">
            <button
              class="ghost small"
              class:active-toggle={!p.useAlias}
              onclick={() => onParamToggle(p, false)}
            >Wert</button>
            <button
              class="ghost small"
              class:active-toggle={p.useAlias}
              onclick={() => onParamToggle(p, true)}
            >Alias</button>
          </div>
          {#if p.useAlias}
            <select
              class="alias-select"
              class:error={aiss?.severity === 'error'}
              class:warn={aiss?.severity === 'warn'}
              value={p.aliasRef ?? ''}
              onchange={(e) => onAliasRefChange(p, e)}
              disabled={availableAliases.length === 0}
              title="Verfügbare Aliase aus früheren Schritten"
            >
              <option value="" disabled>Alias wählen…</option>
              {#each availableAliases as a (a)}
                <option value={a}>@{a}</option>
              {/each}
            </select>
            {#if aliasUnavailable(p)}
              <span class="warn-inline">Keine Aliase aus früheren Schritten verfügbar</span>
            {/if}
          {:else if isComplex(p)}
            <textarea
              class="code param-textarea"
              value={p.literalValue ?? ''}
              oninput={(e) => onLiteralChange(p, e)}
              rows={3}
              spellcheck={false}
              placeholder="JSON-Objekt"
            ></textarea>
          {:else if isBool(p)}
            <select
              class="bool-select"
              value={p.literalValue ?? 'false'}
              onchange={(e) => onLiteralChange(p, e)}
            >
              <option value="false">false</option>
              <option value="true">true</option>
            </select>
          {:else}
            <input
              class="literal-input"
              type="text"
              value={p.literalValue ?? ''}
              oninput={(e) => onLiteralChange(p, e)}
              spellcheck={false}
              placeholder={p.parameterName}
            />
          {/if}
        </div>
        {#if aiss}
          <div class="inline-issue {aiss.severity}" title={aiss.where}>{aiss.message}</div>
        {/if}
      {/each}
    </div>
  {:else if step.controller && step.method}
    <div class="empty-block">Keine Parameter für diese Methode.</div>
  {/if}

  <!-- Exposes (Gibt weiter) ------------------------------------------------- -->
  <div class="exposes-block">
    <div class="exposes-header">
      <span class="block-label">Gibt weiter (Exposes)</span>
      <button class="ghost small" onclick={addExpose} title="Expose hinzufügen">+ Expose</button>
    </div>
    {#if step.exposes.length === 0}
      <div class="empty-block thin">Keine Exposes — Ergebnis wird nicht für Folgeschritte weitergereicht.</div>
    {:else}
      {#each step.exposes as ex, ei (ei)}
        {@const iss = exposeIssue(ex)}
        <div class="expose-row">
          <span class="at-sign">@</span>
          <input
            class="alias-input"
            value={ex.alias}
            oninput={(e) => onExposeAliasChange(ex, e)}
            spellcheck={false}
            placeholder="aliasName"
            title="Alias-Name (ohne @)"
          />
          <input
            class="jsonpath-input code"
            class:error={iss?.severity === 'error'}
            class:warn={iss?.severity === 'warn'}
            value={ex.jsonPath}
            oninput={(e) => onExposeJsonPathChange(ex, e)}
            spellcheck={false}
            placeholder="$.Pfad"
            title="Ergebnisrelativer JsonPath"
            list={`jsonpath-opts-${index}-${ei}`}
          />
          <datalist id={`jsonpath-opts-${index}-${ei}`}>
            {#each jsonPathOptions as opt (opt)}
              <option value={opt}></option>
            {/each}
          </datalist>
          <button class="ghost small icon" onclick={() => removeExpose(ei)} title="Expose entfernen">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <line x1="18" y1="6" x2="6" y2="18"></line>
              <line x1="6" y1="6" x2="18" y2="18"></line>
            </svg>
          </button>
        </div>
        {#if iss}
          <div class="inline-issue {iss.severity}" title={iss.where}>{iss.message}</div>
        {/if}
      {/each}
    {/if}
  </div>
</div>

<style>
  .dep-step {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-overlay);
    padding: 10px 12px;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .step-header {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
  }
  .step-badge {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 22px;
    height: 22px;
    border-radius: 50%;
    background: var(--accent-secondary);
    color: #fff;
    font-size: 0.72rem;
    font-weight: 700;
    flex-shrink: 0;
  }
  .step-id-input {
    width: 90px;
    font-size: 0.8rem;
    font-family: var(--font-mono);
    padding: 3px 6px;
  }
  .ctrl-select {
    flex: 1.2;
    min-width: 110px;
    font-size: 0.82rem;
    padding: 3px 6px;
  }
  .method-select {
    flex: 1.2;
    min-width: 110px;
    font-size: 0.82rem;
    padding: 3px 6px;
  }
  .return-type {
    font-size: 0.72rem;
    color: var(--text-dim);
    font-family: var(--font-mono);
    white-space: nowrap;
  }
  .step-remove {
    flex-shrink: 0;
  }
  .step-remove:hover {
    color: var(--error);
  }

  .block-label {
    display: block;
    font-size: 0.7rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--text-muted);
    margin-bottom: 4px;
  }

  .param-block {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .param-row {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
    padding: 4px 0;
    border-top: 1px solid var(--border-muted);
  }
  .param-name {
    font-weight: 600;
    font-size: 0.82rem;
    min-width: 70px;
  }
  .param-type {
    font-size: 0.72rem;
    color: var(--text-dim);
    font-family: var(--font-mono);
    min-width: 60px;
  }
  .param-toggle {
    display: flex;
    gap: 2px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    overflow: hidden;
    flex-shrink: 0;
  }
  .param-toggle button {
    border: none;
    border-radius: 0;
    padding: 2px 8px;
    font-size: 0.75rem;
  }
  .param-toggle button.active-toggle {
    background: var(--accent-secondary);
    color: #fff;
  }
  .alias-select {
    flex: 1;
    min-width: 120px;
    font-size: 0.8rem;
    padding: 3px 6px;
  }
  .literal-input {
    flex: 1;
    min-width: 120px;
    font-size: 0.8rem;
    padding: 3px 6px;
  }
  .bool-select {
    flex: 1;
    min-width: 80px;
    font-size: 0.8rem;
    padding: 3px 6px;
  }
  .param-textarea {
    flex: 1;
    min-width: 200px;
    min-height: 50px;
    resize: vertical;
    font-size: 0.78rem;
    padding: 4px 6px;
  }
  .warn-inline {
    font-size: 0.72rem;
    color: var(--warning);
    width: 100%;
  }

  .exposes-block {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .exposes-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  .expose-row {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  .at-sign {
    color: var(--accent-secondary);
    font-family: var(--font-mono);
    font-size: 0.82rem;
    font-weight: 700;
  }
  .alias-input {
    width: 110px;
    font-size: 0.8rem;
    font-family: var(--font-mono);
    padding: 3px 6px;
  }
  .jsonpath-input {
    flex: 1;
    min-width: 120px;
    font-size: 0.8rem;
    padding: 3px 6px;
  }

  .empty-block {
    font-size: 0.78rem;
    color: var(--text-muted);
    padding: 4px 0;
  }
  .empty-block.thin {
    font-size: 0.75rem;
    color: var(--text-dim);
  }

  /* Typ-Check-Inline-Meldungen (nicht blockierend — „Send anyway" bleibt). */
  .inline-issue {
    font-size: 0.74rem;
    line-height: 1.4;
    padding: 3px 8px;
    margin-top: 2px;
    border-radius: var(--radius-sm);
    font-family: var(--font-mono);
  }
  .inline-issue.error {
    color: var(--error);
    background: rgba(248, 81, 73, 0.1);
    border: 1px solid rgba(248, 81, 73, 0.35);
  }
  .inline-issue.warn {
    color: var(--warning, #e0a800);
    background: rgba(224, 168, 0, 0.1);
    border: 1px solid rgba(224, 168, 0, 0.3);
  }
  .inline-issue.info {
    color: var(--text-muted);
    background: var(--bg-overlay);
    border: 1px dashed var(--border);
  }

  /* Input-Hervorhebung bei Typ-Befund. */
  .jsonpath-input.error,
  .alias-select.error {
    border-color: var(--error);
  }
  .jsonpath-input.warn,
  .alias-select.warn {
    border-color: var(--warning, #e0a800);
  }
</style>