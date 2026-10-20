using System.Text;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class HitMaterializer
{
    public static MaterializedHit Materialize(
        MappedSegment segment,
        in RawHit hit,
        int contextBefore,
        int contextAfter,
        SiftOptions options)
    {
        var document = segment.GetDocument(hit.DocumentOrdinal.Value);
        return Materialize(
            segment.GetPath(document),
            segment.GetContentBytes(document),
            segment.GetEncodedLineIndexBytes(document),
            (document.Flags & SegmentDocumentFlags.Ascii) != 0,
            hit,
            contextBefore,
            contextAfter,
            options);
    }

    public static MaterializedHitWithLine MaterializeWithMatchingLine(
        MappedSegment segment,
        in RawHit hit,
        int contextBefore,
        int contextAfter,
        SiftOptions options)
    {
        var document = segment.GetDocument(hit.DocumentOrdinal.Value);
        var contentBytes = segment.GetContentBytes(document);
        var encodedLineIndex = segment.GetEncodedLineIndexBytes(document);
        var asciiDocument = (document.Flags & SegmentDocumentFlags.Ascii) != 0;
        var materialized = Materialize(
            segment.GetPath(document),
            contentBytes,
            encodedLineIndex,
            asciiDocument,
            hit,
            contextBefore,
            contextAfter,
            options);
        var matchingLineStart = LineMap.GetLineStart(encodedLineIndex, materialized.StartLine);
        var matchingLineEnd = LineMap.GetLineEndExclusive(
            encodedLineIndex,
            materialized.StartLine,
            contentBytes.Length);
        var matchingLine = DecodeText(
            contentBytes[matchingLineStart..matchingLineEnd],
            asciiDocument,
            trimLineEnd: true);
        return new MaterializedHitWithLine(materialized, matchingLine);
    }

    private static MaterializedHit Materialize(
        string path,
        ReadOnlySpan<byte> contentBytes,
        ReadOnlySpan<byte> encodedLineIndex,
        bool asciiDocument,
        in RawHit hit,
        int contextBefore,
        int contextAfter,
        SiftOptions options)
    {
        if (hit.StartByte < 0 || hit.EndByte < hit.StartByte || hit.EndByte > contentBytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(hit), "Hit byte bounds are outside the document.");
        }

        var startLine = hit.StartLine > 0
            ? hit.StartLine
            : LineMap.GetLineFromByteOffset(encodedLineIndex, hit.StartByte);
        var endLine = hit.EndLine > 0
            ? hit.EndLine
            : LineMap.GetLineFromByteOffset(
                encodedLineIndex,
                Math.Max(hit.StartByte, hit.EndByte == 0 ? hit.StartByte : hit.EndByte - 1));
        var startColumn = GetUtf16Column(
            contentBytes,
            encodedLineIndex,
            hit.StartByte,
            startLine,
            asciiDocument);
        var endColumn = GetUtf16Column(
            contentBytes,
            encodedLineIndex,
            hit.EndByte,
            endLine,
            asciiDocument);
        var materializedMatchEnd = hit.StartByte +
            Math.Min(hit.EndByte - hit.StartByte, options.MaxMaterializedMatchBytes);
        if (materializedMatchEnd < hit.EndByte && !asciiDocument)
        {
            while (materializedMatchEnd > hit.StartByte &&
                   (contentBytes[materializedMatchEnd] & 0xC0) == 0x80)
            {
                materializedMatchEnd--;
            }
        }

        var isMatchTruncated = materializedMatchEnd < hit.EndByte;
        var matchText = materializedMatchEnd > hit.StartByte
            ? DecodeText(
                contentBytes[hit.StartByte..materializedMatchEnd],
                asciiDocument,
                trimLineEnd: false)
            : string.Empty;
        var contextLineBudget = options.MaxMaterializedContextLines;
        var contextByteBudget = options.MaxMaterializedContextBytes;
        var beforeStartLine = (int)Math.Max(1L, (long)startLine - contextBefore);
        var afterStartLine = endLine == int.MaxValue ? int.MaxValue : endLine + 1;
        var afterEndLine = (int)Math.Min(int.MaxValue, (long)endLine + contextAfter);
        var beforeContext = CollectContext(
            contentBytes,
            encodedLineIndex,
            beforeStartLine,
            startLine - 1,
            asciiDocument,
            ref contextLineBudget,
            ref contextByteBudget,
            out var beforeContextTruncated);
        var afterContext = CollectContext(
            contentBytes,
            encodedLineIndex,
            afterStartLine,
            afterEndLine,
            asciiDocument,
            ref contextLineBudget,
            ref contextByteBudget,
            out var afterContextTruncated);

        return new MaterializedHit(
            path: path,
            startLine: startLine,
            startColumn: startColumn,
            endLine: endLine,
            endColumn: endColumn,
            matchText: matchText,
            beforeContext: beforeContext,
            afterContext: afterContext,
            isMatchTruncated: isMatchTruncated,
            isContextTruncated: beforeContextTruncated || afterContextTruncated);
    }

    private static int GetUtf16Column(
        ReadOnlySpan<byte> contentBytes,
        ReadOnlySpan<byte> encodedLineIndex,
        int byteOffset,
        int oneBasedLine,
        bool asciiDocument)
    {
        var start = LineMap.GetLineStart(encodedLineIndex, oneBasedLine);
        if (asciiDocument)
        {
            return Math.Min(byteOffset, contentBytes.Length) - start + 1;
        }

        return Encoding.UTF8.GetCharCount(
            contentBytes[start..Math.Min(byteOffset, contentBytes.Length)]) + 1;
    }

    private static string[] CollectContext(
        ReadOnlySpan<byte> contentBytes,
        ReadOnlySpan<byte> encodedLineIndex,
        int startLine,
        int endLine,
        bool asciiDocument,
        ref int remainingLines,
        ref int remainingBytes,
        out bool truncated)
    {
        truncated = false;
        var lineCount = LineMap.GetLineCount(encodedLineIndex);
        startLine = Math.Max(startLine, 1);
        endLine = Math.Min(endLine, lineCount);
        if (startLine > endLine)
        {
            return [];
        }

        var requestedLines = endLine - startLine + 1;
        var lines = new List<string>(Math.Min(requestedLines, remainingLines));
        for (var line = startLine; line <= endLine; line++)
        {
            if (remainingLines == 0 || remainingBytes == 0)
            {
                truncated = true;
                break;
            }

            var start = LineMap.GetLineStart(encodedLineIndex, line);
            var end = LineMap.GetLineEndExclusive(encodedLineIndex, line, contentBytes.Length);
            var materializedEnd = start + Math.Min(end - start, remainingBytes);
            if (materializedEnd < end && !asciiDocument)
            {
                while (materializedEnd > start &&
                       (contentBytes[materializedEnd] & 0xC0) == 0x80)
                {
                    materializedEnd--;
                }
            }

            lines.Add(DecodeText(
                contentBytes[start..materializedEnd],
                asciiDocument,
                trimLineEnd: true));
            remainingLines--;
            remainingBytes -= materializedEnd - start;
            if (materializedEnd < end)
            {
                truncated = true;
                break;
            }
        }

        return [.. lines];
    }

    private static string DecodeText(
        ReadOnlySpan<byte> bytes,
        bool asciiDocument,
        bool trimLineEnd)
    {
        if (trimLineEnd)
        {
            while (bytes.Length > 0 && bytes[^1] is (byte)'\r' or (byte)'\n')
            {
                bytes = bytes[..^1];
            }
        }

        return asciiDocument
            ? Encoding.ASCII.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
    }
}
