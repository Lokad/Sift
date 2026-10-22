using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

internal static class CorpusManifestCodec
{
    internal static CorpusManifest Read(string manifestPath)
    {
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < SegmentFormat.LegacyManifestHeaderSize + sizeof(ulong) ||
            stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("Invalid Sift manifest length.");
        }

        var length = checked((int)stream.Length);
        Span<byte> header = stackalloc byte[SegmentFormat.ManifestHeaderSize];
        stream.ReadExactly(header[..SegmentFormat.LegacyManifestHeaderSize]);
        ReadOnlySpan<byte> span = header[..SegmentFormat.LegacyManifestHeaderSize];
        if (LittleEndianCodec.ReadUInt64(span) != SegmentFormat.ManifestMagic)
        {
            throw new InvalidDataException("Invalid Sift manifest magic.");
        }

        var version = LittleEndianCodec.ReadInt32(span[8..]);
        if (version is not SegmentFormat.LegacyManifestFormatVersion and
            not SegmentFormat.PreviousManifestFormatVersion and
            not SegmentFormat.ManifestFormatVersion)
        {
            throw new InvalidDataException($"Unsupported Sift manifest version: {version}.");
        }

        var expectedHeaderSize = version == SegmentFormat.ManifestFormatVersion
            ? SegmentFormat.ManifestHeaderSize
            : SegmentFormat.LegacyManifestHeaderSize;
        if (length < expectedHeaderSize + sizeof(ulong))
        {
            throw new InvalidDataException("Invalid Sift manifest length.");
        }
        if (expectedHeaderSize > SegmentFormat.LegacyManifestHeaderSize)
            stream.ReadExactly(header[SegmentFormat.LegacyManifestHeaderSize..expectedHeaderSize]);
        span = header[..expectedHeaderSize];

        var corpusId = new CorpusId(new Guid(span.Slice(12, 16)));
        var generation = new StorageGeneration(LittleEndianCodec.ReadUInt64(span[28..]));
        var count = LittleEndianCodec.ReadInt32(span[36..]);
        var segmentTableOffset = LittleEndianCodec.ReadInt32(span[40..]);
        var segmentTableLength = LittleEndianCodec.ReadInt32(span[44..]);
        var metadataOffset = version == SegmentFormat.ManifestFormatVersion
            ? LittleEndianCodec.ReadInt32(span[48..])
            : 0;
        var metadataLength = version == SegmentFormat.ManifestFormatVersion
            ? LittleEndianCodec.ReadInt32(span[52..])
            : 0;
        var checksumOffset = LittleEndianCodec.ReadInt32(span[(version == SegmentFormat.ManifestFormatVersion ? 56 : 48)..]);
        var checksumLength = LittleEndianCodec.ReadInt32(span[(version == SegmentFormat.ManifestFormatVersion ? 60 : 52)..]);
        var contentRevision = version == SegmentFormat.ManifestFormatVersion
            ? new ContentRevision(LittleEndianCodec.ReadUInt64(span[64..]))
            : new ContentRevision(generation.Value);
        if (count < 0)
        {
            throw new InvalidDataException("Invalid Sift manifest segment count.");
        }

        ValidateManifestBounds(segmentTableOffset, segmentTableLength, length, "segment table");
        if (version != SegmentFormat.ManifestFormatVersion)
        {
            metadataOffset = segmentTableOffset + segmentTableLength;
        }

        ValidateManifestBounds(metadataOffset, metadataLength, length, "commit metadata");
        ValidateManifestBounds(checksumOffset, checksumLength, length, "checksum region");
        if (checksumLength != 8)
        {
            throw new InvalidDataException("Invalid Sift manifest checksum length.");
        }

        if (segmentTableOffset != expectedHeaderSize)
        {
            throw new InvalidDataException("Unexpected Sift manifest segment table offset.");
        }

        if (metadataLength > SegmentFormat.MaximumCommitMetadataBytes ||
            metadataOffset != segmentTableOffset + segmentTableLength ||
            checksumOffset != metadataOffset + metadataLength)
        {
            throw new InvalidDataException("Invalid Sift manifest commit metadata region.");
        }
        if (checksumOffset != length - checksumLength)
            throw new InvalidDataException("Unexpected trailing bytes in Sift manifest.");

