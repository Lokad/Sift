using Lokad.Sift.Storage;

namespace Lokad.Sift;

internal static class SiftOptionsValidator
{
    public static void Validate(SiftOptions options)
    {
        if (options.MaxDocumentBytes <= 0 ||
            options.MaxDocumentBytes > SegmentFormat.MaximumContentBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaxDocumentBytes must be between one and {SegmentFormat.MaximumContentBlobBytes}.");
        }

        if (options.TargetSegmentDocumentCount <= 0 ||
            options.TargetSegmentDocumentCount > SegmentFormat.MaximumDocumentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"TargetSegmentDocumentCount must be between one and {SegmentFormat.MaximumDocumentCount}.");
        }

        if (options.TargetSegmentContentBytes <= 0 ||
            options.TargetSegmentContentBytes > SegmentFormat.MaximumContentBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"TargetSegmentContentBytes must be between one and {SegmentFormat.MaximumContentBlobBytes}.");
        }

        if (options.MaximumPhysicalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumPhysicalBytes must be positive.");
        }

        if (options.MinimumFreeBytesAfterMutation < 0 ||
            options.MinimumFreeBytesAfterMutation >= options.MaximumPhysicalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MinimumFreeBytesAfterMutation must be non-negative and smaller than MaximumPhysicalBytes.");
        }

        if (options.DirectScanScopeDocumentsThreshold < 0 ||
            options.DirectScanScopeBytesThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Direct-scan thresholds cannot be negative.");
        }

        if (double.IsNaN(options.DirectScanCandidateDensityThreshold) ||
            options.DirectScanCandidateDensityThreshold < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DirectScanCandidateDensityThreshold cannot be negative or NaN.");
        }

        if (options.MaxSelectedNgrams <= 0 ||
            options.MaxSelectedNgrams > SiftOptions.MaximumSelectedNgramCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaxSelectedNgrams must be between one and {SiftOptions.MaximumSelectedNgramCount}.");
        }

        if (options.MaxMaterializedMatchBytes <= 0 ||
            options.MaxMaterializedContextBytes < 0 ||
            options.MaxMaterializedContextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Materialization limits are invalid.");
        }

        if (options.MaxPatternCharacters <= 0 ||
            options.MaxPatternNesting <= 0 ||
            options.MaxPathFilterPatterns <= 0 ||
            options.MaxPathFilterCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Pattern limits must be positive.");
        }

        if (!Enum.IsDefined(options.RejectedUpsertPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RejectedUpsertPolicy is invalid.");
        }

        if (options.MaxCommitMetadataBytes < 0 ||
            options.MaxCommitMetadataBytes > SegmentFormat.MaximumCommitMetadataBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaxCommitMetadataBytes must be between zero and {SegmentFormat.MaximumCommitMetadataBytes}.");
        }

        if (options.OverlayCompactionSegmentThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "OverlayCompactionSegmentThreshold must be positive.");
        }

        if (options.MaxSegmentParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxSegmentParallelism must be positive.");
        }
    }
}
