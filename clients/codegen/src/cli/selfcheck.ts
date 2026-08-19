// sleipnir-gen --selfcheck — the client-side contract-drift gate.
//
// Counterpart to the server-side MSBuild drift check described in ROADMAP.md §3
// ("the part that would have made wsdl.exe better"): without a gate, someone
// changes the server contract, forgets to regenerate the client, and the client
// build stays green — the drift surfaces only at runtime as a 400. `--selfcheck`
// regenerates the client tree from `--discovery <src>` in memory and compares it
// against the committed tree rooted at `--out`; any divergence fails the gate.
//
// The comparison is one-directional by design (generated ⊆ committed): every
// file the emitter produces must already exist on disk with byte-identical
// content, or the gate fails. Files present on disk that the emitter no longer
// produces are NOT flagged — `--out` is a project client directory that may
// legitimately hold hand-written files alongside generated ones, and flagging
// extras would risk false positives. A removed controller shows up as a
// `changed` entry (its generated file shrinks), so removal is still caught.

import { readFileSync, existsSync } from "node:fs";
import { join, normalize, isAbsolute } from "node:path";

/** One generated file whose on-disk committed copy is missing or differs. */
export interface SelfcheckEntry {
  /** Relative path of the generated file (as the emitter produces it). */
  path: string;
  /** `missing` — emitted but no such file on disk; `changed` — on disk but content differs. */
  status: "missing" | "changed";
}

/** Result of a `--selfcheck` pass over a committed client tree. */
export interface SelfcheckResult {
  /** `true` iff every emitted file matches its on-disk committed copy (no drift). */
  clean: boolean;
  /** Files the emitter produces that are missing from or differ on disk. Empty when `clean`. */
  drift: SelfcheckEntry[];
  /** Number of emitted files that matched the on-disk file byte-for-byte. */
  unchanged: number;
  /** Total number of emitted files. */
  total: number;
}

/**
 * Compare the freshly emitted client tree (`emitted`, relative path → content)
 * against the committed tree rooted at `outDir`. Returns the drift (missing +
 * changed files); `clean` is `true` when there is none. Reads each emitted
 * relative path from `outDir`; refuses to read outside `outDir` (the same guard
 * the write path applies).
 */
export function selfcheck(emitted: Record<string, string>, outDir: string): SelfcheckResult {
  const drift: SelfcheckEntry[] = [];
  let unchanged = 0;
  for (const [rel, content] of Object.entries(emitted)) {
    const normalized = normalize(rel);
    if (isAbsolute(normalized) || normalized.startsWith("..")) {
      throw new Error(`refusing to read outside output dir: ${rel}`);
    }
    const dest = join(outDir, normalized);
    if (!existsSync(dest)) {
      drift.push({ path: rel, status: "missing" });
      continue;
    }
    const onDisk = readFileSync(dest, "utf8");
    if (onDisk !== content) {
      drift.push({ path: rel, status: "changed" });
      continue;
    }
    unchanged++;
  }
  return { clean: drift.length === 0, drift, unchanged, total: Object.keys(emitted).length };
}