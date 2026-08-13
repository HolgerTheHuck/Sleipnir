// DepStep-aware integration layer for the dependency query-designer canvas.
// Builds the edge model from `DepStep[]` + discovery (reusing dependencyCheck for
// per-edge severity), centralizes the drag-commit mutation, generates alias names,
// and drives auto-layout over the @alias graph. Pure functions only — no Svelte —
// so they are unit-tested in canvasGraph.test.ts.
//
// Reuses (do not duplicate the runtime binding logic here):
//   dependencyCheck.ts — checkExpose / checkAliasBinding / methodMetaFor
//   params.ts          — toCamelCase (via re-export) is used by SchemaPortTree, not here
//   canvasLayout.ts    — autoLayout / LayoutEdge
//   canvasViewport.ts  — Point

import type { DiscoveryInfo, MethodMeta } from 'sleipnir-client';
import type { DepStep } from '../state/tabs.svelte.ts';
import { methodMetaFor, checkExpose, checkAliasBinding, type Severity } from './dependencyCheck';
import { defaultLiteralValue } from './params';
import { autoLayout, type LayoutEdge, type Point } from './canvasLayout';

// --- Types ------------------------------------------------------------------

export type EdgeSeverity = Severity | 'ok';

export interface CanvasEdge {
  /** Stable id: `${fromStepId}::${alias}::${toStepId}::${paramName}`. */
  id: string;
  fromStepId: string;
  toStepId: string;
  /** The alias carried on the edge (without @). */
  alias: string;
  /** Consumer parameter name the alias binds to. */
  paramName: string;
  /** Provider expose jsonPath (result-relative). */
  providerJsonPath: string;
  /** Index of the expose in the provider's exposes (for the source anchor). */
  fromPortIndex: number;
  /** Index of the param in the consumer's params (for the target anchor). */
  toPortIndex: number;
  severity: EdgeSeverity;
  message?: string;
}

// --- Severity helpers -------------------------------------------------------

const RANK: Record<EdgeSeverity, number> = { ok: 0, info: 1, warn: 2, error: 3 };

/** Pick the worse (highest-rank) of two severities; ties keep the first message. */
function worse(a: { severity: EdgeSeverity; message?: string }, b: { severity: EdgeSeverity; message?: string }): {
  severity: EdgeSeverity;
  message?: string;
} {
  return RANK[a.severity] >= RANK[b.severity] ? a : b;
}

// --- @alias edge graph (Serial: provider must come before consumer) ----------

/** Derive the provider→consumer edge list from @alias usage, honoring Serial
 *  order: a consumer may only bind to an alias exposed by an *earlier* step
 *  (mirrors DependencyBuilderPage.aliasProvidersFor / runtime exposedDependencies). */
export function aliasEdges(steps: DepStep[]): LayoutEdge[] {
  const providerOf = new Map<string, string>(); // alias → stepId
  const edges: LayoutEdge[] = [];
  for (const s of steps) {
    // Bind this step's alias-params against providers known *so far* (earlier steps).
    for (const p of s.params) {
      if (p.useAlias && p.aliasRef) {
        const fromId = providerOf.get(p.aliasRef);
        if (fromId && fromId !== s.id) edges.push({ from: fromId, to: s.id });
      }
    }
    // Then register this step's exposes for later steps.
    for (const ex of s.exposes) {
      if (ex.alias) providerOf.set(ex.alias, s.id);
    }
  }
  return edges;
}

// --- Edge model + severity --------------------------------------------------

/** Derive the full edge model with per-edge severity from the static binding
 *  checker. An edge combines the provider's expose-path check (checkExpose) with
 *  the consumer binding check (checkAliasBinding) and keeps the worse verdict —
 *  so a broken expose path colors the edge red even if the binding alone were ok. */
