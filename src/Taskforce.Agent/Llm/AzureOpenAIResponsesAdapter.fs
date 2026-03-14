namespace Taskforce.Agent.Llm

open System
open System.Net.Http

[<Sealed>]
type AzureOpenAIResponsesAdapter(httpClient: HttpClient) =
    inherit ResponsesAdapterBase(httpClient)

    interface IProviderAdapter with
        member _.Supports provider = provider = LlmProvider.AzureOpenAI

        member this.Complete config request =
            match config with
            | LlmConfig.AzureOpenAIConfig azureConfig ->
                let endpoint = azureConfig.Endpoint.TrimEnd('/')
                let url = endpoint + "/openai/v1/responses"

                let authHeaders (req: HttpRequestMessage) =
                    match azureConfig.ApiKey, azureConfig.UseEntraId, azureConfig.EntraToken with
                    | Some apiKey, _, _ ->
                        req.Headers.TryAddWithoutValidation("api-key", apiKey) |> ignore
                    | None, true, Some bearer ->
                        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}") |> ignore
                    | _ ->
                        LlmError.InvalidRequest("AzureOpenAI requires either ApiKey or EntraToken when UseEntraId=true")
                        |> LlmError.raiseError

                // Azure model input should reference deployment name.
                this.SendResponsesRequest(LlmProvider.AzureOpenAI, url, authHeaders, request, azureConfig.Deployment)
            | _ -> LlmError.InvalidRequest("Expected Azure OpenAI config for Azure OpenAI adapter") |> LlmError.raiseError

        member this.Stream config request =
            match config with
            | LlmConfig.AzureOpenAIConfig azureConfig ->
                let endpoint = azureConfig.Endpoint.TrimEnd('/')
                let url = endpoint + "/openai/v1/responses"

                let authHeaders (req: HttpRequestMessage) =
                    match azureConfig.ApiKey, azureConfig.UseEntraId, azureConfig.EntraToken with
                    | Some apiKey, _, _ ->
                        req.Headers.TryAddWithoutValidation("api-key", apiKey) |> ignore
                    | None, true, Some bearer ->
                        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}") |> ignore
                    | _ ->
                        LlmError.InvalidRequest("AzureOpenAI requires either ApiKey or EntraToken when UseEntraId=true")
                        |> LlmError.raiseError

                this.SendResponsesStreamRequest(LlmProvider.AzureOpenAI, url, authHeaders, request, azureConfig.Deployment)
            | _ -> LlmError.InvalidRequest("Expected Azure OpenAI config for Azure OpenAI adapter") |> LlmError.raiseError
