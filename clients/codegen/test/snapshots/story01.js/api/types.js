// Auto-generated Sleipnir data types (JSDoc). Properties are camelCase (wire);
// nullable properties are bracketed ([name]) — presence-optional on the wire.

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
 * @property {number} customerId
 * @property {number} id
 * @property {string | null} [note]
 * @property {string} placedAt
 * @property {number} shippingAddressId
 * @property {string} status
 */

/**
 * @typedef {Object} Customer
 * @property {number} id
 * @property {string} name
 * @property {number | null} [score]
 */

/**
 * @typedef {Object} Address
 * @property {number} id
 * @property {string} street
 * @property {string} zip
 * @property {string} city
 */
