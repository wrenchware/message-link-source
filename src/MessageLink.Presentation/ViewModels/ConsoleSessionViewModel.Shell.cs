using System.Globalization;
using System.Text.RegularExpressions;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// The shell around commands: echoing a command line, chains and pipes, aliases, history expansion, and where command
// output goes. A line that can send is never touched by any of it: it goes to RunAsync exactly as typed.
public sealed partial class ConsoleSessionViewModel
{
    public const int AliasDepth = 5;

    // One submitted command line's output: captured for a pipe, or printed wide; and whether anything in it failed.
    private sealed class RunState
    {
        public List<ConsoleOutLine>? Capture { get; init; }
        public int Errors { get; set; }
    }

    // Flows with the command's awaits and no further, so two command lines in flight at once keep their own output.
    private static readonly AsyncLocal<RunState?> running = new();

    /// <summary>The terminal's width in cells, which tables and wrapped output are laid out to. Set by the window.</summary>
    public int Columns { get; set; } = 100;

    /// <summary>The window should put this in the prompt for editing (a recalled send, never submitted by itself).</summary>
    public event Action<string>? InputSuggested;

    private static readonly Regex HistoryEvent = new(@"^!(!|-?\d{1,4}|[A-Za-z/][^\s]*)$", RegexOptions.CultureInvariant);

    // ---- Output ----

    /// <summary>A command's output line: wide, in inks; into the pipe when one is capturing.</summary>
    private void Emit(ConsoleOutLine line)
    {
        var run = running.Value;
        if (run is not null && line.Error) run.Errors++;
        if (run?.Capture is { } capture) { lock (capture) capture.Add(line); return; }
        lock (gate)
        {
            if (disposed) return;
            var now = clock.GetUtcNow();
            Add(now, (n, withDate) => formatter.Output(line.Spans, line.Error, now, n, withDate, wide: run is not null), ConsoleVerbosity.Quiet, null);
        }
    }

    private void Out(params ConsoleSpan[] spans) => Emit(new ConsoleOutLine(spans));
    private void Out(string text, ConsoleTone tone = ConsoleTone.Default) => Emit(ConsoleOutLine.Plain(text, tone));
    private void Fail(string text) => Emit(new ConsoleOutLine([new ConsoleSpan(text, ConsoleTone.Error)], Error: true));
    private void Blank() => Out("");

    private void Rows(IEnumerable<(IReadOnlyList<ConsoleSpan> Spans, bool Selected)> rows)
    {
        foreach (var row in rows) Emit(new ConsoleOutLine(row.Spans));
    }

    // The command line as it was typed, after the prompt it was typed at, as a shell's scrollback shows it.
    private void Echo(string prompt, string input)
    {
        lock (gate)
        {
            if (disposed) return;
            var now = clock.GetUtcNow();
            Add(now, (n, withDate) => formatter.Command(prompt, input, now, n, withDate), ConsoleVerbosity.Quiet, null);
        }
    }

    /// <summary>Ctrl+C at the prompt: the line is dropped, left in the scroll with ^C after it as a shell leaves it, and
    /// not kept in history.</summary>
    public void Interrupt(string input)
    {
        string prompt;
        lock (gate)
        {
            if (disposed) return;
            completion = null;
            historyIndex = -1;
            currentInput = "";
            held = null;
            if (replyHold == 0) ReleaseReply();
            prompt = Prompt;
        }
        Echo(prompt, input.Replace('\n', ' ') + "^C");
    }

    // ---- Running a command line ----

    // A command line (not a send): echoed, history-expanded, split into its chain, each pipeline run.
    private async Task RunCommandLineAsync(string line, string prompt)
    {
        var previous = running.Value;
        running.Value = new RunState();
        try
        {
            Echo(prompt, line);
            await RunLineAsync(line, 0);
        }
        finally { running.Value = previous; }
    }

