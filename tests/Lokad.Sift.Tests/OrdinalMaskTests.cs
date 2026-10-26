using Lokad.Sift.LowLevel;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class OrdinalMaskTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 32)]
    [InlineData(1, 31)]
    [InlineData(31, 33)]
    [InlineData(32, 64)]
    [InlineData(33, 65)]
    [InlineData(31, 129)]
    [InlineData(160, 161)]
    [InlineData(161, 161)]
    public void RangeWritesPreserveExistingBitsAndDoNotSetPadding(int start, int end)
    {
        using var bits = new PooledBitSet(161);
        bits.Set(0);
        bits.Set(32);
        bits.Set(160);

        bits.SetRange(start, end);
        bits.SetRange(31, 34);

        var expected = Enumerable.Range(start, end - start).Concat([0, 32, 160, 31, 32, 33])
            .Distinct().Order().Select(n => (uint)n);
        Assert.Equal(expected, bits.ToOrdinals());
    }

    [Theory]
    [InlineData(0, 129)]
    [InlineData(1, 130)]
    [InlineData(31, 129)]
    [InlineData(32, 128)]
    [InlineData(65, 161)]
    public void RangeScopesRespectHolesAndPreserveDestinationMembership(uint start, uint end)
    {
        foreach (var excluded in new uint[][]
        {
            [], [0, 1, 31, 32, 64, 128, 129, 160],
            Enumerable.Range(0, 161).Where(n => n % 7 != 0).Select(n => (uint)n).ToArray()
        })
        {
            var scope = OrdinalSet.FromRange(start, end, DeadOrdinalSet.FromOwned(excluded));
            using var bits = new PooledBitSet(161);
            bits.Set(0);
            bits.Set(32);
            bits.Set(160);

            scope.CopyTo(bits);

            var expected = Enumerable.Range((int)start, (int)(end - start)).Select(n => (uint)n)
                .Where(n => !excluded.Contains(n)).Concat([0u, 32u, 160u]).Distinct().Order();
            Assert.Equal(expected, bits.ToOrdinals());
        }
    }
}
