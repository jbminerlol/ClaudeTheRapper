using System.Buffers.Binary;
using System.Drawing.Imaging;

namespace PaRappaPC;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--selftest")
        {
            File.WriteAllText(args[1], SelfTest());
            return;
        }
        ApplicationConfiguration.Initialize();
        if (GameFiles.Find() is null) AskForGameFolder();
        Application.Run(new RhythmGameForm());
    }

    /// <summary>First run: ask where the user's own disc files are and remember the choice.</summary>
    private static void AskForGameFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose your PaRappa game folder. It needs assets\\manifest.json (extracted disc files) and source\\*.bin (your raw disc image).",
            UseDescriptionForTitle = true,
        };
        while (dialog.ShowDialog() == DialogResult.OK)
        {
            if (GameFiles.IsGameFolder(dialog.SelectedPath))
            {
                GameFiles.SaveFolder(dialog.SelectedPath);
                return;
            }
            MessageBox.Show("That folder needs assets\\manifest.json and a .bin disc image in source\\.", "PaRappa PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Loads every song stage's chart and music the way the game does and reports the results.</summary>
    private static string SelfTest()
    {
        var files = GameFiles.Find();
        if (files is null) return "game files not found\n";
        var report = new System.Text.StringBuilder();
        foreach (var (folder, id) in new[] { ("S", 1), ("S2", 2), ("S3", 3), ("S5", 5), ("S6", 6), ("S7", 7) })
        {
            var chart = StageChart.Read(files.AssetPath($"{folder}/COMOD{id}.BIN"));
            var (lba, sectors) = files.DiscLocation($"{folder}/STAGE{id}.XA");
            var mixes = XaAudio.Decode(files.BinPath, lba, sectors, [1, 2, 3, 5]);
            var bank = SoundBank.Read(files.AssetPath($"{folder}/COMPO{id:D2}.INT"));
            using (var hud = HudSprites.Load(files.AssetPath($"{folder}/COMPO{id:D2}.INT")))
                report.AppendLine($"    hud: buttons {hud.Buttons.Count}/6, ranks {hud.Ranks.Count(r => r is not null)}/4, digits {hud.Digits.Count(d => d is not null)}/10 widths [{string.Join(",", hud.Digits.Select(d => d?.Width ?? 0))}], urappin {hud.Urappin?.Width}x{hud.Urappin?.Height}, '!' {hud.Exclaim?.Width}, bar {(hud.Dot is not null && hud.Star is not null ? "ok" : "missing")}");
            var voiceEntries = chart.SoundSets.SelectMany(r => r).Where(c => c is not null).SelectMany(c => c!).Distinct().ToList();
            report.AppendLine($"    voices: bank tones {bank.Count}, table entries {voiceEntries.Count}, missing in bank {voiceEntries.Count(e => bank.Get(e.Program, e.Tone) is null)}");
            var firstNotes = string.Join(" ", chart.Notes.Take(4).Select(n => $"{n.Sixteenth}:{n.Button}"));
            var lengths = string.Join(" ", mixes.OrderBy(m => m.Key).Select(m => $"ch{m.Key}={m.Value.Length / (double)XaAudio.SampleRate:0.00}s"));
            var rms = string.Join(" ", mixes.OrderBy(m => m.Key).Select(m => $"{Math.Sqrt(m.Value.Average(v => (double)v * v)):0}"));
            report.AppendLine($"    perfect: {Simulate(chart, chart.Notes.Select(n => (n.Sixteenth * 24.0, n.Button)))}");
            report.AppendLine($"    silent:  {Simulate(chart, [])}");
            report.AppendLine($"    perfect-run weak lines: {WeakLines(chart)}");
            report.AppendLine($"    mashing: {Simulate(chart, Enumerable.Range(0, chart.Bars.Count * 16).Select(i => (i * 24.0, RapButton.Cross)))}");
            report.AppendLine($"{folder}/COMOD{id}: bars={chart.Bars.Count} notes={chart.Notes.Count} lines={chart.Lines().Count} (half {chart.Lines().Count(l => l.Length == 8)}, two-bar {chart.Lines().Count(l => l.Length == 32)}, notes outside lines {chart.Notes.Count - chart.Lines().Sum(l => l.Notes.Count)}) first=[{firstNotes}] audio {lengths} rms {rms}");
        }
        return report.ToString();
    }

    /// <summary>Feeds presses (song ticks) through the ported rank engine and summarises the run.</summary>
    private static string Simulate(StageChart chart, IEnumerable<(double Tick, RapButton Button)> presses)
    {
        foreach (bool cool in new[] { false, true })
        {
            var engine = new RankEngine(chart, cool);
            var ranks = new List<string>();
            foreach (var (tick, button) in presses.OrderBy(p => p.Tick))
            {
                foreach (var e in engine.Advance(tick)) if (e.NewRank is int r) ranks.Add(RhythmGameForm.RankName(r));
                engine.Press(button, tick);
            }
            foreach (var e in engine.Advance(double.MaxValue / 4)) if (e.NewRank is int r) ranks.Add(RhythmGameForm.RankName(r));
            string result = engine.Failed ? "FAILED" : $"ends {RhythmGameForm.RankName(engine.Rank)}";
            if (!cool) { _simulated = $"{result}, {engine.Points} pts, changes [{string.Join(">", ranks)}]"; continue; }
            return $"{_simulated}  |  Cool unlocked: {result}, {engine.Points} pts, changes [{string.Join(">", ranks)}]";
        }
        return "";
    }

    private static string _simulated = "";

    private static string WeakLines(StageChart chart)
    {
        var engine = new RankEngine(chart, false);
        var weak = new List<string>();
        foreach (var n in chart.Notes.OrderBy(n => n.Sixteenth))
        {
            foreach (var e in engine.Advance(n.Sixteenth * 24.0)) if (e.Bar >= 0 && !e.Text.StartsWith('+') || e.Text == "+0") weak.Add($"bar {e.Bar} {e.Text}");
            engine.Press(n.Button, n.Sixteenth * 24.0);
        }
        foreach (var e in engine.Advance(1e12)) if (e.Bar >= 0 && (!e.Text.StartsWith('+') || e.Text == "+0")) weak.Add($"bar {e.Bar} {e.Text}");
        return weak.Count == 0 ? "none" : string.Join(", ", weak);
    }
}

internal sealed record Resource(string Stage, string Bundle, string Name, string Path, byte[] Bytes)
{
    public override string ToString() => $"{Name}  ({Bytes.Length:N0} bytes)";
}

internal sealed class MainForm : Form
{
    private readonly ComboBox _stage = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _search = new() { PlaceholderText = "Filter textures…", Dock = DockStyle.Fill };
    private readonly ListBox _resources = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(38, 38, 42) };
    private readonly Label _details = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10) };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(8, 2, 8, 2), Text = "Choose the folder containing the extracted disc files." };
    private readonly List<Resource> _all = [];
    private bool _loading;

    public MainForm()
    {
        Text = "PaRappa PC — Asset Lab";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(850, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 6, Padding = new Padding(8, 6, 8, 6) };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var stageLabel = new Label { Text = "Stage", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 8, 0) };
        _stage.Items.AddRange(["All", "Stage 1 · COMPO01", "Stage 2 · COMPO02", "Stage 3 · COMPO03", "Stage 4 · COMPO05", "Stage 5 · COMPO06", "Stage 6 · COMPO07", "Other / shared"]);
        _stage.SelectedIndex = 0;
        var folderButton = new Button { Text = "Open disc folder…", AutoSize = true, Anchor = AnchorStyles.Right };
        folderButton.Click += (_, _) => ChooseFolder();
        var modelButton = new Button { Text = "Preview custom OBJ…", AutoSize = true, Anchor = AnchorStyles.Right };
        modelButton.Click += (_, _) => ChooseModel();
        var vdfButton = new Button { Text = "Browse VDF", AutoSize = true, Anchor = AnchorStyles.Right };
        vdfButton.Click += (_, _) => { using var form = new VdfAnimationForm(); form.ShowDialog(this); };
        toolbar.Controls.Add(stageLabel, 0, 0);
        toolbar.Controls.Add(_stage, 1, 0);
        toolbar.Controls.Add(_search, 2, 0);
        toolbar.Controls.Add(folderButton, 3, 0);
        toolbar.Controls.Add(modelButton, 4, 0);
        toolbar.Controls.Add(vdfButton, 5, 0);

        var split = new SplitContainer
        {
            Size = new Size(1180, 650),
            SplitterDistance = 370,
            Panel1MinSize = 250,
            Panel2MinSize = 300,
            Dock = DockStyle.Fill
        };
        split.Panel1.Controls.Add(_resources);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        right.Controls.Add(_preview, 0, 0);
        right.Controls.Add(_details, 0, 1);
        split.Panel2.Controls.Add(right);

        var statusStrip = new StatusStrip();
        var statusHost = new ToolStripControlHost(_status) { AutoSize = false, Width = 1100, Height = 24 };
        statusStrip.Items.Add(statusHost);
        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        _stage.SelectedIndexChanged += (_, _) => ApplyFilter();
        _search.TextChanged += (_, _) => ApplyFilter();
        _resources.SelectedIndexChanged += (_, _) => ShowSelection();
        Shown += (_, _) => LoadFromDefault();
    }

    private void LoadFromDefault()
    {
        var folder = GameFiles.Find()?.AssetsDir;
        if (folder is null)
        {
            _status.Text = "Use Open disc folder to select the folder containing SYSTEM.CNF and the stage folders.";
            return;
        }
        LoadFolder(folder);
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the extracted PaRappa disc folder (contains SYSTEM.CNF and S0/S2… folders)." };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadFolder(dialog.SelectedPath);
    }

    private void ChooseModel()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a custom model for the PC port preview",
            Filter = "Wavefront OBJ model (*.obj)|*.obj|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            using (var preview = new ModelPreviewForm(dialog.FileName)) preview.ShowDialog(this);
    }

    private void LoadFolder(string folder)
    {
        _loading = true;
        _all.Clear();
        _resources.Items.Clear();
        _preview.Image?.Dispose();
        _preview.Image = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.INT", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(folder, path);
                var bundle = Path.GetFileNameWithoutExtension(path);
                var directory = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                var stage = StageLabel(directory, bundle);
                foreach (var resource in IntBundle.Read(path, stage, bundle))
                    if (resource.Name.EndsWith(".TIM", StringComparison.OrdinalIgnoreCase)) _all.Add(resource);
            }
            _status.Text = $"Loaded {_all.Count:N0} texture records from {folder}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read disc resources: {ex.Message}";
        }
        finally
        {
            _loading = false;
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        if (_loading) return;
        var selectedStage = _stage.SelectedItem?.ToString() ?? "All";
        var query = _search.Text.Trim();
        var filtered = _all.Where(r => (selectedStage == "All" ||
                    r.Stage == selectedStage) &&
                    (query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Bundle.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Stage, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Bundle, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _resources.BeginUpdate();
        _resources.Items.Clear();
        _resources.Items.AddRange(filtered);
        _resources.EndUpdate();
        _status.Text = $"Showing {filtered.Length:N0} of {_all.Count:N0} textures";
    }

    private static string StageLabel(string directory, string bundle) => bundle.ToUpperInvariant() switch
    {
        "COMPO01" => "Stage 1 · COMPO01",
        "COMPO02" => "Stage 2 · COMPO02",
        "COMPO03" => "Stage 3 · COMPO03",
        "COMPO05" => "Stage 4 · COMPO05",
        "COMPO06" => "Stage 5 · COMPO06",
        "COMPO07" => "Stage 6 · COMPO07",
        _ => "Other / shared"
    };

    private void ShowSelection()
    {
        if (_resources.SelectedItem is not Resource resource) return;
        try
        {
            var decoded = TimImage.Decode(resource.Bytes);
            _preview.Image?.Dispose();
            _preview.Image = decoded.Bitmap;
            _details.Text = $"{resource.Stage} / {resource.Bundle} / {resource.Name}\r\n{decoded.Width} × {decoded.Height} px   •   {decoded.Mode}   •   {resource.Bytes.Length:N0} bytes";
            _status.Text = resource.Path;
        }
        catch (Exception ex)
        {
            _preview.Image?.Dispose();
            _preview.Image = null;
            _details.Text = $"{resource.Name}\r\nTexture decode failed: {ex.Message}";
        }
    }
}

internal static class IntBundle
{
    private const int HeaderBytes = 16;
    private const int EntryBytes = 20;
    private const int PayloadOffset = 0x2000;

    public static IEnumerable<Resource> Read(string path, string stage, string bundle)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderBytes || U32(bytes, 0) != 1) yield break;
        var count = checked((int)U32(bytes, 4));
        if (count < 0 || HeaderBytes + (long)count * EntryBytes > bytes.Length || HeaderBytes + count * EntryBytes > PayloadOffset) yield break;
        var offset = PayloadOffset;
        for (var i = 0; i < count; i++)
        {
            var entry = HeaderBytes + i * EntryBytes;
            var size = checked((int)U32(bytes, entry));
            var nameEnd = Array.IndexOf(bytes, (byte)0, entry + 4, 16);
            if (nameEnd < 0) nameEnd = entry + 20;
            var name = System.Text.Encoding.ASCII.GetString(bytes, entry + 4, nameEnd - entry - 4);
            if (size < 0 || offset + (long)size > bytes.Length) yield break;
            yield return new Resource(stage, bundle, name, path, bytes.AsSpan(offset, size).ToArray());
            offset += size;
        }
    }

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
}

