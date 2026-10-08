namespace PaRappaPC;

/// <summary>
/// The U-Rappin' rank system, ported from the decompiled executable:
///   FUN_80014614 records presses into per-bar 16th-note slots,
///   FUN_80014d58 / FUN_80014a80 score a line,
///   FUN_80014d28 / FUN_80014c80 give the good/bad verdict (normal / Cool mode),
///   FUN_80014548 is the pair-end check (Good to Cool threshold, Awful fail flag),
///   FUN_800144b8 is the Awful checkpoint, and FUN_80024fd0 sequences it all per bar.
/// Positions are in song ticks: 0x180 per bar, 0x18 per 16th note.
/// </summary>
internal sealed class RankEngine
{
    // Rank order used by the UI; the game stores Cool 0, Good 1, Bad 2, Awful 3.
    public const int Awful = 0, Bad = 1, Good = 2, Cool = 3;
    public const int TicksPerBar = 0x180, TicksPerSixteenth = 0x18;
    // The sequencer scores at sub-bar 28/32 (_DAT_800943c0[1]) = tick 336, the exact time of slot 14, which
    // charts use (e.g. a closing L/R). Scoring just after slot 14's late window keeps on-time presses there.
    private const int ScoreTick = 14 * TicksPerSixteenth + WindowTicks + 1;
    private const int CoolLineMinimum = 0x42; // FUN_80014c80: a Cool-mode line must gain at least 66 points

    // Press window: the stage overlay's input loop sets event +0x34 to stage parameter +0x168 / 2, and the
    // setup routine stores 0x10 there, so presses within 8 ticks (a third of a 16th) of the grid count.
    public const int WindowTicks = 8;

    // FUN_80014d58 reads 15 slots for a one-bar line, 16 + 15 for two bars (table at 0x80048ca8).
    private static readonly int[][] SegmentSlots = [[], [15], [16, 15]];

    // Meter flash after the first line of a pair, game table at 0x80055420: [game rank][verdict].
    private static readonly int[,] FlashTable = { { 1, 0 }, { 2, 0 }, { 3, 1 }, { 3, 2 } };

    private readonly StageChart _chart;
    private readonly bool _coolUnlocked; // _DAT_8009182a: enables Good to Cool and the rhythm bonus
    private readonly Dictionary<int, Slot[]> _buffers = [];
    private int _presses, _offBeat;                // _DAT_80091810, _DAT_80091814 (reset every bar)
    private int _verdictSnapshot, _pairSnapshot;    // _DAT_80091818, _DAT_8009181c
    private int _silentLines;                       // _DAT_80091820
    private bool _failPending;                      // _DAT_8009181e
    private int _coolMisses;                        // _DAT_80091828
    private int _pairFirst = 2, _pairCount;         // _DAT_8008ed36, _DAT_8008ed38
    private int _nextStartBar, _nextScoreBar;

    public RankEngine(StageChart chart, bool coolUnlocked)
    {
        _chart = chart;
        _coolUnlocked = coolUnlocked;
    }

    public int Rank { get; private set; } = Good;
    public int Points { get; private set; }         // _DAT_80091816
    public bool Failed { get; private set; }
    public int LastBar => _chart.Bars.Count - 2;

    public enum PressResult { Ignored, OnBeat, OffBeat }

    /// <summary>Sound set the last press plays (0 = silent), as FUN_80014614 picks it.</summary>
    public int LastSoundSet { get; private set; }

    public readonly record struct Event(string Text, int? NewRank = null, int Bar = -1);

    private struct Slot
    {
        public uint Mask;
        public int Sound;
        public int Count;
    }

    private static uint MaskOf(RapButton b) => b switch
    {
        RapButton.Triangle => 0x10, RapButton.Circle => 0x20, RapButton.Cross => 0x40, RapButton.Square => 0x80,
        RapButton.L => 0x04, _ => 0x08,
    };

    // FUN_80024b54
    private static int SoundOf(RapButton b) => b switch
    {
        RapButton.Triangle => 1, RapButton.Circle => 2, RapButton.Cross => 3, RapButton.Square => 4,
        RapButton.L => 5, _ => 7,
    };

    private Slot[] Buffer(int bar)
    {
        if (!_buffers.TryGetValue(bar, out var slots)) _buffers[bar] = slots = new Slot[16];
        return slots;
    }

