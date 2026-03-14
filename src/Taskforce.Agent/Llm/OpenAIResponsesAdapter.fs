namespace Taskforce.Agent.Llm

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes
open System.Collections.Generic

[<AbstractClass>]
type ResponsesAdapterBase(httpClient: HttpClient) =
    let tryGetProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    member _.BuildPayload(provider: LlmProvider, request: LlmRequest, modelName: string) =
        let root = JsonObject()
        root["model"] <- JsonValue.Create(modelName)

        let input = JsonArray()
        request.Messages
        |> List.map Json.messageToResponsesInput
        |> List.iter input.Add

        root["input"] <- input

        if request.Stream then
            root["stream"] <- JsonValue.Create(true)

        match request.Temperature with
        | Some temperature -> root["temperature"] <- JsonValue.Create(temperature)
        | None -> ()

        match request.MaxOutputTokens with
        | Some value -> root["max_output_tokens"] <- JsonValue.Create(value)
        | None -> ()

        let tools = JsonArray()
        request.Tools
        |> List.iter (fun tool ->
            let mapped = Json.toolDefinitionToResponsesTool provider tool
            tools.Add(mapped))

        if tools.Count > 0 then
            root["tools"] <- tools

        match request.ResponseFormat with
        | ResponseFormat.TextResponse -> ()
        | ResponseFormat.JsonResponse(schemaName, schemaJson) ->
            let text = JsonObject()
            text["format"] <-
                JsonObject(
                    [ KeyValuePair("type", JsonValue.Create("json_schema") :> JsonNode)
                      KeyValuePair(
                          "json_schema",
                          JsonObject(
                              [ KeyValuePair("name", JsonValue.Create(schemaName) :> JsonNode)
                                KeyValuePair("schema", Json.parseObjectOrFail provider schemaJson :> JsonNode)
                                KeyValuePair("strict", JsonValue.Create(true) :> JsonNode)
                              ]
                          ) :> JsonNode
                      ) ]
                )

            root["text"] <- text

        Json.serializeNode root

    member this.ParseResponsesStreamEvent(provider: LlmProvider, eventJson: string) : LlmStreamEvent list * LlmResponse option =
        use doc = JsonDocument.Parse(eventJson)
        let root = doc.RootElement

        let eventType =
            let mutable t = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("type", &t) && t.ValueKind = JsonValueKind.String then t.GetString()
            else ""

        match eventType with
        | "response.output_text.delta" ->
            let mutable delta = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("delta", &delta) && delta.ValueKind = JsonValueKind.String then
                [ LlmStreamEvent.TextDelta(delta.GetString()) ], None
            else
                [], None
        | "response.output_item.added" ->
            let mutable item = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("item", &item) then
                let mutable t = Unchecked.defaultof<JsonElement>
                let isFunctionCall = item.TryGetProperty("type", &t) && t.ValueKind = JsonValueKind.String && t.GetString() = "function_call"

                if isFunctionCall then
                    let callId =
                        let mutable c = Unchecked.defaultof<JsonElement>
                        if item.TryGetProperty("call_id", &c) && c.ValueKind = JsonValueKind.String then c.GetString()
                        else Guid.NewGuid().ToString("N")
                    let name =
                        let mutable n = Unchecked.defaultof<JsonElement>
                        if item.TryGetProperty("name", &n) && n.ValueKind = JsonValueKind.String then n.GetString()
                        else "unknown_tool"

                    let call = { CallId = callId; Name = name; ArgumentsJson = "{}" }
                    [ LlmStreamEvent.ToolCallStarted call ], None
                else
                    [], None
            else
                [], None
        | "response.function_call_arguments.delta" ->
            let callId =
                let mutable id = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("call_id", &id) && id.ValueKind = JsonValueKind.String then id.GetString()
                else ""

            let delta =
                let mutable d = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("delta", &d) && d.ValueKind = JsonValueKind.String then d.GetString()
                else ""

            if String.IsNullOrWhiteSpace(callId) || String.IsNullOrEmpty(delta) then
                [], None
            else
                [ LlmStreamEvent.ToolCallDelta(callId, delta) ], None
        | "response.completed" ->
            let mutable response = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("response", &response) then
                let parsed = this.ParseResponse(provider, response.GetRawText())
                [ LlmStreamEvent.Completed parsed ], Some parsed
            else
                [], None
        | _ ->
            [], None

    member this.SendResponsesStreamRequest(provider, url: string, authHeaders: (HttpRequestMessage -> unit), request: LlmRequest, modelName: string) : IAsyncEnumerable<LlmStreamEvent> =
        let streamRequest = { request with Stream = true }
        let payload = this.BuildPayload(provider, streamRequest, modelName)

        let mutable initialized = false
        let mutable completed = false
        let mutable responseMsg: HttpResponseMessage option = None
        let mutable responseStream: Stream option = None
        let mutable reader: StreamReader option = None
        let pending = Queue<LlmStreamEvent>()
        let mutable lastResponse: LlmResponse option = None

        let initialize () =
            task {
                if not initialized then
                    initialized <- true
                    let req = new HttpRequestMessage(HttpMethod.Post, url)
                    req.Content <- Json.utf8 payload
                    authHeaders req

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

                                            if lastResponse.IsNone then
                                                let fallback =
                                                    {
                                                        Text = None
                                                        ToolCalls = []
                                                        FinishReason = FinishReason.Unknown "stream_ended_without_completed_event"
                                                        Usage = None
                                                        RawProviderPayload = None
                                                    }
                                                self.Current <- LlmStreamEvent.Completed fallback
                                                lastResponse <- Some fallback
                                                return true

                                            return false
                                        | Some raw when raw = "[DONE]" ->
                                            completed <- true
                                            return false
                                        | Some raw ->
                                            try
                                                let events, parsedFinal = this.ParseResponsesStreamEvent(provider, raw)
                                                parsedFinal |> Option.iter (fun p -> lastResponse <- Some p)
                                                events |> List.iter pending.Enqueue

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

    member _.ParseResponse(provider: LlmProvider, rawResponse: string) =
        use doc = JsonDocument.Parse(rawResponse)
        let root = doc.RootElement

        let usage =
            tryGetProperty "usage" root
            |> Option.map (fun usageEl ->
                let tryInt name =
                    tryGetProperty name usageEl
                    |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.Number then Some(value.GetInt32()) else None)

                {
                    InputTokens = tryInt "input_tokens"
                    OutputTokens = tryInt "output_tokens"
                    TotalTokens = tryInt "total_tokens"
                })

        let finishReason =
            match tryGetProperty "status" root with
            | Some statusEl when statusEl.ValueKind = JsonValueKind.String ->
                match statusEl.GetString() with
                | "completed" -> FinishReason.Stop
                | "incomplete" -> FinishReason.Length
                | value -> FinishReason.Unknown value
            | _ -> FinishReason.Unknown "missing_status"

        let outputs =
            match tryGetProperty "output" root with
            | Some outputEl when outputEl.ValueKind = JsonValueKind.Array -> outputEl.EnumerateArray() |> Seq.toList
            | _ -> []

        let toolCalls =
            outputs
            |> List.collect (fun item ->
                match tryGetProperty "type" item with
                | Some t when t.ValueKind = JsonValueKind.String && t.GetString() = "function_call" ->
                    let callId =
                        match tryGetProperty "call_id" item with
                        | Some id -> id.GetString()
                        | _ -> Guid.NewGuid().ToString("N")

                    let name =
                        match tryGetProperty "name" item with
                        | Some n -> n.GetString()
                        | _ -> "unknown_tool"

                    let args =
                        match tryGetProperty "arguments" item with
                        | Some a when a.ValueKind = JsonValueKind.String -> a.GetString()
                        | Some a -> a.GetRawText()
                        | _ -> "{}"

                    [ { CallId = callId; Name = name; ArgumentsJson = args } ]
                | _ -> [])

        let textParts =
            outputs
            |> List.collect (fun item ->
                match tryGetProperty "type" item with
                | Some t when t.ValueKind = JsonValueKind.String && t.GetString() = "message" ->
                    match tryGetProperty "content" item with
                    | Some c when c.ValueKind = JsonValueKind.Array ->
                        c.EnumerateArray()
                        |> Seq.choose (fun contentItem ->
                            match tryGetProperty "type" contentItem with
                            | Some ct when ct.ValueKind = JsonValueKind.String && ct.GetString() = "output_text" ->
                                tryGetProperty "text" contentItem |> Option.map (fun x -> x.GetString())
                            | _ -> None)
                        |> Seq.toList
                    | _ -> []
                | _ -> [])

        {
            Text =
                match textParts with
                | [] -> None
                | xs -> Some(String.concat "" xs)
            ToolCalls = toolCalls
            FinishReason = if toolCalls.IsEmpty then finishReason else FinishReason.ToolUse
            Usage = usage
            RawProviderPayload = Some rawResponse
        }

    member this.SendResponsesRequest(provider, url: string, authHeaders: (HttpRequestMessage -> unit), request: LlmRequest, modelName: string) =
        async {
            let payload = this.BuildPayload(provider, request, modelName)
            use req = new HttpRequestMessage(HttpMethod.Post, url)
            req.Content <- Json.utf8 payload
            authHeaders req

            let! response = httpClient.SendAsync(req) |> Async.AwaitTask
            let! rawBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            if not response.IsSuccessStatusCode then
                LlmError.ProviderHttpError(provider, int response.StatusCode, rawBody)
                |> LlmError.raiseError

            return this.ParseResponse(provider, rawBody)
        }

