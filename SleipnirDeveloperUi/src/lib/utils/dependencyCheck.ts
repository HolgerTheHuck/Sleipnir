// Statischer Typ-Konsistenz-Check für @alias-Abhängigkeitsketten im Dependency
// Builder.
//
// Problem: Der Server löst Dependencies zur Laufzeit auf — DependencyResolver
// zieht via JsonPath einen JsonNode aus dem serialisierten Ergebnis und BuildParameters
// deserialisiert ihn per System.Text.Json in den echten CLR-Parametertyp. Passt der
// JSON-Shape nicht, wirft STJ → generisches 500. Ein statischer Check ist nur dort
// möglich, wo *beide* Schemas vorliegen — und das ist ausschließlich die DevUI via
// Discovery (provider ReturnType + consumer ParameterMeta).
//
// Was dieser Checker nachbildet (und der Server zur Laufzeit nicht prüft):
//   1. JsonPath-Evaluation gegen das Return-Schema (nicht gegen einen Wert).
//   2. Die Match-Count-Semantik aus DependencyResolver.ExtractValue:
//        1 Match  → skalarer Knoten (auch wenn der Knoten selbst Array/Object ist,
//                   z. B. „$" über List<int> liefert das Array).
//        >1 Matches → Wrap in JsonArray → trifft nur auf List<T>/T[]/IEnumerable<T>.
//      D.h. $[*].Id ist multi-match → Array;  $[0].Id ist single-match → Skalar.
//   3. camelCase-Server-Output: Schema ist PascalCase, Wire ist camelCase, JsonPath
//      ist case-sensitiv. Property-Vergleich erfolgt gegen toCamelCase(propertyName).
//
// Einschätzung der Güte: Der Check ist eine *Warnung*, keine harte Sende-Sperre —
// der Runtime-Shape kann vom statischen Schema abweichen (Polymorphie, dynamische
// Typen). „Send anyway" bleibt möglich. Opaque Return-Typen (BCL/Drittanbieter ohne
// [SleipnirDataContract]-Override) können nicht eingesehen werden → Warnung statt
// falschem Grün.
//
// Type source: the shape model (TypeShape + shapeFromRef/returnShape/paramShape/
// propertyShape/lookupTypeMeta/findProperty) and the scalar tables are imported from
// sleipnir-codegen — the single source of truth (docs/discovery-schema.md). This file
// contains only the schema-walking + compatibility logic that is DevUI-specific.

import type {
  DiscoveryInfo,
  MethodMeta,
  ParameterMeta,
  TypeMeta,
  TypeRef,
} from 'sleipnir-client';
import {
  type TypeShape,
  findProperty,
  returnShape,
  paramShape,
  propertyShape,
  isValueTypeRef,
} from 'sleipnir-codegen';
import { toCamelCase, displayType } from './params';

// --- Öffentliche Typen ------------------------------------------------------

export type Severity = 'error' | 'warn' | 'info';

export interface CheckIssue {
  severity: Severity;
  /** Lokalisierung, z. B. „Schritt 2 (step1), Expose $.id" oder „Schritt 3 (step2), Parameter id". */
  where: string;
  message: string;
}

// --- Discovery-Lookup -------------------------------------------------------

/** Liefert die MethodMeta für einen Schritt aus der geladenen Discovery. */
export function methodMetaFor(step: { controller: string; method: string }, discovery: DiscoveryInfo | null): MethodMeta | null {
  if (!discovery) return null;
  const c = discovery.controllers.find((cc) => cc.name === step.controller);
  if (!c) return null;
  return c.methods.find((m) => m.methodName === step.method) ?? null;
}

// --- JsonPath-Evaluator (Schema-Walk) ---------------------------------------
// Unterstützt den Subset, den die DevUI vorschlägt/erzeugt:
//   $  $.prop  $.prop.sub  $[0]  $[N]  $[*]  $[*].prop  $[0].prop  $..prop
// Alles andere (Filter, Slices, ['key']) → syntaktisch abgelehnt (saubere Meldung
// statt still falschem Ergebnis).

type Sel =
  | { t: 'root' }
  | { t: 'prop'; name: string }
  | { t: 'desc'; name: string }
  | { t: 'idx'; n: number }
  | { t: 'wild' };

