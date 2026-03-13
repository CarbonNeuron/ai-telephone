using System.Text;
using System.Text.RegularExpressions;
using MDView;
using OllamaSharp;
using Spectre.Console;
using Spectre.Console.Rendering;

var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

return await new AiTelephoneApp().RunAsync(args, cancellationSource.Token);

internal sealed class AiTelephoneApp
{
    private const string DefaultModel = "llama3";
    private const int DefaultRounds = 5;
    private static readonly Uri DefaultOllamaUri = new(GetOllamaHost());

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        CliOptions options;

        try
        {
            options = await ParseOptionsAsync(args, cancellationToken);
        }
        catch (CliUsageException exception)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            WriteUsage();
            return 1;
        }

        if (options.ShowHelp)
        {
            WriteUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.Text))
        {
            AnsiConsole.MarkupLine("[red]No input text was provided.[/]");
            WriteUsage();
            return 1;
        }

        var client = new OllamaApiClient(DefaultOllamaUri)
        {
            SelectedModel = options.Model
        };

        var history = new List<RoundSnapshot>
        {
            new(0, "Original", options.Text)
        };

        try
        {
            await AnsiConsole.Live(BuildLiveView(history, null))
                .AutoClear(false)
                .StartAsync(async context =>
                {
                    for (var roundNumber = 1; roundNumber <= options.Rounds; roundNumber++)
                    {
                        var currentInput = history[^1].Text;
                        var inFlight = new RoundSnapshot(roundNumber, options.Model, string.Empty);

                        context.UpdateTarget(BuildLiveView(history, inFlight));
                        context.Refresh();

                        await foreach (var chunk in client.GenerateAsync(BuildPrompt(currentInput), null, cancellationToken))
                        {
                            if (!string.IsNullOrEmpty(chunk?.Response))
                            {
                                inFlight.Text += chunk.Response;
                                context.UpdateTarget(BuildLiveView(history, inFlight));
                                context.Refresh();
                            }
                        }

                        history.Add(inFlight);
                        context.UpdateTarget(BuildLiveView(history, null));
                        context.Refresh();
                    }
                });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine($"[red]AI Telephone failed:[/] {Markup.Escape(exception.Message)}");
            AnsiConsole.MarkupLine($"[grey]Ollama endpoint:[/] {Markup.Escape(DefaultOllamaUri.ToString())}");
            AnsiConsole.MarkupLine($"[grey]Model:[/] {Markup.Escape(options.Model)}");
            return 1;
        }

        WriteFinalComparison(history, options.ShowDiff);
        return 0;
    }

    private static async Task<CliOptions> ParseOptionsAsync(string[] args, CancellationToken cancellationToken)
    {
        string? text = null;
        var rounds = DefaultRounds;
        var model = DefaultModel;
        var showDiff = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--show-diff":
                    showDiff = true;
                    break;
                case "--text":
                    text = GetNextValue(args, ref index, "--text");
                    break;
                case "--rounds":
                    rounds = ParsePositiveInt(GetNextValue(args, ref index, "--rounds"), "--rounds");
                    break;
                case "--model":
                    model = GetNextValue(args, ref index, "--model");
                    break;
                default:
                    if (arg.StartsWith("--text=", StringComparison.Ordinal))
                    {
                        text = arg["--text=".Length..];
                    }
                    else if (arg.StartsWith("--rounds=", StringComparison.Ordinal))
                    {
                        rounds = ParsePositiveInt(arg["--rounds=".Length..], "--rounds");
                    }
                    else if (arg.StartsWith("--model=", StringComparison.Ordinal))
                    {
                        model = arg["--model=".Length..];
                    }
                    else
                    {
                        throw new CliUsageException($"Unrecognized argument: {arg}");
                    }

                    break;
            }
        }

        if (text is null && Console.IsInputRedirected)
        {
            text = await Console.In.ReadToEndAsync(cancellationToken);
        }

        return new CliOptions(text?.TrimEnd('\r', '\n') ?? string.Empty, rounds, model, showDiff, showHelp);
    }

    private static string GetNextValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new CliUsageException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }

    private static int ParsePositiveInt(string value, string optionName)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 1)
        {
            throw new CliUsageException($"{optionName} must be a positive integer.");
        }

        return parsed;
    }

    private static void WriteUsage()
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Option").AddColumn("Description");
        table.AddRow("`--text`", "Initial text passage. If omitted, stdin is read.");
        table.AddRow("`--rounds`", $"Number of telephone rounds. Default: {DefaultRounds}.");
        table.AddRow("`--model`", $"Ollama model name. Default: `{DefaultModel}`.");
        table.AddRow("`--show-diff`", "Highlight changed words between each pass in the final comparison.");
        table.AddRow("`--help`", "Show usage.");

        AnsiConsole.Write(new FigletText("AI Telephone").LeftJustified().Color(Color.Yellow));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[grey]Examples[/]");
        AnsiConsole.MarkupLine("  dotnet run -- --text \"The quick brown fox\" --rounds 5 --model llama3");
        AnsiConsole.MarkupLine("  echo \"A markdown paragraph\" | dotnet run -- --rounds 3 --show-diff");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine($"[grey]Ollama endpoint:[/] {Markup.Escape(DefaultOllamaUri.ToString())}");
    }

    private static IRenderable BuildLiveView(IReadOnlyList<RoundSnapshot> history, RoundSnapshot? inFlight)
    {
        var renderables = new List<IRenderable>
        {
            new Rule("[yellow]AI Telephone[/]").LeftJustified(),
            BuildMetadataPanel(history.Count - 1, inFlight?.Model ?? history[^1].Model)
        };

        foreach (var snapshot in history)
        {
            renderables.Add(BuildRoundPanel(snapshot, isInFlight: false));
        }

        if (inFlight is not null)
        {
            renderables.Add(BuildRoundPanel(inFlight, isInFlight: true));
        }

        return new Rows(renderables);
    }

    private static IRenderable BuildMetadataPanel(int completedRounds, string model)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow(
            new Markup($"[bold]Completed rounds:[/] {completedRounds}"),
            new Markup($"[bold]Model:[/] {Markup.Escape(model)}"));

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("Session", Justify.Left)
        };
    }

    private static IRenderable BuildRoundPanel(RoundSnapshot snapshot, bool isInFlight)
    {
        var headerState = isInFlight ? " [grey](streaming)[/]" : string.Empty;
        var body = string.IsNullOrWhiteSpace(snapshot.Text)
            ? new Markup("[grey]Waiting for model output...[/]")
            : MarkdownRenderer.Render(snapshot.Text);

        return new Panel(body)
        {
            Border = isInFlight ? BoxBorder.Heavy : BoxBorder.Rounded,
            Header = new PanelHeader($"Round {snapshot.RoundNumber} | {snapshot.Model}{headerState}", Justify.Left),
            Expand = true
        };
    }

    private static void WriteFinalComparison(IReadOnlyList<RoundSnapshot> history, bool showDiff)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Final Comparison[/]").LeftJustified());

        for (var index = 1; index < history.Count; index++)
        {
            var previous = history[index - 1];
            var current = history[index];
            var changedWords = CountChangedWords(previous.Text, current.Text);

            if (showDiff)
            {
                var columns = new Columns(
                    BuildDiffPanel(previous.RoundNumber, "Before", HighlightPreviousChanges(previous.Text, current.Text, "[white on darkred]")),
                    BuildDiffPanel(current.RoundNumber, "After", HighlightCurrentChanges(previous.Text, current.Text, "[black on green3_1]")))
                {
                    Expand = true
                };

                AnsiConsole.Write(new Panel(columns)
                {
                    Header = new PanelHeader(
                        $"{previous.Model} -> {current.Model} | pass {index - 1} to {index} | {changedWords} changed word(s)",
                        Justify.Left),
                    Border = BoxBorder.Rounded,
                    Expand = true
                });
            }
            else
            {
                AnsiConsole.Write(new Panel(MarkdownRenderer.Render(previous.Text))
                {
                    Header = new PanelHeader($"{previous.Model} | pass {index - 1}", Justify.Left),
                    Border = BoxBorder.Rounded,
                    Expand = true
                });

                AnsiConsole.Write(new Panel(MarkdownRenderer.Render(current.Text))
                {
                    Header = new PanelHeader($"{current.Model} | pass {index} | {changedWords} changed word(s)", Justify.Left),
                    Border = BoxBorder.Rounded,
                    Expand = true
                });
            }
        }
    }

    private static Panel BuildDiffPanel(int roundNumber, string label, string markup)
    {
        return new Panel(new Markup(markup))
        {
            Header = new PanelHeader($"{label} | round {roundNumber}", Justify.Left),
            Border = BoxBorder.Rounded,
            Expand = true
        };
    }

    private static string HighlightPreviousChanges(string previous, string current, string changedStyle)
    {
        var previousTokens = Tokenize(previous);
        var currentTokens = Tokenize(current);
        var unchangedIndices = BuildUnchangedIndexSet(previousTokens, currentTokens).Previous;

        return BuildMarkup(previousTokens, unchangedIndices, changedStyle);
    }

    private static string HighlightCurrentChanges(string previous, string current, string changedStyle)
    {
        var previousTokens = Tokenize(previous);
        var currentTokens = Tokenize(current);
        var unchangedIndices = BuildUnchangedIndexSet(previousTokens, currentTokens).Current;

        return BuildMarkup(currentTokens, unchangedIndices, changedStyle);
    }

    private static int CountChangedWords(string previous, string current)
    {
        var previousTokens = Tokenize(previous);
        var currentTokens = Tokenize(current);
        var unchanged = BuildUnchangedIndexSet(previousTokens, currentTokens);

        var previousChanged = previousTokens
            .Select((token, index) => (token, index))
            .Count(item => !string.IsNullOrWhiteSpace(item.token) && !unchanged.Previous.Contains(item.index));

        var currentChanged = currentTokens
            .Select((token, index) => (token, index))
            .Count(item => !string.IsNullOrWhiteSpace(item.token) && !unchanged.Current.Contains(item.index));

        return Math.Max(previousChanged, currentChanged);
    }

    private static string BuildPrompt(string input)
    {
        return
            """
            Copy the following text exactly as written, word for word. Do not summarize, improve, or change anything. Return only the copied text.

            <text>
            """ + "\n" + input + "\n</text>";
    }

    private static List<string> Tokenize(string text)
    {
        return Regex.Matches(text, @"\s+|[^\s]+")
            .Select(match => match.Value)
            .ToList();
    }

    private static (HashSet<int> Previous, HashSet<int> Current) BuildUnchangedIndexSet(
        IReadOnlyList<string> previousTokens,
        IReadOnlyList<string> currentTokens)
    {
        var previousWords = previousTokens
            .Select((token, index) => new IndexedToken(index, token))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        var currentWords = currentTokens
            .Select((token, index) => new IndexedToken(index, token))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        var lengths = new int[previousWords.Length + 1, currentWords.Length + 1];

        for (var previousIndex = previousWords.Length - 1; previousIndex >= 0; previousIndex--)
        {
            for (var currentIndex = currentWords.Length - 1; currentIndex >= 0; currentIndex--)
            {
                lengths[previousIndex, currentIndex] = string.Equals(
                    previousWords[previousIndex].Value,
                    currentWords[currentIndex].Value,
                    StringComparison.Ordinal)
                    ? lengths[previousIndex + 1, currentIndex + 1] + 1
                    : Math.Max(lengths[previousIndex + 1, currentIndex], lengths[previousIndex, currentIndex + 1]);
            }
        }

        var unchangedPrevious = new HashSet<int>();
        var unchangedCurrent = new HashSet<int>();
        var previousCursor = 0;
        var currentCursor = 0;

        while (previousCursor < previousWords.Length && currentCursor < currentWords.Length)
        {
            if (string.Equals(previousWords[previousCursor].Value, currentWords[currentCursor].Value, StringComparison.Ordinal))
            {
                unchangedPrevious.Add(previousWords[previousCursor].Index);
                unchangedCurrent.Add(currentWords[currentCursor].Index);
                previousCursor++;
                currentCursor++;
            }
            else if (lengths[previousCursor + 1, currentCursor] >= lengths[previousCursor, currentCursor + 1])
            {
                previousCursor++;
            }
            else
            {
                currentCursor++;
            }
        }

        return (unchangedPrevious, unchangedCurrent);
    }

    private static string BuildMarkup(
        IReadOnlyList<string> tokens,
        HashSet<int> unchangedIndices,
        string changedStyle)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (string.IsNullOrWhiteSpace(token))
            {
                builder.Append(token);
                continue;
            }

            var shouldHighlight = !unchangedIndices.Contains(index);
            var escapedToken = Markup.Escape(token);

            if (shouldHighlight)
            {
                builder.Append(changedStyle);
                builder.Append(escapedToken);
                builder.Append("[/]");
            }
            else
            {
                builder.Append(escapedToken);
            }
        }

        return builder.ToString();
    }

    private static string GetOllamaHost()
    {
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST");

        if (string.IsNullOrWhiteSpace(host))
        {
            return "http://localhost:11434";
        }

        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        return $"http://{host}";
    }
}

internal sealed record CliOptions(string Text, int Rounds, string Model, bool ShowDiff, bool ShowHelp);

internal sealed class RoundSnapshot(int roundNumber, string model, string text)
{
    public int RoundNumber { get; } = roundNumber;
    public string Model { get; } = model;
    public string Text { get; set; } = text;
}

internal sealed record IndexedToken(int Index, string Value);

internal sealed class CliUsageException(string message) : Exception(message);
