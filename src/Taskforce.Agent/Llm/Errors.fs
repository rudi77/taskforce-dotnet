namespace Taskforce.Agent.Llm

open System

type LlmError =
    | UnsupportedProvider of LlmProvider
    | MissingProviderConfiguration of LlmProvider
    | InvalidRequest of string
    | ProviderHttpError of provider: LlmProvider * statusCode: int * body: string
    | InvalidProviderResponse of provider: LlmProvider * details: string

exception LlmException of LlmError

[<RequireQualifiedAccess>]
module LlmError =
    let toMessage = function
        | UnsupportedProvider provider -> $"No adapter found for provider '{provider}'."
        | MissingProviderConfiguration provider -> $"No configuration available for provider '{provider}'."
        | InvalidRequest details -> $"Invalid LLM request: {details}"
        | ProviderHttpError(provider, statusCode, body) ->
            $"Provider '{provider}' returned HTTP {statusCode}. Body: {body}"
        | InvalidProviderResponse(provider, details) ->
            $"Provider '{provider}' returned an invalid response: {details}"

    let raiseError error =
        raise (LlmException error)
