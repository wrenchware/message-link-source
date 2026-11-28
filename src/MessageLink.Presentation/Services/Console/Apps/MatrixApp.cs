namespace MessageLink.Presentation.Services;

/// <summary>/matrix: digital rain until any key. One still frame when Windows' animation effects are off.</summary>
public sealed class MatrixApp(IConsoleAppHost shell) : ConsoleApp(shell)
{
    private const string Glyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789:.=*+-<>¦";
    private readonly Random random = new();
    private int[] heads = [];
    private int[] speeds = [];
    private char[,] cells = new char[0, 0];
    private int width, height;

    public override string Name => "matrix";
    public override TimeSpan Refresh => Shell.Motion ? TimeSpan.FromMilliseconds(80) : TimeSpan.Zero;

    // Any key at all wakes you up.
    public override bool Key(ConsoleKeyPress key) { Shell.Close(); return true; }
    public override void Click(int row, bool twice) => Shell.Close();

    private void Resize(int w, int h)
    {
        width = w; height = h;
        cells = new char[w, h];
        heads = [.. Enumerable.Range(0, w).Select(_ => random.Next(-h, h))];
        speeds = [.. Enumerable.Range(0, w).Select(_ => random.Next(1, 3))];
        for (var x = 0; x < w; x++) for (var y = 0; y < h; y++) cells[x, y] = Glyphs[random.Next(Glyphs.Length)];
    }

    public override void Tick()
    {
        for (var x = 0; x < width; x += 2)
        {
            heads[x] += speeds[x];
            if (heads[x] - height > height / 2 && random.Next(8) == 0) { heads[x] = -random.Next(height); speeds[x] = random.Next(1, 3); }
            if (random.Next(4) == 0) cells[x, random.Next(height)] = Glyphs[random.Next(Glyphs.Length)];
        }
    }

    public override ConsoleScreen Render(int w, int h)
    {
        if (w != width || h != height) { Resize(w, h); if (!Shell.Motion) for (var i = 0; i < h; i++) Tick(); }
        var rows = new List<ConsoleRow>(h);
        for (var y = 0; y < h; y++)
        {
            var spans = new List<ConsoleSpan>();
            var run = new System.Text.StringBuilder();
            var tone = ConsoleTone.Dim;
            void Flush() { if (run.Length > 0) { spans.Add(new ConsoleSpan(run.ToString(), tone)); run.Clear(); } }
            for (var x = 0; x + 1 < w; x += 2)
            {
                var distance = heads[x] - y;
                var next = distance == 0 ? ConsoleTone.Strong : distance is > 0 and < 4 ? ConsoleTone.Ok : distance is >= 4 and < 18 ? ConsoleTone.Dim : ConsoleTone.Default;
                var glyph = distance is >= 0 and < 18 ? cells[x, y] : ' ';
                if (next != tone) { Flush(); tone = next; }
                run.Append(glyph).Append(' ');
            }
            Flush();
            rows.Add(new ConsoleRow(spans));
        }
        return Screen([new ConsoleSpan("wake up, neo…", ConsoleTone.Ok)], rows, ("any key", "return"));
    }
}