        var entryHeaderSize = version is SegmentFormat.ManifestFormatVersion or SegmentFormat.PreviousManifestFormatVersion
            ? SegmentFormat.ManifestEntryHeaderSize
            : SegmentFormat.LegacyManifestEntryHeaderSize;
        if (count > segmentTableLength / entryHeaderSize)
        {
            throw new InvalidDataException("Invalid Sift manifest segment count.");
        }

        // Admit the complete versioned layout before allocating a file-sized
        // buffer. Valid legacy manifests have no unchecked extension at EOF.
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        header[..expectedHeaderSize].CopyTo(bytes);
        stream.ReadExactly(bytes.AsSpan(expectedHeaderSize));
        span = bytes;
        var expectedChecksum = LittleEndianCodec.ReadUInt64(span.Slice(checksumOffset, checksumLength));
        if (expectedChecksum != Hashing.Fnv1A64(span.Slice(0, checksumOffset)))
            throw new InvalidDataException("Invalid Sift manifest checksum.");

        var segments = new SegmentManifestEntry[count];
        var segmentIds = new HashSet<SegmentId>();
        var offset = segmentTableOffset;
        var limit = segmentTableOffset + segmentTableLength;
        for (var i = 0; i < count; i++)
        {
            if (offset > limit - entryHeaderSize)
            {
                throw new InvalidDataException("Sift manifest segment table is truncated.");
            }

            var segmentId = new SegmentId(LittleEndianCodec.ReadUInt64(span[offset..]));
            if (!segmentIds.Add(segmentId))
            {
                throw new InvalidDataException($"Duplicate Sift manifest segment id '{segmentId.Value:X16}'.");
            }

            var documentCount = LittleEndianCodec.ReadInt32(span[(offset + 8)..]);
            var contentBytes = LittleEndianCodec.ReadInt64(span[(offset + 12)..]);
            var deadCount = LittleEndianCodec.ReadInt32(span[(offset + 20)..]);
            var deadBytes = LittleEndianCodec.ReadInt32(span[(offset + 24)..]);
            var deadContentBytes = version is SegmentFormat.ManifestFormatVersion or SegmentFormat.PreviousManifestFormatVersion
                ? LittleEndianCodec.ReadInt64(span[(offset + 28)..])
                : -1;
            offset += entryHeaderSize;
            if (documentCount < 0 ||
                contentBytes < 0 ||
                deadCount < 0 ||
                deadCount > documentCount ||
                deadBytes < 0 ||
                deadCount > deadBytes ||
                deadContentBytes < (version is SegmentFormat.ManifestFormatVersion or SegmentFormat.PreviousManifestFormatVersion ? 0 : -1) ||
                deadContentBytes > contentBytes ||
                offset > limit - deadBytes)
            {
                throw new InvalidDataException("Invalid Sift manifest segment entry.");
            }

            var deadPayload = span.Slice(offset, deadBytes);
            offset += deadBytes;
            var deadOrdinals = new uint[deadCount];
            uint current = 0;
            var deadOffset = 0;
            for (var j = 0; j < deadCount; j++)
            {
                current += VarInt.ReadUInt32(deadPayload, ref deadOffset);
                if (current >= documentCount ||
                    (j > 0 && current <= deadOrdinals[j - 1]))
                {
                    throw new InvalidDataException("Invalid Sift manifest dead ordinal.");
                }

                deadOrdinals[j] = current;
            }

            if (deadOffset != deadBytes)
            {
                throw new InvalidDataException("Invalid Sift manifest dead-ordinal payload.");
            }

            segments[i] = new SegmentManifestEntry(
                segmentId,
                documentCount,
                contentBytes,
                DeadOrdinalSet.FromOwned(deadOrdinals),
                deadContentBytes);
        }

        if (offset != limit)
        {
            throw new InvalidDataException("Unexpected trailing bytes in Sift manifest segment table.");
        }

