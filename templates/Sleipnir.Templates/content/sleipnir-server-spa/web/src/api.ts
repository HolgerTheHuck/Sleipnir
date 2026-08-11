import { createClient, SleipnirCall } from 'sleipnir-client';

const baseUrl = import.meta.env.DEV ? '' : 'https://localhost:5001';

export const sleipnir = createClient(baseUrl);

export async function hello(name: string) {
  const req = SleipnirCall.init('Greeting', 'Hello').with({ name }).toRequest();
  return sleipnir.rest.callJson<string>(req);
}

export async function ping() {
  const req = SleipnirCall.init('Greeting', 'Ping').toRequest();
  return sleipnir.rest.callJson<{ time: string }>(req);
}
