namespace Lokad.Sift.Storage;

public enum SegmentRegionId
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
    Stats = 17,

    // Reserved sidecar slots keep the on-disk id space explicit without changing the base format layout.
    ReservedSidecar01 = 18,
    ReservedSidecar02 = 19,
    ReservedSidecar03 = 20,
    ReservedSidecar04 = 21,
    ReservedSidecar05 = 22,
    ReservedSidecar06 = 23,
    ReservedSidecar07 = 24,
    ReservedSidecar08 = 25
}

internal static class SegmentFormat
{
    public const ulong SegmentMagic = 0x3154474553464953UL;
    public const ulong ManifestMagic = 0x314E414D46544953UL;
    public const int FormatVersion = 3;
    public const int ManifestFormatVersion = 3;
    public const int LegacyManifestFormatVersion = 2;
    public const int RegionCount = 17;
    public const int ReservedRegionIdStart = 18;
    public const int ReservedRegionIdEnd = 25;
    public const int DocumentRowSize = 58;
    public const int HeaderSize = 60;
    public const int RegionDirectoryEntrySize = 20;
    public const int ChecksumEntrySize = 12;
    public const int ManifestHeaderSize = 56;
    public const int ManifestEntryHeaderSize = 36;
    public const int LegacyManifestEntryHeaderSize = 28;
    public const int LexiconBucketCount = 257;
    public const int LexiconHeaderSize = 4 + (LexiconBucketCount * 4);
    public const int LegacyStatsLength = 8 + 256 * 4;
    public const int NonAsciiDocumentCountOffset = LegacyStatsLength;
    public const int NonAsciiContentBytesOffset = NonAsciiDocumentCountOffset + 4;
    public const int StatsLength = NonAsciiContentBytesOffset + 8;

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

    public static bool IsReservedRegionId(SegmentRegionId regionId)
    {
        var value = (int)regionId;
        return value >= ReservedRegionIdStart && value <= ReservedRegionIdEnd;
    }
}
