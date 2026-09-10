using MassTransit;
using Microsoft.EntityFrameworkCore;
using Order.Service.Data;
using Order.Service.DTOs;
using Order.Service.Models;
using Shared.Contracts.Events;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure PostgreSQL DbContext
var connectionString = builder.Configuration.GetConnectionString("OrderDb")
    ?? throw new InvalidOperationException("Connection string 'OrderDb' not found.");

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure MassTransit with RabbitMQ
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("localhost", "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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

// Minimal API Endpoints
var ordersGroup = app.MapGroup("/orders").WithTags("Orders");

// POST /orders (Sipariş oluşturur, DB'ye yazar, OrderCreated event'i fırlatır)
ordersGroup.MapPost("/", async (CreateOrderDto dto, OrderDbContext db, IPublishEndpoint publishEndpoint) =>
{
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

    // Event-Driven İletişim: RabbitMQ'ya OrderCreated event'i yayınla
    await publishEndpoint.Publish(new OrderCreated(
        order.Id,
        order.ProductName,
        order.Quantity,
        order.TotalPrice,
        order.CreatedAt
    ));

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
