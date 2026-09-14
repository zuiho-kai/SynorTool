using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum ApiMode
{
    Responses,
    ChatCompletions
}

/// <summary>
/// 当前 OpenAI 兼容服务的配置，整体保存在 macOS 钥匙串中。
/// </summary>
internal sealed record AiSettings
{
    /// <summary>
    /// 协议模式；未写入该字段的旧配置默认为 Responses。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ApiMode ApiMode { get; init; } = ApiMode.Responses;
    /// <summary>
    /// 服务的 API 基础地址，包含版本路径。
    /// </summary>
    public string BaseUrl { get; init; } = "http://127.0.0.1:1234/v1";
    /// <summary>
    /// 请求使用的模型标识。
    /// </summary>
    public string Model { get; init; } = "google/gemma-4-e4b";
    /// <summary>
    /// Bearer 密钥，本地无认证服务可留空。
    /// </summary>
    public string ApiKey { get; init; } = "";
    /// <summary>
    /// 每轮请求最前面的系统指令。
    /// </summary>
    public string SystemPrompt { get; init; } = "";
    /// <summary>
    /// 服务是否接受 reasoning_effort 参数。
    /// </summary>
    public bool ReasoningControl { get; init; } = true;

    /// <summary>
    /// 请求使用的 User-Agent；部分中转服务只放行已知客户端标识，留空时使用默认值。
    /// </summary>
    public string UserAgent { get; init; } = DefaultUserAgent;

    /// <summary>
    /// 与 Pi 一致的默认客户端标识，供要求特定 User-Agent 的中转服务使用。
    /// </summary>
    internal static string DefaultUserAgent { get; } =
        $"pi ({RuntimeInformation.OSDescription}; {(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant())})";

    /// <summary>
    /// 工具的默认工作目录；仅决定相对路径，不构成权限隔离。
    /// </summary>
    public string WorkingDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 读取或写入钥匙串；返回值由原生层持有，调用方不释放。
    /// </summary>
    /// <param name="json">要保存的配置；为空时读取。</param>
    /// <returns>原生 JSON 结果的地址。</returns>
    [DllImport("SynorPanel", EntryPoint = "synor_settings", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Access([MarshalAs(UnmanagedType.LPUTF8Str)] string? json);

    /// <summary>
    /// 在原生主线程读取当前配置，或验证并保存新配置。
    /// </summary>
    /// <param name="settings">新配置；为空时只读取。</param>
    /// <returns>已保存的有效配置或首次运行默认配置。</returns>
    internal static AiSettings LoadOrSave(AiSettings? settings = null)
    {
        if (settings is not null)
        {
            settings = settings with { BaseUrl = settings.BaseUrl?.Trim().TrimEnd('/') ?? "", Model = settings.Model?.Trim() ?? "", ApiKey = settings.ApiKey?.Trim() ?? "", SystemPrompt = settings.SystemPrompt ?? "", UserAgent = string.IsNullOrWhiteSpace(settings.UserAgent) ? DefaultUserAgent : settings.UserAgent.Trim() };
            var directory = AgentTools.ResolvePath(settings.WorkingDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!Directory.Exists(directory)) throw new ArgumentException("工作目录不存在。");
            settings = settings with { WorkingDirectory = directory };
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new ArgumentException("请输入 HTTPS API 基础地址；本机服务可使用 HTTP。地址中不要包含密钥、查询参数或 /chat/completions。");
            }
            if (uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("请填写 API 基础地址，例如 https://example.com/v1，不包含 /chat/completions。");
            }
            if (string.IsNullOrWhiteSpace(settings.Model) || settings.ApiKey.Contains('\r') || settings.ApiKey.Contains('\n'))
            {
                throw new ArgumentException("模型名称不能为空，API Key 不能包含换行。");
            }
            if (settings.UserAgent.Contains('\r') || settings.UserAgent.Contains('\n'))
            {
                throw new ArgumentException("User-Agent 不能包含换行。");
            }
        }
        using var result = JsonDocument.Parse(Marshal.PtrToStringUTF8(Access(settings is null ? null : JsonSerializer.Serialize(settings))) ?? "{}");
        if (result.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.GetString());
        }
        var value = result.RootElement.GetProperty("settings");
        return value.ValueKind == JsonValueKind.Null ? new AiSettings() : value.Deserialize<AiSettings>() ?? new AiSettings();
    }
}
