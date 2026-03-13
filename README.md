# AI Telephone

A .NET CLI that repeatedly sends a text passage through an Ollama model and shows how the output drifts across rounds.

## Requirements

- .NET 10 SDK
- A running Ollama server, defaulting to `http://localhost:11434`
- The target model pulled locally, for example `llama3`

## Run

```bash
dotnet run -- --text "The quick brown fox jumps over the lazy dog." --rounds 5 --model llama3 --temperature 0.2 --show-diff
```

Or pipe text through stdin:

```bash
printf '# Heading\n\nSome markdown text.\n' | dotnet run -- --rounds 3 --show-diff
```

## Options

- `--text`: Initial text input. If omitted, stdin is read.
- `--rounds`: Number of rounds to run. Defaults to `5`.
- `--model`: Ollama model name. Defaults to `llama3`.
- `--temperature`: Ollama sampling temperature. Defaults to `0.8`.
- `--show-diff`: Show word-level highlighted comparisons after the rounds finish.

## Notes

- Live round panels render the current output through `MDView.Renderer`.
- The final comparison highlights drift between each pass when `--show-diff` is enabled.
- Set `OLLAMA_HOST` if your Ollama API is not on the default local endpoint.
