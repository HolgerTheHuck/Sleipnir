// Shared test helper: loads + validates the committed Story-01 discovery fixture
// once and caches it. Tests import `readFixture()` to get a typed DiscoveryInfo.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { assertDiscoveryShape } from "../../src/core/discovery.js";
import type { DiscoveryInfo } from "sleipnir-client";

const here = dirname(fileURLToPath(import.meta.url));
const fixturePath = join(here, "..", "fixtures", "story01-discovery.json");

let cached: DiscoveryInfo | null = null;

export function readFixture(): DiscoveryInfo {
  if (cached) return cached;
  const text = readFileSync(fixturePath, "utf8");
  cached = assertDiscoveryShape(JSON.parse(text));
  return cached;
}

export const fixtureLocation = fixturePath;