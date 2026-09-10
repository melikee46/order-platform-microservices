using MassTransit;
using Microsoft.EntityFrameworkCore;
using Payment.Service.Consumers;
using Payment.Service.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure PostgreSQL DbContext for Payment.Service
var connectionString = builder.Configuration.GetConnectionString("PaymentDb")
    ?? throw new InvalidOperationException("Connection string 'PaymentDb' not found.");

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure MassTransit with RabbitMQ & Consumer
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderCreatedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("localhost", "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        // RabbitMQ üzerinde Consumer için kuyrukları ve binding'leri otomatik oluşturur
        cfg.ConfigureEndpoints(context);
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

// Minimal API Endpoints
var paymentsGroup = app.MapGroup("/payments").WithTags("Payments");

// GET /payments (İşlenen tüm ödemeleri listeler)
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
