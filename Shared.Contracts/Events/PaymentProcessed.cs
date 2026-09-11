namespace Shared.Contracts.Events;

public record PaymentProcessed(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Status,
    DateTime ProcessedAt
);
