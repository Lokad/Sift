using Lokad.Sift.Search;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Tests;

public sealed class CandidateGenerationTests
{
    [Fact]
    public void PostingsCodecAdaptiveIntersectionMatchesPlainEncoding()
    {
        var right = CreateRange(0, 1536, 3);
        var payload = PostingsCodec.Encode(right);
        var left = new uint[] { 9, 12, 99, 150, 1999, 3000, 4095, 4605 };

        var result = PostingsCodec.IntersectSorted(left, payload, right.Length);

        Assert.Equal(new uint[] { 9, 12, 99, 150, 3000, 4095, 4605 }, result);
    }

    [Fact]
    public void PostingsCodecAdaptiveIntersectionMatchesCheckpointedEncoding()
    {
        var right = CreateRange(0, 5000, 2);
        var payload = PostingsCodec.Encode(right);
        var left = new uint[] { 2, 6, 14, 511, 2048, 6000, 9998, 10_000 };

        var result = PostingsCodec.IntersectSorted(left, payload, right.Length);

        Assert.Equal(new uint[] { 2, 6, 14, 2048, 6000, 9998 }, result);
    }

    [Fact]
    public void PostingsCodecAdaptiveIntersectionRejectsDisjointCheckpointedRanges()
    {
        var right = CreateRange(10_000, 5000, 1);
        var payload = PostingsCodec.Encode(right);
        var left = new uint[] { 1, 10, 100, 9999 };

        var result = PostingsCodec.IntersectSorted(left, payload, right.Length);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(3500)]
    public void PostingsCodecAdaptiveIntersectionRejectsMismatchedDocumentFrequency(int documentFrequency)
    {
        var ordinals = CreateRange(0, 3000, 1);
        var payload = PostingsCodec.Encode(ordinals);

        Assert.Throws<InvalidDataException>(() =>
            PostingsCodec.IntersectSorted(ordinals, payload, documentFrequency));
    }

    [Theory]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 })]
    public void VarIntRejectsMalformedValues(byte[] payload)
    {
        var offset = 0;

        Assert.Throws<InvalidDataException>(() => VarInt.ReadUInt32(payload, ref offset));
    }

    [Fact]
    public void PostingsCodecRejectsOverflowingCheckpointCount()
    {
        var payload = new byte[5];
        payload[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), int.MaxValue);

        Assert.Throws<InvalidDataException>(() => PostingsCodec.Parse(payload));
    }

    [Fact]
    public void PostingsCodecRejectsCheckpointOutsideData()
    {
        var payload = new byte[14];
        payload[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9), 2);

        Assert.Throws<InvalidDataException>(() => PostingsCodec.Parse(payload));
    }

    private static uint[] CreateRange(uint start, int count, uint step)
    {
        var values = new uint[count];
        var current = start;
        for (var i = 0; i < count; i++)
        {
            values[i] = current;
            current += step;
        }

        return values;
    }
}



