using Lokad.Sift.Search;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Tests;

public sealed class CandidateGenerationTests
{
    [Fact]
    public async Task IntersectSortedHandlesBalancedInputs()
    {
        var left = new uint[] { 1, 3, 5, 7, 9 };
        var right = new uint[] { 0, 3, 4, 7, 8, 9 };

        var result = CandidateGenerator.IntersectSorted(left, right);

        Assert.Equal(new uint[] { 3, 7, 9 }, result);
    }

    [Fact]
    public async Task IntersectSortedHandlesHighlySkewedInputs()
    {
        var small = new uint[] { 10, 100, 1000, 10_000 };
        var large = Enumerable.Range(0, 20_000)
            .Where(static i => i % 3 == 0 || i == 10 || i == 100 || i == 1000 || i == 10_000)
            .Select(static i => (uint)i)
            .ToArray();

        var result = CandidateGenerator.IntersectSorted(small, large);

        Assert.Equal(new uint[] { 10, 100, 1000, 10_000 }, result);
    }

    [Fact]
    public async Task IntersectSortedReturnsEmptyWhenNoMatches()
    {
        var left = new uint[] { 1, 5, 9 };
        var right = new uint[] { 2, 4, 6, 8 };

        var result = CandidateGenerator.IntersectSorted(left, right);

        Assert.Empty(result);
    }

    [Fact]
    public async Task PostingsCodecAdaptiveIntersectionMatchesPlainEncoding()
    {
        var right = CreateRange(0, 1536, 3);
        var payload = PostingsCodec.Encode(right);
        var left = new uint[] { 9, 12, 99, 150, 1999, 3000, 4095, 4605 };

        var result = PostingsCodec.IntersectSorted(left, payload, right.Length);

        Assert.Equal(new uint[] { 9, 12, 99, 150, 3000, 4095, 4605 }, result);
    }

    [Fact]
    public async Task PostingsCodecAdaptiveIntersectionMatchesCheckpointedEncoding()
    {
        var right = CreateRange(0, 5000, 2);
        var payload = PostingsCodec.Encode(right);
        var left = new uint[] { 2, 6, 14, 511, 2048, 6000, 9998, 10_000 };

        var result = PostingsCodec.IntersectSorted(left, payload, right.Length);

        Assert.Equal(new uint[] { 2, 6, 14, 2048, 6000, 9998 }, result);
    }

    [Fact]
    public async Task PostingsCodecAdaptiveIntersectionRejectsDisjointCheckpointedRanges()
    {
        var right = CreateRange(10_000, 5000, 1);
        var payload = PostingsCodec.Encode(right);
        var left = new uint[] { 1, 10, 100, 9999 };

        var result = PostingsCodec.IntersectSorted(left, payload, right.Length);

        Assert.Empty(result);
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



