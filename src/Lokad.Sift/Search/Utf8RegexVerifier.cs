using System.Text.RegularExpressions;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class Utf8RegexVerifier
{
    public static void SearchDocument(MappedSegment segment, SegmentDocument document,
        PreparedUtf8Regex prepared, int hitLimit, IHitCollector collector,
        CancellationToken cancellationToken, ref Utf8RegexHitSink? sink,
        ref int hitsReturned, ref int invocations, ref int matchedDocuments,
        ref long matchesEnumerated, ref SearchStopReason stopReason)
    {
        sink ??= new Utf8RegexHitSink(cancellationToken);
        sink.Reset(segment, document, collector, hitLimit, hitsReturned);
        invocations++;
        try
        {
            sink.Checkpoint();
            RegexEngineGuards.Enumerate(prepared.Regex, segment.GetContentBytes(document), sink);
            sink.Checkpoint();
        }
        catch (RegexMatchTimeoutException)
        {
            if (prepared.RegexTimeoutPolicy == RegexTimeoutPolicy.Throw) throw;
            sink.StopReason = SearchStopReason.RegexTimeout;
        }
        finally
        {
            hitsReturned = sink.HitsReturned;
            matchesEnumerated += sink.MatchCount;
            stopReason = sink.StopReason;
            if (sink.MatchCount > 0) matchedDocuments++;
            sink.ReleaseInput();
        }
    }
}

// Reused for all documents of one segment operation. Neither an input span nor
// a match collection is stored or allocated by this sink.
internal sealed class Utf8RegexHitSink : ISiftRegexMatchSink
{
    private MappedSegment? _segment;
    private SegmentDocument _document;
    private IHitCollector? _collector;
    private int _limit;
    private int _previousStart;
    private int _previousEnd;
    private bool _previousEmpty;
    private bool _stopped;
    public Action Checkpoint { get; }
    public int HitsReturned { get; private set; }
    public int MatchCount { get; private set; }
    public SearchStopReason StopReason { get; set; }
    public Exception? StorageFailure { get; private set; }

    public Utf8RegexHitSink(CancellationToken cancellationToken) =>
        Checkpoint = cancellationToken.ThrowIfCancellationRequested;

    public void Reset(MappedSegment segment, SegmentDocument document, IHitCollector collector,
        int limit, int hitsReturned)
    {
        _segment = segment;
        _document = document;
        _collector = collector;
        _limit = limit;
        HitsReturned = hitsReturned;
        MatchCount = 0;
        StopReason = SearchStopReason.None;
        StorageFailure = null;
        _previousStart = -1;
        _previousEnd = 0;
        _previousEmpty = false;
        _stopped = false;
    }

    public bool OnMatch(int byteOffset, int byteLength)
    {
        Checkpoint();
        var segment = _segment ?? throw new InvalidOperationException("The regex sink is no longer active.");
        if (_stopped || byteOffset < 0 || byteLength < 0 ||
            byteOffset > _document.ContentLength - byteLength || byteOffset < _previousEnd ||
            (_previousEmpty && byteOffset == _previousStart))
            throw new InvalidOperationException("The supplied regex engine returned an invalid or unordered byte range.");
        _previousStart = byteOffset;
        _previousEnd = byteOffset + byteLength;
        _previousEmpty = byteLength == 0;
        int startLine;
        int endLine;
        try
        {
            var lines = segment.GetEncodedLineIndexBytes(_document);
            startLine = LineMap.GetLineFromByteOffset(lines, byteOffset);
            endLine = LineMap.GetLineFromByteOffset(lines, Math.Max(byteOffset, _previousEnd - 1));
        }
        catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
        {
            StorageFailure = exception;
            throw;
        }
        var keepGoing = _collector!.OnHit(new RawHit(segment.SegmentId,
            new DocumentOrdinal(_document.Ordinal), byteOffset, _previousEnd, startLine, endLine));
        HitsReturned++;
        MatchCount++;
        if (!keepGoing) StopReason = SearchStopReason.CollectorStopped;
        _stopped = !keepGoing || HitsReturned >= _limit;
        return !_stopped;
    }

    public void ReleaseInput()
    {
        _segment = null;
        _collector = null;
    }
}
