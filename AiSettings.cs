using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum ApiMode
{
    Responses,
    ChatCompletions
}

/// <summary>
/// 累计 token 用量；跨应用重启和清空对话持续累加，只由 <see cref="AiSettings.AddUsage"/> 更新。
/// </summary>
internal sealed record UsageTotals
{
    /// <summary>累计输入 Token。</summary>
    public long InputTokens { get; init; }
    /// <summary>累计输出 Token（含思考）。</summary>
    public long OutputTokens { get; init; }
    /// <summary>累计合计 Token。</summary>
    public long TotalTokens { get; init; }
    /// <summary>累计命中缓存的输入 Token。</summary>
    public long CachedTokens { get; init; }
}

/// <summary>
/// 当前 OpenAI 兼容服务的配置，整体保存在应用数据目录的 JSON 文件中。
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
    /// 自动压缩上下文的字符上限，默认约 128 KB。
    /// </summary>
    public int ContextCompressionBytes { get; init; } = 128 * 1024;

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
    /// 累计用量；配置窗口保存时不会被旧快照覆盖。
    /// </summary>
    public UsageTotals Cumulative { get; init; } = new();

    /// <summary>
    /// 配置目录：macOS 应用数据目录 ~/Library/Application Support/Synor。
    /// </summary>
    private static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Synor");

    /// <summary>
    /// 配置文件路径；整个配置（含 API Key）以一个 JSON 文件保存。
    /// </summary>
    internal static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// 序列化配置读写，避免聊天回写累计用量与配置窗口保存相互覆盖。
    /// </summary>
    private static readonly object SettingsLock = new();

    /// <summary>
    /// 从应用数据目录读取当前配置，或校验并保存新配置。
    /// </summary>
    /// <param name="settings">新配置；为空时只读取。</param>
    /// <returns>已保存的有效配置或首次运行默认配置。</returns>
    internal static AiSettings LoadOrSave(AiSettings? settings = null)
    {
        if (settings is null)
        {
            lock (SettingsLock) return LoadCore();
        }
        settings = settings with { Model = settings.Model?.Trim() ?? "", ApiKey = settings.ApiKey?.Trim() ?? "", SystemPrompt = settings.SystemPrompt ?? "", UserAgent = string.IsNullOrWhiteSpace(settings.UserAgent) ? DefaultUserAgent : settings.UserAgent.Trim() };
        settings = settings with { BaseUrl = NormalizeBaseUrl(settings.BaseUrl) };
        var directory = AgentTools.ResolvePath(settings.WorkingDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!Directory.Exists(directory)) throw new ArgumentException("工作目录不存在。");
        settings = settings with { WorkingDirectory = directory };
        if (string.IsNullOrWhiteSpace(settings.Model) || settings.ApiKey.Contains('\r') || settings.ApiKey.Contains('\n'))
        {
            throw new ArgumentException("模型名称不能为空，API Key 不能包含换行。");
        }
        if (settings.ContextCompressionBytes is < 16 * 1024 or > 2 * 1024 * 1024)
            throw new ArgumentException("上下文压缩大小必须在 16 KB 到 2 MB 之间。");
        if (settings.UserAgent.Contains('\r') || settings.UserAgent.Contains('\n'))
        {
            throw new ArgumentException("User-Agent 不能包含换行。");
        }
        lock (SettingsLock)
        {
            // 累计用量只由 AddUsage 维护；保存配置时保留磁盘上的最新值，避免配置窗口的旧快照把它清零。
            settings = settings with { Cumulative = LoadCore().Cumulative };
            SaveCore(settings);
        }
        return settings;
    }

    /// <summary>
    /// 把一次请求的用量累加到配置文件并返回累计值；跨应用重启和清空对话持续累加。
    /// </summary>
    /// <param name="inputTokens">本次请求累计输入 Token。</param>
    /// <param name="outputTokens">本次请求累计输出 Token。</param>
    /// <param name="totalTokens">本次请求累计合计 Token。</param>
    /// <param name="cachedTokens">本次请求累计命中缓存 Token。</param>
    /// <returns>更新后的累计用量。</returns>
    internal static UsageTotals AddUsage(long inputTokens, long outputTokens, long totalTokens, long cachedTokens)
    {
        lock (SettingsLock)
        {
            var current = LoadCore();
            var cumulative = current.Cumulative with
            {
                InputTokens = current.Cumulative.InputTokens + inputTokens,
                OutputTokens = current.Cumulative.OutputTokens + outputTokens,
                TotalTokens = current.Cumulative.TotalTokens + totalTokens,
                CachedTokens = current.Cumulative.CachedTokens + cachedTokens
            };
            SaveCore(current with { Cumulative = cumulative });
            return cumulative;
        }
    }

    /// <summary>
    /// 读取配置文件；文件不存在时返回默认配置。调用方需自行持有 SettingsLock。
    /// </summary>
    private static AiSettings LoadCore()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AiSettings();
            return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(SettingsPath)) ?? new AiSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException($"读取配置失败：{error.Message}");
        }
    }

    /// <summary>
    /// 写入配置文件：先写临时文件再原子替换，并把权限收紧到仅当前用户可读写（配置含 API Key）。
    /// </summary>
    private static void SaveCore(AiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"保存配置失败：{error.Message}");
        }
    }

    /// <summary>
    /// 校验并规范化 API 基础地址；配置保存与模型列表查询共用同一规则。
    /// </summary>
    /// <param name="value">用户填写的基础地址。</param>
    /// <returns>去除首尾空白与末尾斜杠后的地址。</returns>
    internal static string NormalizeBaseUrl(string? value)
    {
        var normalized = value?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("请输入 HTTPS API 基础地址；本机服务可使用 HTTP。地址中不要包含密钥、查询参数或 /chat/completions。");
        }
        if (uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("请填写 API 基础地址，例如 https://example.com/v1，不包含 /chat/completions。");
        }
        return normalized;
    }
}
