using MassTransit;
using Notification.Service.Consumers;
using Serilog;
using SerilogLogContext = Serilog.Context.LogContext;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, _, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("ServiceName", "Notification.Service")
        .WriteTo.Console()
        .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341");
});

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure MassTransit with RabbitMQ & Consumer (credentials from configuration)
var rabbitMqConfig = builder.Configuration.GetSection("RabbitMQ");
var rabbitMqConnectionString =
    $"amqp://{rabbitMqConfig["Username"]}:{rabbitMqConfig["Password"]}@{rabbitMqConfig["Host"]}:5672";
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
        cfg.UseMessageRetry(r => r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)));

        // Automatically create queues and bindings for Notification.Service
        cfg.ConfigureEndpoints(context);
    });

    builder.Services.AddHealthChecks()
        .AddRabbitMQ(rabbitMqConnectionString, name: "rabbitmq");
});

var app = builder.Build();

app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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

// Health check endpoint
app.MapHealthChecks("/health")
    .WithName("HealthCheck")
    .WithOpenApi();

app.Run();
