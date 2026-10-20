namespace Lokad.Sift.Storage;

[Flags]
internal enum SegmentDocumentFlags : ushort
{
    Utf8Text = 1,
    Ascii = 1 << 1,
    HadUtf8Bom = 1 << 2
}

internal enum SegmentRegionId
{
    DocumentTable = 1,
    PathBlob = 2,
    ContentBlob = 3,
    LineIndexBlob = 4,
    ContentTriCsLex = 5,
    ContentTriCsPost = 6,
    ContentTriIcLex = 7,
    ContentTriIcPost = 8,
    ContentBiCsLex = 9,
    ContentBiCsPost = 10,
    ContentBiIcLex = 11,
    ContentBiIcPost = 12,
    PathTriCsLex = 13,
    PathTriCsPost = 14,
    PathTriIcLex = 15,
    PathTriIcPost = 16,
    Stats = 17
}

internal static class SegmentFormat
{
    public const ulong SegmentMagic = 0x3154474553464953UL;
    public const ulong ManifestMagic = 0x314E414D46544953UL;
    public const int FormatVersion = 4;
    public const int PreviousFormatVersion = 3;
    public const int ManifestFormatVersion = 4;
    public const int PreviousManifestFormatVersion = 3;
    public const int LegacyManifestFormatVersion = 2;
    public const int RegionCount = 17;
    public const int DocumentRowSize = 58;
    public const int HeaderSize = 60;
    public const int RegionDirectoryEntrySize = 20;
    public const int ChecksumEntrySize = 12;
    public const int ManifestHeaderSize = 72;
    public const int LegacyManifestHeaderSize = 56;
    public const int ManifestEntryHeaderSize = 36;
    public const int LegacyManifestEntryHeaderSize = 28;
    public const int MaximumCommitMetadataBytes = 1024 * 1024;
    public const int LexiconBucketCount = 257;
    public const int LexiconHeaderSize = 4 + (LexiconBucketCount * 4);
    public const int NonAsciiDocumentCountOffset = 8;
    public const int NonAsciiContentBytesOffset = NonAsciiDocumentCountOffset + 4;
    public const int StatsLength = NonAsciiContentBytesOffset + 8;
    // Version 3 has an obsolete 1024-byte area after the document/content totals.
    public const int PreviousStatsPrefixLength = 8 + 1024;
    public const int PreviousStatsLength = PreviousStatsPrefixLength + 12;
    public const int FixedSegmentBytes =
        HeaderSize +
        RegionCount * RegionDirectoryEntrySize +
        StatsLength +
        RegionCount * ChecksumEntrySize;
    public const int MaximumContentBlobBytes = int.MaxValue - FixedSegmentBytes;
    public const int MaximumDocumentCount = (int.MaxValue - FixedSegmentBytes) / DocumentRowSize;

    public static int GetLexiconSuffixSize(SegmentRegionId regionId)
    {
        return regionId is SegmentRegionId.ContentBiCsLex or SegmentRegionId.ContentBiIcLex
            ? 1
            : 2;
    }

    public static int GetLexiconEntrySize(SegmentRegionId regionId)
    {
        return GetLexiconSuffixSize(regionId) + 12;
    }
}
