// Auto-generated Sleipnir controllers (JSDoc-typed JS).
import { SleipnirCall } from "sleipnir-client";
export class StockClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number[]} articleIds
   * @returns {Promise<SleipnirResponse<StockInfo[] | null>>}
   */
  async getByArticles(articleIds) {
    const call = this._build("Stock", "GetByArticles").with({ articleIds: articleIds });
    return call;
  }
}

export class OrderLineClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number} orderId
   * @returns {Promise<SleipnirResponse<OrderLine[] | null>>}
   */
  async getByOrder(orderId) {
    const call = this._build("OrderLine", "GetByOrder").with({ orderId: orderId });
    return call;
  }
}

export class ArticleClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number[]} articleIds
   * @returns {Promise<SleipnirResponse<Article[] | null>>}
   */
  async getByIds(articleIds) {
    const call = this._build("Article", "GetByIds").with({ articleIds: articleIds });
    return call;
  }
}

export class OrderClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number} id
   * @returns {Promise<SleipnirResponse<Order | null>>}
   */
  async getById(id) {
    const call = this._build("Order", "GetById").with({ id: id });
    return call;
  }
}

export class CustomerClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number} customerId
   * @returns {Promise<SleipnirResponse<Customer | null>>}
   */
  async getById(customerId) {
    const call = this._build("Customer", "GetById").with({ customerId: customerId });
    return call;
  }
}

export class AddressClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number} addressId
   * @returns {Promise<SleipnirResponse<Address | null>>}
   */
  async getById(addressId) {
    const call = this._build("Address", "GetById").with({ addressId: addressId });
    return call;
  }
}
