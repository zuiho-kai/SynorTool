using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PiAi;

/// <summary>OpenAI Responses API adapter, also used by compatible relays.</summary>
public sealed class OpenAiResponsesProvider : IAiProvider
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly string? _userAgent;

    public OpenAiResponsesProvider(HttpClient client, string baseUrl, string? apiKey = null, string? userAgent = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpoint = OpenAiUri.Endpoint(baseUrl, "responses");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }

    public async IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = OpenAiResponsesWire.Input(request.Input),
            ["stream"] = request.Stream,
            ["store"] = false
        };
        if (request.Tools is { Count: > 0 }) body["tools"] = OpenAiResponsesWire.Tools(request.Tools);
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) body["reasoning"] = new { effort = request.ReasoningEffort };

        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
        if (_apiKey is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (_userAgent is not null) message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await OpenAiUri.ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            yield return new AiError(detail);
            yield break;
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var callIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var canonicalCallIds = new Dictionary<int, string>();
        var argumentDeltasSeen = new HashSet<int>();
        var callNamesSeen = new HashSet<int>();
        var nextIndex = 0;
        var completed = false;
        await foreach (var frame in Sse.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data == "[DONE]") { completed = true; break; }
            JsonDocument? document = null;
            try { document = JsonDocument.Parse(frame.Data); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                var kind = Sse.String(root, "type") ?? frame.Event;
                if (kind is "error" or "response.failed")
                {
                    var error = root.TryGetProperty("error", out var errorValue) ? errorValue.ToString() : root.ToString();
                    yield return new AiError(error);
                    continue;
                }
                if (kind == "response.output_text.delta")
                {
                    var text = Sse.String(root, "delta");
                    if (!string.IsNullOrEmpty(text)) yield return new AiTextDelta(text);
                }
                else if (kind is "response.reasoning_summary_text.delta" or "response.reasoning_content.delta" or "response.reasoning_text.delta")
                {
                    var text = Sse.String(root, "delta");
                    if (!string.IsNullOrEmpty(text)) yield return new AiReasoningDelta(text);
                }
                else if (kind is ("response.output_item.added" or "response.output_item.done") && root.TryGetProperty("item", out var item) && Sse.String(item, "type") == "function_call")
                {
                    var callId = Sse.String(item, "call_id") ?? Sse.String(item, "id") ?? "";
                    var itemId = Sse.String(item, "id");
                    var index = GetCallIndex(callIndexes, callId, ref nextIndex);
                    canonicalCallIds[index] = callId;
                    if (!string.IsNullOrEmpty(itemId)) callIndexes[itemId] = index;
                    var name = Sse.String(item, "name");
                    var nameDelta = callNamesSeen.Add(index) ? name : null;
                    var completeArguments = kind == "response.output_item.done" && argumentDeltasSeen.Add(index) ? Sse.String(item, "arguments") : null;
                    yield return new AiToolCallDelta(index, callId, nameDelta, completeArguments);
                }
                else if (kind is "response.function_call_arguments.delta" or "response.function_call_arguments.done")
                {
                    var id = Sse.String(root, "call_id") ?? Sse.String(root, "item_id") ?? "";
                    var index = GetCallIndex(callIndexes, id, ref nextIndex);
                    var canonicalId = canonicalCallIds.TryGetValue(index, out var knownId) ? knownId : id;
                    canonicalCallIds[index] = canonicalId;
                    // 增量事件逐段透传；done 事件携带与增量相同的完整内容，已收到增量时忽略，避免参数被拼接两次。
                    if (kind == "response.function_call_arguments.delta")
                    {
                        var delta = Sse.String(root, "delta");
                        if (!string.IsNullOrEmpty(delta)) { argumentDeltasSeen.Add(index); yield return new AiToolCallDelta(index, canonicalId, null, delta); }
                    }
                    else if (argumentDeltasSeen.Add(index))
                    {
                        var arguments = Sse.String(root, "arguments");
                        if (!string.IsNullOrEmpty(arguments)) yield return new AiToolCallDelta(index, canonicalId, null, arguments);
                    }
                }
                else if (kind == "response.completed")
                {
                    completed = true;
                    if (root.TryGetProperty("response", out var completedResponse)) root = completedResponse;
                    if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var outputItem in output.EnumerateArray())
                        {
                            if (Sse.String(outputItem, "type") == "reasoning") yield return new AiReasoningItem(outputItem.Clone());
                        }
                    }
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        var input = Sse.Long(usage, "input_tokens");
                        var outputTokens = Sse.Long(usage, "output_tokens");
                        yield return new AiUsage(input, outputTokens, Sse.Long(usage, "total_tokens") == 0 ? input + outputTokens : Sse.Long(usage, "total_tokens"));
                    }
                    yield return new AiCompleted(AiFinishReason.Stop);
                }
                else if (root.TryGetProperty("usage", out var genericUsage) && genericUsage.ValueKind == JsonValueKind.Object)
                {
                    var input = Sse.Long(genericUsage, "input_tokens");
                    var output = Sse.Long(genericUsage, "output_tokens");
                    yield return new AiUsage(input, output, Sse.Long(genericUsage, "total_tokens"));
                }
            }
        }
        if (!completed) yield return new AiCompleted();
    }

    private static int GetCallIndex(Dictionary<string, int> indexes, string id, ref int next)
    {
        if (indexes.TryGetValue(id, out var value)) return value;
        value = next++;
        indexes[id] = value;
        return value;
    }
}

