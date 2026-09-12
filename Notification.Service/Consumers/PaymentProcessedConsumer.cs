using MassTransit;
using Shared.Contracts.Events;
using SerilogLogContext = Serilog.Context.LogContext;

namespace Notification.Service.Consumers;

public class PaymentProcessedConsumer : IConsumer<PaymentProcessed>
{
    private readonly ILogger<PaymentProcessedConsumer> _logger;

    public PaymentProcessedConsumer(ILogger<PaymentProcessedConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<PaymentProcessed> context)
    {
        using var correlationScope = SerilogLogContext.PushProperty(
            "CorrelationId",
            context.CorrelationId ?? context.ConversationId);
        var message = context.Message;

        _logger.LogInformation(
            "📬 [RabbitMQ] PaymentProcessed Event Alındı! PaymentId: {PaymentId}, OrderId: {OrderId}",
            message.PaymentId,
            message.OrderId
        );

        // Müşteriye bildirim simülasyonu (E-posta / SMS)
        _logger.LogInformation(
            "📧 [E-POSTA GÖNDERİLDİ] Sayın Müşteri, #{OrderId} numaralı siparişiniz için {Amount:C} tutarındaki ödemeniz başarıyla alınmıştır. Siparişiniz hazırlanıyor!",
            message.OrderId,
            message.Amount
        );

        _logger.LogInformation(
            "📱 [SMS GÖNDERİLDİ] Siparişiniz onaylandı. Takip No: #{OrderId}",
            message.OrderId
        );

        return Task.CompletedTask;
    }
}
