namespace RegNeps.Domain.Entities;

/// <summary>Tela del catálogo VICUNHA.</summary>
public sealed class Fabric
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
