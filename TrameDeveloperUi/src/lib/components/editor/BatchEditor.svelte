<script lang="ts">
  // Metadata-getriebener Batch-Editor. Pro Zeile: Controller/Methode per Dropdown
  // aus den Discovery-Metadaten, pro-Parameter-Eingabe (Literal ODER @alias) und
  // optionale Exposes (→ dependencyMapping, damit der DependencyGraph live läuft
  // und leichte Serial-Verkettung im selben Editor möglich ist).
  //
  // Vorbild: DependencyStep.svelte (Combobox/Param-Toggle/Exposes) und
  // ParamEditor.svelte (pro-Parameter-Eingabe). `rowToRequest` spiegelt
  // DependencyStep.buildRequest. Der Prop-Vertrag (requests/mode/onchange, bind-
  // fähig) bleibt erhalten — EditorPane.svelte braucht keine Änderung.

  import { ExecutionMode, type TrameRequest, type TrameParameter, type ControllerMeta, type MethodMeta, type ParameterMeta } from 'trame-client';
  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { defaultValueForParam, inferValue, jsonPathSuggestions, isObjectParam, isBoolParam, objectPropertyCount, displayType } from '../../utils/params';
  import BatchCodegen from './BatchCodegen.svelte';

  // `stringDataRaw` ist ein JSON-String für die Anzeige (Fallback-Textarea); für
  // den Wire-Call wird er in das native `params`-Array geparst.
  function safeParseParams(text: string): TrameParameter[] {
    try {
      const arr = JSON.parse(text);
      return Array.isArray(arr) ? arr : [];
    } catch {
      return [];
    }
  }

  let {
    requests = [],
    mode = ExecutionMode.Parallel,
    onchange,
  }: {
    requests: TrameRequest[];
    mode: ExecutionMode;
    onchange?: (requests: TrameRequest[], mode: ExecutionMode) => void;
  } = $props();

  interface BatchParam extends ParameterMeta {
    value: unknown;
    useAlias: boolean;
    aliasRef?: string;
  }

  interface BatchRow {
    key: string;
    controllerName: string;
    methodName: string;
    id: string;
    params: BatchParam[];
    exposes: { alias: string; jsonPath: string }[];
    /** Fallback-Eingabe, wenn die Methode nicht in der Discovery gefunden wird. */
    stringDataRaw: string;
    /** Advanced-Block (Exposes) ein-/ausgeklappt. */
    showExposes: boolean;
  }

  let keyCounter = 0;
  function nextKey(): string {
    keyCounter++;
    return `batch-${keyCounter}`;
  }

  function newBlankRow(): BatchRow {
    return {
      key: nextKey(),
      controllerName: '',
      methodName: '',
      id: `req-${keyCounter}`,
      params: [],
      exposes: [],
      stringDataRaw: '[]',
      showExposes: false,
    };
  }

  // --- Discovery-Lookups -------------------------------------------------------

  function controllerMeta(name: string): ControllerMeta | undefined {
    return discoveryState.data?.controllers.find((c) => c.name === name);
  }

  function methodMeta(row: BatchRow): MethodMeta | undefined {
    return controllerMeta(row.controllerName)?.methods.find((m) => m.methodName === row.methodName);
  }

  /** Verfügbare Aliase = Exposes aller VORHERIGEN Zeilen mit gesetztem Alias.
   *  Dedupliziert: zwei Zeilen dürfen densben Alias exponieren (Nutzerfehler, aber
   *  kein UI-Absturz); das {#each ... (a)} der Alias-Datalist verträgt keine
   *  doppelten Keys (each_key_duplicate). */
  function availableAliases(rowIndex: number): string[] {
    return [
      ...new Set(
        rows
          .slice(0, rowIndex)
          .flatMap((r) => r.exposes.map((e) => e.alias).filter(Boolean)),
      ),
    ];
  }

  /** JsonPath-Vorschläge aus dem Return-Typ. camelCase + passender Präfix
   *  ($[0].prop bei Listen, sonst $.prop) — siehe utils/params.ts
   *  (JsonPath ist case-sensitiv gegen den camelCase-Server-Output). */
  function jsonPathOptions(row: BatchRow): string[] {
    const mm = methodMeta(row);
    // Dedup: gleiche JsonPath-Vorschläge würden im {#each ... (opt)} each_key_duplicate
    // auslösen.
    return [...new Set(jsonPathSuggestions(mm?.returnType, discoveryState.data))];
  }

  // --- Editor-Modell (Source of truth) -----------------------------------------

  let rows = $state<BatchRow[]>(hydrate(requests));

  /** Einmalige Hydratation aus dem eingehenden `requests` (z. B. wenn der Toggle
   *  mit schon vorhandenen Requests aktiviert wird). `batchRequests` ist nicht
   *  persistiert, daher ist Empty-Init der Normalfall. */
  function hydrate(reqs: TrameRequest[]): BatchRow[] {
    if (!reqs || reqs.length === 0) return [];
    return reqs.map((r) => {
      const key = nextKey();
      const mm = controllerMeta(r.controller)?.methods.find((m) => m.methodName === r.method);
      const row: BatchRow = {
        key,
        controllerName: r.controller,
        methodName: r.method,
        id: r.id ?? `req-${keyCounter}`,
        params: [],
        exposes: r.dependencyMapping
          ? Object.entries(r.dependencyMapping).map(([alias, jsonPath]) => ({ alias, jsonPath }))
          : [],
        stringDataRaw: r.params ? JSON.stringify(r.params) : '[]',
        showExposes: !!r.dependencyMapping && Object.keys(r.dependencyMapping).length > 0,
      };
      if (mm) {
        // Methode bekannt → Parameter mit Defaults belegen (Werte aus params
        // werden nicht zurückgeparst — Hydratation ist Best-Effort, da batchRequests
        // nicht persistiert wird und Remounts selten sind).
        row.params = mm.parameters.map((p) => ({
          ...p,
          value: defaultValueForParam(p, discoveryState.data),
          useAlias: false,
          aliasRef: undefined,
        }));
      }
      return row;
    });
  }

  // --- row → TrameRequest (spiegelt DependencyStep.buildRequest) -------------

  function rowToRequest(row: BatchRow): TrameRequest {
    const mm = methodMeta(row);
    // `params` ist das native Array (data = native Werte ODER "@alias"-String).
    // Im Fallback-Fall (Methode nicht in Discovery) wird der Rohtext geparst.
    const params = mm
      ? row.params.map((p, i) => ({
          parameterName: p.parameterName,
          num: i,
          data: p.useAlias ? `@${p.aliasRef ?? ''}` : (p.value ?? null),
        }))
      : safeParseParams(row.stringDataRaw);
    const exposes = row.exposes.filter((e) => e.alias);
    const dependencyMapping = exposes.length
      ? Object.fromEntries(exposes.map((e) => [e.alias, e.jsonPath]))
      : null;
    return {
      controller: row.controllerName,
      method: row.methodName,
      params,
      id: row.id,
      dependencyMapping,
    };
  }

  function sync(): void {
    const derived = rows.map(rowToRequest);
    // In-place Mutation des gebundenen Arrays (gleiche Referenz wie batchRequests
    // im Parent) — so sehen executeBatch und der DependencyGraph die Zeilen
    // zuverlässig. Eine reine Reassignierung (requests = derived) propagiert via
    // bind: nicht garantiert in den Parent-Status; die alte BatchEditor-Version
    // nutzte push/splice auf dem gebundenen Array (dasselbe Muster).
    requests.length = 0;
    requests.push(...derived);
    onchange?.(requests, mode);
  }

  // --- Mutations-Handler -------------------------------------------------------

  function addRow(): void {
    rows.push(newBlankRow());
    rows = [...rows];
    sync();
  }

  function removeRow(index: number): void {
    rows.splice(index, 1);
    rows = [...rows];
    sync();
  }

  function onIdInput(row: BatchRow, e: Event): void {
    row.id = (e.target as HTMLInputElement).value;
    sync();
  }

  function onControllerInput(row: BatchRow, e: Event): void {
    row.controllerName = (e.target as HTMLInputElement).value;
    // Controller-Wechsel: Methode + Parameter + Exposes verwerfen.
    row.methodName = '';
    row.params = [];
    row.exposes = [];
    row.stringDataRaw = '[]';
    row.showExposes = false;
    rows = [...rows];
    sync();
  }

  function onMethodInput(row: BatchRow, e: Event): void {
    const name = (e.target as HTMLInputElement).value;
    row.methodName = name;
    const mm = methodMeta(row);
    if (mm) {
      row.params = mm.parameters.map((p) => ({
        ...p,
        value: defaultValueForParam(p, discoveryState.data),
        useAlias: false,
        aliasRef: undefined,
      }));
      row.stringDataRaw = '[]';
    } else {
      row.params = [];
    }
    rows = [...rows];
    sync();
  }

  function onStringDataRawInput(row: BatchRow, e: Event): void {
    row.stringDataRaw = (e.target as HTMLTextAreaElement).value;
    sync();
  }

  function onParamValue(row: BatchRow, p: BatchParam, e: Event): void {
    const v = (e.target as HTMLInputElement | HTMLTextAreaElement).value;
    if (isObjectParam(p.parameterType, discoveryState.data)) {
      try {
        p.value = JSON.parse(v);
      } catch {
        p.value = v;
      }
    } else {
      p.value = inferValue(v, p.parameterType);
    }
    sync();
  }

  function onParamAliasToggle(rowIndex: number, p: BatchParam, useAlias: boolean): void {
    p.useAlias = useAlias;
    if (useAlias) {
      p.aliasRef = availableAliases(rowIndex)[0] ?? '';
    } else {
      p.aliasRef = undefined;
    }
    rows = [...rows];
    sync();
  }

  function onAliasRefChange(p: BatchParam, e: Event): void {
    p.aliasRef = (e.target as HTMLSelectElement).value;
    sync();
  }

  function addExpose(row: BatchRow): void {
    row.exposes.push({ alias: '', jsonPath: '$' });
    row.showExposes = true;
    rows = [...rows];
    sync();
  }

  function removeExpose(row: BatchRow, ei: number): void {
    row.exposes.splice(ei, 1);
    rows = [...rows];
    sync();
  }

  function onExposeAliasInput(ex: { alias: string; jsonPath: string }, e: Event): void {
    ex.alias = (e.target as HTMLInputElement).value;
    sync();
  }

  function onExposeJsonPathInput(ex: { alias: string; jsonPath: string }, e: Event): void {
    ex.jsonPath = (e.target as HTMLInputElement).value;
    sync();
  }

  function updateMode(newMode: ExecutionMode): void {
    mode = newMode;
    onchange?.(requests, mode);
  }

  // --- Render-Heuristik (spiegelt ParamEditor/DependencyStep) ------------------

  function isComplex(p: BatchParam): boolean {
    return isObjectParam(p.parameterType, discoveryState.data);
  }

  function isBool(p: BatchParam): boolean {
    return isBoolParam(p.parameterType);
  }

  function paramInputValue(p: BatchParam): string {
    if (isObjectParam(p.parameterType, discoveryState.data)) {
      if (typeof p.value === 'object' && p.value !== null) {
        return JSON.stringify(p.value, null, 2);
      }
      return '{}';
    }
    if (p.value === null || p.value === undefined) return '';
    return String(p.value);
  }

  // Controller-Optionen (einmalig). Methoden-Optionen pro Zeile (abhängig vom
  // gewählten Controller). Beides als Combobox (input + datalist) — Auswahl per
  // Klick ODER freie Eingabe (Fallback für Controller/Methoden außerhalb der
  // geladenen Discovery).
  let controllerNames = $derived(
    (discoveryState.data?.controllers ?? []).map((c) => c.name),
  );

  function methodNames(row: BatchRow): string[] {
    const c = controllerMeta(row.controllerName);
    const names = c
      ? c.methods.map((m) => m.methodName)
      : // Controller unbekannt (Leerzeile nach "+ Add") → alle Methoden aller
        // Controller anbieten. Dedup: zwei Controller mit gleichem Methodennamen
        // (z. B. "GetById") würden sonst im {#each ... (name)} der Datalist
        // each_key_duplicate auslösen — der Absturz beim Batch-Add.
        (discoveryState.data?.controllers ?? []).flatMap((cc) =>
          cc.methods.map((m) => m.methodName),
        );
    return [...new Set(names)];
  }

  // Platzhalter enthält `{}` — als JS-String gebunden, damit Svelte die braces
  // nicht als Expression parst.
  const fallbackPlaceholder = '[{"parameterName":"x","data":42}]';
