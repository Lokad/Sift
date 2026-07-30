using System.IO.MemoryMappedFiles;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

internal static unsafe class SegmentWriter
{
    public static long Write(string path, string tempPath, ulong segmentId, IReadOnlyList<ISegmentBuildDocument> documents, CancellationToken cancellationToken)
    {
        _ = tempPath;
        var prepared = Prepare(documents, cancellationToken);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            PreallocationSize = prepared.TotalLength
        });
        stream.SetLength(prepared.TotalLength);
        using (var mappedFile = MemoryMappedFile.CreateFromFile(
                   stream,
                   mapName: null,
                   prepared.TotalLength,
                   MemoryMappedFileAccess.ReadWrite,
                   HandleInheritability.None,
                   leaveOpen: true))
        using (var view = mappedFile.CreateViewAccessor(0, prepared.TotalLength, MemoryMappedFileAccess.ReadWrite))
        {
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                var bytes = new Span<byte>(
                    pointer + view.PointerOffset,
                    prepared.TotalLength);
                WritePrepared(bytes, segmentId, documents, prepared, cancellationToken);
                view.Flush();
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        RandomAccess.FlushToDisk(stream.SafeFileHandle);
        return prepared.ContentBlobLength;
    }

    public static (byte[] Bytes, long ContentBytes) WriteToBytes(ulong segmentId, IReadOnlyList<ISegmentBuildDocument> documents, CancellationToken cancellationToken)
    {
        var prepared = Prepare(documents, cancellationToken);
        var bytes = GC.AllocateUninitializedArray<byte>(prepared.TotalLength);
        WritePrepared(bytes, segmentId, documents, prepared, cancellationToken);
        return (bytes, prepared.ContentBlobLength);
    }

    private static PreparedSegment Prepare(
        IReadOnlyList<ISegmentBuildDocument> documents,
        CancellationToken cancellationToken)
    {
        // Build indexes sequentially so one segment cannot retain four 64 MiB trigram
        // workspaces plus their variable postings state at the same time.
        var indexes = new[]
        {
            SegmentIndexBuilder.BuildContentIndex(documents, 3, caseInsensitive: false),
            SegmentIndexBuilder.BuildContentIndex(documents, 3, caseInsensitive: true),
            SegmentIndexBuilder.BuildContentIndex(documents, 2, caseInsensitive: false),
            SegmentIndexBuilder.BuildContentIndex(documents, 2, caseInsensitive: true),
            SegmentIndexBuilder.BuildPathTrigramIndex(documents, caseInsensitive: false),
            SegmentIndexBuilder.BuildPathTrigramIndex(documents, caseInsensitive: true)
        };

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
            pathBlobLength = checked(pathBlobLength + pathUtf8.Length);
            contentBlobLength = checked(contentBlobLength + content.Length);
            lineIndexBlobLength = checked(lineIndexBlobLength + encodedLineIndex.Length);
        }

        var totalLength = checked(
            SegmentFormat.HeaderSize +
            SegmentFormat.RegionCount * SegmentFormat.RegionDirectoryEntrySize +
            rows.Length * SegmentFormat.DocumentRowSize +
            pathBlobLength +
            contentBlobLength +
            lineIndexBlobLength);
        foreach (var index in indexes)
        {
            totalLength = checked(totalLength + index.Lexicon.Length + index.Postings.Length);
        }

        totalLength = checked(
            totalLength +
            PreparedSegment.StatsLength +
            SegmentFormat.RegionCount * SegmentFormat.ChecksumEntrySize);
        return new PreparedSegment(
            rows,
            pathBlobLength,
            contentBlobLength,
            lineIndexBlobLength,
            indexes,
            totalLength);
    }

    private static void WritePrepared(
        Span<byte> bytes,
        ulong segmentId,
        IReadOnlyList<ISegmentBuildDocument> documents,
        PreparedSegment prepared,
        CancellationToken cancellationToken)
    {
        if (bytes.Length != prepared.TotalLength)
        {
            throw new ArgumentException("The Sift segment destination has an invalid length.", nameof(bytes));
        }

        var rows = prepared.Rows;
        var indexes = prepared.Indexes;
        var regionDirectoryOffset = SegmentFormat.HeaderSize;
        var documentTableOffset = checked(
            SegmentFormat.HeaderSize +
            SegmentFormat.RegionCount * SegmentFormat.RegionDirectoryEntrySize);
        var pathBlobOffset = checked(documentTableOffset + rows.Length * SegmentFormat.DocumentRowSize);
        var contentBlobOffset = checked(pathBlobOffset + prepared.PathBlobLength);
        var lineIndexBlobOffset = checked(contentBlobOffset + prepared.ContentBlobLength);
        var contentTriCsLexOffset = checked(lineIndexBlobOffset + prepared.LineIndexBlobLength);
        var contentTriCsPostOffset = checked(contentTriCsLexOffset + indexes[0].Lexicon.Length);
        var contentTriIcLexOffset = checked(contentTriCsPostOffset + indexes[0].Postings.Length);
        var contentTriIcPostOffset = checked(contentTriIcLexOffset + indexes[1].Lexicon.Length);
        var contentBiCsLexOffset = checked(contentTriIcPostOffset + indexes[1].Postings.Length);
        var contentBiCsPostOffset = checked(contentBiCsLexOffset + indexes[2].Lexicon.Length);
        var contentBiIcLexOffset = checked(contentBiCsPostOffset + indexes[2].Postings.Length);
        var contentBiIcPostOffset = checked(contentBiIcLexOffset + indexes[3].Lexicon.Length);
        var pathTriCsLexOffset = checked(contentBiIcPostOffset + indexes[3].Postings.Length);
        var pathTriCsPostOffset = checked(pathTriCsLexOffset + indexes[4].Lexicon.Length);
        var pathTriIcLexOffset = checked(pathTriCsPostOffset + indexes[4].Postings.Length);
        var pathTriIcPostOffset = checked(pathTriIcLexOffset + indexes[5].Lexicon.Length);
        var statsOffset = checked(pathTriIcPostOffset + indexes[5].Postings.Length);
        var checksumTableOffset = checked(statsOffset + PreparedSegment.StatsLength);

        var header = bytes[..SegmentFormat.HeaderSize];
        LittleEndianCodec.WriteUInt64(header, SegmentFormat.SegmentMagic);
        LittleEndianCodec.WriteInt32(header[8..], SegmentFormat.FormatVersion);
        LittleEndianCodec.WriteUInt64(header[12..], segmentId);
        LittleEndianCodec.WriteInt64(header[20..], DateTime.UtcNow.Ticks);
        LittleEndianCodec.WriteInt32(header[28..], rows.Length);
        LittleEndianCodec.WriteInt64(header[32..], prepared.ContentBlobLength);
        LittleEndianCodec.WriteInt32(header[40..], SegmentFormat.RegionCount);
        LittleEndianCodec.WriteInt64(header[44..], regionDirectoryOffset);
        LittleEndianCodec.WriteInt64(header[52..], checksumTableOffset);

        WriteRegionDirectory(
            bytes.Slice(
                regionDirectoryOffset,
                SegmentFormat.RegionCount * SegmentFormat.RegionDirectoryEntrySize),
            [
                documentTableOffset, rows.Length * SegmentFormat.DocumentRowSize,
                pathBlobOffset, prepared.PathBlobLength,
                contentBlobOffset, prepared.ContentBlobLength,
                lineIndexBlobOffset, prepared.LineIndexBlobLength,
                contentTriCsLexOffset, indexes[0].Lexicon.Length,
                contentTriCsPostOffset, indexes[0].Postings.Length,
                contentTriIcLexOffset, indexes[1].Lexicon.Length,
                contentTriIcPostOffset, indexes[1].Postings.Length,
                contentBiCsLexOffset, indexes[2].Lexicon.Length,
                contentBiCsPostOffset, indexes[2].Postings.Length,
                contentBiIcLexOffset, indexes[3].Lexicon.Length,
                contentBiIcPostOffset, indexes[3].Postings.Length,
                pathTriCsLexOffset, indexes[4].Lexicon.Length,
                pathTriCsPostOffset, indexes[4].Postings.Length,
                pathTriIcLexOffset, indexes[5].Lexicon.Length,
                pathTriIcPostOffset, indexes[5].Postings.Length,
                statsOffset, PreparedSegment.StatsLength
            ]);

        var documentTable = bytes.Slice(documentTableOffset, rows.Length * SegmentFormat.DocumentRowSize);
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

        var pathBlob = bytes.Slice(pathBlobOffset, prepared.PathBlobLength);
        var contentBlob = bytes.Slice(contentBlobOffset, prepared.ContentBlobLength);
        var lineIndexBlob = bytes.Slice(lineIndexBlobOffset, prepared.LineIndexBlobLength);
        var stats = bytes.Slice(statsOffset, PreparedSegment.StatsLength);
        stats.Clear();
        LittleEndianCodec.WriteInt32(stats, rows.Length);
        LittleEndianCodec.WriteInt32(stats[4..], prepared.ContentBlobLength);

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
                var bucketOffset = 8 + content[i] * 4;
                var current = LittleEndianCodec.ReadInt32(stats.Slice(bucketOffset, 4));
                LittleEndianCodec.WriteInt32(stats.Slice(bucketOffset, 4), current + 1);
            }

            contentOffsetCursor += content.Length;
            encodedLineIndex.CopyTo(lineIndexBlob[lineOffsetCursor..]);
            lineOffsetCursor += encodedLineIndex.Length;
        }

        indexes[0].Lexicon.CopyTo(bytes[contentTriCsLexOffset..]);
        indexes[0].Postings.CopyTo(bytes[contentTriCsPostOffset..]);
        indexes[1].Lexicon.CopyTo(bytes[contentTriIcLexOffset..]);
        indexes[1].Postings.CopyTo(bytes[contentTriIcPostOffset..]);
        indexes[2].Lexicon.CopyTo(bytes[contentBiCsLexOffset..]);
        indexes[2].Postings.CopyTo(bytes[contentBiCsPostOffset..]);
        indexes[3].Lexicon.CopyTo(bytes[contentBiIcLexOffset..]);
        indexes[3].Postings.CopyTo(bytes[contentBiIcPostOffset..]);
        indexes[4].Lexicon.CopyTo(bytes[pathTriCsLexOffset..]);
        indexes[4].Postings.CopyTo(bytes[pathTriCsPostOffset..]);
        indexes[5].Lexicon.CopyTo(bytes[pathTriIcLexOffset..]);
        indexes[5].Postings.CopyTo(bytes[pathTriIcPostOffset..]);
        WriteChecksumTable(
            bytes.Slice(
                checksumTableOffset,
                SegmentFormat.RegionCount * SegmentFormat.ChecksumEntrySize),
            documentTable,
            pathBlob,
            contentBlob,
            lineIndexBlob,
            indexes[0].Lexicon,
            indexes[0].Postings,
            indexes[1].Lexicon,
            indexes[1].Postings,
            indexes[2].Lexicon,
            indexes[2].Postings,
            indexes[3].Lexicon,
            indexes[3].Postings,
            indexes[4].Lexicon,
            indexes[4].Postings,
            indexes[5].Lexicon,
            indexes[5].Postings,
            stats);
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
            LittleEndianCodec.WriteUInt64(
                checksumTable.Slice(offset + 4, 8),
                Hashing.Fnv1A64(regionBytes));
        }
    }

    private sealed record PreparedSegment(
        DocumentRow[] Rows,
        int PathBlobLength,
        int ContentBlobLength,
        int LineIndexBlobLength,
        BuiltIndex[] Indexes,
        int TotalLength)
    {
        public const int StatsLength = 8 + 256 * 4;
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
