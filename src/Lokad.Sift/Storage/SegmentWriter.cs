using Lokad.Sift.LowLevel;
using System.Threading.Tasks;

namespace Lokad.Sift.Storage;

internal static class SegmentWriter
{
    public static long Write(string path, string tempPath, ulong segmentId, IReadOnlyList<ISegmentBuildDocument> documents, CancellationToken cancellationToken)
    {
        var (bytes, contentBytes) = WriteToBytes(segmentId, documents, cancellationToken);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(bytes.Length);
        RandomAccess.Write(stream.SafeFileHandle, bytes, 0);
        RandomAccess.FlushToDisk(stream.SafeFileHandle);
        return contentBytes;
    }

    public static (byte[] Bytes, long ContentBytes) WriteToBytes(ulong segmentId, IReadOnlyList<ISegmentBuildDocument> documents, CancellationToken cancellationToken)
    {
        BuiltIndex contentTriCs = default;
        BuiltIndex contentTriIc = default;
        BuiltIndex contentBiCs = default;
        BuiltIndex contentBiIc = default;
        BuiltIndex pathTriCs = default;
        BuiltIndex pathTriIc = default;
        if (documents.Count >= 64)
        {
            Parallel.Invoke(
                new ParallelOptions { CancellationToken = cancellationToken },
                () => contentTriCs = SegmentIndexBuilder.BuildContentIndex(documents, 3, caseInsensitive: false),
                () => contentTriIc = SegmentIndexBuilder.BuildContentIndex(documents, 3, caseInsensitive: true),
                () => contentBiCs = SegmentIndexBuilder.BuildContentIndex(documents, 2, caseInsensitive: false),
                () => contentBiIc = SegmentIndexBuilder.BuildContentIndex(documents, 2, caseInsensitive: true),
                () => pathTriCs = SegmentIndexBuilder.BuildPathTrigramIndex(documents, caseInsensitive: false),
                () => pathTriIc = SegmentIndexBuilder.BuildPathTrigramIndex(documents, caseInsensitive: true));
        }
        else
        {
            contentTriCs = SegmentIndexBuilder.BuildContentIndex(documents, 3, caseInsensitive: false);
            contentTriIc = SegmentIndexBuilder.BuildContentIndex(documents, 3, caseInsensitive: true);
            contentBiCs = SegmentIndexBuilder.BuildContentIndex(documents, 2, caseInsensitive: false);
            contentBiIc = SegmentIndexBuilder.BuildContentIndex(documents, 2, caseInsensitive: true);
            pathTriCs = SegmentIndexBuilder.BuildPathTrigramIndex(documents, caseInsensitive: false);
            pathTriIc = SegmentIndexBuilder.BuildPathTrigramIndex(documents, caseInsensitive: true);
        }

        var rows = new DocumentRow[documents.Count];
        var pathBlobLength = 0;
        var contentBlobLength = 0;
        var lineIndexBlobLength = 0;

        for (var i = 0; i < documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents[i];
            var pathUtf8 = document.GetPathUtf8();
            var content = document.GetContentBytes();
            var encodedLineIndex = document.GetEncodedLineIndex();
            rows[i] = new DocumentRow(
                (ulong)pathBlobLength,
                pathUtf8.Length,
                (ulong)contentBlobLength,
                content.Length,
                (ulong)lineIndexBlobLength,
                encodedLineIndex.Length,
                document.LineCount,
                document.Flags,
                document.MTimeUtcTicks,
                document.ContentHash64);
            pathBlobLength += pathUtf8.Length;
            contentBlobLength += content.Length;
            lineIndexBlobLength += encodedLineIndex.Length;
        }

        var regionDirectoryOffset = SegmentFormat.HeaderSize;
        var documentTableOffset = SegmentFormat.HeaderSize + SegmentFormat.RegionCount * SegmentFormat.RegionDirectoryEntrySize;
        var pathBlobOffset = documentTableOffset + rows.Length * SegmentFormat.DocumentRowSize;
        var contentBlobOffset = pathBlobOffset + pathBlobLength;
        var lineIndexBlobOffset = contentBlobOffset + contentBlobLength;
        var contentTriCsLexOffset = lineIndexBlobOffset + lineIndexBlobLength;
        var contentTriCsPostOffset = contentTriCsLexOffset + contentTriCs.Lexicon.Length;
        var contentTriIcLexOffset = contentTriCsPostOffset + contentTriCs.Postings.Length;
        var contentTriIcPostOffset = contentTriIcLexOffset + contentTriIc.Lexicon.Length;
        var contentBiCsLexOffset = contentTriIcPostOffset + contentTriIc.Postings.Length;
        var contentBiCsPostOffset = contentBiCsLexOffset + contentBiCs.Lexicon.Length;
        var contentBiIcLexOffset = contentBiCsPostOffset + contentBiCs.Postings.Length;
        var contentBiIcPostOffset = contentBiIcLexOffset + contentBiIc.Lexicon.Length;
        var pathTriCsLexOffset = contentBiIcPostOffset + contentBiIc.Postings.Length;
        var pathTriCsPostOffset = pathTriCsLexOffset + pathTriCs.Lexicon.Length;
        var pathTriIcLexOffset = pathTriCsPostOffset + pathTriCs.Postings.Length;
        var pathTriIcPostOffset = pathTriIcLexOffset + pathTriIc.Lexicon.Length;
        var statsOffset = pathTriIcPostOffset + pathTriIc.Postings.Length;
        var statsLength = 8 + (256 * 4);
        var checksumTableOffset = statsOffset + statsLength;
        var totalLength = checksumTableOffset + SegmentFormat.RegionCount * SegmentFormat.ChecksumEntrySize;

        var bytes = GC.AllocateUninitializedArray<byte>(totalLength);
        var header = bytes.AsSpan(0, SegmentFormat.HeaderSize);
        LittleEndianCodec.WriteUInt64(header, SegmentFormat.SegmentMagic);
        LittleEndianCodec.WriteInt32(header[8..], SegmentFormat.FormatVersion);
        LittleEndianCodec.WriteUInt64(header[12..], segmentId);
        LittleEndianCodec.WriteInt64(header[20..], DateTime.UtcNow.Ticks);
        LittleEndianCodec.WriteInt32(header[28..], rows.Length);
        LittleEndianCodec.WriteInt64(header[32..], contentBlobLength);
        LittleEndianCodec.WriteInt32(header[40..], SegmentFormat.RegionCount);
        LittleEndianCodec.WriteInt64(header[44..], regionDirectoryOffset);
        LittleEndianCodec.WriteInt64(header[52..], checksumTableOffset);

        WriteRegionDirectory(
            bytes.AsSpan(regionDirectoryOffset, SegmentFormat.RegionCount * SegmentFormat.RegionDirectoryEntrySize),
            (int[])[
                documentTableOffset, rows.Length * SegmentFormat.DocumentRowSize,
                pathBlobOffset, pathBlobLength,
                contentBlobOffset, contentBlobLength,
                lineIndexBlobOffset, lineIndexBlobLength,
                contentTriCsLexOffset, contentTriCs.Lexicon.Length,
                contentTriCsPostOffset, contentTriCs.Postings.Length,
                contentTriIcLexOffset, contentTriIc.Lexicon.Length,
                contentTriIcPostOffset, contentTriIc.Postings.Length,
                contentBiCsLexOffset, contentBiCs.Lexicon.Length,
                contentBiCsPostOffset, contentBiCs.Postings.Length,
                contentBiIcLexOffset, contentBiIc.Lexicon.Length,
                contentBiIcPostOffset, contentBiIc.Postings.Length,
                pathTriCsLexOffset, pathTriCs.Lexicon.Length,
                pathTriCsPostOffset, pathTriCs.Postings.Length,
                pathTriIcLexOffset, pathTriIc.Lexicon.Length,
                pathTriIcPostOffset, pathTriIc.Postings.Length,
                statsOffset, statsLength
            ]);
        var documentTable = bytes.AsSpan(documentTableOffset, rows.Length * SegmentFormat.DocumentRowSize);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var rowOffset = i * SegmentFormat.DocumentRowSize;
            LittleEndianCodec.WriteUInt64(documentTable.Slice(rowOffset, 8), row.PathOffset);
            LittleEndianCodec.WriteInt32(documentTable.Slice(rowOffset + 8, 4), row.PathLength);
            LittleEndianCodec.WriteUInt64(documentTable.Slice(rowOffset + 12, 8), row.ContentOffset);
            LittleEndianCodec.WriteInt32(documentTable.Slice(rowOffset + 20, 4), row.ContentLength);
            LittleEndianCodec.WriteUInt64(documentTable.Slice(rowOffset + 24, 8), row.LineIndexOffset);
            LittleEndianCodec.WriteInt32(documentTable.Slice(rowOffset + 32, 4), row.LineIndexLength);
            LittleEndianCodec.WriteInt32(documentTable.Slice(rowOffset + 36, 4), row.LineCount);
            LittleEndianCodec.WriteUInt16(documentTable.Slice(rowOffset + 40, 2), row.Flags);
            LittleEndianCodec.WriteInt64(documentTable.Slice(rowOffset + 42, 8), row.MTimeUtcTicks);
            LittleEndianCodec.WriteUInt64(documentTable.Slice(rowOffset + 50, 8), row.ContentHash64);
        }

