# Synor 聊天 Demo

.NET 10 启动原生 AppKit 标准玻璃窗口，内部 WKWebView 加载本地 React 页面。
使用 assistant-ui 管理消息与输入，使用 Streamdown 渲染 Markdown。

## 运行

需要 macOS 26+、Xcode macOS 26 SDK、.NET SDK 10.0.400，以及 Node.js / npm。

```sh
cd /Users/fengying/Desktop/SynorTool
npm --prefix Web ci
dotnet run
```

`dotnet build` 会运行 TypeScript 检查、打包前端、编译原生桥接并复制网页到输出目录。
页面打包为本地单文件，不需要开发服务器，也不请求远程前端资源。

## 体验

- 默认连接 LM Studio，可先启动本地服务器 `http://127.0.0.1:1234`，加载 `google/gemma-4-e4b`。
- 点击输入框底部“配置”，或菜单栏右键菜单“AI 配置…”，打开独立原生玻璃窗口，填写 API 模式（Responses 或 Chat Completions）、OpenAI 兼容 API 基础地址、模型、API Key 与系统提示词。配置和密钥保存在应用数据目录 `~/Library/Application Support/Synor/settings.json`（含 API Key，文件权限仅当前用户可读写），重启后保留。配置在独立窗口内编辑，不跳转聊天页面，也不影响正在进行的对话。
- 地址包含版本路径（例如 `/v1`），不包含具体接口路径；远程服务要求 HTTPS，本机可使用 HTTP。LM Studio 与 CLIProxyAPI 均可按服务端支持选择 API 模式。
- “模型名称”旁的“获取列表”会按当前填写的基础地址与 API Key 请求服务端的 `/models`，读取结果填入可输入的候选项，仍可手动输入。服务未提供该接口、认证失败或列表为空时会提示原因；列表只用于本次配置，不写入配置文件。
- 保存对下一次请求生效，系统提示词作为首条 `system` 消息发送。配置窗口中 Esc、取消或保存后都会返回聊天；取消不保存。
- 服务不接受 `reasoning_effort` 时，在配置页关闭“发送思考控制参数”。
- 输入框底部可开关思考模式，并在 low/high/max 间选择思考强度（默认 high），对下一次请求生效；生成中禁用切换。关闭思考模式时发送 `reasoning_effort: none`，配置中关闭“发送思考控制参数”则完全不携带该字段；设置保留至本次运行结束。
- 输入消息后 Enter 发送，Shift + Enter 换行；生成中可以停止。
- 在输入框粘贴剪贴板图片，显示缩略图并可移除；支持仅图片发送，图片随消息发送到已配置的 AI 服务，需选择支持视觉的模型。
- 模型返回 `reasoning_content` 时流式展开思考过程，正文开始后自动收起，点击可重新展开；不返回该字段时不显示思考区。
- 每次请求携带当前分支的消息历史，支持多轮对话；停止会取消底层 HTTP 请求。
- “复制回答”旁显示该次请求的首 Token 本地耗时（含首个思考片段）与服务端输出 Token，形如 `首 Token 0.50 s 速度 40.0 Token/s ↓51 Token`；大数用 k/M 缩写，悬停可查看精确值；输出包含思考 Token，提前停止且未收到用量时显示未返回。
- 底部提示栏在 `Shift + Enter 换行` 旁显示当前上下文 `上下文 ↑9k`（取最近一次带用量的请求，因为每次请求都携带完整历史，所以最新一次的输入即当前上下文大小；新一轮生成用量返回前沿用上一次的值），再接累计用量 `累计 ↑1.2M ↓123k | 1.3M`。累计值随每次回答写入配置文件，软件重启、重新开聊或清理上下文后都继续累加，不会清零。服务端上报缓存时再接一段 `缓存率 90%`（累计命中缓存 ÷ 累计输入 Token，兼容 OpenAI `*_tokens_details.cached_tokens` 与 Anthropic 风格 `cache_read_input_tokens`），悬停可看原始值，未上报则不显示。
- 回答支持 Markdown、滚动、复制；代码仅展示，不执行，也尚未加入语法高亮。
- 输入框底部与菜单栏右键菜单提供独立原生浮动窗口“快捷翻译”：自动识别原文语言，选择目标语言后点击翻译，支持流式输出、停止和复制译文；复用当前服务配置，使用独立翻译提示词，不带入聊天历史。
- 在其他应用中选中文字后连按两次 Option，会将可读取的选区填入翻译面板；不自动发送，不修改剪贴板。读取顺序：焦点元素 → 子树 → 祖先元素（网页的跨节点选区由 `AXWebArea` 报告），依次尝试 `AXSelectedText`、Text Marker、选区范围参数化属性和整段文本切片。应用不支持读取选区时请手动粘贴。
- 前台是 Codex（`com.openai.codex`）且选区在**可编辑控件**（发送框）时，改为后台自动翻译并把译文就地替换回原选区，不弹出翻译面板；输入框内容在翻译期间被改动则放弃回填。选区在 AI 回复等静态文本上（无可写回位置）时仍走翻译面板。
- Chromium / Electron 默认不构建辅助功能树（Chrome、VSCode、Cursor 等），读不到选区时会按业界做法（selection-hook、SuperCmd 均为同一方案）对前台应用设置 `AXEnhancedUserInterface` 与 `AXManualAccessibility`，幂等且不还原：第一次按仍然走下面的复制兜底，之后即可直接用辅助功能读到选区。代价是目标应用会构建完整辅助功能树（Chrome 有内存与渲染开销），VSCode 等 Electron 编辑器会进入屏幕阅读器优化模式，仅本次运行生效，不写入它的配置。
- 仍然读不到时（自绘应用、Sublime 等）才模拟一次 ⌘C：复制前备份剪贴板，复制后轮询 `changeCount`，读完还原。部分编辑器没有选区时按 ⌘C 会复制整行（VSCode 的 `editor.emptySelectionClipboard`），结果与“选中一整行”完全无法区分，因此这类应用的复制结果若为“单行 + 行尾换行”，按无选区处理，只显示聊天窗口，不填写翻译面板。
- 单独连按两次左或右 Option（两次间隔小于系统双击间隔）即可显示窗口；组合键、鼠标点击或滚动会取消本次计数。首次需在“系统设置 → 隐私与安全性 → 辅助功能”中允许 Synor，然后点击菜单栏图标使权限生效；右键菜单也提供权限入口。
- 原生顶部可拖动，Esc 隐藏窗口；菜单栏图标左键显示窗口，右键可显示、隐藏或退出。隐藏保留聊天，退出后聊天记录清空，聊天、翻译、配置三个窗口各自记住上次的位置与大小（存在 `~/Library/Preferences/com.synor.tool.plist`），首次运行或显示器变化导致位置失效时自动居中。