    /// <summary>FUN_80014614: a press at song tick <paramref name="tick"/>.</summary>
    public PressResult Press(RapButton button, double tick)
    {
        LastSoundSet = 0;
        if (Failed) return PressResult.Ignored;
        int position = (int)Math.Floor(tick) + WindowTicks;
        if (position < 0) return PressResult.Ignored;
        int bar = position / TicksPerBar;
        int within = position % TicksPerBar;
        int slot = within / TicksPerSixteenth;
        bool late = within % TicksPerSixteenth > WindowTicks * 2;
        var info = _chart.BarInfo(bar);
        if (info is null || info.SoundSet == 0 || info.WindowRow >= _chart.WindowTable.Length) return PressResult.Ignored;
        byte window = _chart.WindowTable[info.WindowRow][slot / 2];
        if (window == 0) return PressResult.Ignored;
        LastSoundSet = info.SoundSet;
        if (window != 2) return PressResult.Ignored;

        _presses++;
        if (late)
        {
            _offBeat++;
            return PressResult.OffBeat;
        }
        ref var s = ref Buffer(bar)[slot];
        s.Mask = MaskOf(button);
        s.Sound = SoundOf(button);
        s.Count++;
        return PressResult.OnBeat;
    }

    /// <summary>Runs the per-bar sequencer up to song tick <paramref name="tick"/>.</summary>
    public IEnumerable<Event> Advance(double tick)
    {
        var events = new List<Event>();
        while (!Failed)
        {
            // Bar-start checkpoints and sub-bar scoring interleave in song order.
            long start = _nextStartBar <= LastBar ? (long)_nextStartBar * TicksPerBar : long.MaxValue;
            long score = _nextScoreBar <= LastBar ? (long)_nextScoreBar * TicksPerBar + ScoreTick : long.MaxValue;
            long next = Math.Min(start, score);
            if (next == long.MaxValue || next > tick) break;
            if (start <= score) BarStart(_nextStartBar++, events);
            else ScoreBar(_nextScoreBar++, events);
        }
        return events;
    }

    public bool Finished => _nextScoreBar > LastBar;

    // Checkpoints at the start of a bar (FUN_80024fd0 at sub-bar _DAT_800943c0[0]).
    private void BarStart(int bar, List<Event> events)
    {
        var info = _chart.BarInfo(bar);
        if (info is null) return;
        if ((info.Events & 0x10) != 0 && Rank is Bad or Awful) Fail(events);
        else if ((info.Events & 0x08) != 0 && AwfulCheckpoint()) Fail(events);
    }

    // FUN_800144b8
    private bool AwfulCheckpoint()
    {
        bool fail = false;
        if (Rank == Awful) fail = _silentLines > 0 || _failPending;
        else if (Rank == Cool) fail = _failPending;
        _silentLines = 0;
        _failPending = false;
        return fail;
    }

    private void Fail(List<Event> events)
    {
        Failed = true;
        events.Add(new Event("FAIL"));
    }

    // FUN_80024fd0 at sub-bar _DAT_800943c0[1].
    private void ScoreBar(int bar, List<Event> events)
    {
        var info = _chart.BarInfo(bar);
        if (info is null) return;
        bool fullReset = false;
        if ((info.Events & 3) != 0)
        {
            int gained = ScoreLine(bar, info);
            if (_presses == 0) _silentLines++; // FUN_80014458
            events.Add(new Event($"{(gained >= 0 ? "+" : "")}{gained}", Bar: bar));
        }
        if ((info.Events & 2) != 0)
        {
            if (Rank == Cool) CoolVerdict(events);
            else Verdict(events);
        }
        if (Rank != Cool && (info.Events & 4) != 0) fullReset = ResolvePair(info, events);

        // FUN_80014c1c every bar; FUN_80014400 after a rank change.
        _presses = 0;
        _offBeat = 0;
        if (fullReset)
        {
            _silentLines = 0;
            _failPending = false;
            _coolMisses = 0;
        }
    }

    // FUN_80014d58 (first half-record path).
    private int ScoreLine(int bar, ChartBar info)
    {
        int line;
        int segments = info.Segments;
        if (_presses == 0) line = -1;
        else if (segments is < 1 or > 2) line = 0;
        else
        {
            var buffers = Enumerable.Range(bar - segments + 1, segments).Select(Buffer).ToArray();
            uint union = 0;
            int hits = 0;
            for (int k = 0; k < segments; k++)
                for (int i = 0; i < SegmentSlots[segments][k]; i++)
                {
                    var s = buffers[k][i];
                    union |= s.Mask;
                    if (s.Count != 0 && (info.ButtonMask == 0 || (info.ButtonMask & s.Mask) != 0)) hits++;
                }
            if (info.ButtonMask != 0 && (union & info.ButtonMask) != info.ButtonMask) hits = 0;

            int score;
            int maxPresses = segments * 12;
            if (buffers[0][info.PhraseSlot & 15].Sound == info.PhraseSound && _presses <= maxPresses)
            {
                score = hits * 3;
                if (hits > 0)
                    for (int k = 0; k < segments; k++) score += RhythmBonus(buffers[k], SegmentSlots[segments][k]);
            }
            else score = hits * 3 - info.Penalty;

            int penalty = Math.Max(-(info.Penalty * _offBeat), ~(info.Penalty * 3));
            if (_presses > maxPresses) score = penalty = 0; // button mashing
            line = score + penalty;
        }
        int before = Points;
        Points = Math.Max(0, Points + line);
        return Points - before;
    }

