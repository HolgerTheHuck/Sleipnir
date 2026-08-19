import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchObservability, setBearer, setEndpoint } from './client';

// Smoke test for the /observability client wiring (URL construction, bearer
// header, non-2xx → throw). No DOM — fetch is stubbed globally. The endpoint is
// opt-in (EnableObservability) and RequireAuth-gated server-side; here we only
// verify the client side builds the right request and surfaces errors.

afterEach(() => {
  vi.unstubAllGlobals();
  // Reset to same-origin defaults so tests are order-independent.
  setEndpoint('/', 'api/sleipnir');
  setBearer('');
});

describe('fetchObservability', () => {
  it('requests /api/sleipnir/observability with the configured bearer', async () => {
    setEndpoint('/', 'api/sleipnir');
    setBearer('test-token');

    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      expect(url).toBe('/api/sleipnir/observability');
      expect((init?.headers as Record<string, string>)?.['Authorization']).toBe('Bearer test-token');
      return new Response(
        JSON.stringify({
          transports: { rest: true, webSocket: true, signalR: false },
          activeConnections: 2,
          activeSubscriptions: 5,
          eventDroppedTotal: 1,
          callCount: 42,
          errorCount: 3,
          batchCount: 7,
          uptimeMs: 123456,
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    });
    vi.stubGlobal('fetch', fetchMock);

    const snap = await fetchObservability();
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(snap.activeConnections).toBe(2);
    expect(snap.activeSubscriptions).toBe(5);
    expect(snap.transports.signalR).toBe(false);
    expect(snap.callCount).toBe(42);
  });

  it('omits the Authorization header when no bearer is set', async () => {
    setEndpoint('/', 'api/sleipnir');
    setBearer('');

    const fetchMock = vi.fn(async () => new Response('{"transports":{"rest":true,"webSocket":true,"signalR":false},"activeConnections":0,"activeSubscriptions":0,"eventDroppedTotal":0,"callCount":0,"errorCount":0,"batchCount":0,"uptimeMs":0}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await fetchObservability();
    const init = fetchMock.mock.calls[0]?.[1] as RequestInit | undefined;
    expect((init?.headers as Record<string, string> | undefined)?.['Authorization']).toBeUndefined();
  });

  it('throws on a non-2xx response (e.g. 401 when RequireAuth is on and unauthenticated)', async () => {
    setEndpoint('/', 'api/sleipnir');
    setBearer('');

    vi.stubGlobal('fetch', vi.fn(async () => new Response('Unauthorized', { status: 401 })));

    await expect(fetchObservability()).rejects.toThrow(/401/);
  });

  it('builds the URL against a custom base + api path', async () => {
    setEndpoint('https://example.com', 'custom/api');
    setBearer('');

    const fetchMock = vi.fn(async (url: string) => {
      expect(url).toBe('https://example.com/custom/api/observability');
      return new Response('{"transports":{"rest":true,"webSocket":true,"signalR":false},"activeConnections":0,"activeSubscriptions":0,"eventDroppedTotal":0,"callCount":0,"errorCount":0,"batchCount":0,"uptimeMs":0}', { status: 200 });
    });
    vi.stubGlobal('fetch', fetchMock);

    await fetchObservability();
    expect(fetchMock).toHaveBeenCalledOnce();
  });
});