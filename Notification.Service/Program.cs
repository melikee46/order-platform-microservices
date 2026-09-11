using MassTransit;
using Notification.Service.Consumers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure MassTransit with RabbitMQ & Consumer (credentials from configuration)
var rabbitMqConfig = builder.Configuration.GetSection("RabbitMQ");
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentProcessedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqConfig["Host"] ?? "localhost", "/", h =>
        {
            h.Username(rabbitMqConfig["Username"] ?? throw new InvalidOperationException("RabbitMQ Username not configured."));
            h.Password(rabbitMqConfig["Password"] ?? throw new InvalidOperationException("RabbitMQ Password not configured."));
        });

        // Automatically create queues and bindings for Notification.Service
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

app.UseHttpsRedirection();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "Notification.Service" }))
    .WithName("HealthCheck")
    .WithOpenApi();

app.Run();