    private async Task RunEchoed(string prompt, string line, Func<Task> action)
    {
        var previous = running.Value;
        running.Value = new RunState();
        try { Echo(prompt, line); await action(); }
        finally { running.Value = previous; }
    }

    private async Task<bool> RunLineAsync(string line, int depth)
    {
        var (chain, error) = ConsoleShell.Split(line);
        if (error is not null) { Command("shell"); Fail(error); return false; }
        // A command that acts on a conversation or a message runs only typed alone: a chain, a pipe or an alias with one
        // in it runs none of it, so nothing before it happens either.
        if ((chain.Count > 1 || depth > 0 || chain.Any(p => p.Filters.Count > 0)) &&
            chain.FirstOrDefault(p => ConsoleCatalog.StartsWithAct(p.Command)) is { } acting)
        {
            var verb = ConsoleCatalog.Find(SplitWord(acting.Command).Word)!.Name;
            log("console command name=shell refused=act");
            Fail(NotAlone(verb));
            return false;
        }
        var ok = true;
        foreach (var pipeline in chain)
        {
            // "a && b; c": b only when a worked; c whatever happened, as a shell has it.
            if (pipeline.Join == ConsoleJoin.IfOk && !ok) continue;
            ok = await RunPipelineAsync(pipeline, depth, alone: chain.Count == 1);
        }
        return ok;
    }

    private async Task<bool> RunPipelineAsync(ConsolePipeline pipeline, int depth, bool alone)
    {
        if (pipeline.Filters.Count == 0) return await RunOneAsync(pipeline.Command, depth, direct: alone && depth == 0);
        var outer = running.Value;
        var inner = new RunState { Capture = [] };
        running.Value = inner;
        bool ok;
        try { ok = await RunOneAsync(pipeline.Command, depth, direct: false); }
        finally { running.Value = outer; }
        if (outer is not null) outer.Errors += inner.Errors;
        log("console pipe filters=" + string.Join("|", pipeline.Filters.Select(f => f.Name)) + " lines=" + inner.Capture!.Count.ToString(CultureInfo.InvariantCulture));
        var result = ConsoleShell.Apply(inner.Capture!, pipeline.Filters, OpenPager, host.Copy, out var problem);
        if (problem is not null) { Fail(problem); return false; }
        foreach (var line in result ?? []) Emit(line);
        // "/ls * | grep dinner" finds conversation names only; say where words in messages are found.
        if (result is { Count: 0 } && SplitWord(pipeline.Command).Word == "ls" && pipeline.Filters.Any(f => f.Name == "grep"))
            Out("No conversation name matches. For words in messages, try /msgs * | grep words.", ConsoleTone.Dim);
        return ok;
    }

    // One command, after its alias is expanded. True when it printed no error.
    private async Task<bool> RunOneAsync(string command, int depth, bool direct)
    {
        var (word, rest) = SplitWord(command);
        // "cmd --help" or "cmd -h" shows the manual and never runs the command.
        if (ConsoleCatalog.HelpAsked(command) is { } asked && !Aliases.ContainsKey(word))
        {
            Command("help");
            Manual(asked);
            return true;
        }
        if (Aliases.TryGetValue(word, out var expansion))
        {
            if (rest.Split(' ').Any(w => w is "--help" or "-h"))
            {
                Command("help");
                Out(new ConsoleSpan("/" + word, ConsoleTone.Accent), new ConsoleSpan(" is an alias for ", ConsoleTone.Dim), new ConsoleSpan(expansion));
                if (ConsoleCatalog.Find(SplitWord(expansion).Word) is { } info) Manual(info);
                return true;
            }
            if (depth >= AliasDepth) { Fail($"/{word}: aliases nest too deeply."); return false; }
            return await RunLineAsync(rest.Length == 0 ? expansion : expansion + " " + rest, depth + 1);
        }
        var run = running.Value;
        var before = run?.Errors ?? 0;
        await RunAsync(ConsoleCommandParser.Parse(command), null, direct);
        return (running.Value?.Errors ?? 0) == before;
    }

