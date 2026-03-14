namespace Taskforce.Agent

open System
open System.Collections.Generic

[<Struct>]
type AgentId = AgentId of string

type AgentKind =
    | GeneralAssistant
    | Planner
    | Researcher
    | Judge
    | ToolWorker

type ExecutionMode =
    | Reactive
    | Planned
    | Replanning

type PlanStepStatus =
    | Pending
    | InProgress
    | Done
    | Failed of string
    | Skipped

type PlanStep = {
    StepId: string
    Goal: string
    Status: PlanStepStatus
    DependsOn: string list
}

type WorkingMemory = {
    CurrentGoal: string option
    CurrentPlan: PlanStep list
    LastUserMessages: string list
    IntermediateFacts: string list
}

type EpisodicMemoryItem = {
    MemoryId: string
    Content: string
    Relevance: float
    Tags: string list
    CreatedAtUtc: DateTime
}

type AgentState = {
    AgentId: AgentId
    Kind: AgentKind
    Mode: ExecutionMode
    Working: WorkingMemory
    EpisodicMemory: EpisodicMemoryItem list
}

type TaskComplexity =
    | Simple
    | MultiStep
    | OpenEnded
    | RequiresTools
    | RequiresValidation

type AgentIntent =
    | AnswerNow
    | PlanFirst
    | ContinueExistingPlan

type ToolCall = {
    ToolName: string
    Input: string
}

type ToolResult = {
    ToolName: string
    Success: bool
    Output: string
    Error: string option
}

type ReplanReason =
    | MissingInformation of string
    | ToolFailure of string
    | NewUserInput of string
    | ContradictionDetected of string
    | BetterPathFound of string

type ExecutionPlan = {
    PlanId: string
    Goal: string
    Steps: PlanStep list
    CreatedAtUtc: DateTime
}

type PlannerCommand =
    | BuildInitialPlan of goal: string
    | RevisePlan of currentPlan: ExecutionPlan * reason: ReplanReason

type MemoryCandidate = {
    Content: string
    Tags: string list
    EstimatedUsefulness: float
    EstimatedLongevity: float
}

type AgentDecision =
    | DirectResponse of string
    | CreatePlan of string
    | UpdatePlan of reason: string
    | ExecuteStep of stepId: string
    | CallTool of ToolCall
    | StoreMemory of content: string * tags: string list
    | ReplyAndStoreMemory of reply: string * memory: string * tags: string list
    | Escalate of reason: string
    | NoOp

type AgentInput =
    | UserMessage of string
    | ToolReturned of ToolResult
    | StepCompleted of stepId: string * result: string
    | StepFailed of stepId: string * reason: string

type AgentOutput =
    | Reply of string
    | ToolRequest of ToolCall
    | PlanCreated of ExecutionPlan
    | PlanUpdated of ExecutionPlan
    | MemoryWritten of string
    | Idle

type AgentEffect =
    | SendReply of string
    | DispatchTool of ToolCall
    | SaveMemory of MemoryCandidate
    | ReplacePlan of ExecutionPlan
    | MarkStepDone of string
    | MarkStepFailed of string * string
    | RequestReplan of ReplanReason

type AgentCapabilities = {
    CanAnswerDirectly: bool
    CanPlan: bool
    CanReplan: bool
    CanUseTools: bool
    CanRemember: bool
}

[<RequireQualifiedAccess>]
module AgentCapabilities =
    let defaultCapabilities = {
        CanAnswerDirectly = true
        CanPlan = true
        CanReplan = true
        CanUseTools = true
        CanRemember = true
    }

type ITaskAnalyzer =
    abstract Classify: AgentState -> string -> Async<TaskComplexity list * AgentIntent>

type IPlanner =
    abstract CreatePlan: AgentState -> string -> Async<ExecutionPlan>
    abstract UpdatePlan: AgentState -> ReplanReason -> Async<ExecutionPlan>

type IReasoner =
    abstract Decide: AgentState -> AgentInput -> Async<AgentDecision list>

type IMemoryPolicy =
    abstract ShouldStore: AgentState -> MemoryCandidate -> bool

type IMemoryStore =
    abstract Remember: AgentId -> MemoryCandidate -> Async<unit>
    abstract Recall: AgentId -> query: string -> Async<EpisodicMemoryItem list>

