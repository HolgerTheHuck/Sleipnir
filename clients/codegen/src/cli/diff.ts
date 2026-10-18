// sleipnir-gen diff — the client-side contract compatibility gate.
//
//   sleipnir-gen diff --base <url|file|-> --head <url|file|->
//                     [--fail-on breaking|soft|never] [--rollback] [--json]
//
// --base is the contract deployed clients were built against (e.g. the server's
// contract.sleipnir.json at the last release: `git show v1.7.0:Api/contract.sleipnir.json`),
// --head the contract the server now serves. Same engine and rules as the server-side MSBuild
// gate (Sleipnir.Server.Codegen, SleipnirContractBaseline) — see CONTRACT_EVOLUTION.md.
//
// Exit codes: 0 no change at/above --fail-on · 5 changes at/above --fail-on · 1 usage ·
//             2 discovery shape mismatch · 3 I/O.

import { loadDiscovery, DiscoveryShapeError } from "../core/discovery.js";
import { compareContracts, formatCompatReport, type CompatReport } from "../core/compat.js";

export type FailOn = "breaking" | "soft" | "never";

export interface DiffArgs {
  base: string | undefined;
  head: string | undefined;
  failOn: FailOn;
  rollback: boolean;
  json: boolean;
  bearer: string | undefined;
  timeout: number | undefined;
}

export const DIFF_EXIT_CHANGES = 5;

/** Parse `diff` options. Throws an Error with a usage message on bad input. */
export function parseDiffArgs(argv: string[]): DiffArgs {
  const args: DiffArgs = { base: undefined, head: undefined, failOn: "breaking", rollback: false, json: false, bearer: undefined, timeout: undefined };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = (): string => {
      if (i + 1 >= argv.length) throw new Error(`option ${a} requires a value`);
      return argv[++i];
    };
    switch (a) {
      case "--base": args.base = next(); break;
      case "--head": args.head = next(); break;
      case "--fail-on": {
        const v = next();
        if (v !== "breaking" && v !== "soft" && v !== "never") throw new Error(`--fail-on must be breaking, soft or never, got "${v}"`);
        args.failOn = v;
        break;
      }
      case "--rollback": args.rollback = true; break;
      case "--json": args.json = true; break;
      case "--bearer": args.bearer = next(); break;
      case "--timeout": {
        const n = Number(next());
        if (!Number.isFinite(n) || n <= 0) throw new Error("--timeout must be a positive number");
        args.timeout = n;
        break;
      }
      default: throw new Error(a.startsWith("--") ? `unknown option ${a}` : `unexpected argument ${a}`);
    }
  }
  if (!args.base) throw new Error("--base is required (url | file | -)");
  if (!args.head) throw new Error("--head is required (url | file | -)");
  if (args.base === "-" && args.head === "-") throw new Error("only one of --base / --head can read stdin");
  return args;
}

/** Exit code for a report under the --fail-on threshold. */
export function diffExitCode(report: CompatReport, failOn: FailOn): number {
  const { breaking, soft } = report.summary;
  if (failOn === "breaking" && breaking > 0) return DIFF_EXIT_CHANGES;
  if (failOn === "soft" && breaking + soft > 0) return DIFF_EXIT_CHANGES;
  return 0;
}

export const DIFF_USAGE =
  `Usage:\n` +
  `  sleipnir-gen diff --base <url|file|-> --head <url|file|-> [--fail-on breaking|soft|never]\n` +
  `                    [--rollback] [--json] [--bearer <token>] [--timeout <ms>]\n` +
  `\n` +
  `  --base <src>         Contract deployed clients were built against (e.g. the last release's\n` +
  `                       contract.sleipnir.json: git show v1.7.0:Api/contract.sleipnir.json > base.json).\n` +
  `  --head <src>         Contract the server serves now (file, URL, or - for stdin).\n` +
  `  --fail-on <level>    Exit 5 on breaking (default), on breaking or soft, or never.\n` +
  `  --rollback           Also check current clients against the baseline server (rollback safety).\n` +
  `  --json               Print the machine-readable report instead of text.\n`;

/** Run `diff`; returns the process exit code. Output goes to the given writers. */
export async function runDiff(argv: string[], out: (s: string) => void, err: (s: string) => void): Promise<number> {
  let args: DiffArgs;
  try {
    args = parseDiffArgs(argv);
  } catch (e) {
    err(`sleipnir-gen diff: error: ${(e as Error).message}\n${DIFF_USAGE}`);
    return 1;
  }

  let base: unknown;
  let head: unknown;
  try {
    base = await loadDiscovery(args.base!, { bearer: args.bearer, timeout: args.timeout });
    head = await loadDiscovery(args.head!, { bearer: args.bearer, timeout: args.timeout });
  } catch (e) {
    if (e instanceof DiscoveryShapeError) {
      err(`sleipnir-gen diff: error: discovery shape: ${e.message}\n`);
      return 2;
    }
    err(`sleipnir-gen diff: error: failed to load a contract: ${(e as Error).message}\n`);
    return 3;
  }

  const report = compareContracts(base, head, args.rollback);
  out(args.json ? JSON.stringify(report, null, 2) + "\n" : formatCompatReport(report));
  const code = diffExitCode(report, args.failOn);
  if (code !== 0) {
    err(`sleipnir-gen diff: ${report.summary.breaking} breaking${args.failOn === "soft" ? ` and ${report.summary.soft} soft` : ""} change(s) - see CONTRACT_EVOLUTION.md for the safe alternatives (optional parameters, additive outputs, Controller.v2 for real breaks).\n`);
  }
  return code;
}