/// <summary>OpenAI Chat Completions adapter for LM Studio and CLIProxyAPI.</summary>
public sealed class OpenAiChatCompletionsProvider : IAiProvider
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly string? _userAgent;

    public OpenAiChatCompletionsProvider(HttpClient client, string baseUrl, string? apiKey = null, string? userAgent = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpoint = OpenAiUri.Endpoint(baseUrl, "chat/completions");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }

    public async IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = OpenAiChatCompletionsWire.Messages(request.Input),
            ["stream"] = request.Stream,
            ["stream_options"] = new { include_usage = true }
        };
        if (request.Tools is { Count: > 0 }) { body["tools"] = request.Tools; body["tool_choice"] = "auto"; }
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) body["reasoning_effort"] = request.ReasoningEffort;

        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
        if (_apiKey is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (_userAgent is not null) message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            yield return new AiError(await OpenAiUri.ErrorAsync(response, cancellationToken).ConfigureAwait(false));
            yield break;
        }
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var completed = false;
        await foreach (var frame in Sse.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data == "[DONE]") { completed = true; break; }
            JsonDocument? document = null;
            try { document = JsonDocument.Parse(frame.Data); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error)) { yield return new AiError(error.ToString()); continue; }
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    yield return new AiUsage(Sse.Long(usage, "prompt_tokens"), Sse.Long(usage, "completion_tokens"), Sse.Long(usage, "total_tokens"));
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                var finish = choice.TryGetProperty("finish_reason", out var finishProperty) ? Sse.Finish(Sse.String(choice, "finish_reason")) : AiFinishReason.Unknown;
                if (choice.TryGetProperty("delta", out var delta))
                {
                    var text = Sse.String(delta, "content");
                    if (!string.IsNullOrEmpty(text)) yield return new AiTextDelta(text);
                    var reasoning = Sse.String(delta, "reasoning_content") ?? Sse.String(delta, "reasoning");
                    if (!string.IsNullOrEmpty(reasoning)) yield return new AiReasoningDelta(reasoning);
                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                        foreach (var call in toolCalls.EnumerateArray())
                        {
                            var index = call.TryGetProperty("index", out var indexValue) ? indexValue.GetInt32() : 0;
                            var id = Sse.String(call, "id");
                            var function = call.TryGetProperty("function", out var functionValue) ? functionValue : default;
                            yield return new AiToolCallDelta(index, id, Sse.String(function, "name"), Sse.String(function, "arguments"));
                        }
                }
                if (finish != AiFinishReason.Unknown) { completed = true; yield return new AiCompleted(finish); }
            }
        }
        if (!completed) yield return new AiCompleted();
    }
}

internal static class OpenAiChatCompletionsWire
{
    /// <summary>Chat Completions 不认识 Responses 专用的 reasoning_items，发送前移除。</summary>
    /// <param name="input">消息数组。</param>
    /// <returns>可直接写入请求体的消息列表。</returns>
    internal static List<object> Messages(JsonElement input)
    {
        var result = new List<object>();
        foreach (var message in input.EnumerateArray())
        {
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("reasoning_items", out _))
            {
                var copy = new Dictionary<string, object?>();
                foreach (var property in message.EnumerateObject())
                {
                    if (property.NameEquals("reasoning_items")) continue;
                    copy[property.Name] = property.Value.Clone();
                }
                result.Add(copy);
            }
            else
            {
                result.Add(message.Clone());
            }
        }
        return result;
    }
}

