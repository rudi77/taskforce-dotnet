namespace Taskforce.Agent.Llm

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes
open System.Collections.Generic

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

    let streamEvents (provider: LlmProvider) (url: string) (apiKey: string) (request: LlmRequest) : IAsyncEnumerable<LlmStreamEvent> =
        let mutable initialized = false
        let mutable completed = false
        let mutable responseMsg: HttpResponseMessage option = None
        let mutable responseStream: Stream option = None
        let mutable reader: StreamReader option = None
        let pending = Queue<LlmStreamEvent>()
        let textBuffer = System.Text.StringBuilder()
        let toolCalls = ResizeArray<ToolCall>()

        let initialize () =
            task {
                if not initialized then
                    initialized <- true
                    let (ModelId modelName) = request.Model
                    let payload =
                        let raw = buildPayload { request with Stream = true } modelName
                        let node = JsonNode.Parse(raw) :?> JsonObject
                        node["stream"] <- JsonValue.Create(true)
                        Json.serializeNode node

                    let req = new HttpRequestMessage(HttpMethod.Post, url)
                    req.Headers.TryAddWithoutValidation("x-api-key", apiKey) |> ignore
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01") |> ignore
                    req.Content <- Json.utf8 payload

                    let! response = httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                    if not response.IsSuccessStatusCode then
                        let! body = response.Content.ReadAsStringAsync()
                        response.Dispose()
                        LlmError.ProviderHttpError(provider, int response.StatusCode, body) |> LlmError.raiseError

                    let! stream = response.Content.ReadAsStreamAsync()
                    responseMsg <- Some response
                    responseStream <- Some stream
                    reader <- Some(new StreamReader(stream))
            }

        let disposeAll () =
            reader |> Option.iter (fun r -> r.Dispose())
            responseStream |> Option.iter (fun s -> s.Dispose())
            responseMsg |> Option.iter (fun r -> r.Dispose())
            reader <- None
            responseStream <- None
            responseMsg <- None

        let rec readNextEvent (r: StreamReader) (dataLines: ResizeArray<string>) =
            task {
                let! line = r.ReadLineAsync()
                if isNull line then
                    if dataLines.Count = 0 then
                        return None
                    else
                        return Some(String.concat "\n" dataLines)
                elif String.IsNullOrWhiteSpace(line) then
                    if dataLines.Count = 0 then
                        return! readNextEvent r dataLines
                    else
                        return Some(String.concat "\n" dataLines)
                elif line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) then
                    dataLines.Add(line.Substring(5).TrimStart())
                    return! readNextEvent r dataLines
                else
                    return! readNextEvent r dataLines
            }

        let parseEvent (raw: string) =
            use doc = JsonDocument.Parse(raw)
            let root = doc.RootElement

            let getString name =
                let mutable el = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty(name, &el) && el.ValueKind = JsonValueKind.String then Some(el.GetString()) else None

            match getString "type" with
            | Some "content_block_delta" ->
                let mutable delta = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("delta", &delta) then
                    let mutable deltaType = Unchecked.defaultof<JsonElement>
                    if delta.TryGetProperty("type", &deltaType) && deltaType.ValueKind = JsonValueKind.String then
                        match deltaType.GetString() with
                        | "text_delta" ->
                            let mutable text = Unchecked.defaultof<JsonElement>
                            if delta.TryGetProperty("text", &text) && text.ValueKind = JsonValueKind.String then
                                let value = text.GetString()
                                textBuffer.Append(value) |> ignore
                                pending.Enqueue(LlmStreamEvent.TextDelta value)
                        | "input_json_delta" ->
                            let mutable partial = Unchecked.defaultof<JsonElement>
                            if delta.TryGetProperty("partial_json", &partial) && partial.ValueKind = JsonValueKind.String then
                                let mutable idxEl = Unchecked.defaultof<JsonElement>
                                let idx =
                                    if root.TryGetProperty("index", &idxEl) && idxEl.ValueKind = JsonValueKind.Number then idxEl.GetInt32()
                                    else 0

                                if idx < toolCalls.Count then
                                    let call = toolCalls[idx]
                                    pending.Enqueue(LlmStreamEvent.ToolCallDelta(call.CallId, partial.GetString()))
                        | _ -> ()
            | Some "content_block_start" ->
                let mutable block = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("content_block", &block) then
                    let mutable blockType = Unchecked.defaultof<JsonElement>
                    if block.TryGetProperty("type", &blockType) && blockType.ValueKind = JsonValueKind.String && blockType.GetString() = "tool_use" then
                        let id =
                            let mutable idEl = Unchecked.defaultof<JsonElement>
                            if block.TryGetProperty("id", &idEl) && idEl.ValueKind = JsonValueKind.String then idEl.GetString()
                            else Guid.NewGuid().ToString("N")
                        let name =
                            let mutable nameEl = Unchecked.defaultof<JsonElement>
                            if block.TryGetProperty("name", &nameEl) && nameEl.ValueKind = JsonValueKind.String then nameEl.GetString()
                            else "unknown_tool"
                        let call = { CallId = id; Name = name; ArgumentsJson = "{}" }
                        toolCalls.Add(call)
                        pending.Enqueue(LlmStreamEvent.ToolCallStarted call)
            | Some "message_stop" ->
                let final =
                    {
                        Text =
                            let txt = textBuffer.ToString()
                            if String.IsNullOrWhiteSpace(txt) then None else Some txt
                        ToolCalls = toolCalls |> Seq.toList
                        FinishReason = if toolCalls.Count = 0 then FinishReason.Stop else FinishReason.ToolUse
                        Usage = None
                        RawProviderPayload = None
                    }
                pending.Enqueue(LlmStreamEvent.Completed final)
                completed <- true
            | _ -> ()

        { new IAsyncEnumerable<LlmStreamEvent> with
            member _.GetAsyncEnumerator(_ct: CancellationToken) =
                { new IAsyncEnumerator<LlmStreamEvent> with
                    member val Current = LlmStreamEvent.Failed "stream_not_started" with get, set

                    member self.MoveNextAsync() =
                        ValueTask<bool>(
                            task {
                                do! initialize ()

                                if pending.Count > 0 then
                                    self.Current <- pending.Dequeue()
                                    return true
                                elif completed then
                                    return false
                                else
                                    match reader with
                                    | None ->
                                        completed <- true
                                        return false
                                    | Some r ->
                                        let! maybeEvent = readNextEvent r (ResizeArray())
                                        match maybeEvent with
                                        | None ->
                                            completed <- true
                                            return false
                                        | Some raw ->
                                            try
                                                parseEvent raw
                                                if pending.Count = 0 then
                                                    return! self.MoveNextAsync().AsTask()
                                                else
                                                    self.Current <- pending.Dequeue()
                                                    return true
                                            with ex ->
                                                self.Current <- LlmStreamEvent.Failed ex.Message
                                                completed <- true
                                                return true
                            }
                        )

                    member _.DisposeAsync() =
                        completed <- true
                        disposeAll ()
                        ValueTask(Task.CompletedTask)
                } }

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

        member _.Stream config request =
            match config with
            | LlmConfig.AnthropicConfig anthropicConfig ->
                let baseUrl = anthropicConfig.BaseUrl |> Option.defaultValue "https://api.anthropic.com"
                let url = baseUrl.TrimEnd('/') + "/v1/messages"
                streamEvents LlmProvider.Anthropic url anthropicConfig.ApiKey request
            | _ -> LlmError.InvalidRequest("Expected Anthropic config for Anthropic adapter") |> LlmError.raiseError