type IToolInvoker =
    abstract Invoke: ToolCall -> Async<ToolResult>

type IAgentKernel =
    abstract Step: AgentState -> AgentInput -> Async<AgentState * AgentEffect list>

[<RequireQualifiedAccess>]
module MemoryPolicies =
    let thresholdPolicy usefulness longevity =
        { new IMemoryPolicy with
            member _.ShouldStore _state candidate =
                candidate.EstimatedUsefulness >= usefulness
                && candidate.EstimatedLongevity >= longevity }

[<RequireQualifiedAccess>]
module TaskAnalyzers =
    let llmBased (classifyWithLlm: AgentState -> string -> Async<TaskComplexity list * AgentIntent>) =
        { new ITaskAnalyzer with
            member _.Classify state input = classifyWithLlm state input }

    let heuristicFallback () =
        { new ITaskAnalyzer with
            member _.Classify state input =
                async {
                    let lower = input.ToLowerInvariant()
                    let containsAny (needles: string list) = needles |> List.exists lower.Contains
                    let requiresTools = containsAny [ "analys"; "datei"; "dokument"; "tool"; "fetch" ]
                    let requiresValidation = containsAny [ "vergleich"; "validier"; "prüf" ]
                    let multiStep = containsAny [ "schritt"; "plan"; "strategie" ]
                    let openEnded = input.Length > 220

                    let complexity =
                        [ if requiresTools then RequiresTools
                          if requiresValidation then RequiresValidation
                          if multiStep then MultiStep
                          if openEnded then OpenEnded ]
                        |> function
                            | [] -> [ Simple ]
                            | xs -> xs

                    let intent =
                        if state.Working.CurrentPlan |> List.isEmpty |> not then
                            ContinueExistingPlan
                        elif complexity |> List.exists (fun c -> c <> Simple) then
                            PlanFirst
                        else
                            AnswerNow

                    return complexity, intent
                } }

[<RequireQualifiedAccess>]
module Planning =
    let updateWorkingPlan (plan: ExecutionPlan) (state: AgentState) =
        {
            state with
                Mode = Planned
                Working =
                    { state.Working with
                        CurrentGoal = Some plan.Goal
                        CurrentPlan = plan.Steps }
        }

    let markStepStatus stepId newStatus state =
        let nextPlan =
            state.Working.CurrentPlan
            |> List.map (fun step ->
                if String.Equals(step.StepId, stepId, StringComparison.OrdinalIgnoreCase) then
                    { step with Status = newStatus }
                else
                    step)

        { state with Working = { state.Working with CurrentPlan = nextPlan } }

