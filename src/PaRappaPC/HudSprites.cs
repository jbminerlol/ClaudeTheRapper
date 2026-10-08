using System.Drawing.Imaging;

namespace PaRappaPC;

/// <summary>
/// HUD images from the stage's COMPO bundle on the user's disc: the beat bar (BAR_BAL/BAR_STR), button
/// icons (GUI_*), the U-Rappin' meter words (RANK_0..5) and the score label and digits (SCORE, SCOR_SUJ).
/// </summary>
internal sealed class HudSprites : IDisposable
{
    private readonly List<Bitmap> _owned = [];

    public Bitmap? Urappin { get; private set; }
    public Bitmap? Exclaim { get; private set; }
    /// <summary>Rank words indexed like RankEngine (Awful 0 .. Cool 3).</summary>
    public Bitmap?[] Ranks { get; } = new Bitmap?[4];
    public Bitmap? CoolLocked { get; private set; }
    public Bitmap? ScoreLabel { get; private set; }
    public Bitmap?[] Digits { get; } = new Bitmap?[10];
    public Bitmap? Dot { get; private set; }
    public Bitmap? DotLit { get; private set; }
    public Bitmap? Star { get; private set; }
    public Bitmap? StarLit { get; private set; }
    public Dictionary<RapButton, Bitmap> Buttons { get; } = [];

    public static HudSprites Load(string compoBundle)
    {
        var hud = new HudSprites();
        var resources = IntBundle.Read(compoBundle, "", "")
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Bytes, StringComparer.OrdinalIgnoreCase);
        Bitmap? Get(string name)
        {
            if (!resources.TryGetValue(name, out var bytes)) return null;
            try { return hud.Own(TimImage.Decode(bytes).Bitmap); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return null; }
        }

        if (Get("RANK_0.TIM") is { } meter)
        {
            // "U rappin'" and "!" share one image; the widest transparent gap separates them.
            var parts = Segments(meter, 1);
            int split = 0, widest = -1;
            for (int i = 1; i < parts.Count; i++)
            {
                int gap = parts[i].Left - parts[i - 1].Right;
                if (gap > widest) { widest = gap; split = i; }
            }
            if (split > 0)
            {
                hud.Urappin = hud.Crop(meter, Rectangle.FromLTRB(parts[0].Left, 0, parts[split - 1].Right, meter.Height));
                hud.Exclaim = hud.Crop(meter, Rectangle.FromLTRB(parts[split].Left, 0, parts[^1].Right, meter.Height));
            }
            else hud.Urappin = meter;
        }
        hud.Ranks[RankEngine.Cool] = Get("RANK_1.TIM");
        hud.Ranks[RankEngine.Good] = Get("RANK_2.TIM");
        hud.Ranks[RankEngine.Bad] = Get("RANK_3.TIM");
        hud.Ranks[RankEngine.Awful] = Get("RANK_4.TIM");
        hud.CoolLocked = Get("RANK_5.TIM");
        hud.ScoreLabel = Get("SCORE.TIM");
        if (Get("SCOR_SUJ.TIM") is { } digits)
        {
            var cells = Segments(digits, 1);
            if (cells.Count != 10) cells = Enumerable.Range(0, 10).Select(i => new Rectangle(i * digits.Width / 10, 0, digits.Width / 10, digits.Height)).ToList();
            for (int i = 0; i < 10; i++) hud.Digits[i] = hud.Crop(digits, cells[i]);
        }
        hud.Dot = Get("BAR_BAL1.TIM");
        hud.DotLit = Get("BAR_BAL2.TIM");
        hud.Star = Get("BAR_STR1.TIM");
        hud.StarLit = Get("BAR_STR2.TIM");
        foreach (var (button, name) in new[] { (RapButton.Triangle, "GUI_SANK.TIM"), (RapButton.Circle, "GUI_MARU.TIM"), (RapButton.Cross, "GUI_PEKE.TIM"), (RapButton.Square, "GUI_SIKA.TIM"), (RapButton.L, "GUI_L.TIM"), (RapButton.R, "GUI_R.TIM") })
            if (Get(name) is { } icon) hud.Buttons[button] = icon;
        return hud;
    }

    private Bitmap Own(Bitmap bitmap)
    {
        _owned.Add(bitmap);
        return bitmap;
    }

    private Bitmap Crop(Bitmap source, Rectangle area) => Own(source.Clone(area, PixelFormat.Format32bppArgb));

    /// <summary>Column runs containing opaque pixels, separated by at least <paramref name="minGap"/> clear columns.</summary>
    private static List<Rectangle> Segments(Bitmap bitmap, int minGap)
    {
        var opaque = new bool[bitmap.Width];
        for (int x = 0; x < bitmap.Width; x++)
            for (int y = 0; y < bitmap.Height && !opaque[x]; y++)
                opaque[x] = bitmap.GetPixel(x, y).A > 0;
        var runs = new List<Rectangle>();
        int start = -1, lastOpaque = -1;
        for (int x = 0; x <= bitmap.Width; x++)
        {
            bool on = x < bitmap.Width && opaque[x];
            if (on)
            {
                if (start < 0) start = x;
                else if (x - lastOpaque - 1 >= minGap)
                {
                    runs.Add(Rectangle.FromLTRB(start, 0, lastOpaque + 1, bitmap.Height));
                    start = x;
                }
                lastOpaque = x;
            }
        }
        if (start >= 0) runs.Add(Rectangle.FromLTRB(start, 0, lastOpaque + 1, bitmap.Height));
        return runs;
    }

    public void Dispose()
    {
        foreach (var bitmap in _owned) bitmap.Dispose();
        _owned.Clear();
    }
}
