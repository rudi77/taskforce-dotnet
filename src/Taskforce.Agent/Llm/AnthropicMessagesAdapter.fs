namespace Taskforce.Agent.Llm

open System
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes

[<Sealed>]
type AnthropicMessagesAdapter(httpClient: HttpClient) =
    let tryGetProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let buildPayload (request: LlmRequest) (apiModel: string) =
        let root = JsonObject()
        root["model"] <- JsonValue.Create(apiModel)
        root["max_tokens"] <- JsonValue.Create(request.MaxOutputTokens |> Option.defaultValue 1024)

        request.Temperature |> Option.iter (fun t -> root["temperature"] <- JsonValue.Create(t))

        let systemParts =
            request.Messages
            |> List.filter (fun message -> message.Role = ChatRole.System)
            |> List.collect (fun message -> message.Parts)
            |> List.choose (function
                | ContentPart.Text text -> Some text
                | ContentPart.Json json -> Some json)

        if not systemParts.IsEmpty then
            root["system"] <- JsonValue.Create(String.concat "\n" systemParts)

        let messages = JsonArray()

        request.Messages
        |> List.filter (fun m -> m.Role <> ChatRole.System)
        |> List.iter (fun message ->
            let msg = JsonObject()
            msg["role"] <-
                JsonValue.Create(
                    match message.Role with
                    | ChatRole.User -> "user"
                    | ChatRole.Assistant -> "assistant"
                    | ChatRole.Tool -> "user"
                    | ChatRole.System -> "user"
                )

            let content = JsonArray()
            message.Parts
            |> List.iter (function
                | ContentPart.Text text -> content.Add(Json.textPart text)
                | ContentPart.Json json ->
                    let p = JsonObject()
                    p["type"] <- JsonValue.Create("text")
                    p["text"] <- JsonValue.Create(json)
                    content.Add(p))

            if content.Count = 0 then
                content.Add(Json.textPart "")

            msg["content"] <- content
            messages.Add(msg))

        root["messages"] <- messages

        if not request.Tools.IsEmpty then
            let tools = JsonArray()
            request.Tools
            |> List.iter (fun tool ->
                let mapped = JsonObject()
                mapped["name"] <- JsonValue.Create(tool.Name)
                mapped["description"] <- JsonValue.Create(tool.Description)
                mapped["input_schema"] <- Json.parseObjectOrFail LlmProvider.Anthropic tool.JsonSchema
                tools.Add(mapped))

            root["tools"] <- tools

        Json.serializeNode root

    let parseResponse (rawResponse: string) =
        use doc = JsonDocument.Parse(rawResponse)
        let root = doc.RootElement

        let stopReason =
            match tryGetProperty "stop_reason" root with
            | Some value when value.ValueKind = JsonValueKind.String ->
                match value.GetString() with
                | "end_turn" -> FinishReason.Stop
                | "max_tokens" -> FinishReason.Length
                | "tool_use" -> FinishReason.ToolUse
                | other -> FinishReason.Unknown other
            | _ -> FinishReason.Unknown "missing_stop_reason"

        let usage =
            tryGetProperty "usage" root
            |> Option.map (fun usageEl ->
                let tryInt name =
                    tryGetProperty name usageEl
                    |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.Number then Some(value.GetInt32()) else None)

                let input = tryInt "input_tokens"
                let output = tryInt "output_tokens"
                let total =
                    match input, output with
                    | Some i, Some o -> Some(i + o)
                    | _ -> None

                {
                    InputTokens = input
                    OutputTokens = output
                    TotalTokens = total
                })

        let contentItems =
            match tryGetProperty "content" root with
            | Some content when content.ValueKind = JsonValueKind.Array -> content.EnumerateArray() |> Seq.toList
            | _ -> []

        let textParts, toolCalls =
            contentItems
            |> List.fold
                (fun (texts, calls) item ->
                    match tryGetProperty "type" item with
                    | Some t when t.ValueKind = JsonValueKind.String && t.GetString() = "text" ->
                        match tryGetProperty "text" item with
                        | Some txt -> txt.GetString() :: texts, calls
                        | _ -> texts, calls
                    | Some t when t.ValueKind = JsonValueKind.String && t.GetString() = "tool_use" ->
                        let id =
                            match tryGetProperty "id" item with
                            | Some idEl -> idEl.GetString()
                            | _ -> Guid.NewGuid().ToString("N")

                        let name =
                            match tryGetProperty "name" item with
                            | Some n -> n.GetString()
                            | _ -> "unknown_tool"

                        let args =
                            match tryGetProperty "input" item with
                            | Some input -> input.GetRawText()
                            | _ -> "{}"

                        texts, ({ CallId = id; Name = name; ArgumentsJson = args } :: calls)
                    | _ -> texts, calls)
                ([], [])

        {
            Text =
                textParts
                |> List.rev
                |> String.concat ""
                |> fun s -> if String.IsNullOrWhiteSpace(s) then None else Some s
            ToolCalls = toolCalls |> List.rev
            FinishReason = if toolCalls.IsEmpty then stopReason else FinishReason.ToolUse
            Usage = usage
            RawProviderPayload = Some rawResponse
        }

    interface IProviderAdapter with
        member _.Supports provider = provider = LlmProvider.Anthropic

        member _.Complete config request =
            match config with
            | LlmConfig.AnthropicConfig anthropicConfig ->
                async {
                    let (ModelId modelName) = request.Model
                    let baseUrl = anthropicConfig.BaseUrl |> Option.defaultValue "https://api.anthropic.com"
                    let url = baseUrl.TrimEnd('/') + "/v1/messages"
                    let payload = buildPayload request modelName

                    use req = new HttpRequestMessage(HttpMethod.Post, url)
                    req.Headers.TryAddWithoutValidation("x-api-key", anthropicConfig.ApiKey) |> ignore
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01") |> ignore
                    req.Content <- Json.utf8 payload

                    let! response = httpClient.SendAsync(req) |> Async.AwaitTask
                    let! rawBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                    if not response.IsSuccessStatusCode then
                        LlmError.ProviderHttpError(LlmProvider.Anthropic, int response.StatusCode, rawBody)
                        |> LlmError.raiseError

                    return parseResponse rawBody
                }
            | _ -> LlmError.InvalidRequest("Expected Anthropic config for Anthropic adapter") |> LlmError.raiseError

        member _.Stream _config _request =
            raise (NotSupportedException("Streaming is planned for phase 2."))
