using Grpc.Core;
using Trame.Services;
using TrameModel = Trame.Model;

namespace Trame.Grpc;

/// <summary>
/// gRPC service that mirrors the Trame CustomerHandler for benchmark comparison.
/// </summary>
public class CustomerGrpcService(CustomerService service) : CustomerGrpc.CustomerGrpcBase
{
    public override async Task<CustomerList> GetAllCustomers(Empty request, ServerCallContext context)
    {
        var customers = await service.GetAllCustomers();
        var result = new CustomerList();
        foreach (var c in customers)
        {
            result.Customers.Add(MapCustomer(c));
        }
        return result;
    }

    public override async Task<Customer> GetCustomerById(CustomerRequest request, ServerCallContext context)
    {
        var customer = await service.GetCustomerById(request.Id);
        return customer != null ? MapCustomer(customer) : new Customer();
    }

    public override async Task<AddCustomerResponse> AddCustomer(AddCustomerRequest request, ServerCallContext context)
    {
        var id = await service.AddCustomer(request.Name);
        return new AddCustomerResponse { Id = id };
    }

    public override async Task<OrderList> GetOrdersByOrderId(OrderRequest request, ServerCallContext context)
    {
        var orders = await service.GetOrdersById(request.Id);
        var result = new OrderList();
        foreach (var o in orders)
        {
            result.Orders.Add(new Order { Id = o.Id, ProductName = o.ProductName, Price = (double)o.Price });
        }
        return result;
    }

    private static Customer MapCustomer(TrameModel.Customer c)
    {
        var mapped = new Customer
        {
            Id = c.Id,
            OrderId = c.OrderId,
            Name = c.Name,
            ResourceId = c.ResourceId.ToString(),
            Created = c.Created.Ticks
        };
        foreach (var a in c.Addresses)
        {
            mapped.Addresses.Add(new Address { Id = a.Id, Street = a.Street, City = a.City, PostalCode = a.PostalCode });
        }
        return mapped;
    }
}
