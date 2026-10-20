namespace Lokad.Sift.Storage;

// Postings inspection belongs to the benchmark harness, which compiles the core
// sources directly. This part is excluded from the shipped library by location.
internal sealed unsafe partial class MappedSegment
{
    public void VisitPostings(IndexRegionPair pair, PostingsVisitor visitor)
    {
        var lexiconRegion = GetRegion(pair.Lexicon);
        var postingsRegion = GetRegion(pair.Postings);
        var lexicon = new ReadOnlySpan<byte>(BasePointer + lexiconRegion.Offset, (int)lexiconRegion.Length);
        if (lexicon.Length < SegmentFormat.LexiconHeaderSize)
        {
            return;
        }

        var entryCount = LittleEndianCodec.ReadInt32(lexicon);
        if (entryCount == 0)
        {
            return;
        }

        var suffixSize = SegmentFormat.GetLexiconSuffixSize(pair.Lexicon);
        var entrySize = SegmentFormat.GetLexiconEntrySize(pair.Lexicon);
        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = SegmentFormat.LexiconHeaderSize + i * entrySize;
            var postingsOffset = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize, 4));
            var postingsBytes = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 4, 4));
            var docFreq = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 8, 4));
            ValidateDocumentFrequency(docFreq);
            if (postingsOffset < 0 || postingsBytes < 0 || postingsOffset > postingsRegion.Length || postingsBytes > postingsRegion.Length - postingsOffset)
            {
                throw new InvalidDataException($"Invalid postings bounds in region '{pair.Postings}'.");
            }

            visitor(new ReadOnlySpan<byte>(BasePointer + postingsRegion.Offset + postingsOffset, postingsBytes), docFreq);
        }
    }
}

internal delegate void PostingsVisitor(ReadOnlySpan<byte> payload, int docFreq);