export function deriveEdges(steps: DepStep[], discovery: DiscoveryInfo | null): CanvasEdge[] {
  const edges: CanvasEdge[] = [];

  // Build provider index: alias → { stepId, stepIndex, exposeIndex, methodMeta, jsonPath }.
  // Only earlier steps are eligible providers (Serial semantics).
  type Provider = {
    stepId: string;
    stepIndex: number;
    exposeIndex: number;
    methodMeta: ReturnType<typeof methodMetaFor>;
    jsonPath: string;
  };
  const providers = new Map<string, Provider>();

  for (let i = 0; i < steps.length; i++) {
    const s = steps[i];

    // Consumers first (bind against providers registered from earlier steps).
    s.params.forEach((p, pi) => {
      if (!p.useAlias || !p.aliasRef) return;
      const prov = providers.get(p.aliasRef);
      if (!prov) return; // structural validation (alias w/o provider) is handled elsewhere.
      const exposeIss = checkExpose(prov.stepIndex, prov.stepId, prov.methodMeta, prov.jsonPath, discovery);
      const bindIss = checkAliasBinding(i, s.id, prov.methodMeta, prov.jsonPath, p, discovery);
      const base = { severity: 'ok' as EdgeSeverity, message: undefined as string | undefined };
      const a = exposeIss ? { severity: exposeIss.severity as EdgeSeverity, message: exposeIss.message } : base;
      const b = bindIss ? { severity: bindIss.severity as EdgeSeverity, message: bindIss.message } : base;
      const verdict = worse(a, b);
      edges.push({
        id: `${prov.stepId}::${p.aliasRef}::${s.id}::${p.parameterName}`,
        fromStepId: prov.stepId,
        toStepId: s.id,
        alias: p.aliasRef,
        paramName: p.parameterName,
        providerJsonPath: prov.jsonPath,
        fromPortIndex: prov.exposeIndex,
        toPortIndex: pi,
        severity: verdict.severity,
        message: verdict.message,
      });
    });

    // Register this step's exposes for later consumers.
    const mm = methodMetaFor(s, discovery);
    s.exposes.forEach((ex, ei) => {
      if (ex.alias) providers.set(ex.alias, { stepId: s.id, stepIndex: i, exposeIndex: ei, methodMeta: mm, jsonPath: ex.jsonPath });
    });
  }

  return edges;
}

// --- Drag commit ------------------------------------------------------------

/** Apply a drag-to-connect commit: add an expose `{alias, jsonPath}` to the
 *  provider (idempotent — does not duplicate an existing alias) and flip the
 *  consumer's matching parameter to `useAlias:true, aliasRef`. Returns a NEW
 *  steps array (structural sharing — unchanged step objects are kept). Pure. */
export function commitEdge(
  steps: DepStep[],
  providerStepId: string,
  jsonPath: string,
  consumerStepId: string,
  paramName: string,
  aliasName: string,
): DepStep[] {
  return steps.map((s) => {
    if (s.id === providerStepId) {
      if (s.exposes.some((e) => e.alias === aliasName)) return s; // alias already exposed
      return { ...s, exposes: [...s.exposes, { alias: aliasName, jsonPath }] };
    }
    if (s.id === consumerStepId) {
      const params = s.params.map((p) =>
        p.parameterName === paramName ? { ...p, useAlias: true, aliasRef: aliasName } : p,
      );
      return { ...s, params };
    }
    return s;
  });
}

/** Remove an edge: drop the consumer's alias binding (param back to literal) and,
 *  if no other consumer still uses the alias, remove the provider's expose too.
 *  Returns a NEW steps array. Pure. */
