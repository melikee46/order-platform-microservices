using MassTransit;
using Payment.Service.Data;
using Shared.Contracts.Events;

namespace Payment.Service.Consumers;

public class OrderCreatedConsumer : IConsumer<OrderCreated>
{
    private readonly PaymentDbContext _dbContext;
    private readonly ILogger<OrderCreatedConsumer> _logger;

    public OrderCreatedConsumer(PaymentDbContext dbContext, ILogger<OrderCreatedConsumer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OrderCreated> context)
    {
        var message = context.Message;
        _logger.LogInformation(
            "📬 [RabbitMQ] OrderCreated Event Alındı! OrderId: {OrderId}, Ürün: {ProductName}, Tutar: {TotalPrice:C}",
            message.OrderId,
            message.ProductName,
            message.TotalPrice
        );

        // Ödeme işlemi simülasyonu (Ödeme servisi kendi iş mantığını çalıştırır)
        var payment = new Models.Payment
        {
            OrderId = message.OrderId,
            Amount = message.TotalPrice,
            Status = "Success",
            ProcessedAt = DateTime.UtcNow
        };

        // Payment servisi kendi bağımsız veritabanına (payment-db) kaydeder
        _dbContext.Payments.Add(payment);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "✅ [Ödeme Başarılı] PaymentId: {PaymentId}, OrderId: {OrderId} için {Amount:C} ödeme payment-db'ye kaydedildi.",
            payment.Id,
            payment.OrderId,
            payment.Amount
        );

        // Bildirim servisini tetiklemek için PaymentProcessed event'ini RabbitMQ'ya yayınla
        await context.Publish(new PaymentProcessed(
            payment.Id,
            payment.OrderId,
            payment.Amount,
            payment.Status,
            payment.ProcessedAt
        ));

        _logger.LogInformation(
            "📢 [RabbitMQ] PaymentProcessed Event Yayınlandı! PaymentId: {PaymentId}",
            payment.Id
        );
    }
}
