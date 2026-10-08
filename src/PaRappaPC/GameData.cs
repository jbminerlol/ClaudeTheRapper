using System.Buffers.Binary;
using System.Text.Json;

namespace PaRappaPC;

/// <summary>Face/shoulder buttons used by the charts (ids are the game's own).</summary>
internal enum RapButton { Triangle = 1, Circle = 2, Cross = 3, Square = 4, L = 5, R = 8 }

internal readonly record struct ChartNote(int Sixteenth, RapButton Button)
{
    public int Bar => Sixteenth / StageChart.SixteenthsPerBar;
    public int Step => Sixteenth % StageChart.SixteenthsPerBar;
}

/// <summary>
/// One 24-byte bar-list entry. Field meanings come from the executable's rank code
/// (FUN_80014d58 line scoring, FUN_80014548 Cool check, FUN_80024fd0 sequencer):
/// PhraseSlot/PhraseSound: the sound expected at one 16th-note slot to earn the rhythm bonus;
/// Penalty: weight for off-beat presses; CoolThreshold: points a pair must gain to go Good to Cool;
/// ButtonMask: buttons the line uses; WindowRow: row of the press-window table;
/// Segments: bars covered by the line scored here; Events: 1|2 score+verdict, 4 pair end,
/// 8 Awful checkpoint, 0x10 fail checkpoint at Bad/Awful.
/// </summary>
internal sealed record ChartBar(int Section, int PhraseSlot, int PhraseSound, int Penalty, int CoolThreshold,
    uint ButtonMask, int SoundSet, int WindowRow, int Segments, int Events);

/// <summary>
/// Rhythm chart read from a stage overlay (COMOD*.BIN). The overlay's setup code stores
/// pointers into executable globals at 0x800943c0..; see chart/extract_charts.py.
/// </summary>
internal sealed class StageChart
{
    public const int SixteenthsPerBar = 16;
    public const int TicksPerBar = 0x180;
    private const uint OverlayBase = 0x801C3870;

    public required IReadOnlyList<ChartNote> Notes { get; init; }
    public required IReadOnlyList<ChartBar> Bars { get; init; }

    /// <summary>Press-window rows: byte [slot / 2] is 2 where presses score, 0 where they are ignored.</summary>
    public required byte[][] WindowTable { get; init; }

    /// <summary>
    /// Rap voice table (pointer at 0x800943d8, 0x24-byte rows): [sound set][button sound id] is a list of
    /// SsUtKeyOn calls that successive presses step through (FUN_80014614 / FUN_80026fc4).
    /// </summary>
    public required List<SoundEntry[]?[]> SoundSets { get; init; }

    /// <summary>Bar entry for a 0-based song bar (the game's bar list is 1-based).</summary>
    public ChartBar? BarInfo(int bar) => bar + 1 < Bars.Count && bar >= 0 ? Bars[bar + 1] : null;

    /// <summary>
    /// Rap lines as the game scores them: every bar with event bits 1|2 closes a line covering its
    /// Segments bars. For display, a line whose presses all sit in the second half of a bar with a
    /// partial press window is a half-bar call and response (teacher first half, player second half).
    /// </summary>
    public IReadOnlyList<(int Start, int Length, int ScoreBar, List<ChartNote> Notes)> Lines()
    {
        var lines = new List<(int, int, int, List<ChartNote>)>();
        for (int bar = 0; bar + 1 < Bars.Count; bar++)
        {
            var info = Bars[bar + 1];
            if ((info.Events & 3) == 0) continue;
            int segments = Math.Max(1, (int)info.Segments);
            int start = (bar - segments + 1) * SixteenthsPerBar;
            int length = segments * SixteenthsPerBar;
            var notes = Notes.Where(n => n.Sixteenth >= start && n.Sixteenth < start + length).OrderBy(n => n.Sixteenth).ToList();
            if (segments == 1 && info.WindowRow < 8 && notes.Count > 0 && notes.All(n => n.Step >= 8))
            {
                start += 8;
                length = 8;
            }
            lines.Add((start, length, bar, notes));
        }
        return lines;
    }

