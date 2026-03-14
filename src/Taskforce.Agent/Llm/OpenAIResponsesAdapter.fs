namespace Taskforce.Agent.Llm

open System
open System.Net.Http
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

        member _.Stream _config _request =
            raise (NotSupportedException("Streaming is planned for phase 2."))
