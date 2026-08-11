using Sleipnir.Model;
using Sleipnir.Services;
using SleipnirCore.Attributes;

namespace Sleipnir.Api
{
    [SleipnirController("Customer")]
    public class CustomerHandler(CustomerService service)
    {
        [SleipnirMethod("AddCustomer")]
        public async Task<int> AddCustomer(string name)
        {
            return await service.AddCustomer(name);
        }

        [SleipnirMethod("GetCustomerById")]
        // Read a customer by ID
        public async Task<Customer?> GetCustomerById(int id)
        {
            return await service.GetCustomerById(id);
        }

        [SleipnirMethod("GetOrderByOrderId")]
        // Read a customer by ID
        public async Task<List<Order>?> GetOrderByOrderId(int id, CancellationToken ct)
        {
            return await service.GetOrdersById(id);
        }


        // Read all customers
        [SleipnirMethod("GetAllCustomers")]
        public async Task<List<Customer>> GetAllCustomers()
        {
            return await service.GetAllCustomers();
        }

        // Update a customer's name
        [SleipnirMethod("UpdateCustomerName")]
        public async Task UpdateCustomerName(int customerId, string newName)
        {
            await service.UpdateCustomerName(customerId, newName);
        }

        // Delete a customer by ID

        [SleipnirMethod("DeleteCustomer")]
        public async Task DeleteCustomer(int id)
        {
            await service.DeleteCustomer(id);
        }

        // Add an address to a customer
        [SleipnirMethod("AddAddressToCustomer")]
        public async Task AddAddressToCustomer(int customerId, string street, string city, string postalCode)
        {
            await service.AddAddressToCustomer(customerId, street, city, postalCode);
        }

        // Remove an address from a customer
        [SleipnirMethod("RemoveAddressFromCustomer")]
        public async Task RemoveAddressFromCustomer(int customerId, int addressId)
        {
            await service.RemoveAddressFromCustomer(customerId, addressId);
        }

        // Add an order to a customer
        [SleipnirMethod("AddOrderToCustomer")]
        public async Task AddOrderToCustomer(int customerId, string productName, decimal price)
        {
            await service.AddOrderToCustomer(customerId, productName, price);
        }

        [SleipnirMethod("RemoveOrderFromCustomer")]
        public async Task RemoveOrderFromCustomer(int customerId, int orderId)
        {
            await service.RemoveOrderFromCustomer(customerId, orderId);
        }
    }
}
