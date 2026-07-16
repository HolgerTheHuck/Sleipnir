using Trame.Model;
using Trame.Services;
using Microsoft.AspNetCore.Mvc;

namespace Trame.Controllers;

/// <summary>
/// Classic REST controller that mirrors the Trame CustomerHandler for benchmark comparison.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class CustomerController(CustomerService service) : ControllerBase
{
    [HttpGet("all")]
    public async Task<ActionResult<List<Customer>>> GetAll()
    {
        return Ok(await service.GetAllCustomers());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Customer?>> GetById(int id)
    {
        return Ok(await service.GetCustomerById(id));
    }

    [HttpPost]
    public async Task<ActionResult<int>> Add([FromBody] AddCustomerDto dto)
    {
        return Ok(await service.AddCustomer(dto.Name));
    }

    [HttpGet("{id:int}/orders")]
    public async Task<ActionResult<List<Order>>> GetOrders(int id)
    {
        return Ok(await service.GetOrdersById(id));
    }

    public class AddCustomerDto
    {
        public string Name { get; set; } = string.Empty;
    }
}