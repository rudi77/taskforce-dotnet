namespace Taskforce.Agent.Llm

open System
open Taskforce.Agent

[<CLIMutable>]
type TaskAnalysisResponse = {
    complexity: string list
    intent: string
}

[<RequireQualifiedAccess>]
module LlmTaskAnalyzer =
    let private toComplexity = function
        | null -> None
        | value ->
            match value.Trim().ToLowerInvariant() with
            | "simple" -> Some TaskComplexity.Simple
            | "multistep" -> Some TaskComplexity.MultiStep
            | "openended" -> Some TaskComplexity.OpenEnded
            | "requirestools" -> Some TaskComplexity.RequiresTools
            | "requiresvalidation" -> Some TaskComplexity.RequiresValidation
            | _ -> None

    let private toIntent = function
        | null -> None
        | value ->
            match value.Trim().ToLowerInvariant() with
            | "answernow" -> Some AgentIntent.AnswerNow
            | "planfirst" -> Some AgentIntent.PlanFirst
            | "continueexistingplan" -> Some AgentIntent.ContinueExistingPlan
            | _ -> None

    let create (model: IReasoningModel) (provider: LlmProvider) (modelId: string) =
        { new ITaskAnalyzer with
            member _.Classify state input =
                async {
                    let prompt =
                        $"""
Klassifiziere die folgende User-Nachricht für einen Agenten.

Antwortregeln:
- Gib nur JSON zurück.
- complexity ist ein Array mit 1..3 Einträgen aus:
  ["Simple","MultiStep","OpenEnded","RequiresTools","RequiresValidation"]
- intent ist genau einer aus:
  ["AnswerNow","PlanFirst","ContinueExistingPlan"]

Kontext:
- current_plan_exists: {state.Working.CurrentPlan |> List.isEmpty |> not}
- user_input: {input}
"""

                    let request =
                        {
                            Provider = provider
                            Model = ModelId modelId
                            Messages =
                                [ { Role = ChatRole.System
                                    Parts = [ ContentPart.Text "You are a strict classifier. Return valid JSON only." ]
                                    Name = None }
                                  { Role = ChatRole.User
                                    Parts = [ ContentPart.Text prompt ]
                                    Name = None } ]
                            Tools = []
                            Temperature = Some 0.0
                            MaxOutputTokens = Some 180
                            ResponseFormat =
                                ResponseFormat.JsonResponse(
                                    "task_analysis",
                                    """
{
  "type": "object",
  "properties": {
    "complexity": {
      "type": "array",
      "items": {
        "type": "string",
        "enum": ["Simple","MultiStep","OpenEnded","RequiresTools","RequiresValidation"]
      },
      "minItems": 1,
      "maxItems": 3
    },
    "intent": {
      "type": "string",
      "enum": ["AnswerNow","PlanFirst","ContinueExistingPlan"]
    }
  },
  "required": ["complexity", "intent"],
  "additionalProperties": false
}
"""
                                )
                            Stream = false
                        }

                    let! parsed = model.GenerateStructured<TaskAnalysisResponse> request

                    let complexity =
                        parsed.complexity
                        |> List.ofSeq
                        |> List.choose toComplexity
                        |> function
                            | [] -> [ TaskComplexity.Simple ]
                            | xs -> xs |> List.distinct

                    let intent =
                        match toIntent parsed.intent with
                        | Some i -> i
                        | None ->
                            if state.Working.CurrentPlan |> List.isEmpty |> not then
                                AgentIntent.ContinueExistingPlan
                            elif complexity |> List.exists (fun c -> c <> TaskComplexity.Simple) then
                                AgentIntent.PlanFirst
                            else
                                AgentIntent.AnswerNow

                    return complexity, intent
                } }