function parsePath(p: string): Sel[] | null {
  const s = p.trim();
  if (!s.startsWith('$')) return null;
  const sels: Sel[] = [{ t: 'root' }];
  const rest = s.slice(1);
  let i = 0;
  while (i < rest.length) {
    const c = rest[i];
    if (c === '.') {
      if (rest[i + 1] === '.') {
        i += 2;
        const m = rest.slice(i).match(/^[A-Za-z_]\w*/);
        if (!m) return null;
        sels.push({ t: 'desc', name: m[0] });
        i += m[0].length;
      } else {
        i += 1;
        const m = rest.slice(i).match(/^[A-Za-z_]\w*/);
        if (!m) return null;
        sels.push({ t: 'prop', name: m[0] });
        i += m[0].length;
      }
    } else if (c === '[') {
      const end = rest.indexOf(']', i);
      if (end === -1) return null;
      const inner = rest.slice(i + 1, end).trim();
      if (inner === '*') sels.push({ t: 'wild' });
      else if (/^\d+$/.test(inner)) sels.push({ t: 'idx', n: Number(inner) });
      else return null;
      i = end + 1;
    } else {
      return null;
    }
  }
  return sels;
}

interface EvalResult {
  shape: TypeShape;
  multi: boolean;
  found: boolean;
  /** true, wenn der Pfad in einen opaque Typ hineingeht — nicht verifizierbar.
   *  Optional: abwesend bedeutet false (für not-found-Returns). */
  opaque?: boolean;
  /** Hinweistext bei found=false (z. B. verfügbare Properties). */
  hint?: string;
}

function propertyHints(tm: TypeMeta): string {
  const names = (tm.properties ?? []).map((p) => toCamelCase(p.propertyName)).filter(Boolean);
  if (names.length === 0) return '';
  return ` Verfügbar: ${names.map((n) => `$.${n}`).join(', ')}.`;
}

/** Descendant-Suche (..name) — BFS über die Property-Struktur. */
function descendFind(
  shape: TypeShape,
  name: string,
  discovery: DiscoveryInfo | null,
  visited: Set<string> = new Set(),
): TypeShape | null {
  if (shape.kind !== 'object' || !shape.typeMeta) return null;
  const tm = shape.typeMeta;
  const key = tm.typeName || JSON.stringify(tm);
  if (visited.has(key)) return null;
  visited.add(key);
  const prop = findProperty(tm, name);
  if (prop) return propertyShape(prop, discovery);
  for (const p of tm.properties ?? []) {
    const child = propertyShape(p, discovery);
    if (child.kind === 'object') {
      const r = descendFind(child, name, discovery, visited);
      if (r) return r;
    }
  }
  return null;
}

function evalPath(root: TypeShape, sels: Sel[], discovery: DiscoveryInfo | null): EvalResult {
  let cur: TypeShape = root;
  let multi = false;
  for (const sel of sels) {
    if (sel.t === 'root') continue;
    // Opaque Typ kann nicht weiter aufgelöst werden — nicht verifizierbar.
    if (cur.kind === 'unknown') {
      return { shape: { kind: 'unknown', display: cur.display }, multi, found: true, opaque: true };
    }
    if (sel.t === 'prop') {
      if (cur.kind !== 'object' || !cur.typeMeta) {
        return { shape: { kind: 'unknown' }, multi, found: false, hint: 'Punkt-Zugriff nur auf Objekte möglich.' };
      }
      const prop = findProperty(cur.typeMeta, sel.name);
      if (!prop) {
        return { shape: { kind: 'unknown' }, multi, found: false, hint: propertyHints(cur.typeMeta) };
      }
      cur = propertyShape(prop, discovery);
    } else if (sel.t === 'idx') {
      if (cur.kind !== 'array') {
        return { shape: { kind: 'unknown' }, multi, found: false, hint: 'Index-Zugriff $[n] nur auf Listen.' };
      }
      cur = cur.element ?? { kind: 'unknown' };
    } else if (sel.t === 'wild') {
      if (cur.kind !== 'array') {
        return { shape: { kind: 'unknown' }, multi, found: false, hint: '$[*] nur auf Listen.' };
      }
      cur = cur.element ?? { kind: 'unknown' };
      multi = true;
    } else if (sel.t === 'desc') {
      const found = descendFind(cur, sel.name, discovery);
      if (!found) {
        return { shape: { kind: 'unknown' }, multi, found: false, hint: `..${sel.name} nicht im Schema gefunden.` };
      }
      cur = found;
      multi = true; // Descendant kann mehrfach matchen → sicherheitshalber Array.
    }
  }
  return { shape: cur, multi, found: true, opaque: false };
}