    public static StageChart Read(string comodPath)
    {
        byte[] code = File.ReadAllBytes(comodPath);
        var g = ReadSetupGlobals(code);
        uint Need(int key) => g.TryGetValue(key, out uint v) ? v : throw new InvalidDataException($"{Path.GetFileName(comodPath)}: chart global {0x80090000 + key:x8} not found");

        int barCount = checked((int)Need(0x43C8));
        int noteCount = checked((int)Need(0x43E8));
        ReadOnlySpan<byte> bars = Table(code, Need(0x43C4), barCount * 24, comodPath);
        ReadOnlySpan<byte> notes = Table(code, Need(0x43E4), noteCount * 4, comodPath);

        var barList = new List<ChartBar>(barCount);
        for (int i = 0; i < barCount; i++)
        {
            var e = bars.Slice(i * 24, 24);
            barList.Add(new ChartBar(e[0], e[1], e[2], e[3], BinaryPrimitives.ReadInt16LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), e[0x0C], e[0x0D],
                BinaryPrimitives.ReadInt16LittleEndian(e[0x0E..]), BinaryPrimitives.ReadUInt16LittleEndian(e[0x10..])));
        }
        var noteList = new List<ChartNote>(noteCount);
        for (int i = 0; i < noteCount; i++)
        {
            int position = BinaryPrimitives.ReadUInt16LittleEndian(notes[(i * 4)..]);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(notes[(i * 4 + 2)..]);
            RapButton button = id switch { 6 => RapButton.L, 7 => RapButton.R, _ => (RapButton)id };
            if (!Enum.IsDefined(button)) throw new InvalidDataException($"{Path.GetFileName(comodPath)}: unknown button id {id}");
            noteList.Add(new ChartNote(position, button));
        }
        uint windowAddress = Need(0x43E0);
        int maxRow = barList.Max(b => b.WindowRow);
        byte[] windows = Table(code, windowAddress, (maxRow + 1) * 16, comodPath).ToArray();
        var windowRows = Enumerable.Range(0, maxRow + 1).Select(r => windows.AsSpan(r * 16, 16).ToArray()).ToArray();
        var soundSets = new List<SoundEntry[]?[]>();
        if (g.TryGetValue(0x43D8, out uint soundTable))
        {
            int rows = (int)Math.Clamp(g.TryGetValue(0x43DC, out uint n) ? n : 0, 0, 256);
            rows = Math.Max(rows, barList.Max(b => b.SoundSet) + 1);
            for (int row = 0; row < rows; row++)
            {
                var columns = new SoundEntry[]?[9];
                for (int col = 0; col < 9; col++)
                {
                    uint slot = U32At(code, soundTable + (uint)(row * 0x24 + col * 4));
                    if (slot == 0) continue;
                    uint list = U32At(code, slot);
                    int count = (int)(U32At(code, slot + 4) & 0xFFFF);
                    if (list == 0 || count is <= 0 or > 255) continue;
                    var entries = new SoundEntry[count];
                    bool valid = true;
                    for (int i = 0; i < count && valid; i++)
                    {
                        long o = (long)list + i * 12 - OverlayBase;
                        if (o < 0 || o + 12 > code.Length) { valid = false; break; }
                        entries[i] = new SoundEntry(code[o], code[o + 1], code[o + 3]);
                    }
                    if (valid) columns[col] = entries;
                }
                soundSets.Add(columns);
            }
        }
        return new StageChart { Notes = noteList, Bars = barList, WindowTable = windowRows, SoundSets = soundSets };
    }

    private static uint U32At(byte[] code, uint address)
    {
        long o = (long)address - OverlayBase;
        return o >= 0 && o + 4 <= code.Length ? BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan((int)o, 4)) : 0;
    }

    private static ReadOnlySpan<byte> Table(byte[] code, uint address, int size, string path)
    {
        long offset = (long)address - OverlayBase;
        if (offset < 0 || offset + size > code.Length) throw new InvalidDataException($"{Path.GetFileName(path)}: table {address:x8} is outside the overlay");
        return code.AsSpan((int)offset, size);
    }

    /// <summary>Replays the straight-line setup code around the 0x800943e4 store, tracking lui/addiu/ori/lw.</summary>
    private static Dictionary<int, uint> ReadSetupGlobals(byte[] code)
    {
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(o, 4));
        int anchor = -1;
        for (int i = 0; i + 4 <= code.Length; i += 4)
        {
            uint w = U32(i);
            if (w >> 26 == 0x2B && (w & 0xFFFF) == 0x43E4) { anchor = i; break; }
        }
        if (anchor < 0) throw new InvalidDataException("chart setup code not found");

        var regs = new uint?[32];
        regs[0] = 0;
        var found = new Dictionary<int, uint>();
        for (int i = Math.Max(0, anchor - 0x400); i < Math.Min(code.Length - 4, anchor + 0x400); i += 4)
        {
            uint w = U32(i);
            int op = (int)(w >> 26), rs = (int)(w >> 21) & 31, rt = (int)(w >> 16) & 31;
            uint imm = w & 0xFFFF;
            int simm = (short)imm;
            uint? result = null;
            bool writes = true;
            switch (op)
            {
                case 0x0F: result = imm << 16; break;
                case 0x09: result = regs[rs] is uint a ? (uint)(a + simm) : null; break;
                case 0x0D: result = regs[rs] is uint b ? b | imm : null; break;
                case 0x23:
                    if (regs[rs] is uint baseAddr)
                    {
                        long o = (long)(uint)(baseAddr + simm) - OverlayBase;
                        result = o >= 0 && o + 4 <= code.Length ? U32((int)o) : null;
                    }
                    break;
                case 0x2B:
                    writes = false;
                    if (regs[rs] == 0x80090000 && regs[rt] is uint value) found[(int)imm] = value;
                    break;
                default: writes = false; break;
            }
            if (writes && rt != 0) regs[rt] = result;
        }
        return found;
    }
}