    // ---- Commands without the slash, and text that looks like one ----

    // A held line: plain text to a target that looked like a command, waiting for a second Enter.
    private (string Text, DateTimeOffset At)? held;
    /// <summary>How long a held look-alike waits for Enter again.</summary>
    public static readonly TimeSpan HoldWindow = TimeSpan.FromSeconds(15);

    // The command (or alias) a line without its slash starts with, for command mode; null when it starts with none. A
    // command that sends still needs its slash.
    private string? CommandWord(string line)
    {
        var (word, _) = SplitWord("/" + line);
        if (word.Length == 0) return null;
        if (ConsoleCatalog.Find(word) is { } info) return info.Sends ? null : info.Name;
        return Aliases.ContainsKey(word) ? word : null;
    }

    /// <summary>Whether plain text typed to a target reads as a command (ConsoleCatalog.LooksLikeCommand), with this
    /// session's aliases and names.</summary>
    public bool LooksLikeCommand(string line, out string word) =>
        ConsoleCatalog.LooksLikeCommand(line, name => Aliases.ContainsKey(name), IsFirstName, out word);

    // A word that begins a conversation's name as a whole word: "lauren" for Lauren Smith, not "the" for Theo.
    private bool IsFirstName(string word) =>
        word.Length > 1 && targets.Entries().Any(t => t.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } first &&
            first.Equals(word, StringComparison.CurrentCultureIgnoreCase));

    // Plain text to a target that looks like a command is held once: nothing is sent, the words go back in the prompt and
    // a line says what Enter will do. The same words entered again within the window are sent as typed.
    private bool HoldsLookalike(string line, string input, ConsoleRecipient to, string prompt)
    {
        if (!LooksLikeCommand(line, out var word)) return false;
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (held is { } waiting && waiting.Text == line && now - waiting.At <= HoldWindow) { held = null; return false; }
            held = (line, now);
            historyIndex = -1;
            completion = null;
        }
        log("console guard name=lookalike");
        Say(ConsoleLineKind.Info, $"That looks like the {word} command. Enter again sends it to {to.Title} on {to.Service}; /{word} runs it; /to off leaves {to.Title}.");
        InputSuggested?.Invoke(input.Trim());
        return true;
    }

    private static (string Word, string Tail) SplitWord(string command)
    {
        var text = command.Trim();
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return (text[..end].TrimStart('/').ToLowerInvariant(), text[end..].Trim());
    }

    // Whether output is going into a pipe rather than the scroll: an app then prints its screen instead of opening.
    private bool Capturing => running.Value?.Capture is not null;

    // ---- History expansion ----

    /// <summary>
    /// "!!", "!n", "!-n" and "!word", as bash has them. A recalled command runs; a recalled send is only put back in the
    /// prompt, so nothing is ever sent again without Enter on the words themselves. Null when the line is not one.
    /// </summary>
    private string? Expand(string line, out string? error)
    {
        error = null;
        if (!HistoryEvent.IsMatch(line)) return null;
        var spec = line[1..];
        string? found;
        lock (gate)
        {
            if (spec == "!") found = history.Count > 0 ? history[^1] : null;
            else if (int.TryParse(spec, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                found = n > 0 ? (n <= history.Count ? history[n - 1] : null) : (-n >= 1 && -n <= history.Count ? history[history.Count + n] : null);
            else
            {
                var word = spec.TrimStart('/');
                found = history.AsEnumerable().Reverse().FirstOrDefault(h => h.TrimStart('/').StartsWith(word, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (found is null) error = $"{line}: event not found. To send it as a message, type \\{line}";
        return found;
    }

    // ---- Aliases ----

    private Dictionary<string, string>? aliases;
    private bool aliasesChanged;
    private Dictionary<string, string> Aliases
    {
        get
        {
            // Read from the host until it has some: they load from disk in the background as Messages starts the console.
            if (aliases is null || aliases.Count == 0 && !aliasesChanged)
            {
                var read = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var (name, value) in preferences.Aliases) if (AliasProblem(name, value) is null) read[name] = value; }
                catch (Exception) { }
                aliases = read;
            }
            return aliases;
        }
    }

    /// <summary>Why an alias cannot be saved, or null. Aliases run console commands, never sends, and never hide a command.</summary>
    public static string? AliasProblem(string name, string value)
    {
        if (name.Length is 0 or > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return "An alias name is letters, digits, - and _, up to 32.";
        if (ConsoleCatalog.Find(name) is not null) return $"/{name} is already a command.";
        if (value.Length is 0 or > 500) return "An alias runs a command line of up to 500 characters.";
        if (!value.TrimStart().StartsWith('/')) return "An alias runs console commands, which start with /.";
        var (chain, error) = ConsoleShell.Split(value);
        if (error is not null) return error;
        if (chain.Any(p => ConsoleCatalog.StartsWithSend(p.Command))) return "Aliases can't send. They run console commands only.";
        if (chain.Any(p => ConsoleCatalog.StartsWithAct(p.Command)))
            return "Aliases can't change conversations or messages (archive, react, delete …). Type those on a line of their own.";
        if (chain.Any(p => SplitWord(p.Command).Word.Equals(name, StringComparison.OrdinalIgnoreCase))) return "An alias can't run itself.";
        return null;
    }

    private async Task AliasAsync(string rest)
    {
        if (rest.Length == 0)
        {
            if (Aliases.Count == 0) { Out("No aliases yet. /alias u=/ls unread makes one.", ConsoleTone.Dim); return; }
            foreach (var (aliasName, aliasValue) in Aliases.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
                Out(new ConsoleSpan("/" + aliasName, ConsoleTone.Accent), new ConsoleSpan(" = ", ConsoleTone.Dim), new ConsoleSpan(aliasValue));
            return;
        }
        var eq = rest.IndexOf('=');
        if (eq < 0)
        {
            var asked = rest.Trim().TrimStart('/');
            if (Aliases.TryGetValue(asked, out var shown)) Out(new ConsoleSpan("/" + asked, ConsoleTone.Accent), new ConsoleSpan(" = ", ConsoleTone.Dim), new ConsoleSpan(shown));
            else Fail($"No alias called /{asked}. /alias name=command makes one.");
            return;
        }
        var name = rest[..eq].Trim().TrimStart('/');
        var value = rest[(eq + 1)..].Trim().Trim('\'');
        if (AliasProblem(name, value) is { } problem) { Fail(problem); return; }
        Aliases[name] = value;
        aliasesChanged = true;
        await SaveAliasesAsync();
        Out(new ConsoleSpan("/" + name, ConsoleTone.Accent), new ConsoleSpan(" now runs ", ConsoleTone.Dim), new ConsoleSpan(value));
    }

    private async Task UnaliasAsync(string rest)
    {
        var name = rest.Trim().TrimStart('/');
        if (name.Length == 0) { Fail("Type the alias to remove: /unalias name"); return; }
        if (!Aliases.Remove(name)) { Fail($"No alias called /{name}."); return; }
        aliasesChanged = true;
        await SaveAliasesAsync();
        Out($"Removed /{name}.", ConsoleTone.Dim);
    }

    private async Task SaveAliasesAsync()
    {
        try { await preferences.SaveAliasesAsync(new Dictionary<string, string>(Aliases, StringComparer.OrdinalIgnoreCase)); log("console alias_saved count=" + Aliases.Count.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception) { Fail("The alias works for now but couldn't be saved."); }
    }

    /// <summary>The alias names, for completion.</summary>
    public IReadOnlyList<string> AliasNames => [.. Aliases.Keys];
}
