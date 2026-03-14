namespace Taskforce.Agent.Llm

open System.Collections.Generic

[<Interface>]
type ILlmClient =
    abstract Complete: LlmRequest -> Async<LlmResponse>

[<Interface>]
type ILlmStreamingClient =
    abstract Stream: LlmRequest -> IAsyncEnumerable<LlmStreamEvent>

[<Interface>]
type IReasoningModel =
    abstract Generate: LlmRequest -> Async<LlmResponse>
    abstract GenerateStructured<'T>: LlmRequest -> Async<'T>
    abstract RunToolLoop: initialRequest: LlmRequest * invokeTool: (ToolCall -> Async<ToolResult>) -> Async<LlmResponse>

[<Interface>]
type IProviderAdapter =
    abstract Supports: LlmProvider -> bool
    abstract Complete: LlmConfig -> LlmRequest -> Async<LlmResponse>
    abstract Stream: LlmConfig -> LlmRequest -> IAsyncEnumerable<LlmStreamEvent>