// --- Kompatibilität ---------------------------------------------------------

/** Flacher Kind-Kompatibilitätscheck für überlappende Eigenschaften (keine Rekursion
 *  in Element-Typen — das würde die Duck-Typing-Prüfung aufblähen, ohne an der Stelle
 *  Mehrwert zu bringen). Opaque (unknown) auf einer Seite → kompatibel angenommen, um
 *  kein false positive zu erzeugen; acceptsAny-Ziel → schluckt alles. */
function kindsCompatible(a: TypeShape, b: TypeShape): boolean {
  if (b.acceptsAny) return true;
  if (a.kind === 'unknown' || b.kind === 'unknown') return true;
  if (a.kind === b.kind) return true; // skalar==skalar, object==object, array==array
  // number→number ist derselbe Kind (oben), Widening zur Laufzeit ok.
  return false; // cross-kind (number↔string, bool↔number, object↔skalar, array↔skalar …)
}

/** True when a TypeRef is a .NET value-type scalar (missing → silent default under duck-typing). */
function isValueTypeRefOf(ref: TypeRef): boolean {
  return ref.kind === 'scalar' && isValueTypeRef(ref.name ?? '');
}

/** Pro-Eigenschaft-Analyse des object→object-Duck-Typing. Liefert drei Listen:
 *   missing      — Werttyp-Eigenschaften des Consumer, die im Provider fehlen
 *                  → zur Laufzeit still default (der heimtückische Fall, kein 400).
 *   missingRef   — Referenz-Eigenschaften des Consumer, die im Provider fehlen
 *                  → zur Laufzeit null.
 *   kindMismatch — Eigenschaften, die in beiden existieren, aber im Kind inkompatibel
 *                  sind → zur Laufzeit 400 (per-Eigenschaft).
 *  Ist alles leer, ist die Bindung sicher (Consumer ⊆ Provider mit verträglichen
 *  überlappenden Eigenschaften; überzählige Provider-Eigenschaften werden ignoriert). */
function analyzeObjectBinding(
  provider: TypeMeta,
  consumer: TypeMeta,
  discovery: DiscoveryInfo | null,
): {
  missing: { name: string; type: string }[];
  missingRef: { name: string; type: string }[];
  kindMismatch: { name: string; from: string; to: string }[];
} {
  const missing: { name: string; type: string }[] = [];
  const missingRef: { name: string; type: string }[] = [];
  const kindMismatch: { name: string; from: string; to: string }[] = [];
  for (const cp of consumer.properties ?? []) {
    const cpCamel = toCamelCase(cp.propertyName);
    const pp = (provider.properties ?? []).find((p) => toCamelCase(p.propertyName) === cpCamel);
    if (!pp) {
      if (isValueTypeRefOf(cp.propertyType)) missing.push({ name: cpCamel, type: displayType(cp.propertyType) });
      else missingRef.push({ name: cpCamel, type: displayType(cp.propertyType) });
      continue;
    }
    if (!kindsCompatible(propertyShape(pp, discovery), propertyShape(cp, discovery))) {
      kindMismatch.push({ name: cpCamel, from: displayType(pp.propertyType), to: displayType(cp.propertyType) });
    }
  }
  return { missing, missingRef, kindMismatch };
}

function okInfo(message: string): { ok: true; severity: 'info'; message: string } {
  return { ok: true, severity: 'info', message };
}
/** Trivialer Treffer (Typen passen) — wird im Issue-Modell zu null (keine Meldung),
 *  da eine bestätigende „passt"-Info nur UI-Clutter wäre. Sinnvolle Infos (acceptsAny,
 *  opaque-Element) nutzen okInfo und erscheinen als info-Issue. */
function okMatch(message: string): { ok: true; severity: 'ok'; message: string } {
  return { ok: true, severity: 'ok', message };
}

/** Vergleicht den effektiven extrahierten Shape mit dem Consumer-Parameter-Shape.
 *  Liefert {ok} bei verträglich (ggf. mit info/Warnung) oder {ok:false, severity, message}.
 *  severity 'ok' = trivialer Treffer (kein Issue). `discovery` wird gebraucht, um beim
 *  object→object-Duck-Typing die Property-Shapes aufzuschlagen (Rekursion in Element-/
 *  Property-Typen via propertyShape). */
