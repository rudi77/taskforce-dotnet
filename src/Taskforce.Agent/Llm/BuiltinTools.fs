namespace Taskforce.Agent.Llm

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module BuiltinTools =
    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private serialize value = JsonSerializer.Serialize(value, jsonOptions)

    let definitions: ToolDefinition list =
        [ { Name = "ReadFile"
            Description = "Reads a UTF-8 text file from the workspace."
            JsonSchema =
                """
{
  "type": "object",
  "properties": {
    "path": { "type": "string", "description": "Path relative to workspace root." }
  },
  "required": ["path"],
  "additionalProperties": false
}
""" }
          { Name = "WriteFile"
            Description = "Writes UTF-8 content to a file. Supports appending when append=true."
            JsonSchema =
                """
{
  "type": "object",
  "properties": {
    "path": { "type": "string", "description": "Path relative to workspace root." },
    "content": { "type": "string" },
    "append": { "type": "boolean", "default": false }
  },
  "required": ["path", "content"],
  "additionalProperties": false
}
""" }
          { Name = "EditFile"
            Description = "Edits a file by replacing all occurrences of oldText with newText."
            JsonSchema =
                """
{
  "type": "object",
  "properties": {
    "path": { "type": "string", "description": "Path relative to workspace root." },
    "oldText": { "type": "string" },
    "newText": { "type": "string" }
  },
  "required": ["path", "oldText", "newText"],
  "additionalProperties": false
}
""" }
          { Name = "Glob"
            Description = "Returns files in the workspace matching a glob pattern (supports * and **)."
            JsonSchema =
                """
{
  "type": "object",
  "properties": {
    "pattern": { "type": "string", "description": "Glob pattern like src/**/*.fs" },
    "maxResults": { "type": "integer", "minimum": 1, "maximum": 2000, "default": 200 }
  },
  "required": ["pattern"],
  "additionalProperties": false
}
""" }
          { Name = "Grep"
            Description = "Searches files using a regex pattern and returns matching lines."
            JsonSchema =
                """
{
  "type": "object",
  "properties": {
    "pattern": { "type": "string", "description": "Regex pattern." },
    "glob": { "type": "string", "description": "Optional file glob, default **/*" },
    "maxResults": { "type": "integer", "minimum": 1, "maximum": 2000, "default": 200 }
  },
  "required": ["pattern"],
  "additionalProperties": false
}
""" }
          { Name = "Shell"
            Description = "Runs a shell command. Uses PowerShell on Windows and bash on Linux/macOS."
            JsonSchema =
                """
{
  "type": "object",
  "properties": {
    "command": { "type": "string" },
    "timeoutSeconds": { "type": "integer", "minimum": 1, "maximum": 600, "default": 60 }
  },
  "required": ["command"],
  "additionalProperties": false
}
""" }
          { Name = "WebSearch"
            Description = "Performs a web search using DuckDuckGo instant answer API and returns compact results."
            JsonSchema =
                """
{
  "type": "object",
  "properties": {
    "query": { "type": "string" },
    "maxResults": { "type": "integer", "minimum": 1, "maximum": 20, "default": 5 }
  },
  "required": ["query"],
  "additionalProperties": false
}
""" } ]

    let private tryGetProperty (name: string) (doc: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if doc.TryGetProperty(name, &value) then Some value else None

    let private getRequiredString (doc: JsonElement) name =
        match tryGetProperty name doc with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> invalidArg name $"Missing or invalid '{name}'"

    let private getOptionalBool (doc: JsonElement) name fallback =
        match tryGetProperty name doc with
        | Some value when value.ValueKind = JsonValueKind.True -> true
        | Some value when value.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private getOptionalInt (doc: JsonElement) name fallback =
        match tryGetProperty name doc with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0
            if value.TryGetInt32(&parsed) then parsed else fallback
        | _ -> fallback

    let private workspacePath (workspaceRoot: string) (relativePath: string) =
        if String.IsNullOrWhiteSpace(relativePath) then
            invalidArg "path" "Path cannot be empty"

        let baseRoot = Path.GetFullPath(workspaceRoot)
        let fullPath = Path.GetFullPath(Path.Combine(baseRoot, relativePath))
        let comparison =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        let normalizedRoot =
            if baseRoot.EndsWith(Path.DirectorySeparatorChar) || baseRoot.EndsWith(Path.AltDirectorySeparatorChar) then
                baseRoot
            else
                baseRoot + string Path.DirectorySeparatorChar

        if not (fullPath.Equals(baseRoot, comparison) || fullPath.StartsWith(normalizedRoot, comparison)) then
            invalidArg "path" "Path escapes workspace root"

        fullPath

    let private globToRegexPattern (glob: string) =
        let escaped = Regex.Escape(glob)

        escaped
            .Replace(@"\*\*", "::DOUBLESTAR::")
            .Replace(@"\*", "[^/\\]*")
            .Replace("::DOUBLESTAR::", ".*")

    let private toRelative (workspaceRoot: string) (absolutePath: string) =
        Path.GetRelativePath(workspaceRoot, absolutePath).Replace('\\', '/')

    let private globFiles workspaceRoot pattern maxResults =
        let regex = Regex("^" + globToRegexPattern pattern + "$", RegexOptions.IgnoreCase)

        Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
        |> Seq.map (fun p -> toRelative workspaceRoot p)
        |> Seq.filter (fun rel -> regex.IsMatch(rel))
        |> Seq.truncate maxResults
        |> Seq.toList

    let private grepFiles workspaceRoot fileGlob pattern maxResults =
        let regex = Regex(pattern, RegexOptions.Multiline)

        globFiles workspaceRoot fileGlob Int32.MaxValue
        |> Seq.collect (fun relPath ->
            let fullPath = Path.Combine(workspaceRoot, relPath)
            File.ReadLines(fullPath)
            |> Seq.mapi (fun i line -> i + 1, line)
            |> Seq.choose (fun (lineNo, line) ->
                if regex.IsMatch(line) then
                    Some {| path = relPath; line = lineNo; text = line |}
                else
                    None))
        |> Seq.truncate maxResults
        |> Seq.toList

    let private runShell workspaceRoot command timeoutSeconds =
        let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        let fileName, args =
            if isWindows then
                "powershell", [ "-NoProfile"; "-Command"; command ]
            else
                "bash", [ "-lc"; command ]

        use process = new Process()
        process.StartInfo.FileName <- fileName
        process.StartInfo.WorkingDirectory <- workspaceRoot
        process.StartInfo.RedirectStandardOutput <- true
        process.StartInfo.RedirectStandardError <- true
        process.StartInfo.UseShellExecute <- false
        process.StartInfo.CreateNoWindow <- true
        args |> List.iter process.StartInfo.ArgumentList.Add

        let output = StringBuilder()
        let error = StringBuilder()
        process.OutputDataReceived.Add(fun eventArgs -> if not (isNull eventArgs.Data) then output.AppendLine(eventArgs.Data) |> ignore)
        process.ErrorDataReceived.Add(fun eventArgs -> if not (isNull eventArgs.Data) then error.AppendLine(eventArgs.Data) |> ignore)

        if not (process.Start()) then
            failwith "Failed to start shell process"

        process.BeginOutputReadLine()
        process.BeginErrorReadLine()

        if not (process.WaitForExit(timeoutSeconds * 1000)) then
            try
                process.Kill(entireProcessTree = true)
            with _ -> ()
            failwithf "Command timed out after %d seconds" timeoutSeconds

        {| exitCode = process.ExitCode
           stdout = output.ToString().TrimEnd()
           stderr = error.ToString().TrimEnd() |}

    let private flattenWebResults (root: JsonElement) maxResults =
        let results = ResizeArray<{| title: string; url: string |}>()

        let rec collect (element: JsonElement) =
            if results.Count < maxResults then
                match element.ValueKind with
                | JsonValueKind.Object ->
                    match tryGetProperty "Text" element, tryGetProperty "FirstURL" element with
                    | Some text, Some url when text.ValueKind = JsonValueKind.String && url.ValueKind = JsonValueKind.String ->
                        results.Add({| title = text.GetString(); url = url.GetString() |})
                    | _ ->
                        match tryGetProperty "Topics" element with
                        | Some topics when topics.ValueKind = JsonValueKind.Array ->
                            for topic in topics.EnumerateArray() do
                                collect topic
                        | _ -> ()
                | JsonValueKind.Array ->
                    for item in element.EnumerateArray() do
                        collect item
                | _ -> ()

        match tryGetProperty "Results" root with
        | Some direct -> collect direct
        | None -> ()

        match tryGetProperty "RelatedTopics" root with
        | Some related -> collect related
        | None -> ()

        results |> Seq.truncate maxResults |> Seq.toList

    let private webSearchAsync (query: string) (maxResults: int) =
        async {
            use client = new HttpClient(Timeout = TimeSpan.FromSeconds(20))
            let encoded = Uri.EscapeDataString(query)
            let url = $"https://api.duckduckgo.com/?q={encoded}&format=json&no_redirect=1&no_html=1&skip_disambig=1"
            let! response = client.GetStringAsync(url) |> Async.AwaitTask
            use doc = JsonDocument.Parse(response)
            return flattenWebResults doc.RootElement maxResults
        }

    let createInvoker (workspaceRoot: string) =
        let workspaceRoot = Path.GetFullPath(workspaceRoot)

        fun (call: ToolCall) ->
            async {
                try
                    use argsDoc = JsonDocument.Parse(call.ArgumentsJson)
                    let args = argsDoc.RootElement

                    let! outputJson =
                        async {
                            match call.Name with
                            | "ReadFile" ->
                                let path = getRequiredString args "path"
                                let fullPath = workspacePath workspaceRoot path
                                let content = File.ReadAllText(fullPath)
                                return serialize {| path = path; content = content |}
                            | "WriteFile" ->
                                let path = getRequiredString args "path"
                                let content = getRequiredString args "content"
                                let append = getOptionalBool args "append" false
                                let fullPath = workspacePath workspaceRoot path
                                let directory = Path.GetDirectoryName(fullPath)

                                if not (String.IsNullOrWhiteSpace(directory)) then
                                    Directory.CreateDirectory(directory) |> ignore

                                if append then
                                    File.AppendAllText(fullPath, content)
                                else
                                    File.WriteAllText(fullPath, content)

                                return serialize {| path = path; append = append; bytes = Encoding.UTF8.GetByteCount(content) |}
                            | "EditFile" ->
                                let path = getRequiredString args "path"
                                let oldText = getRequiredString args "oldText"
                                let newText = getRequiredString args "newText"
                                let fullPath = workspacePath workspaceRoot path
                                let content = File.ReadAllText(fullPath)
                                let occurrences = Regex.Matches(content, Regex.Escape(oldText)).Count
                                let edited = content.Replace(oldText, newText)
                                File.WriteAllText(fullPath, edited)
                                return serialize {| path = path; replacements = occurrences |}
                            | "Glob" ->
                                let pattern = getRequiredString args "pattern"
                                let maxResults = getOptionalInt args "maxResults" 200 |> max 1 |> min 2000
                                let matches = globFiles workspaceRoot pattern maxResults
                                return serialize {| pattern = pattern; matches = matches |}
                            | "Grep" ->
                                let pattern = getRequiredString args "pattern"
                                let fileGlob =
                                    match tryGetProperty "glob" args with
                                    | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
                                    | _ -> "**/*"
                                let maxResults = getOptionalInt args "maxResults" 200 |> max 1 |> min 2000
                                let matches = grepFiles workspaceRoot fileGlob pattern maxResults
                                return serialize {| pattern = pattern; glob = fileGlob; matches = matches |}
                            | "Shell" ->
                                let command = getRequiredString args "command"
                                let timeoutSeconds = getOptionalInt args "timeoutSeconds" 60 |> max 1 |> min 600
                                let result = runShell workspaceRoot command timeoutSeconds
                                return serialize result
                            | "WebSearch" ->
                                let query = getRequiredString args "query"
                                let maxResults = getOptionalInt args "maxResults" 5 |> max 1 |> min 20
                                let! matches = webSearchAsync query maxResults
                                return serialize {| query = query; results = matches |}
                            | unknown ->
                                return serialize {| error = $"Unknown tool '{unknown}'" |}
                        }

                    return
                        { CallId = call.CallId
                          OutputJson = outputJson }
                with ex ->
                    return
                        { CallId = call.CallId
                          OutputJson = serialize {| error = ex.Message |} }
            }