        var pathBlob = bytes.AsSpan(pathBlobOffset, pathBlobLength);
        var contentBlob = bytes.AsSpan(contentBlobOffset, contentBlobLength);
        var lineIndexBlob = bytes.AsSpan(lineIndexBlobOffset, lineIndexBlobLength);
        var stats = bytes.AsSpan(statsOffset, statsLength);
        stats.Clear();
        LittleEndianCodec.WriteInt32(stats, rows.Length);
        LittleEndianCodec.WriteInt32(stats[4..], contentBlobLength);

        var pathOffsetCursor = 0;
        var contentOffsetCursor = 0;
        var lineOffsetCursor = 0;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathUtf8 = document.GetPathUtf8();
            var content = document.GetContentBytes();
            var encodedLineIndex = document.GetEncodedLineIndex();
            pathUtf8.CopyTo(pathBlob[pathOffsetCursor..]);
            pathOffsetCursor += pathUtf8.Length;
            content.CopyTo(contentBlob[contentOffsetCursor..]);
            for (var i = 0; i < content.Length; i++)
            {
                var bucketOffset = 8 + (content[i] * 4);
                var current = LittleEndianCodec.ReadInt32(stats.Slice(bucketOffset, 4));
                LittleEndianCodec.WriteInt32(stats.Slice(bucketOffset, 4), current + 1);
            }