internal static class OpenAiResponsesWire
{
    /// <summary>把 Chat Completions 形状的工具定义转换为 Responses 形状（name/description/parameters 位于顶层）。</summary>
    /// <param name="tools">请求中的工具定义。</param>
    /// <returns>可直接写入请求体的对象列表。</returns>
    internal static List<object> Tools(IReadOnlyList<JsonElement> tools)
    {
        var result = new List<object>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                var item = new Dictionary<string, object?>
                {
                    ["type"] = Sse.String(tool, "type") ?? "function",
                    ["name"] = Sse.String(function, "name")
                };
                if (Sse.String(function, "description") is { } description) item["description"] = description;
                if (function.TryGetProperty("parameters", out var parameters)) item["parameters"] = parameters;
                result.Add(item);
            }
            else
            {
                result.Add(tool.Clone());
            }
        }
        return result;
    }

    /// <summary>把 Chat Completions 形状的消息历史转换为 Responses 输入项。</summary>
    /// <param name="input">消息数组。</param>
    /// <returns>Responses 的 input 数组。</returns>
    internal static List<object> Input(JsonElement input)
    {
        var result = new List<object>();
        foreach (var message in input.EnumerateArray())
        {
            // 已经是 Responses 输入项（如 function_call）时原样保留。
            if (message.TryGetProperty("type", out var declaredType) && declaredType.ValueKind == JsonValueKind.String)
            {
                result.Add(message.Clone());
                continue;
            }
            switch (Sse.String(message, "role"))
            {
                case "tool":
                    result.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = Sse.String(message, "tool_call_id") ?? "",
                        ["output"] = Text(message)
                    });
                    break;
                case "assistant":
                    if (message.TryGetProperty("reasoning_items", out var reasoningItems) && reasoningItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var reasoningItem in reasoningItems.EnumerateArray()) result.Add(reasoningItem.Clone());
                    }
                    if (Text(message).Length > 0) result.Add(new { role = "assistant", content = Text(message) });
                    if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in toolCalls.EnumerateArray())
                        {
                            var function = call.TryGetProperty("function", out var value) ? value : default;
                            result.Add(new
                            {
                                type = "function_call",
                                call_id = Sse.String(call, "id") ?? "",
                                name = Sse.String(function, "name") ?? "",
                                arguments = Sse.String(function, "arguments") ?? "{}"
                            });
                        }
                    }
                    break;
                default:
                    result.Add(Message(message));
                    break;
            }
        }
        return result;
    }

    /// <summary>构造普通文本或多模态消息，图片改用 Responses 的 input_image 形状。</summary>
    /// <param name="message">Chat Completions 消息。</param>
    /// <returns>Responses 消息对象。</returns>
    private static object Message(JsonElement message)
    {
        var role = Sse.String(message, "role") ?? "user";
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return new { role, content = Text(message) };
        }
        var parts = new List<object>();
        foreach (var part in content.EnumerateArray())
        {
            if (Sse.String(part, "type") == "image_url")
            {
                var url = part.TryGetProperty("image_url", out var image)
                    ? image.ValueKind == JsonValueKind.String ? image.GetString() : Sse.String(image, "url")
                    : null;
                if (!string.IsNullOrEmpty(url)) parts.Add(new { type = "input_image", detail = "auto", image_url = url });
            }
            else if (Sse.String(part, "text") is { Length: > 0 } text)
            {
                parts.Add(new { type = role == "assistant" ? "output_text" : "input_text", text });
            }
        }
        return new { role, content = parts };
    }

    /// <summary>把消息内容展平为文本，数组只取文本片段。</summary>
    /// <param name="message">包含 content 的消息。</param>
    /// <returns>拼接后的文本。</returns>
    private static string Text(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var text = new System.Text.StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (Sse.String(part, "text") is { } value) text.Append(value);
        }
        return text.ToString();
    }
}

internal static class OpenAiUri
{
    internal static Uri Endpoint(string baseUrl, string suffix)
    {
        if (!Uri.TryCreate(baseUrl?.Trim().TrimEnd('/') ?? "", UriKind.Absolute, out var baseUri)) throw new ArgumentException("Invalid AI base URL.", nameof(baseUrl));
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith('/' + suffix, StringComparison.OrdinalIgnoreCase)) return baseUri;
        return new UriBuilder(baseUri) { Path = path + "/" + suffix }.Uri;
    }

    internal static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) ? message.GetString() ?? error.ToString() : error.ToString();
        }
        catch (JsonException) { }
        return $"AI service returned HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}";
    }
}
