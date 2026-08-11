// Node-only entry: re-exports the browser-safe core AND adds discovery loading
// (which needs `node:fs/promises` + stdin). Browser consumers must import
// `sleipnir-codegen`, not `sleipnir-codegen/node`, so the Node-only modules never
// enter a browser bundle.

export * from "./index.js";
export {
  loadDiscovery,
  assertDiscoveryShape,
  DiscoveryShapeError,
  type LoadDiscoveryOptions,
} from "./core/discovery.js";