/// <summary>One SsUtKeyOn call from the rap voice table: VAB program, tone and volume (0-127).</summary>
internal readonly record struct SoundEntry(int Program, int Tone, int Volume);

/// <summary>A decoded VAB tone: mono PCM at the rate the game plays it (note = tone + 24).</summary>
internal sealed record ToneSample(short[] Pcm, int Rate, int Volume);

/// <summary>
/// SPU sound bank (VAB) embedded in an INT bundle: header, 128 program records, 16 tone
/// records per used program, a VAG size table, then the VAG ADPCM bodies. See chart/rip_sounds.py.
/// </summary>
internal sealed class SoundBank
{
    private static readonly int[] K0 = [0, 60, 115, 98, 122];
    private static readonly int[] K1 = [0, 0, -52, -55, -60];
    private readonly Dictionary<(int Program, int Tone), ToneSample> _tones = [];

    public ToneSample? Get(int program, int tone) => _tones.TryGetValue((program, tone), out var t) ? t : null;
    public int Count => _tones.Count;

    public static SoundBank Read(string bundlePath)
    {
        byte[] data = File.ReadAllBytes(bundlePath);
        var bank = new SoundBank();
        int o = data.AsSpan().IndexOf("pBAV"u8);
        if (o < 0) return bank;
        int programs = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 18));
        int vagCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 22));
        int toneBase = o + 32 + 2048;
        int sizeTable = toneBase + programs * 512;
        var vags = new Dictionary<int, (int Start, int Size)>();
        int cursor = sizeTable + 512;
        for (int v = 1; v <= vagCount; v++)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sizeTable + v * 2)) << 3;
            vags[v] = (cursor, size);
            cursor += size;
        }
        int slot = 0;
        for (int program = 0; program < 128; program++)
        {
            int toneCount = data[o + 32 + program * 16];
            if (toneCount == 0) continue;
            for (int t = 0; t < toneCount && t < 16; t++)
            {
                int a = toneBase + slot * 512 + t * 32;
                int vag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(a + 22));
                if (!vags.TryGetValue(vag, out var body) || body.Start + body.Size > data.Length) continue;
                int center = data[a + 4], shift = data[a + 5];
                double rate = 44100 * Math.Pow(2, (t + 24 - center + shift / 128.0) / 12.0);
                bank._tones[(program, t)] = new ToneSample(DecodeVag(data.AsSpan(body.Start, body.Size)), (int)Math.Round(Math.Clamp(rate, 4000, 48000)), data[a + 2]);
            }
            slot++;
        }
        return bank;
    }

    private static short[] DecodeVag(ReadOnlySpan<byte> data)
    {
        var output = new List<short>(data.Length * 2);
        int h0 = 0, h1 = 0;
        for (int block = 0; block + 16 <= data.Length; block += 16)
        {
            int shift = data[block] & 0x0F, filter = Math.Min(data[block] >> 4, 4), flags = data[block + 1];
            if (shift > 12) shift = 9;
            for (int i = 0; i < 28; i++)
            {
                int b = data[block + 2 + i / 2];
                int nibble = (i & 1) != 0 ? b >> 4 : b & 0x0F;
                int value = ((short)(nibble << 12) >> shift) + ((h0 * K0[filter] + h1 * K1[filter] + 32) >> 6);
                value = Math.Clamp(value, short.MinValue, short.MaxValue);
                h1 = h0;
                h0 = value;
                output.Add((short)value);
            }
            if ((flags & 1) != 0 && block > 0) break;
        }
        return output.ToArray();
    }
}