export function removeEdge(
  steps: DepStep[],
  edge: { fromStepId: string; toStepId: string; alias: string; paramName: string },
): DepStep[] {
  // Does any *other* consumer still bind this alias?
  let otherConsumer = false;
  for (const s of steps) {
    if (s.id === edge.toStepId) continue;
    if (s.params.some((p) => p.useAlias && p.aliasRef === edge.alias)) {
      otherConsumer = true;
      break;
    }
  }
  return steps.map((s) => {
    if (s.id === edge.toStepId) {
      const params = s.params.map((p) =>
        p.parameterName === edge.paramName && p.useAlias && p.aliasRef === edge.alias
          ? { ...p, useAlias: false, aliasRef: undefined }
          : p,
      );
      return { ...s, params };
    }
    if (s.id === edge.fromStepId && !otherConsumer) {
      return { ...s, exposes: s.exposes.filter((e) => e.alias !== edge.alias) };
    }
    return s;
  });
}

// --- Alias name generation --------------------------------------------------

/** Derive a readable alias base from a jsonPath's last segment, or '' if none.
 *  `$.order.customerId` → `customerId`; `$[0].id` → `id`; `$` / `$[0]` → ''. */
export function aliasBaseFromPath(jsonPath: string): string {
  const seg = jsonPath.split(/[.[]/).filter(Boolean).pop() ?? '';
  const cleaned = seg.replace(/[\]$.]/g, '');
  return /^[A-Za-z][A-Za-z0-9_]*$/.test(cleaned) ? cleaned : '';
}

/** Generate a unique alias name across all existing exposes. Prefers a path-derived
 *  base (`customerId`), then numeric suffixes (`customerId2`), then `a1`, `a2`, … */
export function generateAlias(steps: DepStep[], jsonPath: string): string {
  const taken = new Set<string>();
  for (const s of steps) for (const ex of s.exposes) if (ex.alias) taken.add(ex.alias);

  const base = aliasBaseFromPath(jsonPath);
  if (base && !taken.has(base)) return base;
  if (base) {
    let n = 2;
    while (taken.has(`${base}${n}`)) n++;
    return `${base}${n}`;
  }
  let n = 1;
  while (taken.has(`a${n}`)) n++;
  return `a${n}`;
}

// --- Step id + layout integration ------------------------------------------

/** Next default step id (`stepN`) — the first gap (step1, step2, …) not already
 *  taken, so deleting a middle step and re-adding reuses its id. */
export function nextDefaultStepId(steps: DepStep[]): string {
  const ids = new Set(steps.map((s) => s.id));
  let n = 1;
  while (ids.has(`step${n}`)) n++;
  return `step${n}`;
}

/** Build a fresh DepStep for a controller/method, with params defaulted from the
 *  method signature (mirrors DependencyStep.onMethodChange). Shared by the
 *  toolbar "+ Aufruf" picker and the Explorer drag-onto-canvas drop. */
export function createStep(controller: string, method: MethodMeta, discovery: DiscoveryInfo | null, id: string): DepStep {
  return {
    id,
    controller,
    method,
    params: method.parameters.map((p) => ({
      parameterName: p.parameterName,
      parameterType: p.parameterType,
      useAlias: false,
      aliasRef: undefined,
      literalValue: defaultLiteralValue(p.parameterType, discovery),
    })),
    exposes: [],
  };
}

/** autoLayout over a DepStep[] using its @alias edges. */
export function autoLayoutSteps(steps: DepStep[]): Map<string, Point> {
  return autoLayout(
    steps.map((s) => s.id),
    aliasEdges(steps),
  );
}

/** Merge saved `step.x/y` with auto-layout defaults for positionless steps, so
 *  old localStorage tabs (no x/y) upgrade gracefully. Steps with explicit
 *  positions keep them; the rest get auto-layout coordinates. */
export function ensurePositions(steps: DepStep[]): Map<string, Point> {
  const auto = autoLayoutSteps(steps);
  const pos = new Map<string, Point>();
  for (const s of steps) {
    if (typeof s.x === 'number' && typeof s.y === 'number') {
      pos.set(s.id, { x: s.x, y: s.y });
    } else {
      pos.set(s.id, auto.get(s.id) ?? { x: 28, y: 28 });
    }
  }
  return pos;
}