namespace Payment.Service.Models;

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // Mantıksal referans (Logical Reference) - Başka bir mikroservisin DB'si olduğu için fiziksel Foreign Key YOKTUR!
    public Guid OrderId { get; set; }
    
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Success";
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
