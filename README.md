# taskforce-dotnet

Ein minimaler F# Agentenkern in hexagonaler Architektur.

## Struktur

- `src/Taskforce.Agent/Agent.fs`: Domain-Typen, Ports, Policies und `AgentKernel`.
- `src/Taskforce.Agent/Taskforce.Agent.fsproj`: F# Klassenbibliothek (`net8.0`).

## Enthaltene Bausteine

- Agent-Identität, Zustand und Modi (`Reactive`, `Planned`, `Replanning`)
- Strukturierte Planrepräsentation (`ExecutionPlan`, `PlanStep`)
- Explizite Agent-Entscheidungen (`AgentDecision`)
- Input-/Output-/Effect-Modell für den Kern
- Ports für Analyzer, Planner, Reasoner, MemoryStore und ToolInvoker
- LLM-basierte `ITaskAnalyzer`-Implementierung (`LlmTaskAnalyzer`) mit heuristischem Fallback (`TaskAnalyzers.heuristicFallback`)
- Schwellwertbasierte Memory-Policy
- `AgentKernel.Step`: `State + Input -> Decision -> Effects -> NewState`

## Beispielidee

Der Kernel unterstützt:

- direkte Antwort (`DirectResponse`)
- Planerstellung (`CreatePlan`)
- Replanning (`UpdatePlan`, `RequestReplan`)
- Tool-Dispatch (`CallTool` -> `DispatchTool`)
- selektives Memory-Schreiben (`StoreMemory`, `ReplyAndStoreMemory`)

> Hinweis: In dieser Umgebung ist das .NET SDK ggf. nicht installiert, daher konnte kein lokaler Build ausgeführt werden.
