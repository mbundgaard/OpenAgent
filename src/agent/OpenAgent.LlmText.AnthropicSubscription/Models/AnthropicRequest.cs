using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.AnthropicSubscription.Models;

internal sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 16000;

    [JsonPropertyName("system")]
    public required List<AnthropicTextBlock> System { get; set; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessage> Messages { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnthropicToolDefinition>? Tools { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicThinking? Thinking { get; set; }
}

internal sealed class AnthropicTextBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

internal sealed class AnthropicThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "adaptive";
}

internal sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required object Content { get; set; } // string or List<AnthropicContentBlock>
}

/// <summary>
/// A content block in a message — text, tool_use, or tool_result.
/// Uses a single class with nullable fields to simplify serialization.
/// </summary>
internal sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    // text block
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    // tool_use block
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Input { get; set; }

    // tool_result block
    [JsonPropertyName("tool_use_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolUseId { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }
}

internal sealed class AnthropicToolDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public required object InputSchema { get; set; }
}
