using System.Drawing.Imaging;
using System.Text.Json;

namespace PaRappaPC;

/// <summary>
/// Six-stage rhythm game driven by the original data: charts are read from each stage's
/// COMOD overlay and the music (four rank mixes) is decoded from the disc image at startup.
/// Nothing from the disc is stored in this program.
/// </summary>
internal sealed class RhythmGameForm : Form
{
    // Tempo (BPM x 100 at +6) and start offset (+8 plus +10, in ticks) from the executable's stage
    // parameter records (0x16c bytes each from 0x8005474c); the overlay setup routine turns them into the
    // song clock: ticks = BPM * 96 / 60 * seconds + offset.
    private static readonly StageDefinition[] Stages =
    [
        new(1, 1, "S", "Chop Chop Master Onion", "Kick! Punch!", "Dojo", 110.0, 96),
        new(2, 2, "S2", "Instructor Mooselini", "Driver's Test", "Driving school", 107.0, -46),
        new(3, 3, "S3", "Prince Fleaswallow", "Flea Market", "Flea market", 78.0, -144),
        new(4, 5, "S5", "Cheap Cheap the Cooking Chicken", "Seafood Cake", "Cooking show", 100.0, -21),
        new(5, 6, "S6", "All Master", "Full Tank", "Gas station", 96.0, 0),
        new(6, 7, "S7", "MC King Kong Mushi", "PaRappa's Live Rap", "Club Fun", 102.0, -96),
    ];

    // Rank -> XA channel, from the executable's table at 0x8005541c (Cool 5, Good 1, Bad 2, Awful 3).
    private static readonly int[] RankMix = [3, 2, 1, 5];
    private const int Awful = 0, Bad = 1, Good = 2, Cool = 3;
    private const double HitWindow = 0.5;   // in 16th notes
    private const double CoolWindow = 0.2;

