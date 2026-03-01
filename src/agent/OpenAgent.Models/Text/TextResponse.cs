namespace OpenAgent.Models.Text;

public sealed class TextResponse
{
    public required string Content { get; init; }
    public required string Role { get; init; }
}
