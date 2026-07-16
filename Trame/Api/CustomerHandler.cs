using Trame.Model;
using Trame.Services;
using TrameCore.Attributes;

namespace Trame.Api
{
    [TrameController("Customer")]
    public class CustomerHandler(CustomerService service)
    {
        [TrameMethod("AddCustomer")]
        public async Task<int> AddCustomer(string name)
        {
            return await service.AddCustomer(name);
        }

        [TrameMethod("GetCustomerById")]
        // Read a customer by ID
        public async Task<Customer?> GetCustomerById(int id)
        {
            return await service.GetCustomerById(id);
        }

        [TrameMethod("GetOrderByOrderId")]
        // Read a customer by ID
        public async Task<List<Order>?> GetOrderByOrderId(int id, CancellationToken ct)
        {
            return await service.GetOrdersById(id);
        }


        // Read all customers
        [TrameMethod("GetAllCustomers")]
        public async Task<List<Customer>> GetAllCustomers()
        {
            return await service.GetAllCustomers();
        }

        // Update a customer's name
        [TrameMethod("UpdateCustomerName")]
        public async Task UpdateCustomerName(int customerId, string newName)
        {
            await service.UpdateCustomerName(customerId, newName);
        }

        // Delete a customer by ID

        [TrameMethod("DeleteCustomer")]
        public async Task DeleteCustomer(int id)
        {
            await service.DeleteCustomer(id);
        }

        // Add an address to a customer
        [TrameMethod("AddAddressToCustomer")]
        public async Task AddAddressToCustomer(int customerId, string street, string city, string postalCode)
        {
            await service.AddAddressToCustomer(customerId, street, city, postalCode);
        }

        // Remove an address from a customer
        [TrameMethod("RemoveAddressFromCustomer")]
        public async Task RemoveAddressFromCustomer(int customerId, int addressId)
        {
            await service.RemoveAddressFromCustomer(customerId, addressId);
        }

        // Add an order to a customer
        [TrameMethod("AddOrderToCustomer")]
        public async Task AddOrderToCustomer(int customerId, string productName, decimal price)
        {
            await service.AddOrderToCustomer(customerId, productName, price);
        }

        [TrameMethod("RemoveOrderFromCustomer")]
        public async Task RemoveOrderFromCustomer(int customerId, int orderId)
        {
            await service.RemoveOrderFromCustomer(customerId, orderId);
        }
    }
}
