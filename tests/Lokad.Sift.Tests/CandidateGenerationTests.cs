using Lokad.Sift.Search;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Tests;

public sealed class CandidateGenerationTests
{
    [Fact]
    public void PostingsEncodingPreservesWireBytesAcrossDeltaWidths()
    {
        uint[] ordinals = [0, 127, 255, 16639, 2113791, 270549247];
        byte[] expected = [0, 0, 127, 128, 1, 128, 128, 1, 128, 128, 128, 1, 128, 128, 128, 128, 1];
        var destination = Enumerable.Repeat((byte)0xCC, expected.Length + 8).ToArray();

        var written = PostingsCodec.Encode(ordinals, destination);

        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, destination[..written]);
        Assert.All(destination[written..], value => Assert.Equal(0xCC, value));
        Assert.Equal(expected, PostingsCodec.Encode(ordinals));
        Assert.Equal(ordinals, PostingsCodec.Decode(destination.AsSpan(0, written)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4095, 0)]
    [InlineData(4096, 16)]
    [InlineData(4097, 17)]
    [InlineData(4353, 18)]
    public void PostingsEncodingRespectsCapacityAndCheckpointBoundaries(int count, int checkpoints)
    {
        var ordinals = CreateRange(0, count, 1);
        var expectedLength = count + (checkpoints == 0 ? 1 : 5 + checkpoints * 8);
        var destination = Enumerable.Repeat((byte)0xCC, expectedLength + 8).ToArray();

        var written = PostingsCodec.Encode(ordinals, destination);

        Assert.Equal(expectedLength, written);
        Assert.Equal(expectedLength, PostingsCodec.GetEncodedLength(ordinals));
        Assert.All(destination[written..], value => Assert.Equal(0xCC, value));
        var payload = destination.AsSpan(0, written);
        var view = PostingsCodec.Parse(payload);
        Assert.Equal(checkpoints > 0, view.IsCheckpointed);
        Assert.Equal(checkpoints * 8, view.Checkpoints.Length);
        if (checkpoints > 0)
        {
            Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0 }, view.Checkpoints[..8].ToArray());
            var last = view.Checkpoints[^8..];
            Assert.Equal((uint)((checkpoints - 1) * 256),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(last));
            Assert.Equal((checkpoints - 1) * 256 + 1,
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(last[4..]));
        }

        PostingsCodec.Validate(payload, count, count);
        Assert.Equal(ordinals, PostingsCodec.Decode(payload));
        Assert.Throws<IndexOutOfRangeException>(() => PostingsCodec.Encode(ordinals, new byte[written - 1]));
    }

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
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(96)]
    public void PostingsCodecSequentialIntersectionRejectsMismatchedDocumentFrequency(int documentFrequency)
    {
        var ordinals = CreateRange(0, 64, 1);
        var payload = PostingsCodec.Encode(ordinals);

        Assert.Throws<InvalidDataException>(() =>
            PostingsCodec.IntersectSorted(ordinals, payload, documentFrequency));
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(6000)]
    public void PostingsCodecCheckpointedIntersectionRejectsMismatchedDocumentFrequency(int documentFrequency)
    {
        var ordinals = CreateRange(0, 5000, 1);
        var payload = PostingsCodec.Encode(ordinals);
        var probes = new uint[] { 0, 100, 1000, 4999 };

        Assert.Throws<InvalidDataException>(() =>
            PostingsCodec.IntersectSorted(probes, payload, documentFrequency));
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