    // FUN_80014a80: rewards mixing on-beat, off-beat and paired 16ths.
    private int RhythmBonus(Slot[] slots, int count)
    {
        if (!_coolUnlocked) return 0;
        int empty = 0, offOnly = 0, onOnly = 0, both = 0, kinds = 0;
        for (int i = 0; i < count; i += 2)
        {
            bool first = slots[i].Count != 0, second = i + 1 < 16 && slots[i + 1].Count != 0;
            if (!first && !second) empty++;
            else if (!first) { kinds |= 2; offOnly++; }
            else if (!second) { kinds |= 4; onOnly++; }
            else { kinds |= 8; both++; }
        }
        return kinds switch
        {
            0xE => offOnly * 15 + onOnly * 6 + both * 9 + (empty > 0 ? 0x12 : 0),
            0xC => both * 9 + onOnly * 6,
            0xA => both * 9 + offOnly * 15,
            0x6 => offOnly * 15 + onOnly * 6,
            _ => 0,
        };
    }

    private static int ToGame(int rank) => 3 - rank;
    private static int FromGame(int rank) => 3 - rank;

    // FUN_80014d28 and the pair bookkeeping around it.
    private void Verdict(List<Event> events)
    {
        int verdict = Points > _verdictSnapshot ? 1 : 0;
        _verdictSnapshot = Points;
        if (_pairCount != 0)
        {
            _failPending = Rank == Awful && _pairFirst == 0 && verdict == 0;
            if (_pairFirst != verdict) _pairFirst = 2;
            _pairCount = 0;
            return;
        }
        if (!(Rank == Good && verdict == 1))
            events.Add(new Event($"FLASH {FromGame(FlashTable[ToGame(Rank), verdict])}"));
        _pairFirst = verdict;
        _pairCount++;
    }

    // FUN_80014c80: in Cool mode two weak lines in a row drop back to Good.
    private void CoolVerdict(List<Event> events)
    {
        int gain = Points - _verdictSnapshot;
        _verdictSnapshot = Points;
        if (gain < CoolLineMinimum)
        {
            if (++_coolMisses >= 2)
            {
                _coolMisses = 0;
                Rank = Good;
                events.Add(new Event("RANK", Good));
            }
            else events.Add(new Event($"FLASH {Good}"));
        }
        else _coolMisses = 0;
    }

    // FUN_80024fd0 pair end + FUN_80014548. Returns true when the rank changed.
    private bool ResolvePair(ChartBar info, List<Event> events)
    {
        int outcome; // 1 up, 0 down, 2 no change
        if (_pairFirst == 1) outcome = Rank == Good ? (PairCheck(info) == 1 ? 1 : 2) : 1;
        else if (_pairFirst == 0) outcome = Rank == Awful ? (PairCheck(info) != 0 ? 2 : 0) : 0;
        else outcome = 2;
        _pairFirst = 2;
        _pairCount = 0;

        if (outcome == 2 || (Rank == Awful && outcome == 0) || (Rank == Good && outcome == 1 && !_coolUnlocked)) return false;
        int before = Rank;
        Rank = Rank switch
        {
            Good => outcome == 1 ? Cool : Bad,
            Bad => outcome == 1 ? Good : Awful,
            _ => Bad,
        };
        events.Add(new Event(Rank > before ? "UP" : "DOWN", Rank));
        return true;
    }

    // FUN_80014548
    private int PairCheck(ChartBar info)
    {
        int gain = Points - _pairSnapshot;
        _pairSnapshot = Points;
        if (Rank == Good)
        {
            if (info.CoolThreshold < gain) return 1;
        }
        else if (Rank != Bad)
        {
            if (gain > 0) return 1;
            if (gain < 0) { _failPending = true; return 0; }
            return 2;
        }
        else if (gain > 0) return 1;
        if (gain < 0) return 0;
        return _failPending ? 0 : 2;
    }
}
