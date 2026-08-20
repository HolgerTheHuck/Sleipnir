// Auto-generated Sleipnir controllers (JSDoc-typed JS).
import { SleipnirCall } from "sleipnir-client";
export class ChatClient {
  /**
   * @param {(controller: string, method: string) => SleipnirCall} build
   * @param {(req: SleipnirRequest, handlers: SubscribeHandlers<unknown>) => Promise<SleipnirSubscription>} subscribe
   */
  constructor(build, subscribe) {
    this._build = build;
    this._subscribe = subscribe;
  }
  /**
   * @param {number} chatId
   * @param {SubscribeHandlers<Message>} handlers
   * @returns {Promise<SleipnirSubscription>}
   */
  async messageReceived(chatId, handlers) {
    return this._subscribe(this._build("Chat", "MessageReceived").with({ chatId: chatId }).toRequest(), handlers);
  }

  /**
   * @param {number} chatId
   * @returns {Promise<SleipnirResponse<Message[] | null>>}
   */
  async getHistory(chatId) {
    const call = this._build("Chat", "GetHistory").with({ chatId: chatId });
    return call;
  }
}

export class TickerClient {
  /**
   * @param {(controller: string, method: string) => SleipnirCall} build
   * @param {(req: SleipnirRequest, handlers: SubscribeHandlers<unknown>) => Promise<SleipnirSubscription>} subscribe
   */
  constructor(build, subscribe) {
    this._build = build;
    this._subscribe = subscribe;
  }
  /**

   * @param {SubscribeHandlers<number>} handlers
   * @returns {Promise<SleipnirSubscription>}
   */
  async ticks(handlers) {
    return this._subscribe(this._build("Ticker", "Ticks").toRequest(), handlers);
  }
}

export class UserClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number} userId
   * @returns {Promise<SleipnirResponse<User | null>>}
   */
  async getById(userId) {
    const call = this._build("User", "GetById").with({ userId: userId });
    return call;
  }
}
