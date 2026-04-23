using Lokad.Sift;

namespace Lokad.Sift.Search;

internal readonly record struct SearchLiteralAnchor(string Text);

internal readonly record struct SearchOrderedAnchor(
    byte[] Utf8Bytes,
    bool IgnoreCase,
    int? MaxGapBytesFromPrevious,
    bool SameLineFromPrevious);

internal readonly record struct SearchOrderedAnchorGroup(SearchOrderedAnchor[] Anchors);

internal readonly record struct SearchQueryAnchors(
    SearchLiteralAnchor[] MandatoryAnchors,
    SearchLiteralAnchor[] AdvisoryAnchors,
    CaseMode AdvisoryCaseMode,
    SearchOrderedAnchorGroup[] OrderedAnchorGroups)
{
    public bool HasMandatoryAnchors => MandatoryAnchors.Length > 0;

    public string[] GetMandatoryTexts()
    {
        if (MandatoryAnchors.Length == 0)
        {
            return [];
        }

        var values = new string[MandatoryAnchors.Length];
        for (var i = 0; i < MandatoryAnchors.Length; i++)
        {
            values[i] = MandatoryAnchors[i].Text;
        }

        return values;
    }

    public string[] GetAdvisoryTexts()
    {
        if (AdvisoryAnchors.Length == 0)
        {
            return [];
        }

        var values = new string[AdvisoryAnchors.Length];
        for (var i = 0; i < AdvisoryAnchors.Length; i++)
        {
            values[i] = AdvisoryAnchors[i].Text;
        }

        return values;
    }
}
