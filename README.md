# TestGen

Console tool (.NET 10) that fills in missing unit tests for a .NET solution using a local, OpenAI-compatible LLM (Ollama, LM Studio, llama.cpp). Design: `PLAN.md`.

## Requirements

- .NET SDK 10
- `git` on PATH
- `dotnet-coverage` tool: `dotnet tool install -g dotnet-coverage`
- A running OpenAI-compatible endpoint (default `http://localhost:11434/v1/`)
- Target repository: git, clean working tree, green `dotnet build` and `dotnet test`

## Usage

No CLI parameters. Edit `appsettings.json` next to the binary (or override with environment variables, e.g. `TestGen__SolutionPath=...`, `Llm__Model=...`):

```json
{
  "Llm":     { "BaseUrl": "http://localhost:11434/v1/", "Model": "qwen2.5-coder:14b" },
  "TestGen": { "SolutionPath": "/path/to/solution.sln", "Parallel": 3, "MaxRetries": 3, "MinCoverage": 80 }
}
```

Custom headers for the LLM endpoint go in `Llm:Headers`. `{ApiKey}` in a value is replaced with `Llm:ApiKey`. A configured `Authorization` header overrides the default `Bearer <ApiKey>`:

```json
"Llm": {
  "ApiKey": "secret",
  "Headers": { "Authorization": "Token {ApiKey}", "X-Org": "my-org" }
}
```

```sh
dotnet publish TestGen.Cli -o out
cd out && ./testgen
```

Result: branch `testgen/result` in the target repository with bootstrapped test projects and merged test commits. Reports in `<solution dir>/.testgen/` (`coverage-before.json`, `coverage-after.json`, `coverage-report.html`, `summary.md`, `summary.json`). An interrupted run resumes from `.testgen/state.json`.

Exit codes: 0 ok, 1 unhandled error, 2 invalid configuration, 3 preflight failed, 4 final solution build/test red, 130 cancelled.

## Development

```sh
dotnet build
dotnet test TestGen.Tests
```

Projects: `Cli` (host, orchestration, worker, bootstrap, merge, report), `Coverage` (dotnet-coverage + cobertura), `Analysis` (Roslyn), `Git` (worktrees via Process), `Queue` (channel, workers, resumable state), `Llm` (client, prompts).
