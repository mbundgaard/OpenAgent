using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAISubscription.Models;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.LlmText.OpenAISubscription;

/// <summary>
/// ChatGPT subscription-backed text provider using the chatgpt.com Codex responses endpoint.
/// </summary>
public sealed class OpenAiSubscriptionTextProvider(IAgentLogic agentLogic, ILogger<OpenAiSubscriptionTextProvider> logger) : ILlmTextProvider, IDisposable
{
    private OpenAiSubscriptionConfig? _config;
    private HttpClient? _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string OAuthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string OAuthRedirectUri = "http://localhost:1455/auth/callback";
    private const string OAuthAuthorizeEndpoint = "https://auth.openai.com/oauth/authorize";
    private const string OAuthTokenEndpoint = "https://auth.openai.com/oauth/token";
    private static readonly TimeSpan PendingAuthTtl = TimeSpan.FromMinutes(15);

    // Pending PKCE verifiers keyed by OAuth state. State is round-tripped through the IdP and
    // echoed back on the callback URL; the matching verifier is pulled from here when
    // NormalizeConfigAsync runs the token exchange. Static so the dict survives across the
    // GET-config / POST-config endpoint pair (both hit the same singleton).
    private static readonly ConcurrentDictionary<string, PendingAuth> PendingAuths = new();

    public const string ProviderKey = "openai-subscription";

    public string Key => ProviderKey;

    // Generated fresh on every read so each form-open mints a unique state + PKCE pair.
    // OpenAI's IdP requires state >= 8 chars and the Codex flow requires PKCE; a static
    // URL fails both checks.
    public IReadOnlyList<ProviderConfigField> ConfigFields => BuildConfigFields();

    public IReadOnlyList<string> Models => _config?.Models ?? [];

    public async ValueTask<JsonElement> NormalizeConfigAsync(JsonElement configuration, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in configuration.EnumerateObject())
            dict[prop.Name] = prop.Value;

