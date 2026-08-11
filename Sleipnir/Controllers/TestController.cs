using Sleipnir.Api;
using SleipnirCore.Attributes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Sleipnir.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    //[SleipnirAuthorise]
    public class TestController(TestService service) : ControllerBase
    {
        [HttpGet("{id}/{greet}")]
        public Task<ActionResult> Get(int id, string greet)
        {
            return Task.FromResult((ActionResult)Ok(service.GetAdresse(id, greet, CancellationToken.None)));
        }
    }
}
