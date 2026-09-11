using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Configure YARP Reverse Proxy from appsettings.json
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Centralized Rate Limiter at the Gateway perimeter
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("gateway-limiter", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 150;
        opt.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseRateLimiter();

// Gateway Status Endpoint
app.MapGet("/", () => Results.Ok(new
{
    gateway = "API Gateway (YARP)",
    status = "Online",
    version = "1.0",
    routes = new[]
    {
        new { path = "/orders", destination = "Order.Service (:5001)" },
        new { path = "/payments", destination = "Payment.Service (:5002)" }
    }
}));

// Route incoming traffic to downstream microservices via YARP
app.MapReverseProxy();

app.Run();
