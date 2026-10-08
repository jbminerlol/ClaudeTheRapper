using System.Numerics;

namespace PaRappaPC;

internal sealed class VdfAnimationForm : Form
{
    private readonly ListBox _files = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _object = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly TrackBar _frame = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None };
    private readonly Button _play = new() { Text = "Play", AutoSize = true };
    private readonly Label _frameLabel = new() { AutoSize = true, Text = "No VDF selected", Padding = new Padding(4, 7, 4, 0) };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10) };
    private readonly VdfDeltaCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly List<string> _paths = [];
    private VdfAnimation? _animation;
    private string? _selectedPath;

    public VdfAnimationForm()
    {
        Text = "PaRappa VDF Animations";
        Width = 1120;
        Height = 740;
        MinimumSize = new Size(850, 560);
        StartPosition = FormStartPosition.CenterParent;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(8, 7, 8, 4), WrapContents = true };
        var folderButton = new Button { Text = "Open VDF folder…", AutoSize = true };
        folderButton.Click += (_, _) => ChooseFolder();
        var singleButton = new Button { Text = "Open VDF file…", AutoSize = true };
        singleButton.Click += (_, _) => ChooseFile();
        var pairButton = new Button { Text = "Preview with base OBJ…", AutoSize = true };
        pairButton.Click += (_, _) => PreviewWithModel();
        var discModelButton = new Button { Text = "Match COMPO model…", AutoSize = true };
        discModelButton.Click += (_, _) => PreviewWithDiscModel();
        toolbar.Controls.Add(folderButton);
        toolbar.Controls.Add(singleButton);
        toolbar.Controls.Add(new Label { Text = "Object", AutoSize = true, Padding = new Padding(10, 5, 2, 0) });
        toolbar.Controls.Add(_object);
        toolbar.Controls.Add(_play);
        toolbar.Controls.Add(pairButton);
        toolbar.Controls.Add(discModelButton);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(6) };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        right.Controls.Add(_canvas, 0, 0);
        right.Controls.Add(_summary, 0, 1);
        var playback = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        playback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        playback.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        playback.Controls.Add(_frame, 0, 0);
        playback.Controls.Add(_frameLabel, 1, 0);
        right.Controls.Add(playback, 0, 2);

        var split = new SplitContainer { Size = new Size(1120, 680), Dock = DockStyle.Fill, SplitterDistance = 310, Panel1MinSize = 210, Panel2MinSize = 420 };
        split.Panel1.Controls.Add(_files);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        Controls.Add(toolbar);

        _files.SelectedIndexChanged += (_, _) => SelectAnimation();
        _object.SelectedIndexChanged += (_, _) => ShowFrame();
        _frame.ValueChanged += (_, _) => ShowFrame();
        _play.Click += (_, _) =>
        {
            _timer.Enabled = !_timer.Enabled;
            _play.Text = _timer.Enabled ? "Pause" : "Play";
        };
        _timer.Tick += (_, _) =>
        {
            if (_frame.Maximum > 0) _frame.Value = _frame.Value >= _frame.Maximum ? 0 : _frame.Value + 1;
        };

        var defaultFolder = FindExtractedFolder();
        if (defaultFolder is not null) LoadFolder(defaultFolder);
        else _summary.Text = "No extracted VDF folder found. Choose a folder containing .VDF files, or open a VDF file directly.";
    }

    private static string? FindExtractedFolder()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "outputs", "vdf-extracted");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.VDF", SearchOption.TopDirectoryOnly).Any()) return candidate;
        }
        return null;
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder containing extracted PaRappa .VDF animation files." };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadFolder(dialog.SelectedPath);
    }

    private void ChooseFile()
    {
        using var dialog = new OpenFileDialog { Title = "Open a VDF vertex-difference animation", Filter = "VDF animation (*.vdf)|*.vdf|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AddFiles([dialog.FileName]);
    }

    private void LoadFolder(string folder)
    {
        try
        {
            AddFiles(Directory.EnumerateFiles(folder, "*.VDF", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not read VDF folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        _timer.Enabled = false;
        _play.Text = "Play";
        _paths.Clear();
        _files.Items.Clear();
        foreach (string path in paths)
        {
            _paths.Add(path);
            _files.Items.Add($"{Path.GetFileName(path)}   ({new FileInfo(path).Length:N0} bytes)");
        }
        if (_files.Items.Count > 0) _files.SelectedIndex = 0;
        else _summary.Text = "No .VDF files found in that folder.";
    }

    private void SelectAnimation()
    {
        int index = _files.SelectedIndex;
        if ((uint)index >= _paths.Count) return;
        try
        {
            _selectedPath = _paths[index];
            _animation = VdfAnimation.Read(_selectedPath);
            _object.Items.Clear();
            foreach (uint objectId in _animation.ObjectIds) _object.Items.Add(objectId);
            _object.SelectedIndex = _object.Items.Count > 0 ? 0 : -1;
            _summary.Text = $"{Path.GetFileName(_selectedPath)}  •  {_animation.ObjectIds.Count():N0} object(s). VDF stores sparse vertex deltas; the plotted points show deltas, not the character silhouette. Pair it with the base mesh to see the character deform.";
            ShowFrame();
        }
        catch (Exception ex)
        {
            _animation = null;
            _canvas.Frame = null;
            _summary.Text = $"Could not parse this VDF: {ex.Message}";
        }
    }

    private void ShowFrame()
    {
        if (_animation is null || _object.SelectedItem is not uint objectId)
        {
            _canvas.Frame = null;
            _frameLabel.Text = "No VDF selected";
            _play.Enabled = false;
            return;
        }
        var frames = _animation.Frames(objectId);
        _frame.Maximum = Math.Max(0, frames.Count - 1);
        _frame.Enabled = frames.Count > 1;
        _play.Enabled = frames.Count > 1;
        _frameLabel.Text = $"Frame {_frame.Value + 1}/{frames.Count}";
        _canvas.Frame = frames[_frame.Value];
        _canvas.Invalidate();
    }

    private void PreviewWithModel()
    {
        if (_selectedPath is null) return;
        using var dialog = new OpenFileDialog { Title = "Choose the base OBJ with the matching vertex order", Filter = "Wavefront OBJ model (*.obj)|*.obj|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            using (var preview = new ModelPreviewForm(dialog.FileName, _selectedPath)) preview.ShowDialog(this);
    }

    private void PreviewWithDiscModel()
    {
        if (_selectedPath is null || _object.SelectedItem is not uint objectId) return;
        string? extractedVdfFolder = FindExtractedFolder();
        string? outputFolder = extractedVdfFolder is null ? null : Directory.GetParent(extractedVdfFolder)?.FullName;
        string? manifestPath = outputFolder is null ? null : Path.Combine(outputFolder, "tmd-extracted", "manifest.json");
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            MessageBox.Show(this, "No extracted TMD model library was found. Run extract_tmd.py on the extracted disc assets first.",
                "COMPO models unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var choices = new List<DiscModelChoice>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("object_index", out var objectIndex) || objectIndex.GetInt32() != objectId) continue;
                if (!item.TryGetProperty("vdf_candidates_by_exact_vertex_span", out var candidates) ||
                    !candidates.EnumerateArray().Any(candidate => string.Equals(candidate.GetString(), Path.GetFileName(_selectedPath), StringComparison.OrdinalIgnoreCase)))
                    continue;
                string objName = item.GetProperty("obj").GetString() ?? "";
                string bundle = item.GetProperty("bundle").GetString() ?? "COMPO";
                int vertexCount = item.GetProperty("vertex_count").GetInt32();
                int triangles = item.GetProperty("triangle_count").GetInt32();
                int tmdOffset = item.GetProperty("tmd_offset").GetInt32();
                string objPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, objName);
                if (File.Exists(objPath))
                    choices.Add(new DiscModelChoice(objPath, $"{bundle}  •  TMD +0x{tmdOffset:X}  •  {vertexCount} vertices / {triangles} triangles"));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            MessageBox.Show(this, $"Could not read the TMD model index: {ex.Message}", "COMPO model index error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (choices.Count == 0)
        {
            MessageBox.Show(this, "No TMD object has the same object index and vertex span as this VDF. Try another VDF frame or choose a base OBJ manually.",
                "No matching model candidate", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DiscModelChoice? selected = choices.Count == 1 ? choices[0] : ChooseDiscModel(choices);
        if (selected is null) return;
        using var preview = new ModelPreviewForm(selected.Path, _selectedPath);
        preview.ShowDialog(this);
    }

    private static DiscModelChoice? ChooseDiscModel(IReadOnlyList<DiscModelChoice> choices)
    {
        using var dialog = new Form
        {
            Text = "Choose a TMD match candidate",
            Width = 620,
            Height = 330,
            MinimumSize = new Size(480, 240),
            StartPosition = FormStartPosition.CenterParent
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var choice in choices) list.Items.Add(choice);
        list.SelectedIndex = 0;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var open = new Button { Text = "Preview", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(open);
        footer.Controls.Add(cancel);
        dialog.AcceptButton = open;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(list);
        dialog.Controls.Add(footer);
        return dialog.ShowDialog() == DialogResult.OK ? list.SelectedItem as DiscModelChoice : null;
    }

    private sealed record DiscModelChoice(string Path, string Label)
    {
        public override string ToString() => Label;
    }
}

internal sealed class VdfDeltaCanvas : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public VdfFrame? Frame { get; set; }

    public VdfDeltaCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(30, 31, 36);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        if (bounds.Width < 20 || bounds.Height < 20) return;
        using var axis = new Pen(Color.FromArgb(65, 72, 82));
        e.Graphics.DrawLine(axis, bounds.Left + 12, bounds.Height / 2, bounds.Right - 12, bounds.Height / 2);
        e.Graphics.DrawLine(axis, bounds.Width / 2, bounds.Top + 12, bounds.Width / 2, bounds.Bottom - 12);
        if (Frame is null)
        {
            TextRenderer.DrawText(e.Graphics, "Select a VDF animation to inspect its vertex-difference frames", Font, bounds, Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        float max = Math.Max(1f, Frame.Vertices.Max(v => Math.Max(Math.Abs((float)v.X), Math.Abs((float)v.Y))));
        float scale = Math.Min(bounds.Width, bounds.Height) * 0.42f / max;
        foreach (var vertex in Frame.Vertices)
        {
            float x = bounds.Width * 0.5f + vertex.X * scale;
            float y = bounds.Height * 0.5f - vertex.Y * scale;
            int intensity = (int)Math.Clamp(110 + Math.Abs(vertex.Z) * 2, 110, 245);
            using var brush = new SolidBrush(Color.FromArgb(intensity, 112, 205, 240));
            e.Graphics.FillEllipse(brush, x - 2.5f, y - 2.5f, 5, 5);
        }
        TextRenderer.DrawText(e.Graphics, $"Object {Frame.ObjectId} • {Frame.Vertices.Length} vertex deltas • x/y projection (z intensity)", Font, new Point(12, 10), Color.Gainsboro);
    }
}
