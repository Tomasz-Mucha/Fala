namespace Fala.Models;

public sealed class QueueItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Episode Episode { get; init; }
    public required string ShowTitle { get; init; }
    public string ArtworkUrl { get; init; } = "";
}
