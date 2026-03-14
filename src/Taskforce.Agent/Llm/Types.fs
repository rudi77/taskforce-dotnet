namespace Taskforce.Agent.Llm

open System

[<RequireQualifiedAccess>]
type LlmProvider =
    | OpenAI
    | AzureOpenAI
    | Anthropic

[<Struct>]
type ModelId = ModelId of string

[<RequireQualifiedAccess>]
type ChatRole =
    | System
    | User
    | Assistant
    | Tool

[<RequireQualifiedAccess>]
type ContentPart =
    | Text of string
    | Json of string

type ChatMessage = {
    Role: ChatRole
    Parts: ContentPart list
    Name: string option
}

type ToolDefinition = {
    Name: string
    Description: string
    JsonSchema: string
}

type ToolCall = {
    CallId: string
    Name: string
    ArgumentsJson: string
}

type ToolResult = {
    CallId: string
    OutputJson: string
}

[<RequireQualifiedAccess>]
type ResponseFormat =
    | TextResponse
    | JsonResponse of schemaName: string * schemaJson: string

type LlmRequest = {
    Provider: LlmProvider
    Model: ModelId
    Messages: ChatMessage list
    Tools: ToolDefinition list
    Temperature: float option
    MaxOutputTokens: int option
    ResponseFormat: ResponseFormat
    Stream: bool
}

[<RequireQualifiedAccess>]
type FinishReason =
    | Stop
    | ToolUse
    | Length
    | ContentFiltered
    | Unknown of string

type LlmUsage = {
    InputTokens: int option
    OutputTokens: int option
    TotalTokens: int option
}

type LlmResponse = {
    Text: string option
    ToolCalls: ToolCall list
    FinishReason: FinishReason
    Usage: LlmUsage option
    RawProviderPayload: string option
}

type OpenAIConfig = {
    ApiKey: string
    BaseUrl: string
}

type AzureOpenAIConfig = {
    Endpoint: string
    Deployment: string
    ApiKey: string option
    UseEntraId: bool
    EntraToken: string option
}

type AnthropicConfig = {
    ApiKey: string
    BaseUrl: string option
}

[<RequireQualifiedAccess>]
type LlmConfig =
    | OpenAIConfig of OpenAIConfig
    | AzureOpenAIConfig of AzureOpenAIConfig
    | AnthropicConfig of AnthropicConfig

[<RequireQualifiedAccess>]
type LlmStreamEvent =
    | TextDelta of string
    | ToolCallStarted of ToolCall
    | ToolCallDelta of callId: string * partialArguments: string
    | Completed of LlmResponse
    | Failed of string