internal sealed record DecodedTim(Bitmap Bitmap, int Width, int Height, string Mode);

internal static class TimImage
{
    public static DecodedTim Decode(byte[] data)
    {
        if (data.Length < 20 || U32(data, 0) != 0x10) throw new InvalidDataException("Invalid TIM header");
        var flags = U32(data, 4);
        var modeBits = (int)(flags & 7);
        if (modeBits > 2) throw new NotSupportedException($"TIM pixel mode {modeBits} is not supported");
        var offset = 8;
        ushort[]? clut = null;
        if ((flags & 8) != 0)
        {
            var block = ReadBlock(data, ref offset);
            if (block.WidthWords < 1 || block.Height < 1) throw new InvalidDataException("Invalid TIM palette dimensions");
            clut = new ushort[checked(block.WidthWords * block.Height)];
            for (var i = 0; i < clut.Length; i++) clut[i] = U16(data, block.DataOffset + i * 2);
        }
        var image = ReadBlock(data, ref offset);
        var bitsPerPixel = modeBits switch { 0 => 4, 1 => 8, _ => 16 };
        var width = checked(image.WidthWords * 16 / bitsPerPixel);
        var height = image.Height;
        if (width < 1 || height < 1 || checked(width * height) > 8_000_000) throw new InvalidDataException("Invalid TIM image dimensions");
        if (modeBits < 2 && clut is null) throw new InvalidDataException("Indexed TIM has no palette");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var locked = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var dst = (byte*)locked.Scan0;
                for (var y = 0; y < height; y++)
                {
                    var row = dst + y * locked.Stride;
                    for (var x = 0; x < width; x++)
                    {
                        ushort color;
                        if (modeBits == 2) color = U16(data, image.DataOffset + (y * width + x) * 2);
                        else
                        {
                            var packed = data[image.DataOffset + y * image.WidthWords * 2 + (modeBits == 0 ? x / 2 : x)];
                            var index = modeBits == 0 ? ((x & 1) == 0 ? packed & 0x0f : packed >> 4) : packed;
                            color = clut![index % clut.Length];
                        }
                        var alpha = (color & 0x7fff) == 0 ? (byte)0 : (byte)255;
                        row[x * 4 + 0] = Expand5((byte)((color >> 10) & 31));
                        row[x * 4 + 1] = Expand5((byte)((color >> 5) & 31));
                        row[x * 4 + 2] = Expand5((byte)(color & 31));
                        row[x * 4 + 3] = alpha;
                    }
                }
            }
        }
        finally { bitmap.UnlockBits(locked); }
        return new DecodedTim(bitmap, width, height, modeBits switch { 0 => "4-bit indexed", 1 => "8-bit indexed", _ => "16-bit color" });
    }

    private static (int WidthWords, int Height, int DataOffset) ReadBlock(byte[] data, ref int offset)
    {
        if (offset + 12 > data.Length) throw new InvalidDataException("Truncated TIM block header");
        var size = checked((int)U32(data, offset));
        if (size < 12 || offset + size > data.Length) throw new InvalidDataException("Invalid TIM block size");
        var width = U16(data, offset + 8);
        var height = U16(data, offset + 10);
        var payload = offset + 12;
        offset += size;
        return (width, height, payload);
    }

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
    private static byte Expand5(byte v) => (byte)((v << 3) | (v >> 2));
}