    private readonly FlowLayoutPanel _header = new() { Dock = DockStyle.Top, Height = 62, Padding = new Padding(16, 10, 12, 4), WrapContents = false, BackColor = Color.FromArgb(26, 28, 34) };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 25) };
    private readonly Label _brand = new() { Text = "PaRappa PC", AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold), ForeColor = Color.White, Margin = new Padding(0, 1, 20, 0) };
    private readonly Button _back = new() { Text = "←  Stages", AutoSize = true, Visible = false, Margin = new Padding(0, 5, 16, 0) };
    private readonly Label _stageHeader = new() { AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.White, Margin = new Padding(0, 11, 8, 0), Visible = false };
    private readonly Button _assetLab = new() { Text = "Assets & VDF Lab", AutoSize = true, Margin = new Padding(12, 5, 0, 0) };
    private readonly Button _vdfLab = new() { Text = "VDF animations", AutoSize = true, Margin = new Padding(4, 5, 0, 0) };
    private readonly Label _footer = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(14, 7, 8, 4), ForeColor = Color.FromArgb(176, 183, 195), BackColor = Color.FromArgb(26, 28, 34) };
    private readonly Panel _stageSelect = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 25) };
    private readonly Playfield _playfield = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10 };
    private readonly Dictionary<int, (StageChart Chart, Dictionary<int, short[]> Mixes, SoundBank Bank, HudSprites Hud)> _cache = [];
    private readonly Dictionary<int, double> _offsetMs = LoadOffsets();
    private readonly GameFiles? _files = GameFiles.Find();
    private readonly ControlConfig _controls = ControlConfig.Load();

    private Session? _session;
    private SongPlayer? _player;
    private int _loadToken;

    public RhythmGameForm()
    {
        Text = "PaRappa PC";
        Width = 1200;
        Height = 800;
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 25);
        KeyPreview = true;

        _header.Controls.AddRange([_brand, _back, _stageHeader, _assetLab, _vdfLab]);
        _content.Controls.Add(_stageSelect);
        _content.Controls.Add(_playfield);
        Controls.Add(_content);
        Controls.Add(_footer);
        Controls.Add(_header);

        BuildStageSelect();
        _back.Click += (_, _) => ShowStageSelect();
        _assetLab.Click += (_, _) => { using var lab = new MainForm(); lab.ShowDialog(this); };
        _vdfLab.Click += (_, _) => { using var lab = new VdfAnimationForm(); lab.ShowDialog(this); };
        _playfield.Click += (_, _) => { if (_session?.Phase is Phase.Cleared or Phase.Failed) ContinueAfterResult(); };
        _timer.Tick += (_, _) => Tick();
        KeyDown += OnKeyDown;
        FormClosed += (_, _) => StopSong();
        ShowStageSelect();
    }

    // ------------------------------------------------------------------ screens

    private void BuildStageSelect()
    {
        var intro = new TableLayoutPanel { Dock = DockStyle.Top, Height = 112, ColumnCount = 1, Padding = new Padding(24, 12, 20, 0) };
        intro.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        intro.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        intro.Controls.Add(new Label { Text = "Choose a stage", Font = new Font("Segoe UI", 24, FontStyle.Bold), ForeColor = Color.White, AutoSize = true }, 0, 0);
        intro.Controls.Add(new Label { Text = _files is null ? "Game files not found: expected game\\assets\\manifest.json and game\\source\\*.bin" : "Listen to the teacher, then rap it back in time.", Font = new Font("Segoe UI", 11), ForeColor = _files is null ? Color.LightCoral : Color.FromArgb(185, 191, 202), AutoSize = true }, 0, 1);
        var cards = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 4, 20, 18), AutoScroll = true, WrapContents = true, BackColor = Color.FromArgb(18, 20, 25) };
        foreach (var stage in Stages)
        {
            var card = new Button
            {
                Text = $"STAGE {stage.Number}\r\n{stage.Song}\r\n{stage.Teacher}\r\n{stage.Bpm:0} BPM\r\n\r\nPLAY",
                Size = new Size(350, 174), Margin = new Padding(10), TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(18, 12, 12, 12), Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.White,
                BackColor = StageColor(stage.Number), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand, Tag = stage, Enabled = _files is not null,
            };
            card.FlatAppearance.BorderColor = Color.FromArgb(80, 87, 102);
            card.Click += (_, _) => StartStage((StageDefinition)card.Tag!);
            cards.Controls.Add(card);
        }
        _stageSelect.Controls.Clear();
        _stageSelect.Controls.Add(cards);
        _stageSelect.Controls.Add(intro);
    }

    private void ShowStageSelect()
    {
        StopSong();
        _loadToken++;
        _session = null;
        _stageSelect.Visible = true;
        _playfield.Visible = false;
        _back.Visible = false;
        _stageHeader.Visible = false;
        _assetLab.Visible = true;
        _vdfLab.Visible = true;
        _footer.Text = _controls.Problems.Count > 0
            ? $"controls.json: {string.Join("; ", _controls.Problems.Take(3))}"
            : $"Charts and music are read from your disc at runtime. Keys are set in {_controls.Path}";
    }

    private async void StartStage(StageDefinition stage)
    {
        if (_files is null) return;
        StopSong();
        int token = ++_loadToken;
        _session = new Session(stage) { Phase = Phase.Loading };
        _stageSelect.Visible = false;
        _playfield.Visible = true;
        _back.Visible = true;
        _stageHeader.Visible = true;
        _assetLab.Visible = false;
        _vdfLab.Visible = false;
        _stageHeader.Text = $"STAGE {stage.Number}  ·  {stage.Song}";
        string KeysFor(GameAction a) => ControlConfig.Describe(a, _controls);
        _footer.Text = $"△ {KeysFor(GameAction.Triangle)}   ○ {KeysFor(GameAction.Circle)}   ✕ {KeysFor(GameAction.Cross)}   □ {KeysFor(GameAction.Square)}   L {KeysFor(GameAction.L)}   R {KeysFor(GameAction.R)}   ·   sync {KeysFor(GameAction.SyncEarlier)} {KeysFor(GameAction.SyncLater)}   ·   back {KeysFor(GameAction.Back)}";
        _playfield.Show(_session, 0);
        _timer.Start();

        try
        {
            if (!_cache.TryGetValue(stage.Number, out var data))
            {
                var files = _files;
                data = await Task.Run(() =>
                {
                    var chart = StageChart.Read(files.AssetPath($"{stage.Folder}/COMOD{stage.Compo}.BIN"));
                    var (lba, sectors) = files.DiscLocation($"{stage.Folder}/STAGE{stage.Compo}.XA");
                    var mixes = XaAudio.Decode(files.BinPath, lba, sectors, RankMix.Distinct().ToArray());
                    var bank = SoundBank.Read(files.AssetPath($"{stage.Folder}/COMPO{stage.Compo:D2}.INT"));
                    var hud = HudSprites.Load(files.AssetPath($"{stage.Folder}/COMPO{stage.Compo:D2}.INT"));
                    return (chart, mixes, bank, hud);
                });
                _cache[stage.Number] = data;
            }
            if (token != _loadToken) return;
            _session = new Session(stage) { Chart = data.Chart, Lines = BuildLines(data.Chart), Phase = Phase.Playing, Engine = new RankEngine(data.Chart, CoolUnlocked), Bank = data.Bank, Hud = data.Hud, CoolUnlocked = CoolUnlocked };
            _player = new SongPlayer(data.Mixes, XaAudio.SampleRate, RankMix[Good]);
            _player.Start();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (token != _loadToken) return;
            _session = new Session(stage) { Phase = Phase.Failed, Message = $"Could not load stage: {ex.Message}" };
        }
    }

    private static List<Line> BuildLines(StageChart chart) =>
        chart.Lines().Where(l => l.Notes.Count > 0).Select(l => new Line(l.Start, l.Length, l.Notes.Select(n => new NoteState(n)).ToList())).ToList();

    private void StopSong()
    {
        _timer.Stop();
        _player?.Dispose();
        _player = null;
    }

    // ------------------------------------------------------------------ gameplay

    private double OffsetMs(StageDefinition stage) => _offsetMs.TryGetValue(stage.Number, out double v) ? v : 0;

    /// <summary>Song position in 16th notes, from the audio device clock.</summary>
    private double SongSixteenth(Session s)
    {
        if (_player is null) return -1;
        double seconds = _player.PositionSeconds - OffsetMs(s.Stage) / 1000.0;
        double ticks = seconds * s.Stage.Bpm * 96 / 60 + s.Stage.StartTicks;
        return ticks / RankEngine.TicksPerSixteenth;
    }

    private void Tick()
    {
        var s = _session;
        if (s is null) return;
        double now = SongSixteenth(s);
        if (s.Phase == Phase.Playing && s.Engine is { } engine)
        {
            ushort pressed = Gamepad.PollPressed();
            if (pressed != 0)
                foreach (var (mask, action) in _controls.Gamepad)
                    if ((pressed & mask) != 0 && s.Phase == Phase.Playing) HandleAction(s, action, now);
            if (_session != s) return; // Back was pressed

            foreach (var ev in engine.Advance(now * RankEngine.TicksPerSixteenth))
            {
                if (ev.Text == "FAIL") { EndStage(s, false); break; }
                if (ev.NewRank is int rank)
                {
                    s.Message = rank == RankEngine.Cool ? "COOL MODE! The teacher steps aside — freestyle!"
                        : ev.Text == "UP" ? $"RANK UP — {RankName(rank)}" : $"RANK DOWN — {RankName(rank)}";
                    if (_player is not null) _player.Mix = RankMix[rank];
                }
                else if (ev.Text.StartsWith("FLASH ", StringComparison.Ordinal))
                    s.Flash = (int.Parse(ev.Text[6..]), now);
                else s.LastLinePoints = ev.Text;
            }
            s.Rank = engine.Rank;
            // Icons of lines that are over: anything not pressed turns red.
            while (s.NextToScore < s.Lines.Count && now > s.Lines[s.NextToScore].End + HitWindow)
                foreach (var n in s.Lines[s.NextToScore++].Notes)
                    if (n.Result == Judgement.None) n.Result = Judgement.Missed;
            // The result waits for the music to play out; a fail (handled above) still stops at once.
            bool songOver = _player is null ? engine.Finished : _player.Finished;
            if (s.Phase == Phase.Playing && songOver) EndStage(s, s.Rank >= Good);
        }
        _playfield.Show(s, now);
    }

    private void Press(Session s, RapButton button, double now)
    {
        if (s.Phase != Phase.Playing || s.Engine is null) return;
        var result = s.Engine.Press(button, now * RankEngine.TicksPerSixteenth);
        PlayRapVoice(s, button);
        if (result == RankEngine.PressResult.Ignored) return;
        if (result == RankEngine.PressResult.OffBeat) s.Message = "OFF BEAT";

        // On-screen feedback only; the score itself comes from RankEngine.
        NoteState? best = null;
        double bestDistance = double.MaxValue;
        foreach (var line in s.Lines)
        {
            if (now < line.Start - 2 || now > line.End + 2) continue;
            foreach (var note in line.Notes)
            {
                double d = Math.Abs(note.Note.Sixteenth - now);
                if (note.Result == Judgement.None && d <= HitWindow && d < bestDistance) { best = note; bestDistance = d; }
            }
        }
        if (best is null)
        {
            if (result == RankEngine.PressResult.OnBeat) s.Message = "FREESTYLE";
            return;
        }
        if (best.Note.Button != button) { best.Result = Judgement.Wrong; s.Message = "DIFFERENT BUTTON"; return; }
        best.Result = bestDistance <= CoolWindow ? Judgement.Cool : Judgement.Good;
        if (result == RankEngine.PressResult.OnBeat) s.Message = best.Result == Judgement.Cool ? "ON THE BEAT!" : "GOOD";
    }

    /// <summary>
    /// FUN_80014614's voice step: the bar's sound set and the button pick a list; pressing the same button
    /// again plays the next entry, a different button starts its list from the top.
    /// </summary>
    private void PlayRapVoice(Session s, RapButton button)
    {
        int set = s.Engine?.LastSoundSet ?? 0;
        if (set <= 0 || s.Chart is null || s.Bank is null || _player is null || set >= s.Chart.SoundSets.Count) return;
        int column = button switch { RapButton.L => 5, RapButton.R => 7, _ => (int)button };
        if (s.Chart.SoundSets[set][column] is not { Length: > 0 } list) return;
        var key = (set, column);
        int index = s.LastVoiceKey == key ? s.VoiceIndex.GetValueOrDefault(key) : 0;
        var entry = list[index % list.Length];
        s.VoiceIndex[key] = (index + 1) % list.Length;
        s.LastVoiceKey = key;
        if (s.Bank.Get(entry.Program, entry.Tone) is { } tone) _player.PlayVoice(tone, entry.Volume);
    }

    private void EndStage(Session s, bool cleared)
    {
        s.Phase = cleared ? Phase.Cleared : Phase.Failed;
        s.Message = cleared
            ? $"Stage clear! You finished at {RankName(s.Rank)} with {s.Engine?.Points ?? 0} points."
            : $"You were at {RankName(s.Rank)} at a checkpoint. Click or press Enter to retry.";
        if (cleared) MarkCleared(s.Stage.Number);
        StopSong();
        _timer.Start(); // keep repainting the result screen
    }

    private void ContinueAfterResult()
    {
        var s = _session;
        if (s is null) return;
        if (s.Phase == Phase.Cleared && s.Stage.Number < Stages.Length) StartStage(Stages[s.Stage.Number]);
        else if (s.Phase == Phase.Failed) StartStage(s.Stage);
        else ShowStageSelect();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var s = _session;
        if (s?.Phase is Phase.Cleared or Phase.Failed && e.KeyCode is Keys.Enter or Keys.Space) { ContinueAfterResult(); e.Handled = true; return; }
        if (!_controls.Keyboard.TryGetValue(e.KeyCode, out var action)) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (action == GameAction.Back) { ShowStageSelect(); return; }
        if (s is null) return;
        if (action is GameAction.SyncEarlier or GameAction.SyncLater) AdjustSync(s, action == GameAction.SyncLater ? 1 : -1, e.Shift);
        else HandleAction(s, action, SongSixteenth(s));
    }

    private void HandleAction(Session s, GameAction action, double now)
    {
        if (action == GameAction.Back) { ShowStageSelect(); return; }
        if (action is GameAction.SyncEarlier or GameAction.SyncLater) { AdjustSync(s, action == GameAction.SyncLater ? 1 : -1, false); return; }
        if (ControlConfig.ToRapButton(action) is RapButton button) Press(s, button, now);
    }

    private void AdjustSync(Session s, int direction, bool fine)
    {
        _offsetMs[s.Stage.Number] = OffsetMs(s.Stage) + direction * (fine ? 1 : 10);
        SaveOffsets(_offsetMs);
        s.Message = $"Sync offset {OffsetMs(s.Stage):+0;-0;0} ms";
    }

    protected override bool ProcessDialogKey(Keys keyData) =>
        // Arrow keys would otherwise move focus between buttons.
        _session is not null && _controls.Keyboard.ContainsKey(keyData) ? false : base.ProcessDialogKey(keyData);

    protected override bool IsInputKey(Keys keyData) => _controls.Keyboard.ContainsKey(keyData) || base.IsInputKey(keyData);

    // ------------------------------------------------------------------ settings

    private static string ProgressPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaRappaPC", "progress.json");
    private readonly HashSet<int> _cleared = LoadCleared();
    private bool CoolUnlocked => _cleared.Count >= Stages.Length;

    private static HashSet<int> LoadCleared()
    {
        try { return JsonSerializer.Deserialize<HashSet<int>>(File.ReadAllText(ProgressPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void MarkCleared(int stage)
    {
        bool wasUnlocked = CoolUnlocked;
        if (!_cleared.Add(stage)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath)!);
            File.WriteAllText(ProgressPath, JsonSerializer.Serialize(_cleared));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!wasUnlocked && CoolUnlocked && _session is not null) _session.Message += "  Cool mode is now unlocked!";
    }

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaRappaPC", "sync-offsets-v2.json");

    private static Dictionary<int, double> LoadOffsets()
    {
        try { return JsonSerializer.Deserialize<Dictionary<int, double>>(File.ReadAllText(SettingsPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static void SaveOffsets(Dictionary<int, double> offsets)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(offsets));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ model

    internal static string RankName(int rank) => rank switch { Awful => "Awful", Bad => "Bad", Good => "Good", _ => "Cool" };

    internal static Color StageColor(int stage) => stage switch
    {
        1 => Color.FromArgb(46, 91, 139), 2 => Color.FromArgb(81, 103, 66), 3 => Color.FromArgb(125, 85, 53),
        4 => Color.FromArgb(143, 90, 51), 5 => Color.FromArgb(94, 76, 141), _ => Color.FromArgb(133, 64, 105),
    };

    internal enum Phase { Loading, Playing, Cleared, Failed }
    internal enum Judgement { None, Cool, Good, Wrong, Missed }

    internal sealed record StageDefinition(int Number, int Compo, string Folder, string Teacher, string Song, string Theme, double Bpm, int StartTicks);

    internal sealed class NoteState(ChartNote note)
    {
        public ChartNote Note { get; } = note;
        public Judgement Result { get; set; }
    }

    /// <summary>A rap line: the player's turn is [Start, End) in 16th notes; the teacher's call is the same length just before.</summary>
    internal sealed class Line(int start, int length, List<NoteState> notes)
    {
        public int Start { get; } = start;
        public int Length { get; } = length;
        public int End => Start + Length;
        public List<NoteState> Notes { get; } = notes;
        public double Score { get; set; } = -1;
    }

    internal sealed class Session(StageDefinition stage)
    {
        public StageDefinition Stage { get; } = stage;
        public StageChart? Chart { get; init; }
        public List<Line> Lines { get; init; } = [];
        public Phase Phase { get; set; }
        public int Rank { get; set; } = Good;
        public int NextToScore { get; set; }
        public RankEngine? Engine { get; init; }
        public SoundBank? Bank { get; init; }
        public HudSprites? Hud { get; init; }
        public bool CoolUnlocked { get; init; }
        public (int Set, int Column)? LastVoiceKey { get; set; }
        public Dictionary<(int Set, int Column), int> VoiceIndex { get; } = [];
        public (int Rank, double At)? Flash { get; set; }
        public string LastLinePoints { get; set; } = "";
        public string Message { get; set; } = "";
        public (RapButton Button, double At)? LastPress { get; set; }
    }

    // ------------------------------------------------------------------ drawing

    private sealed class Playfield : Control
    {
        private Session? _s;
        private double _now;

        public Playfield()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 20, 25);
        }

        public void Show(Session s, double now)
        {
            _s = s;
            _now = now;
            Invalidate();
        }

        // HUD laid out like the original on a 320x240 screen, drawn with the stage's own TIM images: the beat
        // bar across the top (dots, beat stars, button icons), SCORE above a dark panel at the bottom, and the
        // U-Rappin' meter on the right. Scaled up with square pixels.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(Color.Black);
            if (_s is null) return;
            var s = _s;
            float px = Math.Max(1f, Math.Min(Width / 320f, Height / 240f));
            float ox = (Width - 320 * px) / 2, oy = (Height - 240 * px) / 2;
            var screen = new RectangleF(ox, oy, 320 * px, 240 * px);
            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(screen, StageColor(s.Stage.Number), Color.FromArgb(20, 22, 28), 90f))
                g.FillRectangle(bg, screen);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            RectangleF At(float x, float y, float w, float h) => new(ox + x * px, oy + y * px, w * px, h * px);
            void Sprite(Bitmap? b, float x, float y, float alpha = 1f, float scale = 1f)
            {
                if (b is null) return;
                var dest = At(x - b.Width * (scale - 1) / 2, y - b.Height * (scale - 1) / 2, b.Width * scale, b.Height * scale);
                if (alpha >= 1f) { g.DrawImage(b, dest); return; }
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                g.DrawImage(b, Rectangle.Round(dest), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, attributes);
            }
            using var white = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(200, 205, 215));
            using var text = new Font("Segoe UI", Math.Max(8, 9 * px * 0.75f), FontStyle.Bold);
            using var big = new Font("Segoe UI", Math.Max(10, 16 * px * 0.75f), FontStyle.Bold);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            g.DrawString($"STAGE {s.Stage.Number}  {s.Stage.Song}", text, dim, At(6, 226, 200, 12).Location);
            if (s.Phase == Phase.Loading)
            {
                g.DrawString("Decoding stage music and chart from your disc…", text, white, screen, center);
                return;
            }
            if (s.Phase is Phase.Cleared or Phase.Failed)
            {
                g.DrawString(s.Phase == Phase.Cleared ? "STAGE CLEAR" : "TRY AGAIN", big, white, At(0, 60, 320, 40), center);
                g.DrawString(s.Message + (s.Phase == Phase.Cleared ? "\nClick or press Enter to continue." : ""), text, dim, At(10, 110, 300, 60), center);
                return;
            }
            var hud = s.Hud;

            // Beat bar: one marker per 16th note of the current line, a star on each beat, the line's button
            // icons on their 16ths. The cursor sweeps it for the teacher's call, then again for the answer.
            var line = s.Lines.FirstOrDefault(l => _now >= l.Start && _now < l.End + 1) ?? s.Lines.FirstOrDefault(l => _now < l.Start);
            bool playerTurn = line is not null && _now >= line.Start;
            bool teacherTurn = line is not null && !playerTurn && _now >= line.Start - line.Length;
            int steps = line?.Length ?? 16;
            float barLeft = 34, barRight = 296, barY = 22;
            float spacing = (barRight - barLeft) / Math.Max(1, steps - 1);
            double cursor = line is null ? -1 : playerTurn ? _now - line.Start : teacherTurn ? _now - (line.Start - line.Length) : -1;
            for (int i = 0; i < steps; i++)
            {
                int abs = (line?.Start ?? 0) + i;
                bool passed = cursor >= i;
                var note = line?.Notes.FirstOrDefault(n => n.Note.Sixteenth == abs);
                float x = barLeft + i * spacing;
                if (note is not null && hud is not null && hud.Buttons.TryGetValue(note.Note.Button, out var icon))
                {
                    float alpha = note.Result is Judgement.Missed or Judgement.Wrong ? 0.35f : 1f;
                    float scale = note.Result is Judgement.Cool or Judgement.Good ? 1.25f : 1f;
                    Sprite(icon, x - icon.Width / 2f, barY - icon.Height / 2f, alpha, scale);
                }
                else if (abs % 4 == 0) Sprite(passed ? hud?.StarLit : hud?.Star, x - 6, barY - 6);
                else Sprite(passed ? hud?.DotLit : hud?.Dot, x - 4, barY - 4);
            }
            if (cursor >= 0)
            {
                float cx = barLeft + (float)Math.Min(cursor, steps - 1) * spacing;
                var tip = At(cx, barY - 12, 0, 0).Location;
                float r = 4 * px;
                using var marker = new SolidBrush(teacherTurn ? Color.FromArgb(230, 230, 240) : Color.Gold);
                g.FillPolygon(marker, [new PointF(tip.X - r, tip.Y - r), new PointF(tip.X + r, tip.Y - r), new PointF(tip.X, tip.Y + r * 0.6f)]);
            }

            // U-Rappin' meter: COOL/GOOD/BAD/AWFUL stacked on the right, "U rappin'" beside the current rank
            // and "!" after it; the rank the pair is heading to flashes.
            float rankX = 244, rankTop = 126, rankStep = 15;
            for (int i = 0; i < 4; i++)
            {
                int rank = Cool - i;
                bool current = rank == s.Rank;
                bool flashing = s.Flash is { } f && f.Rank == rank && f.Rank != s.Rank && _now - f.At < 16 && ((int)(_now * 2) & 1) == 0;
                var word = rank == Cool && !s.CoolUnlocked && hud?.CoolLocked is not null ? hud.CoolLocked : hud?.Ranks[rank];
                float y = rankTop + i * rankStep;
                Sprite(word, rankX, y, current || flashing ? 1f : 0.45f);
                if (current && hud?.Urappin is { } u) Sprite(u, rankX - u.Width - 6, y - 2);
                if (current && hud?.Exclaim is { } ex) Sprite(ex, rankX + (word?.Width ?? 40) + 6, y - 2);
            }

            // SCORE and digits above the bottom panel.
            Sprite(hud?.ScoreLabel, 26, 166);
            float dx = 26 + (hud?.ScoreLabel?.Width ?? 36) + 6;
            foreach (char c in (s.Engine?.Points ?? 0).ToString())
            {
                var digit = hud?.Digits[c - '0'];
                Sprite(digit, dx, 166);
                dx += (digit?.Width ?? 8) + 1;
            }

            // Bottom panel: status and feedback.
            var panel = At(14, 182, 292, 40);
            using var panelBrush = new SolidBrush(Color.FromArgb(140, 0, 30, 10));
            g.FillRoundedRectangle(panelBrush, panel, 8 * px);
            string status = _now < 0 ? "Get ready" : playerTurn ? "Your turn!" : teacherTurn ? "Teacher" : "";
            string feedback = s.Message.Length > 0 ? s.Message : status;
            g.DrawString(feedback, big, white, panel, center);
        }

        private static string Glyph(RapButton b) => b switch
        {
            RapButton.Triangle => "△", RapButton.Circle => "○", RapButton.Cross => "✕", RapButton.Square => "□",
            RapButton.L => "L", _ => "R",
        };

        private static Color ButtonColor(RapButton b) => b switch
        {
            RapButton.Triangle => Color.FromArgb(40, 140, 120), RapButton.Circle => Color.FromArgb(190, 60, 70),
            RapButton.Cross => Color.FromArgb(70, 110, 190), RapButton.Square => Color.FromArgb(170, 90, 160),
            _ => Color.FromArgb(110, 115, 130),
        };
    }
}

internal static class GraphicsRoundedExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) return;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
