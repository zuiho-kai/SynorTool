# pi-ai C# integration

This document records the completed integration of the pi-ai inspired provider contracts used by SynorTool. It is an implementation note, not a work plan.

## Current implementation

- `PiAi/Core.cs` defines normalized request and streaming event contracts.
- `PiAi/OpenAiAdapters.cs` parses OpenAI Responses and Chat Completions SSE streams.
- `ChatClient.cs` selects the configured API mode and feeds text, reasoning, tool-call, usage, cancellation, and error events into the existing multi-round tool loop.
- `AiSettings.cs` and the Web settings page expose API mode and reasoning controls.

The configured base URL is OpenAI-compatible and excludes the endpoint path. Responses uses `/responses`; Chat Completions uses `/chat/completions`.

## Verification

```sh
dotnet build --no-restore
dotnet run --project Tests/ToolTests.csproj --no-restore
```

Both checks pass in the current tree. Live third-party relay behavior remains environment-dependent; a prior agentrouter check returned HTTP 401 before model output, indicating that its credential or service access must be verified externally.

## Reference

Behavior and prompt wording are informed by pi-mono commit `71dca871bc80b6bc97be37f0ca3189399d651fff`. SynorTool contains an independent C# implementation and does not embed the Pi runtime.