type AgentKernel(
    capabilities: AgentCapabilities,
    analyzer: ITaskAnalyzer,
    planner: IPlanner,
    reasoner: IReasoner,
    memoryPolicy: IMemoryPolicy,
    memoryStore: IMemoryStore
    ) =

    let toMemoryCandidate content tags =
        {
            Content = content
            Tags = tags
            EstimatedUsefulness = 0.8
            EstimatedLongevity = 0.6
        }

    let appendUserMessage text state =
        {
            state with
                Working =
                    {
                        state.Working with
                            LastUserMessages =
                                (text :: state.Working.LastUserMessages)
                                |> List.truncate 12
                    }
        }

    let enrichStateWithMemoryRecall query state =
        async {
            if not capabilities.CanRemember || String.IsNullOrWhiteSpace(query) then
                return state
            else
                let! recalled = memoryStore.Recall state.AgentId query

                let recalledFacts =
                    recalled
                    |> List.sortByDescending (fun item -> item.Relevance)
                    |> List.truncate 5
                    |> List.map (fun item -> $"[memory:{item.MemoryId}] {item.Content}")

                if recalledFacts.IsEmpty then
                    return state
                else
                    let nextFacts =
                        recalledFacts @ state.Working.IntermediateFacts
                        |> List.distinct
                        |> List.truncate 50

                    return { state with Working = { state.Working with IntermediateFacts = nextFacts } }
        }

    let decisionPriority = function
        | UpdatePlan _
        | CreatePlan _ -> 0
        | ExecuteStep _ -> 1
        | CallTool _ -> 2
        | DirectResponse _
        | Escalate _ -> 3
        | ReplyAndStoreMemory _ -> 4
        | StoreMemory _ -> 5
        | NoOp -> 6

    let handleReasonerDecision state decision =
        async {
            match decision with
            | DirectResponse msg when capabilities.CanAnswerDirectly ->
                return state, [ SendReply msg ]
            | CreatePlan goal when capabilities.CanPlan ->
                let! plan = planner.CreatePlan state goal
                return Planning.updateWorkingPlan plan state, [ ReplacePlan plan ]
            | UpdatePlan reason when capabilities.CanReplan ->
                let! plan = planner.UpdatePlan state (BetterPathFound reason)
                return Planning.updateWorkingPlan plan state, [ ReplacePlan plan; RequestReplan(BetterPathFound reason) ]
            | ExecuteStep stepId ->
                return Planning.markStepStatus stepId InProgress state, []
            | CallTool call when capabilities.CanUseTools ->
                return state, [ DispatchTool call ]
            | StoreMemory(content, tags) when capabilities.CanRemember ->
                let candidate = toMemoryCandidate content tags
                if memoryPolicy.ShouldStore state candidate then
                    do! memoryStore.Remember state.AgentId candidate
                    return state, [ SaveMemory candidate ]
                else
                    return state, []
            | ReplyAndStoreMemory(reply, memory, tags) ->
                let candidate = toMemoryCandidate memory tags
                if capabilities.CanRemember && memoryPolicy.ShouldStore state candidate then
                    do! memoryStore.Remember state.AgentId candidate
                    return state, [ SendReply reply; SaveMemory candidate ]
                else
                    return state, [ SendReply reply ]
            | Escalate reason ->
                return state, [ SendReply ("Eskalation erforderlich: " + reason) ]
            | NoOp
            | _ ->
                return state, []
        }

    let applyDecisions state decisions =
        async {
            let ordered =
                decisions
                |> List.mapi (fun index decision -> index, decision)
                |> List.sortBy (fun (index, decision) -> decisionPriority decision, index)
                |> List.map snd

            let effects = ResizeArray<AgentEffect>()
            let mutable currentState = state

            for decision in ordered do
                let! nextState, producedEffects = handleReasonerDecision currentState decision
                currentState <- nextState
                producedEffects |> List.iter effects.Add

            return currentState, effects |> Seq.toList
        }

    interface IAgentKernel with
        member _.Step state input =
            async {
                let state =
                    match input with
                    | UserMessage text -> appendUserMessage text state
                    | _ -> state

                match input with
                | UserMessage text ->
                    let! state = enrichStateWithMemoryRecall text state
                    let! (_, intent) = analyzer.Classify state text
                    if intent = PlanFirst && capabilities.CanPlan then
                        let! plan = planner.CreatePlan state text
                        let state' = Planning.updateWorkingPlan plan state
                        return state', [ ReplacePlan plan ]
                    else
                        let! decisions = reasoner.Decide state input
                        return! applyDecisions state decisions

                | ToolReturned result when not result.Success && capabilities.CanReplan ->
                    let reason = result.Error |> Option.defaultValue "Tool call failed"
                    return { state with Mode = Replanning }, [ RequestReplan(ToolFailure reason) ]

                | StepCompleted(stepId, output) ->
                    let nextState =
                        state
                        |> Planning.markStepStatus stepId Done
                        |> fun s ->
                            {
                                s with
                                    Working =
                                        {
                                            s.Working with
                                                IntermediateFacts =
                                                    (output :: s.Working.IntermediateFacts)
                                                    |> List.truncate 50
                                        }
                            }

                    return nextState, [ MarkStepDone stepId ]

                | StepFailed(stepId, reason) ->
                    let nextState =
                        state
                        |> Planning.markStepStatus stepId (Failed reason)
                        |> fun s -> { s with Mode = Replanning }

                    return nextState, [ MarkStepFailed(stepId, reason); RequestReplan(MissingInformation reason) ]

                | _ ->
                    let! decisions = reasoner.Decide state input
                    return! applyDecisions state decisions
            }

[<RequireQualifiedAccess>]
module AgentState =
    let create agentId kind =
        {
            AgentId = AgentId agentId
            Kind = kind
            Mode = Reactive
            Working =
                {
                    CurrentGoal = None
                    CurrentPlan = []
                    LastUserMessages = []
                    IntermediateFacts = []
                }
            EpisodicMemory = []
        }
