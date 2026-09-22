using System.Numerics;

namespace Norristown.Semantics;

/// <summary>
/// A set of the 65816's 256 banks, such as the <c>[$00..$3f, $80..$bf]</c> a <c>dbr</c> item may
/// say the data bank lies in. It is four words of bits, so two sets that hold the same banks are
/// equal however they were written.
/// </summary>
/// <param name="Low">Banks <c>$00</c> to <c>$3f</c>.</param>
/// <param name="LowMiddle">Banks <c>$40</c> to <c>$7f</c>.</param>
/// <param name="HighMiddle">Banks <c>$80</c> to <c>$bf</c>.</param>
/// <param name="High">Banks <c>$c0</c> to <c>$ff</c>.</param>
public readonly record struct BankSet(ulong Low, ulong LowMiddle, ulong HighMiddle, ulong High)
{
    /// <summary>How many banks it holds.</summary>
    public int Count =>
        BitOperations.PopCount(Low) + BitOperations.PopCount(LowMiddle)
        + BitOperations.PopCount(HighMiddle) + BitOperations.PopCount(High);

    /// <summary>The banks it holds, lowest first.</summary>
    public IEnumerable<long> Banks
    {
        get
        {
            for (var bank = 0L; bank <= 0xff; bank++)
            {
                if (Contains(bank))
                    yield return bank;
            }
        }
    }

    /// <summary>The banks it holds as runs from the lowest to the highest of each: <c>($00, $3f), ($80, $bf)</c>.</summary>
    public IEnumerable<(long First, long Last)> Runs
    {
        get
        {
            long? first = null;
            for (var bank = 0L; bank <= 0x100; bank++)
            {
                var held = bank <= 0xff && Contains(bank);
                if (held && first is null)
                {
                    first = bank;
                }
                else if (!held && first is { } start)
                {
                    yield return (start, bank - 1);
                    first = null;
                }
            }
        }
    }

    /// <summary>The set with the banks <paramref name="first"/> to <paramref name="last"/> added.</summary>
    public BankSet With(long first, long last)
    {
        var set = this;
        for (var bank = first; bank <= last; bank++)
            set = set.With(bank);
        return set;
    }

    /// <summary>Whether it holds <paramref name="bank"/>.</summary>
    public bool Contains(long bank) =>
        bank is >= 0 and <= 0xff && (Word(bank) & (1UL << (int)(bank & 63))) != 0;

    /// <summary>The banks it and <paramref name="other"/> both hold.</summary>
    public BankSet Intersect(BankSet other) =>
        new(Low & other.Low, LowMiddle & other.LowMiddle, HighMiddle & other.HighMiddle, High & other.High);

    /// <summary>Whether every bank it holds is one <paramref name="other"/> holds.</summary>
    public bool IsSubsetOf(BankSet other) =>
        (Low & ~other.Low) == 0 && (LowMiddle & ~other.LowMiddle) == 0
        && (HighMiddle & ~other.HighMiddle) == 0 && (High & ~other.High) == 0;

    /// <summary>The set as a message says it: <c>$00-$3f, $80-$bf</c>.</summary>
    public string Spell() => string.Join(", ", Runs.Select(run => run.First == run.Last
        ? StateValue.Hex(run.First, 2)
        : $"{StateValue.Hex(run.First, 2)}-{StateValue.Hex(run.Last, 2)}"));

    /// <summary>The set as an item writes it: <c>[$00..$3f, $80..$bf]</c>.</summary>
    public string Write() => "[" + string.Join(", ", Runs.Select(run => run.First == run.Last
        ? StateValue.Hex(run.First, 2)
        : $"{StateValue.Hex(run.First, 2)}..{StateValue.Hex(run.Last, 2)}")) + "]";

    private BankSet With(long bank)
    {
        var bit = 1UL << (int)(bank & 63);
        return (bank >> 6) switch
        {
            0 => this with { Low = Low | bit },
            1 => this with { LowMiddle = LowMiddle | bit },
            2 => this with { HighMiddle = HighMiddle | bit },
            _ => this with { High = High | bit },
        };
    }

    private ulong Word(long bank) => (bank >> 6) switch
    {
        0 => Low,
        1 => LowMiddle,
        2 => HighMiddle,
        _ => High,
    };
}
