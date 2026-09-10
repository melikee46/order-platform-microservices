namespace Order.Service.DTOs;

public record CreateOrderDto(
    string ProductName,
    int Quantity,
    decimal TotalPrice
);

public record OrderResponseDto(
    Guid Id,
    string ProductName,
    int Quantity,
    decimal TotalPrice,
    string Status,
    DateTime CreatedAt
);
