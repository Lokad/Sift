using System.Diagnostics;

namespace Lokad.Sift.Search;

internal readonly struct ValueStopwatch
{
    private readonly long _startTimestamp;

    private ValueStopwatch(long startTimestamp)
    {
        _startTimestamp = startTimestamp;
    }

    public long ElapsedMilliseconds =>
        Stopwatch.GetElapsedTime(_startTimestamp).Ticks / TimeSpan.TicksPerMillisecond;

    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());
}
