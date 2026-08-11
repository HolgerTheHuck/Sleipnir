using Sleipnir.Model;
using System.Collections.Concurrent;

namespace Sleipnir.Services
{

    public class CustomerService
    {
        private readonly ConcurrentDictionary<int, Customer> _customers = new();
        private readonly ConcurrentDictionary<int, List<Order>> _orders = new();
        private readonly object _idLock = new();

        private int _nextCustomerId = 1;
        private int _nextAddressId = 1;
        private int _nextOrderId = 1;

        // Create a new customer
        public async Task<int> AddCustomer(string name)
        {
            int customerId;
            int orderId;
            lock (_idLock)
            {
                customerId = _nextCustomerId++;
                orderId = _nextOrderId++;
            }
            await Task.Delay(200);
            var customer = new Customer(customerId, name) { OrderId = orderId };
            _customers.TryAdd(customerId, customer);
            _orders.TryAdd(orderId, new List<Order>());
            return customerId;
        }

        // Read a customer by ID
        public Task<Customer?> GetCustomerById(int id)
        {
            _customers.TryGetValue(id, out var customer);
            return Task.FromResult(customer);
        }

        // Read a customer by ID
        public Task<List<Order>> GetOrdersById(int id)
        {
            if (_orders.TryGetValue(id, out var orders))
            {
                return Task.FromResult(orders);
            }

            return Task.FromResult(new List<Order>());
        }

        // Read all customers
        public Task<List<Customer>> GetAllCustomers()
        {
            return Task.FromResult(_customers.Values.ToList());
        }

        // Update a customer's name
        public async Task UpdateCustomerName(int customerId, string newName)
        {
            var customer = await GetCustomerById(customerId);
            if (customer != null)
            {
                customer.Name = newName;
            }
        }

        // Delete a customer by ID
        public Task DeleteCustomer(int id)
        {
            _customers.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        // Add an address to a customer
        public async Task AddAddressToCustomer(int customerId, string street, string city, string postalCode)
        {
            var customer = await GetCustomerById(customerId);
            if (customer != null)
            {
                int addressId;
                lock (_idLock)
                {
                    addressId = _nextAddressId++;
                }
                var address = new Address(addressId, street, city, postalCode);
                customer.Addresses.Add(address);
            }
        }

        // Remove an address from a customer
        public async Task RemoveAddressFromCustomer(int customerId, int addressId)
        {
            var customer = await GetCustomerById(customerId);
            if (customer != null)
            {
                customer.Addresses.RemoveAll(a => a.Id == addressId);
            }
        }

        // Add an order to a customer
        public async Task AddOrderToCustomer(int customerId, string productName, decimal price)
        {
            var customer = await GetCustomerById(customerId);
            if (customer != null)
            {
                int orderId;
                lock (_idLock)
                {
                    orderId = _nextOrderId++;
                }
                var order = new Order(orderId, productName, price);

                if (_orders.TryGetValue(customer.OrderId, out var orderlist))
                {
                    lock (orderlist)
                    {
                        orderlist.Add(order);
                    }
                }
                else
                {
                    _orders.TryAdd(customer.OrderId, new List<Order> { order });
                }
            }
        }

        // Remove an order from a customer
        public async Task RemoveOrderFromCustomer(int customerId, int orderId)
        {
            var customer = await GetCustomerById(customerId);
            if (customer != null)
            {
                if (_orders.TryGetValue(customer.OrderId, out var orderlist))
                {
                    lock (orderlist)
                    {
                        orderlist.RemoveAll(o => o.Id == orderId);
                    }
                }
            }
        }
    }

}
