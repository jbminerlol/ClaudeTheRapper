using System.Runtime.InteropServices;

namespace PaRappaPC;

/// <summary>
/// Streams mono 16-bit stage music through winmm waveOut. All rank mixes share one
/// timeline, so switching mix keeps the playback position. The song clock comes from
/// waveOutGetPosition (samples the device has actually played), not a wall clock.
/// </summary>
internal sealed class SongPlayer : IDisposable
{
    private const int BufferCount = 8;
    private const int BufferSamples = 512; // ~13.5 ms each, so rap voices start within ~0.1 s
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrDone = 0x00000001;
    private const uint TimeSamples = 0x0002;

    private readonly Dictionary<int, short[]> _mixes;
    private readonly int _sampleRate;
    private readonly IntPtr _device;
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly IntPtr[] _data = new IntPtr[BufferCount];
    private readonly Thread _feeder;
    private readonly int _headerSize = Marshal.SizeOf<WaveHeader>();
    private readonly int _flagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
    private volatile int _mix;
    private volatile bool _stopping;
    private long _cursor;
    private readonly short[] _scratch = new short[BufferSamples];
    private readonly object _voiceLock = new();
    private ToneSample? _voice;
    private double _voicePos, _voiceStep, _voiceGain;

    public SongPlayer(Dictionary<int, short[]> mixes, int sampleRate, int startMix)
    {
        if (mixes.Count == 0) throw new ArgumentException("no audio channels", nameof(mixes));
        _mixes = mixes;
        _sampleRate = sampleRate;
        _mix = startMix;
        Length = mixes.Values.Min(m => m.Length);
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSec = (uint)sampleRate, BitsPerSample = 16,
            BlockAlign = 2, AvgBytesPerSec = (uint)sampleRate * 2,
        };
        timeBeginPeriod(1);
        Check(waveOutOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0), "waveOutOpen");
        Check(waveOutPause(_device), "waveOutPause");
        for (int i = 0; i < BufferCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferSamples * 2);
            _headers[i] = Marshal.AllocHGlobal(_headerSize);
            Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = BufferSamples * 2 }, _headers[i], false);
            Check(waveOutPrepareHeader(_device, _headers[i], (uint)_headerSize), "waveOutPrepareHeader");
            Queue(i);
        }
        _feeder = new Thread(Feed) { IsBackground = true, Name = "SongPlayer" };
        _feeder.Start();
    }

    /// <summary>Song length in samples.</summary>
    public int Length { get; }
    public double DurationSeconds => Length / (double)_sampleRate;
    public bool Finished => PositionSamples >= Length;

    public int Mix { get => _mix; set { if (_mixes.ContainsKey(value)) _mix = value; } }

    public void Start() => Check(waveOutRestart(_device), "waveOutRestart");

    /// <summary>Keys off the current rap voice and starts <paramref name="tone"/> (SsUtKeyOff + SsUtKeyOn).</summary>
    public void PlayVoice(ToneSample tone, int volume)
    {
        lock (_voiceLock)
        {
            _voice = tone;
            _voicePos = 0;
            _voiceStep = tone.Rate / (double)_sampleRate;
            _voiceGain = volume / 127.0 * (tone.Volume / 127.0) * 1.4;
        }
    }

    private void MixVoice()
    {
        lock (_voiceLock)
        {
            if (_voice is null) return;
            var pcm = _voice.Pcm;
            for (int i = 0; i < BufferSamples; i++)
            {
                int index = (int)_voicePos;
                if (index + 1 >= pcm.Length) { _voice = null; break; }
                double frac = _voicePos - index;
                double sample = (pcm[index] * (1 - frac) + pcm[index + 1] * frac) * _voiceGain;
                _scratch[i] = (short)Math.Clamp(_scratch[i] + sample, short.MinValue, short.MaxValue);
                _voicePos += _voiceStep;
            }
        }
    }

    public long PositionSamples
    {
        get
        {
            var time = new MmTime { Type = TimeSamples };
            return waveOutGetPosition(_device, ref time, (uint)Marshal.SizeOf<MmTime>()) == 0 ? time.Value : 0;
        }
    }

    public double PositionSeconds => PositionSamples / (double)_sampleRate;

    private void Queue(int index)
    {
        var source = _mixes[_mix];
        int count = (int)Math.Clamp(Length - _cursor, 0, BufferSamples);
        Array.Clear(_scratch);
        if (count > 0) Array.Copy(source, _cursor, _scratch, 0, count);
        _cursor += BufferSamples;
        MixVoice();
        Marshal.Copy(_scratch, 0, _data[index], BufferSamples);
        int flags = Marshal.ReadInt32(_headers[index], _flagsOffset);
        Marshal.WriteInt32(_headers[index], _flagsOffset, flags & ~(int)WhdrDone);
        waveOutWrite(_device, _headers[index], (uint)_headerSize);
    }

    private void Feed()
    {
        while (!_stopping)
        {
            bool queued = false;
            for (int i = 0; i < BufferCount && !_stopping; i++)
            {
                if ((Marshal.ReadInt32(_headers[i], _flagsOffset) & WhdrDone) != 0)
                {
                    Queue(i);
                    queued = true;
                }
            }
            if (!queued) Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _feeder.Join(500);
        waveOutReset(_device);
        for (int i = 0; i < BufferCount; i++)
        {
            waveOutUnprepareHeader(_device, _headers[i], (uint)_headerSize);
            Marshal.FreeHGlobal(_headers[i]);
            Marshal.FreeHGlobal(_data[i]);
        }
        waveOutClose(_device);
        timeEndPeriod(1);
    }

    private static void Check(int result, string call)
    {
        if (result != 0) throw new InvalidOperationException($"{call} failed with MMRESULT {result}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmTime
    {
        public uint Type;
        public uint Value;
        public uint Padding; // MMTIME's union is 8 bytes wide
    }

    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(uint period);
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr device, ref MmTime time, uint size);
}

/// <summary>Minimal XInput polling; bindings come from controls.json.</summary>
internal static class Gamepad
{
    private static bool _available = true;
    private static ushort _previous;

    /// <summary>Returns the XInput button bits newly pressed since the last call.</summary>
    public static ushort PollPressed()
    {
        if (!_available) return 0;
        try
        {
            if (XInputGetState(0, out var state) != 0) { _previous = 0; return 0; }
            ushort pressed = (ushort)(state.Buttons & ~_previous);
            _previous = state.Buttons;
            return pressed;
        }
        catch (DllNotFoundException) { _available = false; return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int index, out XInputState state);
}
