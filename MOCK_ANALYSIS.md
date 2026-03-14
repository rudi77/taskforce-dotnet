# Analyse: Mögliche Mock-/Stub-Bereiche (Status nach Umsetzung)

Diese Notiz wurde nach den letzten Implementierungen aktualisiert.

## 1) Streaming in Provider-Adaptern

**Status: umgesetzt.**
- OpenAI, Azure OpenAI und Anthropic besitzen jetzt echte Streaming-Pfade via `IAsyncEnumerable<LlmStreamEvent>`.

## 2) Tool-Loop: vereinfachte Tool-Result-Messages

**Status: verbessert.**
- Tool-Rückgaben werden jetzt mit `call_id` und `tool_name` in den JSON-Payload geschrieben und als `Name=Some toolName` zurückgeführt.

## 3) Fallback-Antwort bei zu vielen Tool-Loop-Iterationen

**Status: umgesetzt.**
- Statt synthetischer Fake-Response wird jetzt ein fachlicher Fehler (`InvalidProviderResponse`) geworfen.

## 4) `failwith "unreachable"` nach Fehler-Throw

**Status: umgesetzt.**
- Die verbliebenen `failwith "unreachable"`-Stellen im LLM-Pfad wurden entfernt.

## 5) Heuristischer TaskAnalyzer statt LLM-gestützter Analyse

**Status: umgesetzt (mit Fallback).**
- Es gibt jetzt einen LLM-basierten Analyzer (`LlmTaskAnalyzer`) für Klassifikation.
- Eine Heuristik bleibt als `heuristicFallback` für Degradationsfälle verfügbar.

## 6) README-Hinweis auf minimalen Kern

**Status: weiterhin gültig.**
- Das Projekt bleibt bewusst schlank, dokumentiert aber nun die LLM-basierte Analyzer-Option.
