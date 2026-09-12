using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Payment.Service.Consumers;
using Payment.Service.Data;
using Serilog;
using SerilogLogContext = Serilog.Context.LogContext;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, _, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("ServiceName", "Payment.Service")
        .WriteTo.Console()
        .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341");
});

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

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure PostgreSQL DbContext for Payment.Service
var connectionString = builder.Configuration.GetConnectionString("PaymentDb")
    ?? throw new InvalidOperationException("Connection string 'PaymentDb' not found.");

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure MassTransit with RabbitMQ & Consumer (credentials from appsettings, NOT hardcoded)
var rabbitMqConfig = builder.Configuration.GetSection("RabbitMQ");
var rabbitMqConnectionString =
    $"amqp://{rabbitMqConfig["Username"]}:{rabbitMqConfig["Password"]}@{rabbitMqConfig["Host"]}:5672";
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderCreatedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqConfig["Host"] ?? "localhost", "/", h =>
        {
            h.Username(rabbitMqConfig["Username"] ?? throw new InvalidOperationException("RabbitMQ Username not configured."));
            h.Password(rabbitMqConfig["Password"] ?? throw new InvalidOperationException("RabbitMQ Password not configured."));
        });
        cfg.UseMessageRetry(r => r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)));

        // Automatically create queues and bindings for consumers on RabbitMQ
        cfg.ConfigureEndpoints(context);
    });

    builder.Services.AddHealthChecks()
        .AddNpgSql(connectionString, name: "postgresql")
        .AddRabbitMQ(rabbitMqConnectionString, name: "rabbitmq");
});

// Rate Limiting — prevent endpoint spam / DoS
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("fixed", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 100;
        opt.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Security middleware
if (!app.Environment.IsProduction())
    app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
        ?? Guid.NewGuid().ToString("N");
    context.Response.Headers["X-Correlation-ID"] = correlationId;

    using (SerilogLogContext.PushProperty("CorrelationId", correlationId))
        await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Automatically ensure PostgreSQL database and tables are created
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    try
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not automatically initialize database. Ensure Docker payment-db container is running on port 5434.");
    }
}

app.MapHealthChecks("/health").AllowAnonymous();

// Minimal API Endpoints
var paymentsGroup = app.MapGroup("/payments")
    .WithTags("Payments")
    .RequireRateLimiting("fixed")
    .RequireAuthorization();

// GET /payments (List all processed payments)
paymentsGroup.MapGet("/", async (PaymentDbContext db) =>
{
    var payments = await db.Payments
        .OrderByDescending(p => p.ProcessedAt)
        .ToListAsync();

    return Results.Ok(payments);
})
.WithName("GetPayments")
.WithOpenApi();

// GET /payments/{id}
paymentsGroup.MapGet("/{id:guid}", async (Guid id, PaymentDbContext db) =>
{
    var payment = await db.Payments.FindAsync(id);
    if (payment is null)
    {
        return Results.NotFound(new { message = $"Payment with Id '{id}' not found." });
    }

    return Results.Ok(payment);
})
.WithName("GetPaymentById")
.WithOpenApi();

app.Run();