function compatible(
  effective: TypeShape,
  param: TypeShape,
  discovery: DiscoveryInfo | null,
): { ok: boolean; severity: Severity | 'ok'; message: string } {
  // Ziel schluckt beliebige Struktur — nicht tiefer prüfbar.
  if (param.acceptsAny) {
    return okInfo(`Zieltyp „${param.display}" akzeptiert beliebige JSON-Struktur — nicht tiefer verifizierbar.`);
  }

  // Effektiver Typ opaque (nicht expandiert) — kann nicht geprüft werden.
  if (effective.kind === 'unknown') {
    return {
      ok: true,
      severity: 'warn',
      message: `Provider-Wert an diesem Pfad ist nicht expandiert/opaque — Typbindung nicht statisch prüfbar.`,
    };
  }

  // Array → Consumer muss Liste sein.
  if (effective.kind === 'array') {
    if (param.kind !== 'array') {
      return {
        ok: false,
        severity: 'error',
        message: `Pfad liefert mehrere Werte (Array), Parameter ist aber skalar „${param.display}". Nutze $[0]… für ein Element oder einen Listen-Parameter (List<T>/T[]).`,
      };
    }
    if (!param.element) {
      return okInfo(`Listen-Parameter ohne Element-Typ im Schema — Elementtyp nicht prüfbar.`);
    }
    const elem = effective.element ?? { kind: 'unknown' };
    return compatible(elem, param.element, discovery);
  }

  // Skalar/Objekt vs. Liste-Parameter.
  if (param.kind === 'array') {
    return {
      ok: false,
      severity: 'error',
      message: `Pfad liefert einen Einzelwert (${effective.display ?? effective.kind}), Parameter ist aber eine Liste „${param.display}". Nutze $[*]… für Fan-out in eine Liste.`,
    };
  }

  // Skalar vs. Objekt.
  if (effective.kind === 'object' && param.kind !== 'object') {
    return {
      ok: false,
      severity: 'error',
      message: `Pfad liefert ein Objekt (${effective.display ?? 'object'}), Parameter ist aber skalar „${param.display}".`,
    };
  }
  if (effective.kind !== 'object' && param.kind === 'object') {
    return {
      ok: false,
      severity: 'error',
      message: `Pfad liefert ${effective.display ?? effective.kind}, Parameter ist aber ein Objekt „${param.display}".`,
    };
  }

  // Objekt vs. Objekt: Duck-Typing via System.Text.Json. STJ bildet überlappende
  // Eigenschaften (case-insensitiv) ab, ignoriert überzählige Provider-Eigenschaften
  // und setzt im Consumer *fehlende* Eigenschaften still auf default — Werttypen auf
  // 0/false (heimtückisch, kein 400), Referenztypen auf null (sichtbarer). Überlappt
  // eine Eigenschaft, deren JSON-Kind aber inkompatibel ist (z. B. number vs. string),
  // wirft STJ zur Laufzeit → 400. Das prüfen wir hier pro Eigenschaft nach.
  if (effective.kind === 'object' && param.kind === 'object') {
    const eName = effective.typeMeta?.typeName ?? effective.display ?? '?';
    const pName = param.typeMeta?.typeName ?? param.display ?? '?';

    // Gleicher Typ → Schema identisch, passt (vorbehaltlich Laufzeit-Polymorphie).
    if (eName && pName && eName === pName) {
      return okMatch('Objekt-Typen passen zusammen.');
    }

    // Ohne TypeMeta auf einer Seite nicht per-Eigenschaft prüfbar → generische Warnung.
    if (!effective.typeMeta || !param.typeMeta) {
      return {
        ok: true,
        severity: 'warn',
        message: `Strukturen unterscheiden sich („${shortName(eName)}" vs „${shortName(pName)}") — JSON-duck-typing, Shape nicht tiefer prüfbar.`,
      };
    }

    const a = analyzeObjectBinding(effective.typeMeta, param.typeMeta, discovery);

    // Kind-Mismatch auf überlappenden Eigenschaften → harter Laufzeitfehler (400).
    if (a.kindMismatch.length > 0) {
      const list = a.kindMismatch.map((m) => `„${m.name}" (${m.from} → ${m.to})`).join(', ');
      return {
        ok: false,
        severity: 'error',
        message: `Struktur-Konflikt („${shortName(eName)}" → „${shortName(pName)}"): überlappende Eigenschaft(en) mit inkompatiblem JSON-Kind → System.Text.Json lehnt zur Laufzeit ab (400): ${list}.`,
      };
    }

    // Fehlende Werttyp-Eigenschaft → still default (der heimtückische Fall).
    if (a.missing.length > 0) {
      const list = a.missing.map((m) => `„${m.name}" (${m.type})`).join(', ');
      return {
        ok: true,
        severity: 'warn',
        message: `Strukturen unterscheiden sich („${shortName(eName)}" → „${shortName(pName)}"): fehlende Werttyp-Eigenschaft(en) im Provider → zur Laufzeit still default (kein 400): ${list}.`,
      };
    }

    // Fehlende Referenz-Eigenschaft → null (sichtbar, weniger gefährlich).
    if (a.missingRef.length > 0) {
      const list = a.missingRef.map((m) => `„${m.name}" (${m.type})`).join(', ');
      return {
        ok: true,
        severity: 'warn',
        message: `Strukturen unterscheiden sich („${shortName(eName)}" → „${shortName(pName)}"): fehlende Referenz-Eigenschaft(en) im Provider → zur Laufzeit null: ${list}.`,
      };
    }

    // Consumer ⊆ Provider, alle überlappenden Eigenschaften kind-verträglich → sicher.
    // (Überzählige Provider-Eigenschaften werden von STJ ignoriert.) Keine Meldung.
    return okMatch('Consumer-Struktur ist Teilmenge des Provider-Shape — duck-typing sicher.');
  }

  // Skalar vs. Skalar.
  if (effective.kind === param.kind) {
    return okMatch('Typen passen.');
  }
  return {
    ok: false,
    severity: 'error',
    message: `Typ-Konflikt: Pfad liefert ${effective.display ?? effective.kind} (JSON-${effective.kind}), Parameter erwartet ${param.display ?? param.kind}. System.Text.Json lehnt das zur Laufzeit ab.`,
  };
}