/// <summary>CD-XA ADPCM decoding straight from the raw 2352-byte-sector disc image.</summary>
internal static class XaAudio
{
    public const int SampleRate = 37800;
    private const int Sector = 2352;
    private static readonly int[] K0 = [0, 60, 115, 98, 122];
    private static readonly int[] K1 = [0, 0, -52, -55, -60];

    /// <summary>Decodes the requested mono channels of an interleaved XA file.</summary>
    public static Dictionary<int, short[]> Decode(string binPath, long lba, int sectors, IReadOnlyCollection<int> wanted)
    {
        var outputs = wanted.ToDictionary(c => c, _ => new List<short>(SampleRate * 200));
        var history = wanted.ToDictionary(c => c, _ => new int[2]);
        using var disc = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        disc.Seek(lba * Sector, SeekOrigin.Begin);
        var raw = new byte[Sector];
        for (int s = 0; s < sectors; s++)
        {
            if (disc.Read(raw, 0, Sector) < Sector) break;
            int channel = raw[17], submode = raw[18], coding = raw[19];
            if ((submode & 0x04) == 0 || !outputs.TryGetValue(channel, out var output)) continue;
            if ((coding & 0x01) != 0) throw new InvalidDataException("stereo XA is not used by the song stages");
            if (((coding >> 4) & 3) != 0) throw new InvalidDataException("8-bit XA is not supported");
            int[] h = history[channel];
            for (int group = 0; group < 18; group++)
            {
                int g = 24 + group * 128;
                for (int unit = 0; unit < 8; unit++)
                {
                    int header = raw[g + 4 + unit];
                    int shift = header & 0x0F;
                    if (shift > 12) shift = 9;
                    int filter = Math.Min((header >> 4) & 3, 4);
                    for (int i = 0; i < 28; i++)
                    {
                        int b = raw[g + 16 + i * 4 + unit / 2];
                        int nibble = (unit & 1) != 0 ? b >> 4 : b & 0x0F;
                        int value = (short)(nibble << 12) >> shift;
                        value += (h[0] * K0[filter] + h[1] * K1[filter] + 32) >> 6;
                        value = Math.Clamp(value, short.MinValue, short.MaxValue);
                        h[1] = h[0];
                        h[0] = value;
                        output.Add((short)value);
                    }
                }
            }
        }
        return outputs.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }
}

/// <summary>Locates the user's extracted disc files and raw disc image.</summary>
internal sealed class GameFiles
{
    public required string AssetsDir { get; init; }
    public required string BinPath { get; init; }
    public required Dictionary<string, (long Lba, int Size)> Files { get; init; }

    private static string SavedFolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaRappaPC", "game-folder.txt");

    /// <summary>Game folder layout: &lt;root&gt;\assets\manifest.json (extracted files) and &lt;root&gt;\source\*.bin (raw image).</summary>
    public static bool IsGameFolder(string root) =>
        File.Exists(Path.Combine(root, "assets", "manifest.json")) &&
        Directory.Exists(Path.Combine(root, "source")) &&
        Directory.EnumerateFiles(Path.Combine(root, "source"), "*.bin").Any();

    public static void SaveFolder(string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavedFolderPath)!);
        File.WriteAllText(SavedFolderPath, root);
    }

    public static GameFiles? Find()
    {
        string? saved = File.Exists(SavedFolderPath) ? File.ReadAllText(SavedFolderPath).Trim() : null;
        string?[] roots = [saved, Path.Combine(AppContext.BaseDirectory, "game"), AppContext.BaseDirectory];
        foreach (string? root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string assets = Path.Combine(root, "assets");
            string manifest = Path.Combine(assets, "manifest.json");
            string? bin = Directory.Exists(Path.Combine(root, "source"))
                ? Directory.EnumerateFiles(Path.Combine(root, "source"), "*.bin").FirstOrDefault()
                : null;
            if (!File.Exists(manifest) || bin is null) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var files = new Dictionary<string, (long, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
                files[f.GetProperty("path").GetString()!.Replace('\\', '/')] = (f.GetProperty("lba").GetInt64(), f.GetProperty("size").GetInt32());
            return new GameFiles { AssetsDir = assets, BinPath = bin, Files = files };
        }
        return null;
    }

    public string AssetPath(string relative) => Path.Combine(AssetsDir, relative.Replace('/', Path.DirectorySeparatorChar));

    public (long Lba, int Sectors) DiscLocation(string relative)
    {
        if (!Files.TryGetValue(relative, out var f)) throw new FileNotFoundException($"{relative} is not in the disc manifest");
        return (f.Lba, (f.Size + 2047) / 2048);
    }
}
