using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>
/// 读取 OpenAI 兼容服务的模型列表，供配置页下拉选择；不保存任何配置。
/// </summary>
internal static class ModelCatalog
{
    /// <summary>
    /// 列表请求的独立连接，超时短于聊天请求，避免配置页长时间等待。
    /// </summary>
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// 请求 {baseUrl}/models 并解析模型标识。
    /// </summary>
    /// <param name="baseUrl">已校验的 API 基础地址，包含版本路径。</param>
    /// <param name="apiKey">Bearer 密钥，可为空。</param>
    /// <param name="userAgent">请求 User-Agent，部分中转服务要求已知客户端标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按服务端顺序去重后的模型标识。</returns>
    internal static async Task<string[]> ListAsync(string baseUrl, string apiKey, string userAgent, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(userAgent)) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"服务返回 HTTP {(int)response.StatusCode}，请检查基础地址与 API Key。");
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            return Extract(document.RootElement);
        }
        catch (JsonException)
        {
            throw new JsonException("服务返回的内容不是有效的模型列表 JSON。");
        }
    }

    /// <summary>
    /// 兼容 OpenAI 的 data 数组、Ollama 风格的 models 数组以及裸数组。
    /// </summary>
    /// <param name="root">已解析的响应根节点。</param>
    /// <returns>去重后的模型标识。</returns>
    private static string[] Extract(JsonElement root)
    {
        var entries = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
            JsonValueKind.Object when root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array => models,
            _ => throw new JsonException("响应中没有可识别的模型列表。")
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            // 字符串数组直接取值；对象数组优先取 id，兼容 name。
            var id = entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String => value.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String => value.GetString(),
                _ => null
            };
            id = id?.Trim();
            if (!string.IsNullOrEmpty(id) && seen.Add(id)) result.Add(id);
        }
        if (result.Count == 0) throw new JsonException("服务未返回任何模型。");
        return [.. result];
    }
}
