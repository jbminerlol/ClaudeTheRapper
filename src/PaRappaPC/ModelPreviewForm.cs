using System.Globalization;
using System.Numerics;

namespace PaRappaPC;

internal sealed class ModelPreviewForm : Form
{
    private readonly ModelViewport _viewport = new() { Dock = DockStyle.Fill };
    private readonly Label _info = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10) };
    private readonly ComboBox _aspect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _animationObject = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, Enabled = false };
    private readonly TrackBar _frameSlider = new() { Width = 230, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None, Enabled = false };
    private readonly Label _frameLabel = new() { AutoSize = true, Padding = new Padding(4, 5, 4, 0), Text = "No animation" };
    private readonly Button _playButton = new() { Text = "Play", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 100 };
    private ObjMesh? _baseMesh;
    private VdfAnimation? _animation;

    public ModelPreviewForm(string path, string? animationPath = null)
    {
        Text = "Custom Model Preview — PaRappa PC";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;

        _aspect.Items.AddRange(["Window aspect", "4:3 classic", "16:9 widescreen", "21:9 ultrawide"]);
        _aspect.SelectedIndex = 0;
        _aspect.SelectedIndexChanged += (_, _) => _viewport.AspectMode = _aspect.SelectedIndex;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(8, 7, 8, 4), WrapContents = true };
        toolbar.Controls.Add(new Label { Text = "Camera view", AutoSize = true, Padding = new Padding(0, 5, 8, 0) });
        toolbar.Controls.Add(_aspect);
        toolbar.Controls.Add(new Label { Text = "Drag to orbit • wheel to zoom", AutoSize = true, Padding = new Padding(14, 5, 0, 0) });
        var animationButton = new Button { Text = "Load VDF…", AutoSize = true };
        animationButton.Click += (_, _) => LoadAnimation();
        toolbar.Controls.Add(animationButton);
        toolbar.Controls.Add(_animationObject);
        toolbar.Controls.Add(_playButton);
        toolbar.Controls.Add(_frameSlider);
        toolbar.Controls.Add(_frameLabel);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 54 };
        footer.Controls.Add(_info);
        Controls.Add(_viewport);
        Controls.Add(footer);
        Controls.Add(toolbar);
        _animationObject.SelectedIndexChanged += (_, _) => UpdateFrame();
        _frameSlider.ValueChanged += (_, _) => UpdateFrame();
        _playButton.Click += (_, _) =>
        {
            _playTimer.Enabled = !_playTimer.Enabled;
            _playButton.Text = _playTimer.Enabled ? "Pause" : "Play";
        };
        _playTimer.Tick += (_, _) =>
        {
            if (_frameSlider.Maximum < 1) return;
            _frameSlider.Value = _frameSlider.Value >= _frameSlider.Maximum ? 0 : _frameSlider.Value + 1;
        };
        LoadModel(path);
        if (animationPath is not null) LoadAnimation(animationPath);
    }

    private void LoadModel(string path)
    {
        try
        {
            var mesh = ObjMesh.Load(path);
            _baseMesh = mesh;
            _viewport.Mesh = mesh;
            _info.Text = $"{Path.GetFileName(path)}  •  {mesh.Vertices.Count:N0} vertices  •  {mesh.Triangles.Count:N0} triangles  •  OBJ static-mesh preview";
        }
        catch (Exception ex)
        {
            _info.Text = $"Could not load model: {ex.Message}";
        }
    }

    private void LoadAnimation()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a PlayStation VDF vertex-difference animation",
            Filter = "VDF animation (*.vdf)|*.vdf|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        LoadAnimation(dialog.FileName);
    }

    private void LoadAnimation(string path)
    {
        try
        {
            _animation = VdfAnimation.Read(path);
            _animationObject.Items.Clear();
            foreach (uint objectId in _animation.ObjectIds) _animationObject.Items.Add(objectId);
            _animationObject.Enabled = _animationObject.Items.Count > 0;
            if (_animationObject.Items.Count > 0) _animationObject.SelectedIndex = 0;
            _info.Text = $"{Path.GetFileName(path)}  •  {_animation.ObjectIds.Count():N0} object(s)  •  {_animation.ObjectIds.Sum(id => _animation.Frames(id).Count):N0} VDF frame records; load the matching base mesh";
            UpdateFrame();
        }
        catch (Exception ex)
        {
            _playTimer.Enabled = false;
            _playButton.Text = "Play";
            MessageBox.Show(this, ex.Message, "Could not read VDF animation", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateFrame()
    {
        if (_animation is null || _animationObject.SelectedItem is not uint objectId)
        {
            _playButton.Enabled = false;
            _frameSlider.Enabled = false;
            return;
        }
        var frames = _animation.Frames(objectId);
        _frameSlider.Maximum = Math.Max(0, frames.Count - 1);
        _frameSlider.Enabled = frames.Count > 1;
        _playButton.Enabled = frames.Count > 1;
        _frameLabel.Text = $"{_frameSlider.Value + 1}/{frames.Count}";
        if (_baseMesh is null) return;
        try
        {
            _viewport.Mesh = _animation.ApplyFrame(_baseMesh, objectId, _frameSlider.Value);
        }
        catch (InvalidDataException ex)
        {
            _viewport.Mesh = _baseMesh;
            _info.Text = ex.Message;
        }
    }
}

internal sealed class ModelViewport : Control
{
    private float _yaw = -0.45f;
    private float _pitch = 0.2f;
    private float _distance = 3.2f;
    private Point? _lastMouse;
    private int _aspectMode;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ObjMesh? Mesh { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int AspectMode { get => _aspectMode; set { _aspectMode = value; Invalidate(); } }

    public ModelViewport()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(30, 31, 36);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) _lastMouse = e.Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _lastMouse = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_lastMouse is not Point last) return;
        _yaw += (e.X - last.X) * 0.01f;
        _pitch = Math.Clamp(_pitch + (e.Y - last.Y) * 0.01f, -1.45f, 1.45f);
        _lastMouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _distance = Math.Clamp(_distance * (e.Delta < 0 ? 1.12f : 0.89f), 1.15f, 12f);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Rectangle view = GetViewRectangle();
        using (var border = new Pen(Color.FromArgb(75, 79, 88))) e.Graphics.DrawRectangle(border, view);
        if (Mesh is null || Mesh.Triangles.Count == 0)
        {
            TextRenderer.DrawText(e.Graphics, "Open an OBJ mesh to preview it", Font, view, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var projected = Mesh.Vertices.Select(Project).ToArray();
        var faces = new List<(int A, int B, int C, float Depth, Vector3 Normal)>();
        foreach (var tri in Mesh.Triangles)
        {
            var a = Mesh.Vertices[tri.A];
            var b = Mesh.Vertices[tri.B];
            var c = Mesh.Vertices[tri.C];
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() > 1e-8f) normal = Vector3.Normalize(normal);
            faces.Add((tri.A, tri.B, tri.C, (projected[tri.A].Depth + projected[tri.B].Depth + projected[tri.C].Depth) / 3f, normal));
        }

        using var outline = new Pen(Color.FromArgb(38, 12, 16, 20), 0.65f);
        foreach (var face in faces.OrderByDescending(f => f.Depth))
        {
            var points = new[] { projected[face.A].Point, projected[face.B].Point, projected[face.C].Point };
            var light = Math.Clamp(0.3f + Math.Abs(Vector3.Dot(face.Normal, Vector3.Normalize(new Vector3(-0.4f, 0.8f, -0.5f)))) * 0.7f, 0.25f, 1f);
            using var brush = new SolidBrush(Color.FromArgb((int)(215 * light), (int)(99 * light), (int)(174 * light), (int)(206 * light)));
            e.Graphics.FillPolygon(brush, points);
            e.Graphics.DrawPolygon(outline, points);
        }

        var mode = _aspectMode switch { 1 => "4:3 classic", 2 => "16:9 widescreen", 3 => "21:9 ultrawide", _ => "window aspect" };
        var actualAspect = view.Width / (float)Math.Max(1, view.Height);
        TextRenderer.DrawText(e.Graphics, $"{mode}  •  {actualAspect:0.00}:1", Font, new Point(view.Left + 8, view.Top + 8), Color.FromArgb(210, 222, 228));

        (PointF Point, float Depth) Project(Vector3 original)
        {
            var cy = MathF.Cos(_yaw); var sy = MathF.Sin(_yaw);
            var cx = MathF.Cos(_pitch); var sx = MathF.Sin(_pitch);
            var x = original.X * cy + original.Z * sy;
            var z = -original.X * sy + original.Z * cy;
            var y = original.Y * cx - z * sx;
            z = original.Y * sx + z * cx;
            float depth = _distance - z;
            float focal = (view.Height * 0.5f) / MathF.Tan(45f * MathF.PI / 360f);
            return (new PointF(view.Left + view.Width * 0.5f + x * focal / depth,
                view.Top + view.Height * 0.5f - y * focal / depth), depth);
        }
    }

    private Rectangle GetViewRectangle()
    {
        if (_aspectMode == 0 || ClientSize.Width < 1 || ClientSize.Height < 1) return ClientRectangle;
        float target = _aspectMode switch { 1 => 4f / 3f, 2 => 16f / 9f, _ => 21f / 9f };
        float current = ClientSize.Width / (float)ClientSize.Height;
        if (current > target)
        {
            int width = (int)(ClientSize.Height * target);
            return new Rectangle((ClientSize.Width - width) / 2, 0, width, ClientSize.Height);
        }
        int height = (int)(ClientSize.Width / target);
        return new Rectangle(0, (ClientSize.Height - height) / 2, ClientSize.Width, height);
    }
}

internal sealed class ObjMesh
{
    public List<Vector3> Vertices { get; } = [];
    public List<(int A, int B, int C)> Triangles { get; } = [];
    public float NormalizationScale { get; private set; } = 1f;
    public float AnimationYSign { get; private set; } = 1f;

    public ObjMesh WithVertices(IReadOnlyList<Vector3> vertices)
    {
        var result = new ObjMesh { NormalizationScale = NormalizationScale, AnimationYSign = AnimationYSign };
        result.Vertices.AddRange(vertices);
        result.Triangles.AddRange(Triangles);
        return result;
    }

    public static ObjMesh Load(string path)
    {
        if (!Path.GetExtension(path).Equals(".obj", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This preview currently accepts Wavefront OBJ files.");
        var mesh = new ObjMesh();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty) continue;
            if (line[0] == '#')
            {
                if (line.SequenceEqual("# codex_psx_y_down_to_obj_y_up=true")) mesh.AnimationYSign = -1f;
                continue;
            }
            int split = line.IndexOf(' ');
            if (split < 0) continue;
            var kind = line[..split];
            var rest = line[(split + 1)..].Trim();
            if (kind.SequenceEqual("v"))
            {
                var parts = rest.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                mesh.Vertices.Add(new Vector3(Parse(parts[0]), Parse(parts[1]), Parse(parts[2])));
            }
            else if (kind.SequenceEqual("f"))
            {
                var indices = new List<int>();
                foreach (var token in rest.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var indexText = token.Split('/')[0];
                    if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index == 0)
                        throw new InvalidDataException("OBJ face contains an invalid vertex index.");
                    indices.Add(index > 0 ? index - 1 : mesh.Vertices.Count + index);
                }
                for (int i = 1; i + 1 < indices.Count; i++)
                {
                    int a = indices[0], b = indices[i], c = indices[i + 1];
                    if ((uint)a >= mesh.Vertices.Count || (uint)b >= mesh.Vertices.Count || (uint)c >= mesh.Vertices.Count)
                        throw new InvalidDataException("OBJ face index is outside the vertex list.");
                    mesh.Triangles.Add((a, b, c));
                }
            }
        }
        if (mesh.Vertices.Count < 3 || mesh.Triangles.Count == 0) throw new InvalidDataException("OBJ contains no triangle mesh.");
        var min = new Vector3(mesh.Vertices.Min(v => v.X), mesh.Vertices.Min(v => v.Y), mesh.Vertices.Min(v => v.Z));
        var max = new Vector3(mesh.Vertices.Max(v => v.X), mesh.Vertices.Max(v => v.Y), mesh.Vertices.Max(v => v.Z));
        var center = (min + max) * 0.5f;
        float extent = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
        float scale = extent > 1e-6f ? 2f / extent : 1f;
        mesh.NormalizationScale = scale;
        for (int i = 0; i < mesh.Vertices.Count; i++) mesh.Vertices[i] = (mesh.Vertices[i] - center) * scale;
        return mesh;

        static float Parse(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
