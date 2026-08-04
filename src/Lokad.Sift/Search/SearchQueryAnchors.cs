using Lokad.Sift;

namespace Lokad.Sift.Search;

internal readonly record struct SearchOrderedAnchor(
    byte[] Utf8Bytes,
    bool IgnoreCase,
    int? MaxGapBytesFromPrevious,
    bool SameLineFromPrevious);

internal readonly record struct SearchOrderedAnchorGroup(SearchOrderedAnchor[] Anchors);

internal readonly record struct SearchQueryAnchors(
    string[] MandatoryTexts,
    string[] AdvisoryTexts,
    CaseMode AdvisoryCaseMode,
    SearchOrderedAnchorGroup[] OrderedAnchorGroups)
{
    public bool HasMandatoryTexts => MandatoryTexts.Length > 0;
}
