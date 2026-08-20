// Auto-generated Sleipnir controllers. Method names are camelCase; parameter
// names bind case-sensitively on the wire (keys passed verbatim to SleipnirCall).
import { SleipnirCall } from "sleipnir-client";
import type { SleipnirRequest, SubscribeHandlers, SleipnirSubscription } from "sleipnir-client";
import { TypedCall } from "./typed-call.js";
import type { Message, User } from "./types.js";
import type { MessageArrayPaths, UserPaths } from "./typed-call.js";

export class ChatClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  /** @internal */ _subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>) => Promise<SleipnirSubscription>;
  constructor(
    build: (controller: string, method: string) => SleipnirCall,
    subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>) => Promise<SleipnirSubscription>,
  ) {
    this._build = build;
    this._subscribe = subscribe;
  }
  messageReceived(chatId: number, handlers: SubscribeHandlers<Message>): Promise<SleipnirSubscription> {
    return this._subscribe<Message>(this._build("Chat", "MessageReceived").with({ chatId: chatId }).toRequest(), handlers);
  }

  getHistory(chatId: number): TypedCall<Message[], MessageArrayPaths> {
    return new TypedCall<Message[], MessageArrayPaths>(this._build("Chat", "GetHistory").with({ chatId: chatId }));
  }
}

export class TickerClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  /** @internal */ _subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>) => Promise<SleipnirSubscription>;
  constructor(
    build: (controller: string, method: string) => SleipnirCall,
    subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>) => Promise<SleipnirSubscription>,
  ) {
    this._build = build;
    this._subscribe = subscribe;
  }
  ticks(handlers: SubscribeHandlers<number>): Promise<SleipnirSubscription> {
    return this._subscribe<number>(this._build("Ticker", "Ticks").toRequest(), handlers);
  }
}

export class UserClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
  getById(userId: number): TypedCall<User, UserPaths> {
    return new TypedCall<User, UserPaths>(this._build("User", "GetById").with({ userId: userId }));
  }
}
