using CsharpAppBuildDocker.Api.Services;
using System;
using Microsoft.AspNetCore.Mvc;

namespace CsharpAppBuildDocker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HealthController(IHealthService healthService) : ControllerBase
{
    [HttpGet]
    public IActionResult GetStatus()
    {
        var healthFail = Environment.GetEnvironmentVariable("HEALTH_FAIL");
        if (!string.IsNullOrEmpty(healthFail) &&
            healthFail.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(500, new { error = "Forced health failure by HEALTH_FAIL environment variable." });
        }

        var version = healthService.GetVersion();
        return Ok(new { status = healthService.GetStatus(), version = version, commitHash = healthService.GetCommitHash() });
    }
}
