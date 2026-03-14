namespace Taskforce.Agent.Llm

open System
open System.Collections.Generic

[<Sealed>]
type LlmRouter(adapters: IProviderAdapter list, configs: Map<LlmProvider, LlmConfig>) =
    let resolveAdapter provider =
        match adapters |> List.tryFind (fun adapter -> adapter.Supports provider) with
        | Some adapter -> adapter
        | None ->
            LlmError.UnsupportedProvider provider |> LlmError.raiseError
            failwith "unreachable"

    let resolveConfig provider =
        match configs |> Map.tryFind provider with
        | Some cfg -> cfg
        | None ->
            LlmError.MissingProviderConfiguration provider |> LlmError.raiseError
            failwith "unreachable"

    interface ILlmClient with
        member _.Complete request =
            async {
                let adapter = resolveAdapter request.Provider
                let config = resolveConfig request.Provider
                return! adapter.Complete config request
            }

    interface ILlmStreamingClient with
        member _.Stream request =
            let adapter = resolveAdapter request.Provider
            let config = resolveConfig request.Provider
            adapter.Stream config request

[<Sealed>]
type ReasoningModel(client: ILlmClient) =
    interface IReasoningModel with
        member _.Generate request = client.Complete request

        member this.GenerateStructured<'T> request =
            async {
                let! response = (this :> IReasoningModel).Generate request
                match response.Text with
                | None ->
                    LlmError.InvalidProviderResponse(request.Provider, "Structured response did not return text payload")
                    |> LlmError.raiseError
                    return Unchecked.defaultof<'T>
                | Some text ->
                    return System.Text.Json.JsonSerializer.Deserialize<'T>(text)
            }

        member this.RunToolLoop(initialRequest, invokeTool) =
            let rec loop maxIterations (request: LlmRequest) =
                async {
                    if maxIterations <= 0 then
                        return {
                            Text = None
                            ToolCalls = []
                            FinishReason = FinishReason.Unknown "tool_loop_iteration_limit"
                            Usage = None
                            RawProviderPayload = None
                        }
                    else
                        let! response = (this :> IReasoningModel).Generate request
                        match response.ToolCalls with
                        | [] -> return response
                        | toolCalls ->
                            let! toolResults =
                                toolCalls
                                |> List.map invokeTool
                                |> Async.Parallel

                            let toolMessages =
                                toolResults
                                |> Array.toList
                                |> List.map (fun result ->
                                    {
                                        Role = ChatRole.Tool
                                        Name = None
                                        Parts = [ ContentPart.Json result.OutputJson ]
                                    })

                            let nextRequest =
                                {
                                    request with
                                        Messages = request.Messages @ toolMessages
                                }

                            return! loop (maxIterations - 1) nextRequest
                }

            loop 8 initialRequest
