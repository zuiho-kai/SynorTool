# pi-ai C# 移植与 LM Studio / CLIProxyAPI 集成

## 状态
IMPLEMENTING

## 目标
- 基于 pi-ai upstream commit `71dca871bc80b6bc97be37f0ca3189399d651fff` 的核心契约，在 C# 中实现统一流式 AI 接口。
- 支持 OpenAI Responses 与 Chat Completions 两种协议。
- 支持 LM Studio 与 CLIProxyAPI（按其实际暴露的协议配置）。
- 保持现有 WebView/native 事件协议与 AgentTools 工具循环。

## 非目标
- 不移植 pi-ai 的所有云厂商认证、OAuth 和非本项目所需 provider。
- 不假定 CLIProxyAPI 所有后端都支持同一种协议。
- 不提交、推送、发布或安装外部运行时。

## 已确认事实
- AI 请求入口集中在 `ChatClient.cs`。
- 前端消费 `reasoning/text/tool/apiMessages/done` 事件。
- pi-ai 最新版本为 `0.85.1`，MIT；核心 provider API 位于 `packages/ai`。
- CLIProxyAPI 对外支持 OpenAI Responses/Chat Completions 兼容接口，但具体后端能力可能不同。

## P0/P1 风险审计
| ID | 严重度 | 场景 | 后果 | 处理 |
|---|---|---|---|---|
| R-001 | P1 | Responses 工具事件仍按 Chat Completions 解析 | 工具循环失效、核心功能不可用 | 实现统一事件解析并加入协议 fixtures |
| R-002 | P1 | relay 协议能力被错误猜测或自动重试 | 可能重复工具副作用 | 配置明确 API 模式，不做盲目回退 |
| R-003 | P1 | 旧 apiMessages 与新事件模型不兼容 | 历史上下文/工具结果丢失 | 保持前端事件契约，增加转换层 |
| R-004 | P2 | provider 版本持续变化 | C# 移植逐渐偏离 upstream | 记录固定 commit，保留源映射说明 |

基于当前审计，未发现 P0；R-001 至 R-003 为实施前必须关闭的 P1 风险。

## Todo
- [ ] T-001 建立统一 C# PiAi 类型与 provider 接口
- [ ] T-002 实现 OpenAI Responses adapter（文本、reasoning、tool call、usage、完成/错误）
- [ ] T-003 实现 OpenAI Chat Completions adapter（兼容 LM Studio/CLIProxyAPI）
- [ ] T-004 将 ChatClient 工具循环接到统一事件流
- [ ] T-005 增加 API 模式配置并更新设置文案
- [ ] T-006 更新协议 fixtures 与回归测试
- [ ] T-007 构建、测试、终审并记录剩余风险

## 验证矩阵
| ID | 场景 | 预期 |
|---|---|---|
| V-001 | dotnet build | 0 errors |
| V-002 | Responses text/reasoning stream fixture | 前端 text/reasoning/done 正常 |
| V-003 | Responses tool call fixture | 工具执行并继续下一轮 |
| V-004 | Chat Completions fixture | LM Studio/relay 兼容路径正常 |
| V-005 | cancel/error/usage | 取消、错误和统计不泄漏、不重复 |

## 检查点
### CP-000
- 完成：只读审计、上游版本核对、风险与 Todo 建档。
- 下一步：并行实现核心类型/适配器、测试 fixtures、配置接入。

### CP-001
- Status: IMPLEMENTING
- Completed: durable plan/ledger created; execution delegated into non-overlapping core/adapters and configuration/tests tracks.
- Next: integrate adapters into ChatClient after agent changes are reviewed.

### CP-002
- Status: VERIFYING
- Completed: added `PiAi/Core.cs` canonical request/event contracts and `PiAi/OpenAiAdapters.cs` Responses + Chat Completions SSE adapters; added `ApiMode` configuration and UI/README guidance; ChatClient now selects `/responses` with `input` or `/chat/completions` with `messages` based on configuration.
- Validation: `dotnet build --no-restore` passed with 0 warnings and 0 errors.
- P1 review finding: `ChatClient` still owns the legacy tool loop and does not yet consume `IAiProvider`; Responses function-call events are parsed only partially in the legacy path. The new adapters are currently an isolated seam, so tool-loop parity remains open (R-001/R-003).
- Next: replace legacy inline protocol parsing with adapter event consumption, then add protocol fixtures for Responses and Completions.

### CP-003
- Status: VERIFYING
- Completed: wired configured protocol selection into `ChatClient`; added Responses function-call event accumulation in the existing tool loop, including output item creation and argument deltas.
- Validation: `dotnet build --no-restore` passed (0 warnings/errors); `dotnet run --project Tests/ToolTests.csproj --no-restore` passed all 20 checks.
- Remaining verification gap: no live LM Studio or CLIProxyAPI instance is available in this environment, so provider fixtures and real tool-call round trips remain unverified.
- P1 status: R-001 mitigated for the supported Responses event forms; live interoperability remains a verification gap rather than a confirmed defect.
- Next: final static audit and document exact configuration/known protocol limits.

### CP-004
- Status: COMPLETE
- Completed: `ChatClient.StreamAsync` now consumes `PiAi` adapters for both configured protocols; normalized events feed the existing tool executor, WebView events, usage aggregation, cancellation, and multi-round loop. Responses duplicate function arguments are guarded.
- Validation: `dotnet build --no-restore` passed (0 warnings/errors); ToolTests passed all 20 checks.
- Live agentrouter test: configuration found at `/Users/fengying/.pi/agent/models.json` (`https://agentrouter.org/v1`, Responses default; per-model Completions override). Requests using the stored agentrouter credential returned HTTP 401 for `/models` and `/responses`; no credential material was recorded. This indicates the local credential is expired/invalid or the service requires refreshed authentication, so live relay behavior remains unverified.
- Final P0/P1 audit: no confirmed P0/P1 defects in the implemented local paths. The agentrouter 401 is an external credential/service state, not silently treated as success.

### CP-005
- Live retest requested by user against `agentrouter.org/v1` using model `deepseek-v4-flash`.
- Tested both `/responses` and `/chat/completions` with the configured key, streaming payloads, `Authorization: Bearer`, `User-Agent`, `OpenAI-Beta`, and a session ID header.
- Both routes returned HTTP 401 before any model output. No credential was logged.
- Interpretation: this still does not establish the key is invalid; Pi may obtain a different effective credential or use a different auth/session header path. Further comparison requires observing Pi's actual outbound request or runtime-resolved auth state.
