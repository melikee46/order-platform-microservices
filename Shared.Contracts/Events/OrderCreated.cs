namespace Shared.Contracts.Events;

public record OrderCreated(
    Guid OrderId,
    string ProductName,
    int Quantity,
    decimal TotalPrice,
    DateTime CreatedAt
);