        if (dict.TryGetValue("callbackUrl", out var callbackElement)
            && callbackElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(callbackElement.GetString()))
        {
            var callbackUrl = callbackElement.GetString()!;
            if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callbackUri))
                throw new InvalidOperationException("callbackUrl must be an absolute URL.");

            // The IdP may have rejected the request and redirected with ?error=...; surface that
            // verbatim instead of looking for a code that isn't there.
            var oauthError = GetQueryParameter(callbackUri, "error");
            if (!string.IsNullOrWhiteSpace(oauthError))
            {
                var description = GetQueryParameter(callbackUri, "error_description") ?? "";
                throw new InvalidOperationException($"OpenAI OAuth error: {oauthError}. {description}".Trim());
            }

            var code = GetQueryParameter(callbackUri, "code");
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("callbackUrl must contain a 'code' query parameter.");

            var state = GetQueryParameter(callbackUri, "state");
            if (string.IsNullOrWhiteSpace(state))
                throw new InvalidOperationException("callbackUrl must contain a 'state' query parameter.");

            // Single-use lookup: TryRemove consumes the pending entry so a leaked code can't
            // be replayed. Expired/unknown state means the user needs to open the link again.
            if (!PendingAuths.TryRemove(state, out var pending))
                throw new InvalidOperationException("Auth state expired or unknown. Re-open the OpenAI Subscription Login link and try again.");
            if (pending.Expires < DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Auth state expired. Re-open the OpenAI Subscription Login link and try again.");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var tokenResponse = await client.PostAsync(OAuthTokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OAuthClientId,
                ["code"] = code,
                ["redirect_uri"] = OAuthRedirectUri,
                ["code_verifier"] = pending.CodeVerifier
            }), ct);

            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);
            if (!tokenResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI token exchange failed ({(int)tokenResponse.StatusCode}): {tokenBody}");

            using var tokenDoc = JsonDocument.Parse(tokenBody);
            if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenProp)
                || string.IsNullOrWhiteSpace(accessTokenProp.GetString()))
                throw new InvalidOperationException("OpenAI token response missing access_token.");

            dict["setupToken"] = JsonSerializer.SerializeToElement(accessTokenProp.GetString()!);
            dict["callbackUrl"] = JsonSerializer.SerializeToElement(string.Empty);
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<OpenAiSubscriptionConfig>(configuration, JsonOptions)
                  ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.SetupToken))
            throw new InvalidOperationException(
                "OpenAI subscription is not configured. Open the OpenAI Subscription Login link, sign in, then paste the redirect URL into 'Callback URL' and save.");

        if (configuration.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.String)
            _config.Models = modelsProp.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _httpClient?.Dispose();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        logger.LogInformation("OpenAI subscription provider configured with {ModelCount} models", _config.Models.Length);
    }

    public void Dispose() => _httpClient?.Dispose();

    public int? GetContextWindow(string model)
    {
        if (model.Contains("gpt-5", StringComparison.OrdinalIgnoreCase)) return 400_000;
        if (model.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase)) return 128_000;
        if (model.Contains("gpt-4", StringComparison.OrdinalIgnoreCase)) return 128_000;
        return null;
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        if (conversation.ContextWindowTokens is null)
        {
            var window = GetContextWindow(conversation.Model);
            if (window is not null)
                conversation.ContextWindowTokens = window;
        }

        var conversationId = conversation.Id;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        agentLogic.AddMessage(conversationId, userMessage);

        var accountId = ExtractAccountId(_config.SetupToken);
        var input = BuildInput(conversation);
        var tools = BuildTools();

        const int maxToolRounds = 10;
        for (var round = 0; round < maxToolRounds; round++)
        {
            var requestBody = new
            {
                model = conversation.Model,
                store = false,
                stream = true,
                instructions = agentLogic.GetSystemPrompt(conversation.Id, conversation.Source, voice: false, conversation.ActiveSkills, conversation.Intention),
                input,
                tools,
                tool_choice = tools is { Count: > 0 } ? "auto" : null,
                parallel_tool_calls = true
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, ResolveCodexUrl())
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SetupToken);
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"OpenAI subscription returned {(int)response.StatusCode}: {body}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var text = new StringBuilder();
            var toolCalls = new Dictionary<string, PendingToolCall>();
            int? promptTokens = null;
            int? completionTokens = null;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..].Trim();
                if (data.Length == 0 || data == "[DONE]") continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp)) continue;
                    var type = typeProp.GetString();

                    if (type == "response.output_item.added")
                    {
                        if (root.TryGetProperty("item", out var item)
                            && item.TryGetProperty("type", out var itemType)
                            && itemType.GetString() == "function_call")
                        {
                            var callId = item.TryGetProperty("call_id", out var callIdProp) ? callIdProp.GetString() : null;
                            var itemId = item.TryGetProperty("id", out var itemIdProp) ? itemIdProp.GetString() : null;
                            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                            var key = callId ?? Guid.NewGuid().ToString();
                            toolCalls[key] = new PendingToolCall
                            {
                                CallId = callId ?? key,
                                ItemId = itemId,
                                Name = name ?? "",
                                Arguments = item.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() ?? "" : ""
                            };
                        }
                    }
                    else if (type == "response.function_call_arguments.delta")
                    {
                        var callId = root.TryGetProperty("call_id", out var callIdProp) ? callIdProp.GetString() : null;
                        var delta = root.TryGetProperty("delta", out var deltaProp) ? deltaProp.GetString() : null;
                        if (callId is not null && delta is not null && toolCalls.TryGetValue(callId, out var pending))
                            pending.Arguments += delta;
                    }
                    else if (type == "response.output_text.delta")
                    {
                        if (root.TryGetProperty("delta", out var deltaProp))
                        {
                            var delta = deltaProp.GetString();
                            if (!string.IsNullOrEmpty(delta))
                            {
                                text.Append(delta);
                                yield return new TextDelta(delta);
                            }
                        }
                    }
                    else if (type == "response.completed")
                    {
                        if (root.TryGetProperty("response", out var responseNode)
                            && responseNode.TryGetProperty("usage", out var usage))
                        {
                            if (usage.TryGetProperty("input_tokens", out var inTok)) promptTokens = inTok.GetInt32();
                            if (usage.TryGetProperty("output_tokens", out var outTok)) completionTokens = outTok.GetInt32();
                        }
                    }
                    else if (type == "response.failed")
                    {
                        throw new InvalidOperationException("OpenAI subscription response failed.");
                    }
                }
            }

            if (toolCalls.Count > 0)
            {
                var storedCalls = toolCalls.Values.Select(tc => new StoredToolCall
                {
                    Id = tc.StoredId,
                    Type = "function",
                    Function = new StoredToolCallFunction
                    {
                        Name = tc.Name,
                        Arguments = tc.Arguments
                    }
                }).ToList();

                agentLogic.AddMessage(conversationId, new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ConversationId = conversationId,
                    Role = "assistant",
                    ToolCalls = JsonSerializer.Serialize(storedCalls),
                    Modality = MessageModality.Text
                });

                foreach (var tc in toolCalls.Values)
                {
                    yield return new ToolCallEvent(tc.StoredId, tc.Name, tc.Arguments);
                    var result = await agentLogic.ExecuteToolAsync(conversationId, tc.Name, tc.Arguments, ct);
                    yield return new ToolResultEvent(tc.StoredId, tc.Name, result);

                    agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(tc.Name, result),
                        FullToolResult = result,
                        ToolCallId = tc.StoredId,
                        Modality = MessageModality.Text
                    });
                }

                input = BuildInput(agentLogic.GetConversation(conversationId) ?? conversation);
                continue;
            }

            stopwatch.Stop();
            var assistantMessageId = Guid.NewGuid().ToString();
            agentLogic.AddMessage(conversationId, new Message
            {
                Id = assistantMessageId,
                ConversationId = conversationId,
                Role = "assistant",
                Content = text.ToString(),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Modality = MessageModality.Text
            });

            var fresh = agentLogic.GetConversation(conversationId) ?? conversation;
            fresh.LastPromptTokens = promptTokens;
            fresh.TotalPromptTokens += promptTokens ?? 0;
            fresh.TotalCompletionTokens += completionTokens ?? 0;
            fresh.TurnCount++;
            fresh.LastActivity = DateTimeOffset.UtcNow;
            agentLogic.UpdateConversation(fresh);

            yield return new AssistantMessageSaved(assistantMessageId);
            yield break;
        }

        throw new InvalidOperationException($"Tool call loop exceeded {maxToolRounds} rounds.");
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var accountId = ExtractAccountId(_config.SetupToken);
        var input = messages.Where(m => m.Role != "system").Select(m => new
        {
            role = m.Role == "tool" ? "user" : m.Role,
            content = m.Content ?? ""
        }).ToList();

        var requestBody = new
        {
            model,
            store = false,
            stream = true,
            instructions = messages.FirstOrDefault(m => m.Role == "system")?.Content,
            input
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveCodexUrl())
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SetupToken);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"OpenAI subscription returned {(int)response.StatusCode}: {body}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() == "response.output_text.delta"
                && doc.RootElement.TryGetProperty("delta", out var deltaProp))
            {
                var delta = deltaProp.GetString();
                if (!string.IsNullOrEmpty(delta))
                    yield return new TextDelta(delta);
            }
        }
    }

    private List<object> BuildInput(Conversation conversation)
    {
        var input = new List<object>();

        if (!string.IsNullOrEmpty(conversation.Context))
        {
            input.Add(new
            {
                role = "user",
                content = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n" + conversation.Context + "\n</summary>"
            });
        }

        var messages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.ToolCalls is not null)
            {
                var toolCalls = JsonSerializer.Deserialize<List<StoredToolCall>>(msg.ToolCalls, JsonOptions) ?? [];
                foreach (var tc in toolCalls)
                {
                    var parts = tc.Id.Split('|', 2);
                    var callId = parts[0];
                    var itemId = parts.Length > 1 ? parts[1] : null;
                    input.Add(new
                    {
                        type = "function_call",
                        call_id = callId,
                        id = itemId,
                        name = tc.Function.Name,
                        arguments = tc.Function.Arguments
                    });
                }

                while (i + 1 < messages.Count && messages[i + 1].Role == "tool")
                {
                    i++;
                    var toolMsg = messages[i];
                    if (toolMsg.ToolCallId is null) continue;
                    var callId = toolMsg.ToolCallId.Split('|', 2)[0];
                    input.Add(new
                    {
                        type = "function_call_output",
                        call_id = callId,
                        output = toolMsg.FullToolResult ?? toolMsg.Content ?? ""
                    });
                }
                continue;
            }

            if (msg.Role == "tool") continue;

            input.Add(new
            {
                role = msg.Role,
                content = msg.Content ?? ""
            });
        }

        return input;
    }

    private List<object>? BuildTools()
    {
        return agentLogic.Tools.Count == 0
            ? null
            : agentLogic.Tools.Select(t => new
            {
                type = "function",
                name = t.Name,
                description = t.Description,
                parameters = t.Parameters,
                strict = false
            } as object).ToList();
    }

    private static string ResolveCodexUrl()
    {
        return "https://chatgpt.com/backend-api/codex/responses";
    }

    private static IReadOnlyList<ProviderConfigField> BuildConfigFields()
    {
        var (state, verifier, challenge) = GeneratePkce();
        var expires = DateTimeOffset.UtcNow + PendingAuthTtl;
        PendingAuths[state] = new PendingAuth { CodeVerifier = verifier, Expires = expires };
        ExpirePendingAuths();

        var url = OAuthAuthorizeEndpoint
                  + "?response_type=code"
                  + "&client_id=" + OAuthClientId
                  + "&redirect_uri=" + Uri.EscapeDataString(OAuthRedirectUri)
                  + "&scope=" + Uri.EscapeDataString("openid profile email offline_access")
                  + "&id_token_add_organizations=true"
                  + "&codex_cli_simplified_flow=true"
                  + "&originator=openagent"
                  + "&state=" + state
                  + "&code_challenge=" + challenge
                  + "&code_challenge_method=S256";

        return
        [
            new() { Key = "authUrl", Label = "OpenAI Subscription Login", Type = "Url", DefaultValue = url },
            new() { Key = "callbackUrl", Label = "Callback URL", Type = "String", Required = true },
            new() { Key = "setupToken", Label = "Setup Token", Type = "Secret" },
            new() { Key = "models", Label = "Models (comma-separated)", Type = "String", Required = true }
        ];
    }

    private static (string State, string Verifier, string Challenge) GeneratePkce()
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (state, verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ExpirePendingAuths()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in PendingAuths)
            if (value.Expires < now)
                PendingAuths.TryRemove(key, out _);
    }

    private static string? GetQueryParameter(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query)) return null;
        if (query[0] == '?') query = query[1..];

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kvp = part.Split('=', 2);
            var name = Uri.UnescapeDataString(kvp[0]);
            if (!string.Equals(name, key, StringComparison.Ordinal)) continue;
            var value = kvp.Length > 1 ? Uri.UnescapeDataString(kvp[1]) : string.Empty;
            return value;
        }

        return null;
    }

    private static string ExtractAccountId(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("setupToken is not a JWT; cannot extract chatgpt account id.");

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        var mod = payload.Length % 4;
        if (mod > 0) payload = payload.PadRight(payload.Length + (4 - mod), '=');

        var bytes = Convert.FromBase64String(payload);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
            || !auth.TryGetProperty("chatgpt_account_id", out var accountIdProp)
            || string.IsNullOrWhiteSpace(accountIdProp.GetString()))
        {
            throw new InvalidOperationException("setupToken missing chatgpt_account_id claim.");
        }

        return accountIdProp.GetString()!;
    }

    private sealed class PendingToolCall
    {
        public string CallId { get; set; } = "";
        public string? ItemId { get; set; }
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string StoredId => ItemId is null ? CallId : $"{CallId}|{ItemId}";
    }

    private sealed class PendingAuth
    {
        public required string CodeVerifier { get; init; }
        public required DateTimeOffset Expires { get; init; }
    }

    private sealed class StoredToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public StoredToolCallFunction Function { get; set; } = new();
    }

    private sealed class StoredToolCallFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = "";
    }
}