function shortName(fullName: string): string {
  const i = fullName.lastIndexOf('.');
  return i >= 0 ? fullName.slice(i + 1) : fullName;
}

// --- Öffentliche Checks -----------------------------------------------------

/** Prüft einen einzelnen Expose-Pfad gegen das Return-Schema der Methode. */
export function checkExpose(
  stepIndex: number,
  stepId: string,
  methodMeta: MethodMeta | null,
  jsonPath: string,
  discovery: DiscoveryInfo | null,
): CheckIssue | null {
  if (!jsonPath) return null;
  const where = `Schritt ${stepIndex + 1} (${stepId}), Expose ${jsonPath}`;
  if (!methodMeta) return null;

  const ret = returnShape(methodMeta, discovery);
  if (!ret) {
    return { severity: 'error', where, message: `Methode „${methodMeta.methodName}" hat keinen Rückgabewert — Exposes liefern zur Laufzeit nichts.` };
  }

  const sels = parsePath(jsonPath);
  if (!sels) {
    return { severity: 'error', where, message: `JsonPath „${jsonPath}" wird nicht unterstützt (erlaubt: $, $.prop, $[0], $[*], $..prop).` };
  }

  const ev = evalPath(ret, sels, discovery);
  if (ev.opaque) {
    return { severity: 'warn', where, message: `Rückgabetyp an Pfad „${jsonPath}" ist opaque — Pfad nicht statisch verifizierbar.` };
  }
  if (!ev.found) {
    return { severity: 'error', where, message: `JsonPath „${jsonPath}" trifft nichts im Rückgabe-Schema — zur Laufzeit „Unresolved".${ev.hint ?? ''}` };
  }

  // Pfad löst auf. Multi-Match → Array-Hinweis (Info, nicht blockierend).
  if (ev.multi) {
    return { severity: 'info', where, message: `Pfad „${jsonPath}" liefert mehrere Werte → Array. Ziel-Parameter muss List<T>/T[] sein.` };
  }
  return null;
}

