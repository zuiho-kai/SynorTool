using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PiAi;

/// <summary>Canonical request consumed by PiAi provider adapters.</summary>
public sealed record AiRequest
{
    public required string Model { get; init; }
    /// <summary>Canonical message array. Adapters preserve its JSON shape.</summary>
    public required JsonElement Input { get; init; }
    public IReadOnlyList<JsonElement>? Tools { get; init; }
    public string? ReasoningEffort { get; init; }
    public bool Stream { get; init; } = true;
}

public enum AiFinishReason
{
    Unknown,
    Stop,
    ToolCalls,
    Length,
    ContentFilter,
    Error
}

/// <summary>Normalized streaming events shared by all PiAi providers.</summary>
public abstract record AiEvent;
public sealed record AiTextDelta(string Text) : AiEvent;
public sealed record AiReasoningDelta(string Text) : AiEvent;
public sealed record AiToolCallDelta(int Index, string? CallId, string? Name, string? Arguments) : AiEvent;
/// <summary>Responses 的 reasoning 输出项；store:false 时必须原样回传才能继续工具循环。</summary>
public sealed record AiReasoningItem(JsonElement Element) : AiEvent;
public sealed record AiUsage(long InputTokens, long OutputTokens, long TotalTokens, long? CachedTokens = null) : AiEvent;
public sealed record AiCompleted(AiFinishReason FinishReason = AiFinishReason.Unknown) : AiEvent;
public sealed record AiError(string Message) : AiEvent;

public interface IAiProvider
{
    IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken = default);
}

internal static class Sse
{
    internal sealed record Frame(string? Event, string Data);

    internal static async IAsyncEnumerable<Frame> ReadAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? eventName = null;
        var data = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Count > 0) yield return new Frame(eventName, string.Join("\n", data));
                eventName = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].TrimStart());
        }
        if (data.Count > 0) yield return new Frame(eventName, string.Join("\n", data));
    }

    internal static string? String(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    internal static long Long(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) ? result : 0;

    /// <summary>
    /// 缓存命中的输入 Token：兼容 OpenAI 的 prompt/input_tokens_details.cached_tokens 与 Anthropic 风格的 cache_read_input_tokens。
    /// 没有任何相关字段时返回 null，让前端区分“服务端未上报”和“命中为 0”。
    /// </summary>
    internal static long? Cached(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        if (Detail(usage, "prompt_tokens_details", "cached_tokens") is { } chatCompletions) return chatCompletions;
        if (Detail(usage, "input_tokens_details", "cached_tokens") is { } responses) return responses;
        return usage.TryGetProperty("cache_read_input_tokens", out var value) && value.TryGetInt64(out var read) ? read : null;
    }

    private static long? Detail(JsonElement usage, string container, string name)
        => usage.TryGetProperty(container, out var details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;

    internal static AiFinishReason Finish(string? value) => value?.ToLowerInvariant() switch
    {
        "stop" or "completed" => AiFinishReason.Stop,
        "tool_calls" or "function_call" => AiFinishReason.ToolCalls,
        "length" or "max_tokens" => AiFinishReason.Length,
        "content_filter" => AiFinishReason.ContentFilter,
        _ => AiFinishReason.Unknown
    };
}
