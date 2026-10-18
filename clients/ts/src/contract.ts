// Contract identity on the wire (CONTRACT_EVOLUTION.md §7). A generated client passes its
// embedded contract hash (`contractHash` router option); the router sends it on every request so
// the server can count which contracts its callers were built against:
//   - REST + SSE: header `Sleipnir-Contract`,
//   - WebSocket + SignalR: query parameter `sleipnir-contract` on connect — a browser cannot set
//     headers on a WebSocket upgrade.

/** Request header carrying the client's contract hash (REST, SSE). */
export const CONTRACT_HEADER = "Sleipnir-Contract";

/** Query parameter carrying the client's contract hash (WebSocket, SignalR). */
export const CONTRACT_QUERY = "sleipnir-contract";

/** `url` with the contract query parameter appended (unchanged without a hash). */
export function withContractQuery(url: string, contractHash: string | undefined): string {
  if (!contractHash) return url;
  return `${url}${url.includes("?") ? "&" : "?"}${CONTRACT_QUERY}=${encodeURIComponent(contractHash)}`;
}

/** Default headers carrying the contract hash, merged under the caller's own headers. */
export function withContractHeader(
  headers: Record<string, string> | undefined,
  contractHash: string | undefined,
): Record<string, string> | undefined {
  if (!contractHash) return headers;
  return { [CONTRACT_HEADER]: contractHash, ...(headers ?? {}) };
}