/** Prüft die Typbindung eines @alias-Parameters gegen den provider-Expose-Pfad. */
export function checkAliasBinding(
  stepIndex: number,
  stepId: string,
  providerMethodMeta: MethodMeta | null,
  providerJsonPath: string,
  consumerParam: ParameterMeta,
  discovery: DiscoveryInfo | null,
): CheckIssue | null {
  const where = `Schritt ${stepIndex + 1} (${stepId}), Parameter ${consumerParam.parameterName}`;
  if (!providerMethodMeta) return null; // Strukturell bereits gemeldet (alias ohne Provider).

  const ret = returnShape(providerMethodMeta, discovery);
  if (!ret) {
    return { severity: 'error', where, message: `@alias verweist auf Methode ohne Rückgabewert.` };
  }

  const sels = parsePath(providerJsonPath);
  if (!sels) {
    return { severity: 'error', where, message: `Provider-JsonPath „${providerJsonPath}" wird nicht unterstützt.` };
  }

  const ev = evalPath(ret, sels, discovery);
  if (ev.opaque) {
    return { severity: 'warn', where, message: `Provider-Rückgabe ist opaque — Typbindung nicht prüfbar.` };
  }
  if (!ev.found) {
    return { severity: 'error', where, message: `@alias-Pfad „${providerJsonPath}" trifft nichts im Provider-Schema (→ „Unresolved" zur Laufzeit).${ev.hint ?? ''}` };
  }

  // Effektiven Typ unter Match-Count-Semantik bestimmen:
  // multi → Array<shape>;  single → shape (selbst wenn shape ein Array ist, z. B. $ über List<int>).
  const effective: TypeShape = ev.multi
    ? { kind: 'array', element: ev.shape, display: `Array<${ev.shape.display ?? ev.shape.kind}>` }
    : ev.shape;
  const param = paramShape(consumerParam, discovery);
  const cmp = compatible(effective, param, discovery);
  if (cmp.ok) {
    // info (passt mit Vorbehalt: acceptsAny / opaque-Element) und warn (Struktur-
    // Mismatch) werden als nicht-blockierende Inline-Meldung zurückgegeben;
    // severity 'ok' (trivialer Treffer) → null (keine Meldung, kein UI-Clutter).
    return cmp.severity === 'info' || cmp.severity === 'warn'
      ? { severity: cmp.severity, where, message: cmp.message }
      : null;
  }
  // !ok → severity ist 'error' (die kompatible-!ok-Pfade liefern nur 'error'/'warn').
  return { severity: cmp.severity as Severity, where, message: cmp.message };
}

export interface AliasProvider {
  methodMeta: MethodMeta;
  jsonPath: string;
}

/** Struktureller Schritt-Typ für checkSteps — eine Teilmenge von DepStep, die nur
 *  die für den Typ-Check relevanten Felder verlangt (DepStep ist hierzu zuweisbar,
 *  da DepParam alle ParameterMeta-Felder trägt). */
export interface DepCheckParam extends ParameterMeta {
  useAlias?: boolean;
  aliasRef?: string;
}
export interface DepCheckStep {
  id: string;
  controller: string;
  method: string;
  params: DepCheckParam[];
  exposes: { alias: string; jsonPath: string }[];
}

/** Aggregiert alle Typ-Issues über alle Schritte (für die nicht-blockierende
 *  Typ-Check-Box). Liefert error+warn (keine info) in Schritt-Reihenfolge. */
export function checkSteps(
  steps: DepCheckStep[],
  discovery: DiscoveryInfo | null,
): CheckIssue[] {
  const issues: CheckIssue[] = [];
  // alias → provider (zuletzt gewinnt, wie zur Laufzeit in exposedDependencies).
  const providers: Record<string, AliasProvider> = {};
  for (let i = 0; i < steps.length; i++) {
    const s = steps[i];
    const mm = methodMetaFor(s, discovery);

    for (const ex of s.exposes) {
      const issue = checkExpose(i, s.id, mm, ex.jsonPath, discovery);
      if (issue && issue.severity !== 'info') issues.push(issue);
      if (ex.alias && mm) providers[ex.alias] = { methodMeta: mm, jsonPath: ex.jsonPath };
    }

    for (const p of s.params) {
      if (!p.useAlias || !p.aliasRef) continue;
      const provider = providers[p.aliasRef];
      if (!provider) continue; // strukturell schon gemeldet
      const issue = checkAliasBinding(i, s.id, provider.methodMeta, provider.jsonPath, p, discovery);
      if (issue && issue.severity !== 'info') issues.push(issue);
    }
  }
  return issues;
}