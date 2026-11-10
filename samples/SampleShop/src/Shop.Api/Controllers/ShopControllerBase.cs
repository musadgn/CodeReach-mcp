using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Shop.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public abstract class ShopControllerBase : ControllerBase
{
    [HttpGet("ping")]
    public string Ping() => "pong";
}