</script>

<div class="batch-editor">
  <div class="batch-header">
    <span class="field-label">Batch Request ({rows.length} calls)</span>
    <div class="mode-switch">
      <button
        class="ghost small"
        class:active={mode === ExecutionMode.Parallel}
        onclick={() => updateMode(ExecutionMode.Parallel)}
      >Parallel</button>
      <button
        class="ghost small"
        class:active={mode === ExecutionMode.Serial}
        onclick={() => updateMode(ExecutionMode.Serial)}
      >Serial</button>
    </div>
    <button class="ghost small" onclick={addRow}>+ Add</button>
  </div>

  <!-- Gemeinsame Controller-Datalist (Combobox-Vorschläge) -->
  <datalist id="batch-ctrls">
    {#each controllerNames as name (name)}
      <option value={name}></option>
    {/each}
  </datalist>

  <div class="batch-list">
    {#each rows as row, i (row.key)}
      {@const mm = methodMeta(row)}
      {@const aliases = availableAliases(i)}
      {@const jpOpts = jsonPathOptions(row)}
      <div class="batch-card">
        <div class="card-header">
          <span class="row-index" title="Zeilen-Reihenfolge">{i + 1}</span>
          <input
            class="id-input"
            value={row.id}
            oninput={(e) => onIdInput(row, e)}
            spellcheck={false}
            placeholder="ID"
            title="Korrelations-Id (zugleich TrameRequest.id, @alias-Schlüssel)"
          />
          <input
            class="ctrl-input"
            list="batch-ctrls"
            value={row.controllerName}
            oninput={(e) => onControllerInput(row, e)}
            spellcheck={false}
            placeholder="Controller"
            title="Controller wählen oder eingeben"
          />
          <input
            class="method-input"
            list={`batch-methods-${row.key}`}
            value={row.methodName}
            oninput={(e) => onMethodInput(row, e)}
            spellcheck={false}
            placeholder="Methode"
            title="Methode wählen oder eingeben"
          />
          <datalist id={`batch-methods-${row.key}`}>
            {#each methodNames(row) as name (name)}
              <option value={name}></option>
            {/each}
          </datalist>
          {#if mm}
            <span class="return-type" title="Rückgabetyp">{displayType(mm.returnType)}</span>
          {/if}
          <button class="ghost small icon row-remove" onclick={() => removeRow(i)} title="Zeile entfernen">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <line x1="18" y1="6" x2="6" y2="18"></line>
              <line x1="6" y1="6" x2="18" y2="18"></line>
            </svg>
          </button>
        </div>

        <!-- Parameter (nur wenn Methode in Discovery gefunden) ------------------->
        {#if mm}
          {#if row.params.length > 0}
            <div class="param-block">
              {#each row.params as p, pi (p.parameterName + pi)}
                <div class="param-row">
                  <span class="param-name" title={displayType(p.parameterType)}>{p.parameterName}</span>
                  <span class="param-type">{displayType(p.parameterType)}</span>
                  <div class="param-toggle" title="Wert direkt oder als @alias aus früherer Zeile">
                    <button
                      class="ghost small"
                      class:active-toggle={!p.useAlias}
                      onclick={() => onParamAliasToggle(i, p, false)}
                    >Wert</button>
                    <button
                      class="ghost small"
                      class:active-toggle={p.useAlias}
                      onclick={() => onParamAliasToggle(i, p, true)}
                    >Alias</button>
                  </div>
                  {#if p.useAlias}
                    <select
                      class="alias-select"
                      value={p.aliasRef ?? ''}
                      onchange={(e) => onAliasRefChange(p, e)}
                      disabled={aliases.length === 0}
                      title="Verfügbare Aliase aus früheren Zeilen"
                    >
                      <option value="" disabled>Alias wählen…</option>
                      {#each aliases as a (a)}
                        <option value={a}>@{a}</option>
                      {/each}
                    </select>
                    {#if aliases.length === 0}
                      <span class="warn-inline">Keine Aliase aus früheren Zeilen verfügbar</span>
                    {/if}
                  {:else if isComplex(p)}
                    <textarea
                      class="code param-textarea"
                      value={paramInputValue(p)}
                      oninput={(e) => onParamValue(row, p, e)}
                      rows={Math.min(8, objectPropertyCount(p.parameterType, discoveryState.data) + 2)}
                      spellcheck={false}
                      placeholder="JSON-Objekt"
                    ></textarea>
                  {:else if isBool(p)}
                    <select
                      class="bool-select"
                      value={String(p.value)}
                      onchange={(e) => onParamValue(row, p, e)}
                    >
                      <option value="false">false</option>
                      <option value="true">true</option>
                    </select>
                  {:else}
                    <input
                      class="literal-input"
                      type="text"
                      value={paramInputValue(p)}
                      oninput={(e) => onParamValue(row, p, e)}
                      spellcheck={false}
                      placeholder={p.parameterName}
                    />
                  {/if}
                </div>
              {/each}
            </div>
          {:else}
            <div class="empty-block">Keine Parameter für diese Methode.</div>
          {/if}

          <!-- Exposes (advanced, eingeklappt) ----------------------------------->
          <div class="exposes-block">
            <div class="exposes-header">
              <button class="ghost small tiny" onclick={() => { row.showExposes = !row.showExposes; rows = [...rows]; }}>
                {row.showExposes ? '▾' : '▸'} Exposes ({row.exposes.length})
              </button>
              <button class="ghost small tiny" onclick={() => addExpose(row)} title="Expose hinzufügen">+ Expose</button>
            </div>
            {#if row.showExposes}
              {#if row.exposes.length === 0}
                <div class="empty-block thin">Keine Exposes — Ergebnis wird nicht für Folgezeilen weitergereicht.</div>
              {:else}
                {#each row.exposes as ex, ei (ei)}
                  <div class="expose-row">
                    <span class="at-sign">@</span>
                    <input
                      class="alias-input"
                      value={ex.alias}
                      oninput={(e) => onExposeAliasInput(ex, e)}
                      spellcheck={false}
                      placeholder="aliasName"
                      title="Alias-Name (ohne @)"
                    />
                    <input
                      class="jsonpath-input code"
                      value={ex.jsonPath}
                      oninput={(e) => onExposeJsonPathInput(ex, e)}
                      spellcheck={false}
                      placeholder="$.Pfad"
                      title="Ergebnisrelativer JsonPath ($ = ganzes Result)"
                      list={`batch-jp-${row.key}-${ei}`}
                    />
                    <datalist id={`batch-jp-${row.key}-${ei}`}>
                      {#each jpOpts as opt (opt)}
                        <option value={opt}></option>
                      {/each}
                    </datalist>
                    <button class="ghost small icon" onclick={() => removeExpose(row, ei)} title="Expose entfernen">
                      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <line x1="18" y1="6" x2="6" y2="18"></line>
                        <line x1="6" y1="6" x2="18" y2="18"></line>
                      </svg>
                    </button>
                  </div>
                {/each}
              {/if}
            {/if}
          </div>
        {:else}
          <!-- Fallback: Methode nicht in Discovery → params als Rohtext -------->
          <div class="fallback-block">
            <span class="fallback-hint">Methode nicht in der Discovery — Parameter als JSON-Array (params) manuell eingeben:</span>
            <textarea
              class="code fallback-textarea"
              value={row.stringDataRaw}
              oninput={(e) => onStringDataRawInput(row, e)}
              rows={3}
              spellcheck={false}
              placeholder={fallbackPlaceholder}
            ></textarea>
          </div>
        {/if}
      </div>
    {/each}

    {#if rows.length === 0}
      <div class="empty-row">Keine Batch-Zeilen — „+ Add" klickt eine hinzu.</div>
    {/if}
  </div>

  <!-- Code-Generator für die konfigurierte Abfrage (reaktiv über das gebundene
       requests-Array, wie der DependencyGraph). -->
  {#if rows.length > 0}
    <BatchCodegen {requests} {mode} />
  {/if}
</div>

<style>
  .batch-editor {
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .batch-header {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 2px;
  }
  .batch-header .field-label {
    margin: 0;
    flex: 1;
  }
  .mode-switch {
    display: flex;
    gap: 2px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    overflow: hidden;
  }
  .mode-switch button {
    border: none;
    border-radius: 0;
    padding: 3px 10px;
    font-size: 0.8rem;
  }
  .mode-switch button.active {
    background: var(--accent-secondary);
    color: #fff;
  }
  .batch-list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    max-height: 260px;
    overflow-y: auto;
  }
  .batch-card {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-overlay);
    padding: 8px 10px;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .card-header {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-wrap: wrap;
  }
  .row-index {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 20px;
    height: 20px;
    border-radius: 50%;
    background: var(--accent-secondary);
    color: #fff;
    font-size: 0.7rem;
    font-weight: 700;
    flex-shrink: 0;
  }
  .id-input {
    width: 80px;
    min-width: 60px;
    font-size: 0.78rem;
    font-family: var(--font-mono);
    padding: 3px 6px;
  }
  .ctrl-input {
    flex: 1.4;
    min-width: 100px;
    font-size: 0.8rem;
    padding: 3px 6px;
  }
  .method-input {
    flex: 1.4;
    min-width: 100px;
    font-size: 0.8rem;
    padding: 3px 6px;
  }
  .return-type {
    font-size: 0.7rem;
    color: var(--text-dim);
    font-family: var(--font-mono);
    white-space: nowrap;
  }
  .row-remove {
    flex-shrink: 0;
  }
  .row-remove:hover {
    color: var(--error);
  }
  .card-header input {
    font-size: 0.8rem;
    padding: 3px 6px;
  }

  .param-block {
    display: flex;
    flex-direction: column;
    gap: 2px;
    border-top: 1px solid var(--border-muted);
    padding-top: 4px;
  }
  .param-row {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
    padding: 3px 0;
  }
  .param-name {
    font-weight: 600;
    font-size: 0.8rem;
    min-width: 70px;
  }
  .param-type {
    font-size: 0.7rem;
    color: var(--text-dim);
    font-family: var(--font-mono);
    min-width: 56px;
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
    font-size: 0.74rem;
  }
  .param-toggle button.active-toggle {
    background: var(--accent-secondary);
    color: #fff;
  }
  .alias-select {
    flex: 1;
    min-width: 120px;
    font-size: 0.78rem;
    padding: 3px 6px;
  }
  .literal-input {
    flex: 1;
    min-width: 120px;
    font-size: 0.78rem;
    padding: 3px 6px;
  }
  .bool-select {
    flex: 1;
    min-width: 80px;
    font-size: 0.78rem;
    padding: 3px 6px;
  }
  .param-textarea {
    flex: 1;
    min-width: 200px;
    min-height: 50px;
    resize: vertical;
    font-size: 0.76rem;
    padding: 4px 6px;
  }
  .warn-inline {
    font-size: 0.7rem;
    color: var(--warning);
    width: 100%;
  }

  .exposes-block {
    display: flex;
    flex-direction: column;
    gap: 4px;
    border-top: 1px solid var(--border-muted);
    padding-top: 4px;
  }
  .exposes-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
  }
  .tiny {
    padding: 2px 8px;
    font-size: 0.74rem;
  }
  .expose-row {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  .at-sign {
    color: var(--accent-secondary);
    font-family: var(--font-mono);
    font-size: 0.8rem;
    font-weight: 700;
  }
  .alias-input {
    width: 110px;
    font-size: 0.78rem;
    font-family: var(--font-mono);
    padding: 3px 6px;
  }
  .jsonpath-input {
    flex: 1;
    min-width: 120px;
    font-size: 0.78rem;
    padding: 3px 6px;
  }

  .empty-block {
    font-size: 0.76rem;
    color: var(--text-muted);
    padding: 2px 0;
  }
  .empty-block.thin {
    font-size: 0.74rem;
    color: var(--text-dim);
  }
  .empty-row {
    padding: 12px;
    text-align: center;
    color: var(--text-muted);
    font-size: 0.85rem;
    border: 1px dashed var(--border);
    border-radius: var(--radius-sm);
  }

  .fallback-block {
    display: flex;
    flex-direction: column;
    gap: 4px;
    border-top: 1px solid var(--border-muted);
    padding-top: 4px;
  }
  .fallback-hint {
    font-size: 0.74rem;
    color: var(--text-dim);
  }
  .fallback-textarea {
    width: 100%;
    min-height: 56px;
    resize: vertical;
    font-size: 0.76rem;
    padding: 4px 6px;
  }
</style>