            contentOffsetCursor += content.Length;
            encodedLineIndex.CopyTo(lineIndexBlob[lineOffsetCursor..]);
            lineOffsetCursor += encodedLineIndex.Length;
        }

        contentTriCs.Lexicon.CopyTo(bytes, contentTriCsLexOffset);
        contentTriCs.Postings.CopyTo(bytes, contentTriCsPostOffset);
        contentTriIc.Lexicon.CopyTo(bytes, contentTriIcLexOffset);
        contentTriIc.Postings.CopyTo(bytes, contentTriIcPostOffset);
        contentBiCs.Lexicon.CopyTo(bytes, contentBiCsLexOffset);
        contentBiCs.Postings.CopyTo(bytes, contentBiCsPostOffset);
        contentBiIc.Lexicon.CopyTo(bytes, contentBiIcLexOffset);
        contentBiIc.Postings.CopyTo(bytes, contentBiIcPostOffset);
        pathTriCs.Lexicon.CopyTo(bytes, pathTriCsLexOffset);
        pathTriCs.Postings.CopyTo(bytes, pathTriCsPostOffset);
        pathTriIc.Lexicon.CopyTo(bytes, pathTriIcLexOffset);
        pathTriIc.Postings.CopyTo(bytes, pathTriIcPostOffset);
        WriteChecksumTable(
            bytes.AsSpan(checksumTableOffset, SegmentFormat.RegionCount * SegmentFormat.ChecksumEntrySize),
            documentTable,
            pathBlob,
            contentBlob,
            lineIndexBlob,
            contentTriCs.Lexicon,
            contentTriCs.Postings,
            contentTriIc.Lexicon,
            contentTriIc.Postings,
            contentBiCs.Lexicon,
            contentBiCs.Postings,
            contentBiIc.Lexicon,
            contentBiIc.Postings,
            pathTriCs.Lexicon,
            pathTriCs.Postings,
            pathTriIc.Lexicon,
            pathTriIc.Postings,
            stats);
        return (bytes, contentBlobLength);
    }

    private static void WriteRegionDirectory(Span<byte> regionDirectory, int[] regionPairs)
    {
        for (var regionId = 1; regionId <= SegmentFormat.RegionCount; regionId++)
        {
            var offset = (regionId - 1) * SegmentFormat.RegionDirectoryEntrySize;
            var pairOffset = (regionId - 1) * 2;
            LittleEndianCodec.WriteInt32(regionDirectory.Slice(offset, 4), regionId);
            LittleEndianCodec.WriteInt64(regionDirectory.Slice(offset + 4, 8), regionPairs[pairOffset]);
            LittleEndianCodec.WriteInt64(regionDirectory.Slice(offset + 12, 8), regionPairs[pairOffset + 1]);
        }
    }

    private static void WriteChecksumTable(
        Span<byte> checksumTable,
        ReadOnlySpan<byte> documentTable,
        ReadOnlySpan<byte> pathBlob,
        ReadOnlySpan<byte> contentBlob,
        ReadOnlySpan<byte> lineIndexBlob,
        ReadOnlySpan<byte> contentTriCsLex,
        ReadOnlySpan<byte> contentTriCsPost,
        ReadOnlySpan<byte> contentTriIcLex,
        ReadOnlySpan<byte> contentTriIcPost,
        ReadOnlySpan<byte> contentBiCsLex,
        ReadOnlySpan<byte> contentBiCsPost,
        ReadOnlySpan<byte> contentBiIcLex,
        ReadOnlySpan<byte> contentBiIcPost,
        ReadOnlySpan<byte> pathTriCsLex,
        ReadOnlySpan<byte> pathTriCsPost,
        ReadOnlySpan<byte> pathTriIcLex,
        ReadOnlySpan<byte> pathTriIcPost,
        ReadOnlySpan<byte> stats)
    {
        for (var regionId = 1; regionId <= SegmentFormat.RegionCount; regionId++)
        {
            var offset = (regionId - 1) * SegmentFormat.ChecksumEntrySize;
            ReadOnlySpan<byte> regionBytes = (regionId - 1) switch
            {
                0 => documentTable,
                1 => pathBlob,
                2 => contentBlob,
                3 => lineIndexBlob,
                4 => contentTriCsLex,
                5 => contentTriCsPost,
                6 => contentTriIcLex,
                7 => contentTriIcPost,
                8 => contentBiCsLex,
                9 => contentBiCsPost,
                10 => contentBiIcLex,
                11 => contentBiIcPost,
                12 => pathTriCsLex,
                13 => pathTriCsPost,
                14 => pathTriIcLex,
                15 => pathTriIcPost,
                16 => stats,
                _ => default
            };
            LittleEndianCodec.WriteInt32(checksumTable.Slice(offset, 4), regionId);
            LittleEndianCodec.WriteUInt64(checksumTable.Slice(offset + 4, 8), Hashing.Fnv1A64(regionBytes));
        }
    }

    private readonly record struct DocumentRow(
        ulong PathOffset,
        int PathLength,
        ulong ContentOffset,
        int ContentLength,
        ulong LineIndexOffset,
        int LineIndexLength,
        int LineCount,
        ushort Flags,
        long MTimeUtcTicks,
        ulong ContentHash64);
}
