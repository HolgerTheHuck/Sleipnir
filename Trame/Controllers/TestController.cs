using Trame.Api;
using TrameCore.Attributes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Trame.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    //[TrameAuthorise]
    public class TestController(TestService service) : ControllerBase
    {
        [HttpGet("{id}/{greet}")]
        public Task<ActionResult> Get(int id, string greet)
        {
            return Task.FromResult((ActionResult)Ok(service.GetAdresse(id, greet, CancellationToken.None)));
        }
    }
}
