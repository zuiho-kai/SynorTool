using System.Runtime.InteropServices;

if (!OperatingSystem.IsMacOSVersionAtLeast(26))
{
    Console.Error.WriteLine("This native Liquid Glass demo requires macOS 26 or later.");
    return 1;
}

var bundledPage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Web", "index.html"));
var pagePath = File.Exists(bundledPage) ? bundledPage : Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
return NativePanel.Run(pagePath, ChatClient.Callback);

/// <summary>
/// 在主线程启动原生 AppKit 浮动面板。
/// </summary>
internal static partial class NativePanel
{
    /// <summary>
    /// 运行原生事件循环，直到用户从菜单退出应用。
    /// </summary>
    /// <param name="pagePath">本地聊天页面的绝对路径。</param>
    /// <param name="callback">聊天请求的托管回调。</param>
    /// <returns>原生面板的退出状态码。</returns>
    [DllImport("SynorPanel", EntryPoint = "synor_panel_run", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Run([MarshalAs(UnmanagedType.LPUTF8Str)] string pagePath, ChatClient.RequestCallback callback);
}
