import { createClient, TrameCall } from 'trame-client';

const baseUrl = import.meta.env.DEV ? '' : 'https://localhost:5001';

export const trame = createClient(baseUrl);

export async function hello(name: string) {
  const req = TrameCall.init('Greeting', 'Hello').with({ name }).toRequest();
  return trame.rest.callJson<string>(req);
}

export async function ping() {
  const req = TrameCall.init('Greeting', 'Ping').toRequest();
  return trame.rest.callJson<{ time: string }>(req);
}
