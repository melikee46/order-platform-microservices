using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var authenticationConfig = builder.Configuration.GetSection("Authentication");
var signingKey = authenticationConfig["SigningKey"]
    ?? throw new InvalidOperationException("Authentication:SigningKey is not configured.");
var issuer = authenticationConfig["Issuer"]
    ?? throw new InvalidOperationException("Authentication:Issuer is not configured.");
var audience = authenticationConfig["Audience"]
    ?? throw new InvalidOperationException("Authentication:Audience is not configured.");

if (signingKey.Length < 32)
    throw new InvalidOperationException("Authentication:SigningKey must be at least 32 characters long.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

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
app.UseAuthentication();
app.UseAuthorization();
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
app.MapReverseProxy().RequireAuthorization();

app.Run();