[<Sealed>]
type OpenAIResponsesAdapter(httpClient: HttpClient) =
    inherit ResponsesAdapterBase(httpClient)

    interface IProviderAdapter with
        member _.Supports provider = provider = LlmProvider.OpenAI

        member this.Complete config request =
            match config with
            | LlmConfig.OpenAIConfig openAiConfig ->
                let (ModelId modelName) = request.Model
                let baseUrl =
                    if String.IsNullOrWhiteSpace(openAiConfig.BaseUrl) then "https://api.openai.com/v1"
                    else openAiConfig.BaseUrl.TrimEnd('/')

                let url = baseUrl + "/responses"
                this.SendResponsesRequest(
                    LlmProvider.OpenAI,
                    url,
                    (fun req -> req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {openAiConfig.ApiKey}") |> ignore),
                    request,
                    modelName
                )
            | _ -> LlmError.InvalidRequest("Expected OpenAI config for OpenAI adapter") |> LlmError.raiseError

        member this.Stream config request =
            match config with
            | LlmConfig.OpenAIConfig openAiConfig ->
                let (ModelId modelName) = request.Model
                let baseUrl =
                    if String.IsNullOrWhiteSpace(openAiConfig.BaseUrl) then "https://api.openai.com/v1"
                    else openAiConfig.BaseUrl.TrimEnd('/')

                let url = baseUrl + "/responses"
                this.SendResponsesStreamRequest(
                    LlmProvider.OpenAI,
                    url,
                    (fun req -> req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {openAiConfig.ApiKey}") |> ignore),
                    request,
                    modelName
                )
            | _ -> LlmError.InvalidRequest("Expected OpenAI config for OpenAI adapter") |> LlmError.raiseError
