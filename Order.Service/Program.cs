using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Order.Service.Data;
using Order.Service.DTOs;
using Order.Service.Models;
using Shared.Contracts.Events;
using Serilog;
using SerilogLogContext = Serilog.Context.LogContext;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, _, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("ServiceName", "Order.Service")
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

// Configure PostgreSQL DbContext
var connectionString = builder.Configuration.GetConnectionString("OrderDb")
    ?? throw new InvalidOperationException("Connection string 'OrderDb' not found.");

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure MassTransit with RabbitMQ (credentials from appsettings, NOT hardcoded)
var rabbitMqConfig = builder.Configuration.GetSection("RabbitMQ");
var rabbitMqConnectionString =
    $"amqp://{rabbitMqConfig["Username"]}:{rabbitMqConfig["Password"]}@{rabbitMqConfig["Host"]}:5672";
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqConfig["Host"] ?? "localhost", "/", h =>
        {
            h.Username(rabbitMqConfig["Username"] ?? throw new InvalidOperationException("RabbitMQ Username not configured."));
            h.Password(rabbitMqConfig["Password"] ?? throw new InvalidOperationException("RabbitMQ Password not configured."));
        });
        cfg.UseMessageRetry(r => r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)));
    });
});

builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql")
    .AddRabbitMQ(rabbitMqConnectionString, name: "rabbitmq");

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
    var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    try
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not automatically initialize database. Ensure Docker order-db container is running on port 5433.");
    }
}

app.MapHealthChecks("/health").AllowAnonymous();

// Minimal API Endpoints
var ordersGroup = app.MapGroup("/orders")
    .WithTags("Orders")
    .RequireRateLimiting("fixed")
    .RequireAuthorization();

// POST /orders — Create a new order with input validation
ordersGroup.MapPost("/", async (
    CreateOrderDto dto,
    OrderDbContext db,
    IPublishEndpoint publishEndpoint,
    HttpContext httpContext) =>
{
    // Input Validation
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(dto.ProductName))
        errors.Add("ProductName is required.");
    else if (dto.ProductName.Length > 200)
        errors.Add("ProductName cannot exceed 200 characters.");

    if (dto.Quantity <= 0)
        errors.Add("Quantity must be greater than zero.");

    if (dto.TotalPrice <= 0)
        errors.Add("TotalPrice must be greater than zero.");

    if (errors.Count > 0)
        return Results.BadRequest(new { errors });

    var order = new Order.Service.Models.Order
    {
        ProductName = dto.ProductName,
        Quantity = dto.Quantity,
        TotalPrice = dto.TotalPrice,
        Status = "Created",
        CreatedAt = DateTime.UtcNow
    };

    db.Orders.Add(order);
    await db.SaveChangesAsync();

    // Publish event to RabbitMQ (event-driven communication)
    await publishEndpoint.Publish(new OrderCreated(
        order.Id,
        order.ProductName,
        order.Quantity,
        order.TotalPrice,
        order.CreatedAt
    ), publishContext =>
    {
        if (Guid.TryParse(httpContext.Request.Headers["X-Correlation-ID"], out var correlationId))
            publishContext.CorrelationId = correlationId;
    });

    var response = new OrderResponseDto(
        order.Id,
        order.ProductName,
        order.Quantity,
        order.TotalPrice,
        order.Status,
        order.CreatedAt
    );

    return Results.Created($"/orders/{order.Id}", response);
})
.WithName("CreateOrder")
.WithOpenApi();

// GET /orders
ordersGroup.MapGet("/", async (OrderDbContext db) =>
{
    var orders = await db.Orders
        .OrderByDescending(o => o.CreatedAt)
        .Select(order => new OrderResponseDto(
            order.Id,
            order.ProductName,
            order.Quantity,
            order.TotalPrice,
            order.Status,
            order.CreatedAt
        ))
        .ToListAsync();

    return Results.Ok(orders);
})
.WithName("GetOrders")
.WithOpenApi();

// GET /orders/{id}
ordersGroup.MapGet("/{id:guid}", async (Guid id, OrderDbContext db) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null)
    {
        return Results.NotFound(new { message = $"Order with Id '{id}' not found." });
    }

    var response = new OrderResponseDto(
        order.Id,
        order.ProductName,
        order.Quantity,
        order.TotalPrice,
        order.Status,
        order.CreatedAt
    );

    return Results.Ok(response);
})
.WithName("GetOrderById")
.WithOpenApi();

app.Run();
