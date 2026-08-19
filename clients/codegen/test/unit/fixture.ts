// Shared test helper: loads + validates a committed discovery fixture once and
// caches it. Tests import `readFixture()` (Story-01, the flat diamond) or
// `readFixture("story02")` (nested-array shapes) to get a typed DiscoveryInfo.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { assertDiscoveryShape } from "../../src/core/discovery.js";
import type { DiscoveryInfo } from "sleipnir-client";

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, "..", "fixtures");

const cache = new Map<string, DiscoveryInfo>();

/** Load + validate a discovery fixture by name (default `"story01"`). */
export function readFixture(name: "story01" | "story02" | "story03" = "story01"): DiscoveryInfo {
  const cached = cache.get(name);
  if (cached) return cached;
  const text = readFileSync(join(fixturesDir, `${name}-discovery.json`), "utf8");
  const parsed = assertDiscoveryShape(JSON.parse(text));
  cache.set(name, parsed);
  return parsed;
}

/** Path to the Story-01 fixture (kept for backward compatibility). */
export const fixtureLocation = join(fixturesDir, "story01-discovery.json");