聊天工具请求持续执行直到完成或用户停止；bash 可由模型指定 timeout 秒数。
网页仍禁止直接网络请求和任意 HTML；通过受限的原生桥交由 .NET 发起请求。

系统降低透明度设置会影响玻璃外观。单窗口截图可能丢失玻璃背后的内容，
应以实际屏幕显示为准。

聊天页面背景透明，透出下方原生标准玻璃；输入框、代码块、表格保留独立底色。
当前通过 KVC 关闭 WKWebView 的内部 `drawsBackground` 属性。这不是公开 API，
系统升级时需要验证兼容性，不能视为已完成正式分发兼容性验证。

## 打包 macOS App

```sh
./scripts/package-macos.sh
open dist/Synor.app
```

应用固定安装到 `~/Applications/Synor.app`，`dist/Synor.app` 是指向该位置的符号链接。适用于 Apple Silicon、macOS 26+，包含 .NET 10 运行时，无需额外安装 .NET。
应用常驻菜单栏，不显示 Dock 图标；再次打开 App 会显示已有窗口。
固定使用登录钥匙串中的 `Synor Local Development` 本机签名证书，应用标识保持 `com.synor.tool`。打包脚本锁定证书指纹，身份不可用时停止，不生成新证书，也不回退 ad-hoc。私钥不在仓库中；请保留原登录钥匙串。

从旧临时签名切换后首次可能需要重新授予辅助功能或钥匙串访问权限。后续构建保持同一证书与安装位置。当前证书用于本机开发，尚未进行 Developer ID 签名与 Apple 公证。

翻译窗口可以单独拖动和调整大小；Esc 只隐藏翻译窗口，再次打开保留内容。聊天与翻译可同时生成，取消操作相互独立。

## Agent 工具

聊天默认启用 `read`、`grep`、`find`、`ls`、`write`、`edit`、`bash`、`web_fetch`，以当前 macOS 用户权限自动执行，无逐次确认或沙箱。配置页可设置工作目录；相对路径基于该目录，绝对路径和 `~` 也可使用。工作目录不限制访问范围。翻译请求不开放工具。

- `read` 支持文本分页和图片；文本最多返回 2000 行或 50KB，图片通过 macOS sips 转为不超过 2000 像素的 PNG。
- `grep` / `find` 使用随应用附带的 ripgrep / fd，支持 glob 和忽略规则；`ls` 包括隐藏文件。
- `write` 创建父目录并写入 UTF-8；`edit` 支持同一文件多处替换，拒绝不唯一或重叠匹配，保留 BOM/换行格式，返回差异。
- `bash` 使用 `/bin/bash -c`，支持流式输出、退出状态、timeout 和取消进程树。各次调用不保留 `cd` 或环境变量。输出展示末尾 2000 行/50KB，完整日志写入系统临时目录，路径返回模型。
- 工具调用卡片展示参数、状态和输出，可以展开查看。工具结果随下一轮聊天保留，清理按钮或 ⌘L 清空上下文并取消正在进行的请求。
- Token 用量累加各次模型请求并写入配置文件：单条回答只显示本轮输出，累计输入/输出/合计与缓存率常驻底部；平均 Token/s 排除工具执行和各轮首 Token 等待时间，服务端缺少用量时不估算，也不计入累计。
- 默认提示词参考 Pi，配置页的提示词作为附加指令。参考版本与许可证见 `ThirdParty/`。只移植七个工具及调用流程，不包含 Pi 的技能、扩展或会话持久化。

验证：

```sh
dotnet run --project Tests/ToolTests.csproj
python3 Tests/stream_test.py
./scripts/package-macos.sh
```

前两项在临时目录内验证工具行为和模拟 SSE 多轮调用，不调用真实模型，也不读写应用配置。

### 网页读取

`web_fetch` 接受 `url`，使用 C# HttpClient 获取 HTTP(S) 网页，以 HtmlAgilityPack 提取正文，返回来源 URL、标题与简洁 Markdown；也可读取纯文本和 JSON。支持相对重定向（最多 5 次）、中文字符集、取消请求；30 秒超时，解压后的响应最多 2 MB，返回最多 2000 行 / 50 KB，超出时标明截断。无需第三方抓取服务或 API Key。

这是网页读取工具，不执行 JavaScript、不使用浏览器登录态，也不解析 PDF 或图片。参考 [Pi webfetch 插件](https://github.com/k3dom/pi-plugins/tree/master/plugins/webfetch) 的抓取、转换和截断流程，以 C# 独立实现。网页内容按不可信外部数据处理，不作为用户指令。
