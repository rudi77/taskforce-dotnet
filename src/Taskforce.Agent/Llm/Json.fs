namespace Taskforce.Agent.Llm

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

[<RequireQualifiedAccess>]
module Json =
    let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false)

    let serializeNode (node: JsonNode) = node.ToJsonString(options)

    let parseObjectOrFail provider (raw: string) =
        try
            JsonNode.Parse(raw) :?> JsonObject
        with ex ->
            LlmError.InvalidProviderResponse(provider, ex.Message)
            |> LlmError.raiseError
            failwith "unreachable"

    let textPart text =
        let o = JsonObject()
        o["type"] <- JsonValue.Create("text")
        o["text"] <- JsonValue.Create(text)
        o

    let inputTextPart text =
        let o = JsonObject()
        o["type"] <- JsonValue.Create("input_text")
        o["text"] <- JsonValue.Create(text)
        o

    let messageToResponsesInput (message: ChatMessage) =
        let o = JsonObject()
        let role =
            match message.Role with
            | ChatRole.System -> "system"
            | ChatRole.User -> "user"
            | ChatRole.Assistant -> "assistant"
            | ChatRole.Tool -> "tool"

        o["role"] <- JsonValue.Create(role)
        let content = JsonArray()

        message.Parts
        |> List.iter (function
            | ContentPart.Text txt -> content.Add(inputTextPart txt)
            | ContentPart.Json json ->
                let part = JsonObject()
                part["type"] <- JsonValue.Create("input_text")
                part["text"] <- JsonValue.Create(json)
                content.Add(part))

        if content.Count > 0 then
            o["content"] <- content

        message.Name |> Option.iter (fun n -> o["name"] <- JsonValue.Create(n))
        o

    let toolDefinitionToResponsesTool provider (tool: ToolDefinition) =
        let o = JsonObject()
        o["type"] <- JsonValue.Create("function")
        o["name"] <- JsonValue.Create(tool.Name)
        o["description"] <- JsonValue.Create(tool.Description)
        o["parameters"] <- parseObjectOrFail provider tool.JsonSchema
        o

    let utf8 (text: string) = new StringContent(text, Encoding.UTF8, "application/json")
