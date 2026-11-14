using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CHC.Core.Collection;

/// <summary>
/// Times a short, fixed calculation on one core, to say how fast the processor really is: its name and clock
/// speed don't tell that across fifteen years of processors. It reads nothing and changes nothing.
/// </summary>
public static class CpuBenchmark
{
    /// <summary>How long the calculation takes on the reference: a mid-range desktop processor of 2023 with six cores.</summary>
    private const double ReferenceMs = 38;

    private const int Rounds = 8_000_000;
    private const int Runs = 3;

    private static double _best;

    /// <summary>
    /// Speed of one core in percent of the reference, or null if it couldn't be timed. The best since CHC started:
    /// a result near a limit shouldn't change the verdict from one scan to the next.
    /// </summary>
    public static double? Measure()
    {
        if (Time() is not { } now) return _best > 0 ? _best : null;
        return _best = Math.Max(_best, now);
    }

    private static double? Time()
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        try
        {
            // Other programs shouldn't decide the result: a busy PC is a different finding.
            thread.Priority = ThreadPriority.AboveNormal;
            var table = new uint[1 << 18];
            Work(table, 200_000);

            // The best of a few runs: an interruption can only make a run slower, never faster.
            var best = double.MaxValue;
            for (var run = 0; run < Runs; run++)
            {
                var start = Stopwatch.GetTimestamp();
                Work(table, Rounds);
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            return best > 0 ? 100 * ReferenceMs / best : null;
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            thread.Priority = priority;
        }
    }

    /// <summary>
    /// Whole-number arithmetic, branches that can't be predicted and reads all over a 1 MB table: the mix everyday
    /// programs are made of. Nothing in it uses newer instructions, so an old processor runs the same code.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static uint Work(uint[] table, int rounds)
    {
        var mask = (uint)table.Length - 1;
        uint x = 2463534242, sum = 0;
        for (var i = 0; i < rounds; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            var at = x & mask;
            var value = table[at];
            if ((value & 1) == 0) sum += value * 31 + x;
            else sum ^= value >> 3;
            table[at] = value + sum;
        }
        return sum;
    }
}