        return new CorpusManifest(
            corpusId,
            contentRevision,
            generation,
            span.Slice(metadataOffset, metadataLength).ToArray(),
            segments);
    }

    internal static byte[] Encode(CorpusManifest manifest)
    {
        if (manifest.CommitMetadata.Length > SegmentFormat.MaximumCommitMetadataBytes)
        {
            throw new InvalidOperationException("Sift commit metadata exceeds the on-disk format limit.");
        }

        int segmentTableLength;
        int metadataOffset;
        int checksumOffset;
        int size;
        try
        {
            segmentTableLength = 0;
            checked
            {
                foreach (var segment in manifest.Segments)
                {
                    segmentTableLength += SegmentFormat.ManifestEntryHeaderSize + GetDeadOrdinalPayloadLength(segment.DeadOrdinals.Span);
                }

                metadataOffset = SegmentFormat.ManifestHeaderSize + segmentTableLength;
                checksumOffset = metadataOffset + manifest.CommitMetadata.Length;
                size = checksumOffset + sizeof(ulong);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("Sift manifest exceeds the on-disk format limit.", exception);
        }

        var segmentTableOffset = SegmentFormat.ManifestHeaderSize;
        var metadataLength = manifest.CommitMetadata.Length;
        const int checksumLength = sizeof(ulong);
        var bytes = GC.AllocateUninitializedArray<byte>(size);
        var span = bytes.AsSpan();
        LittleEndianCodec.WriteUInt64(span, SegmentFormat.ManifestMagic);
        LittleEndianCodec.WriteInt32(span[8..], SegmentFormat.ManifestFormatVersion);
        manifest.CorpusId.Value.TryWriteBytes(span[12..28]);
        LittleEndianCodec.WriteUInt64(span[28..], manifest.Generation.Value);
        LittleEndianCodec.WriteInt32(span[36..], manifest.Segments.Count);
        LittleEndianCodec.WriteInt32(span[40..], segmentTableOffset);
        LittleEndianCodec.WriteInt32(span[44..], segmentTableLength);
        LittleEndianCodec.WriteInt32(span[48..], metadataOffset);
        LittleEndianCodec.WriteInt32(span[52..], metadataLength);
        LittleEndianCodec.WriteInt32(span[56..], checksumOffset);
        LittleEndianCodec.WriteInt32(span[60..], checksumLength);
        LittleEndianCodec.WriteUInt64(span[64..], manifest.ContentRevision.Value);

        var offset = segmentTableOffset;
        foreach (var segment in manifest.Segments)
        {
            LittleEndianCodec.WriteUInt64(span[offset..], segment.SegmentId.Value);
            LittleEndianCodec.WriteInt32(span[(offset + 8)..], segment.DocumentCount);
            LittleEndianCodec.WriteInt64(span[(offset + 12)..], segment.ContentBytes);
            LittleEndianCodec.WriteInt32(span[(offset + 20)..], segment.DeadOrdinals.Length);
            var deadBytes = GetDeadOrdinalPayloadLength(segment.DeadOrdinals.Span);
            LittleEndianCodec.WriteInt32(span[(offset + 24)..], deadBytes);
            LittleEndianCodec.WriteInt64(span[(offset + 28)..], segment.DeadContentBytes);
            offset += SegmentFormat.ManifestEntryHeaderSize;
            uint previous = 0;
            foreach (var dead in segment.DeadOrdinals)
            {
                offset += VarInt.WriteUInt32(span[offset..], dead - previous);
                previous = dead;
            }
        }

        manifest.CommitMetadata.CopyTo(span[metadataOffset..]);
        LittleEndianCodec.WriteUInt64(
            span.Slice(checksumOffset, checksumLength),
            Hashing.Fnv1A64(span[..checksumOffset]));
        return bytes;
    }

    private static int GetDeadOrdinalPayloadLength(ReadOnlySpan<uint> deadOrdinals)
    {
        var length = 0;
        uint previous = 0;
        checked
        {
            for (var i = 0; i < deadOrdinals.Length; i++)
            {
                length += VarInt.GetUInt32Length(deadOrdinals[i] - previous);
                previous = deadOrdinals[i];
            }
        }

        return length;
    }

    private static void ValidateManifestBounds(int offset, int length, int fileLength, string name)
    {
        if (offset < 0 || length < 0 || offset > fileLength || length > fileLength - offset)
        {
            throw new InvalidDataException($"Invalid Sift manifest {name} bounds.");
        }
    }
}
