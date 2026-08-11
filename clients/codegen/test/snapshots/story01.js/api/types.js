// Auto-generated Sleipnir data types (JSDoc). Properties are camelCase (wire).

/**
 * @typedef {Object} StockInfo
 * @property {number} articleId
 * @property {number} inStock
 */

/**
 * @typedef {Object} OrderLine
 * @property {number} articleId
 * @property {number} qty
 */

/**
 * @typedef {Object} Article
 * @property {number} id
 * @property {string} name
 * @property {number} price
 */

/**
 * @typedef {Object} Order
 * @property {number} id
 * @property {number} customerId
 * @property {number} shippingAddressId
 * @property {string} status
 * @property {string} placedAt
 */

/**
 * @typedef {Object} Customer
 * @property {number} id
 * @property {string} name
 */

/**
 * @typedef {Object} Address
 * @property {number} id
 * @property {string} street
 * @property {string} zip
 * @property {string} city
 */
