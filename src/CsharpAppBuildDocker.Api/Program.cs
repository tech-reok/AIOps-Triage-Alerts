using CsharpAppBuildDocker.Api.Repositories;
using CsharpAppBuildDocker.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddSingleton<IHealthService, HealthService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Fail startup intentionally when environment variable FAIL_ON_STARTUP=true
var failOnStartup = Environment.GetEnvironmentVariable("FAIL_ON_STARTUP");
if (!string.IsNullOrEmpty(failOnStartup) &&
    failOnStartup.Equals("true", StringComparison.OrdinalIgnoreCase))
{
    // Throwing here will make the process exit with a non-zero code
    throw new Exception("Startup failure triggered by FAIL_ON_STARTUP environment variable.");
}

app.Run();
