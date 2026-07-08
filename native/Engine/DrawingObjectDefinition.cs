namespace VectorAnimationEngine;

internal sealed class DrawingObjectDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Drawing Object";
    public string Kind { get; set; } = "Symbol";
    public string Detail { get; set; } = "Reusable drawing object";
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public VaultItem ToVaultItem()
    {
        var payload = string.Join(Environment.NewLine, new[]
        {
            $"DrawingObjectId: {Id}",
            $"Name: {Name}",
            $"Kind: {Kind}",
            $"CreatedAt: {CreatedAt:O}",
            Detail
        });

        return new VaultItem
        {
            Kind = "Drawing Object",
            Name = Name,
            Detail = Detail,
            Payload = payload
        };
    }
}
