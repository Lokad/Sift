using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

const int BenchmarkSearchParallelism = 1;

var options = BenchmarkOptions.Parse(args);
if (options.Mode == BenchmarkMode.RealCorpus)
{
    RunRealCorpusBenchmarks(options);
}
else if (options.Mode == BenchmarkMode.UpdateBench)
{
    RunUpdateBenchmarks(options);
}
else if (options.Mode == BenchmarkMode.CompactBench)
{
    RunCompactBenchmarks(options);
}
else if (options.Mode == BenchmarkMode.PostingsMicro)
{
    RunPostingsMicroBenchmarks(options);
}
else if (options.Mode == BenchmarkMode.PostingsStats)
{
    RunPostingsStats(options);
}
else if (options.Mode == BenchmarkMode.PostingsBdn)
{
    RunPostingsBenchmarkDotNet(options);
}
else
{
    RunSyntheticBenchmarks(options);
}

static void RunSyntheticBenchmarks(BenchmarkOptions options)
{
    var corpusPath = Path.Combine(Path.GetTempPath(), "sift-bench", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(corpusPath);

    try
    {
        using var index = new Sift(corpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1024,
            OverlayCompactionSegmentThreshold = 4
        });
        index.Build(new BenchmarkSource(options.DocumentCount)).GetAwaiter().GetResult();
        using var snapshot = index.OpenSnapshot(new SearchOptions(BenchmarkSearchParallelism));
        using var overlay = index.OpenOverlaySnapshot(new SearchOptions(BenchmarkSearchParallelism));
        var overlayChurnIteration = 0;
        var overlayBodies = new[]
        {
            Encoding.UTF8.GetBytes("alpha needle overlay generation zero\n"),
            Encoding.UTF8.GetBytes("alpha needle overlay generation one\n")
        };

        var trigramAsciiPayload = BuildInsensitiveTrigramPayload(includeNonAscii: false);
        var trigramMixedPayload = BuildInsensitiveTrigramPayload(includeNonAscii: true);
        var results = new List<BenchmarkResult>
        {
            Run("literal-indexed", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
            }),
            Run("snapshot-open-search-dispose", options, () =>
            {
                using var operationSnapshot = index.OpenSnapshot(new SearchOptions(BenchmarkSearchParallelism));
                operationSnapshot.Search(
                    new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
                    new PathFilter(),
                    new NullCollector());
            }),
            Run("overlay-apply-search-churn", options, () =>
            {
                var iteration = overlayChurnIteration++;
                var pathSlot = iteration % 8;
                var bodyGeneration = iteration / 8 % overlayBodies.Length;
                overlay.Apply(
                    new MemoryDocumentSource(
                    [
                        new SubmittedDocument(
                            $"overlay/churn-{pathSlot:D2}.txt",
                            overlayBodies[bodyGeneration],
                            DateTimeOffset.UnixEpoch.AddSeconds(iteration))
                    ]),
                    []).GetAwaiter().GetResult();
                overlay.Search(
                    new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
                    new PathFilter(),
                    new NullCollector());
            }),
            Run("ascii-insensitive-literal", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(new SearchQuery("NEEDLE", PatternMode.Literal, CaseMode.Insensitive), new PathFilter(), collector);
            }),
            Run("regex", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(new SearchQuery("n.e+dle", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
            }),
            Run("regex-fragment-precheck", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(new SearchQuery("needle\\s+\\d+", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
            }),
            Run("mandatory-fragment-precheck", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(new SearchQuery("needle\\s+\\d+\\s+omega", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
            }),
            Run("regex-verifier-path", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(
                    new SearchQuery("^alpha.+needle.+delta$", PatternMode.Regex, CaseMode.Sensitive)
                    {
                        Multiline = true
                    },
                    new PathFilter(),
                    collector);
            }),
            Run("whole-word", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true }, new PathFilter(), collector);
            }),
            Run("path-prefix", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new PathFilter { PathPrefix = "src/00" }, collector);
            }),
            Run("parallel-literal", options, () =>
            {
                var collector = new NullCollector();
                snapshot.Search(
                    new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive)
                    {
                        MaxHits = int.MaxValue,
                        MaxDocuments = int.MaxValue,
                        MaxBytesScanned = long.MaxValue
                    },
                    new PathFilter(),
                    collector);
            }),
            Run("materialize", options, () =>
            {
                var collector = new FirstHitCollector();
                snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
                if (collector.HasHit)
                {
                    _ = snapshot.Materialize(collector.Hit, 2, 2);
                }
            }),
            Run("trigram-extraction", options, static () =>
            {
                var bytes = Encoding.UTF8.GetBytes("alpha beta gamma needle delta epsilon");
                var count = 0;
                for (var i = 0; i <= bytes.Length - 3; i++)
                {
                    _ = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
                    count++;
                }

                _ = count;
            }),
            Run("trigram-insensitive-scalar-ascii", options, () =>
            {
                _ = CountInsensitiveTrigramsScalar(trigramAsciiPayload);
            }),
            Run("trigram-insensitive-ascii-block-ascii", options, () =>
            {
                _ = CountInsensitiveTrigramsAsciiBlocks(trigramAsciiPayload);
            }),
            Run("trigram-insensitive-simd-block-ascii", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdBlocks(trigramAsciiPayload);
            }),
            Run("trigram-insensitive-simd-folded-ascii", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdFolded(trigramAsciiPayload);
            }),
            Run("trigram-insensitive-simd-runs-ascii", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdRuns(trigramAsciiPayload);
            }),
            Run("trigram-insensitive-simd-allascii-ascii", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdAllAscii(trigramAsciiPayload);
            }),
            Run("trigram-insensitive-scalar-mixed", options, () =>
            {
                _ = CountInsensitiveTrigramsScalar(trigramMixedPayload);
            }),
            Run("trigram-insensitive-ascii-block-mixed", options, () =>
            {
                _ = CountInsensitiveTrigramsAsciiBlocks(trigramMixedPayload);
            }),
            Run("trigram-insensitive-simd-block-mixed", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdBlocks(trigramMixedPayload);
            }),
            Run("trigram-insensitive-simd-folded-mixed", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdFolded(trigramMixedPayload);
            }),
            Run("trigram-insensitive-simd-runs-mixed", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdRuns(trigramMixedPayload);
            }),
            Run("trigram-insensitive-simd-allascii-mixed", options, () =>
            {
                _ = CountInsensitiveTrigramsSimdAllAscii(trigramMixedPayload);
            }),
            Run("sorted-intersection", options, static () =>
            {
                var left = BuildSparseOrdinals(100_000, 3);
                var right = BuildSparseOrdinals(100_000, 5);
                _ = Intersect(left, right);
            }),
            Run("bitset-and", options, static () =>
            {
                using var left = new PooledBitSet(100_000);
                using var right = new PooledBitSet(100_000);
                for (var i = 0; i < 100_000; i += 3)
                {
                    left.Set(i);
                }

                for (var i = 0; i < 100_000; i += 5)
                {
                    right.Set(i);
                }

                left.And(right);
                _ = left.Count();
            })
        };

        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name}: median={result.MedianMilliseconds:F2} ms min={result.MinMilliseconds:F2} ms p95={result.P95Milliseconds:F2} ms samples={result.Iterations}");
        }

        if (options.JsonOutputPath is not null)
        {
            var json = JsonSerializer.Serialize(new BenchmarkRun(
                options.DocumentCount,
                options.WarmupIterations,
                options.MeasurementIterations,
                results.ToArray()), CreateJsonOptions());
            File.WriteAllText(options.JsonOutputPath, json);
        }

        ApplyThresholds(options, results);
    }
    finally
    {
        if (Directory.Exists(corpusPath))
        {
            Directory.Delete(corpusPath, recursive: true);
        }
    }
}

static byte[] BuildInsensitiveTrigramPayload(bool includeNonAscii)
{
    const string asciiLine = "public static int Add(int a, int b) { return a + b; }\n";
    const string mixedLine = "public static string Café(string s) => s + \"déjà vu\";\n";
    var builder = new StringBuilder(256 * 1024);
    for (var i = 0; i < 4_096; i++)
    {
        builder.Append(asciiLine);
        if (includeNonAscii && (i % 16 == 0))
        {
            builder.Append(mixedLine);
        }
    }

    return Encoding.UTF8.GetBytes(builder.ToString());
}

static int CountInsensitiveTrigramsScalar(ReadOnlySpan<byte> content)
{
    if (content.Length < 3)
    {
        return 0;
    }

    var checksum = 0;
    for (var i = 0; i < content.Length - 2; i++)
    {
        var first = content[i];
        var second = content[i + 1];
        var third = content[i + 2];
        if ((first | second | third) > 0x7F)
        {
            continue;
        }

        first = FoldAscii(first);
        second = FoldAscii(second);
        third = FoldAscii(third);
        checksum += (first << 16) | (second << 8) | third;
    }

    return checksum;
}

static int CountInsensitiveTrigramsAsciiBlocks(ReadOnlySpan<byte> content)
{
    if (content.Length < 3)
    {
        return 0;
    }

    var checksum = 0;
    var i = 0;
    var blockLimit = content.Length - 18;
    while (i <= blockLimit)
    {
        var block = content.Slice(i, 16);
        if (ContainsNonAscii(block))
        {
            i++;
            continue;
        }

        var end = i + 14;
        for (; i <= end; i++)
        {
            var first = FoldAscii(content[i]);
            var second = FoldAscii(content[i + 1]);
            var third = FoldAscii(content[i + 2]);
            checksum += (first << 16) | (second << 8) | third;
        }
    }

    for (; i < content.Length - 2; i++)
    {
        var first = content[i];
        var second = content[i + 1];
        var third = content[i + 2];
        if ((first | second | third) > 0x7F)
        {
            continue;
        }

        first = FoldAscii(first);
        second = FoldAscii(second);
        third = FoldAscii(third);
        checksum += (first << 16) | (second << 8) | third;
    }

    return checksum;
}

static int CountInsensitiveTrigramsSimdBlocks(ReadOnlySpan<byte> content)
{
    if (content.Length < 3)
    {
        return 0;
    }

    var checksum = 0;
    var i = 0;
    var blockLimit = content.Length - 18;
    while (i <= blockLimit)
    {
        var block = content.Slice(i, 16);
        if (ContainsNonAsciiSimd(block))
        {
            i++;
            continue;
        }

        var end = i + 14;
        for (; i <= end; i++)
        {
            var first = FoldAscii(content[i]);
            var second = FoldAscii(content[i + 1]);
            var third = FoldAscii(content[i + 2]);
            checksum += (first << 16) | (second << 8) | third;
        }
    }

    for (; i < content.Length - 2; i++)
    {
        var first = content[i];
        var second = content[i + 1];
        var third = content[i + 2];
        if ((first | second | third) > 0x7F)
        {
            continue;
        }

        first = FoldAscii(first);
        second = FoldAscii(second);
        third = FoldAscii(third);
        checksum += (first << 16) | (second << 8) | third;
    }

    return checksum;
}

static int CountInsensitiveTrigramsSimdFolded(ReadOnlySpan<byte> content)
{
    if (content.Length < 3)
    {
        return 0;
    }

    if (!(Vector128.IsHardwareAccelerated && Sse2.IsSupported))
    {
        return CountInsensitiveTrigramsScalar(content);
    }

    Span<byte> folded = stackalloc byte[16];
    var checksum = 0;
    var i = 0;
    var blockLimit = content.Length - 18;
    while (i <= blockLimit)
    {
        var block = content.Slice(i, 16);
        if (!TryFoldAsciiBlockSimd(block, folded))
        {
            i++;
            continue;
        }

        for (var j = 0; j < 14; j++)
        {
            checksum += (folded[j] << 16) | (folded[j + 1] << 8) | folded[j + 2];
        }

        i += 14;
    }

    for (; i < content.Length - 2; i++)
    {
        var first = content[i];
        var second = content[i + 1];
        var third = content[i + 2];
        if ((first | second | third) > 0x7F)
        {
            continue;
        }

        first = FoldAscii(first);
        second = FoldAscii(second);
        third = FoldAscii(third);
        checksum += (first << 16) | (second << 8) | third;
    }

    return checksum;
}

static int CountInsensitiveTrigramsSimdRuns(ReadOnlySpan<byte> content)
{
    if (content.Length < 3)
    {
        return 0;
    }

    if (!(Vector128.IsHardwareAccelerated && Sse2.IsSupported))
    {
        return CountInsensitiveTrigramsScalar(content);
    }

    var checksum = 0;
    var limit = content.Length - 2;
    var i = 0;
    while (i < limit)
    {
        while (i < limit && content[i] > 0x7F)
        {
            i++;
        }

        if (i >= limit)
        {
            break;
        }

        var runStart = i;
        i = AdvanceAsciiRun(content, i);
        var runLength = i - runStart;
        if (runLength >= 3)
        {
            checksum += EmitInsensitiveTrigramsAsciiRun(content.Slice(runStart, runLength));
        }

        if (i < content.Length)
        {
            i++;
        }
    }

    return checksum;
}

static int CountInsensitiveTrigramsSimdAllAscii(ReadOnlySpan<byte> content)
{
    if (content.Length < 3)
    {
        return 0;
    }

    if (IsAllAsciiSimd(content))
    {
        return EmitInsensitiveTrigramsAsciiRun(content);
    }

    return CountInsensitiveTrigramsScalar(content);
}

static int AdvanceAsciiRun(ReadOnlySpan<byte> content, int start)
{
    var i = start;
    if (Avx2.IsSupported)
    {
        var blockLimit = content.Length - 32;
        while (i <= blockLimit)
        {
            var block = MemoryMarshal.Read<Vector256<byte>>(content.Slice(i, 32));
            if (Avx2.MoveMask(block.AsSByte()) != 0)
            {
                break;
            }

            i += 32;
        }
    }

    if (Sse2.IsSupported)
    {
        var blockLimit = content.Length - 16;
        while (i <= blockLimit)
        {
            var block = MemoryMarshal.Read<Vector128<byte>>(content.Slice(i, 16));
            if (Sse2.MoveMask(block.AsSByte()) != 0)
            {
                break;
            }

            i += 16;
        }
    }

    while (i < content.Length && content[i] <= 0x7F)
    {
        i++;
    }

    return i;
}

static bool IsAllAsciiSimd(ReadOnlySpan<byte> content)
{
    var i = 0;
    if (Avx2.IsSupported)
    {
        var blockLimit = content.Length - 32;
        while (i <= blockLimit)
        {
            var block = MemoryMarshal.Read<Vector256<byte>>(content.Slice(i, 32));
            if (Avx2.MoveMask(block.AsSByte()) != 0)
            {
                return false;
            }

            i += 32;
        }
    }

    if (Sse2.IsSupported)
    {
        var blockLimit = content.Length - 16;
        while (i <= blockLimit)
        {
            var block = MemoryMarshal.Read<Vector128<byte>>(content.Slice(i, 16));
            if (Sse2.MoveMask(block.AsSByte()) != 0)
            {
                return false;
            }

            i += 16;
        }
    }

    while (i < content.Length)
    {
        if (content[i] > 0x7F)
        {
            return false;
        }

        i++;
    }

    return true;
}

static int EmitInsensitiveTrigramsAsciiRun(ReadOnlySpan<byte> asciiRun)
{
    var checksum = 0;
    var limit = asciiRun.Length - 2;
    var i = 0;
    var unrolledLimit = limit - 3;
    while (i < unrolledLimit)
    {
        var first0 = FoldAscii(asciiRun[i]);
        var second0 = FoldAscii(asciiRun[i + 1]);
        var third0 = FoldAscii(asciiRun[i + 2]);
        checksum += (first0 << 16) | (second0 << 8) | third0;

        var first1 = second0;
        var second1 = third0;
        var third1 = FoldAscii(asciiRun[i + 3]);
        checksum += (first1 << 16) | (second1 << 8) | third1;

        var first2 = second1;
        var second2 = third1;
        var third2 = FoldAscii(asciiRun[i + 4]);
        checksum += (first2 << 16) | (second2 << 8) | third2;

        var first3 = second2;
        var second3 = third2;
        var third3 = FoldAscii(asciiRun[i + 5]);
        checksum += (first3 << 16) | (second3 << 8) | third3;

        i += 4;
    }

    for (; i < limit; i++)
    {
        var first = FoldAscii(asciiRun[i]);
        var second = FoldAscii(asciiRun[i + 1]);
        var third = FoldAscii(asciiRun[i + 2]);
        checksum += (first << 16) | (second << 8) | third;
    }

    return checksum;
}

static bool ContainsNonAscii(ReadOnlySpan<byte> block)
{
    for (var i = 0; i < block.Length; i++)
    {
        if (block[i] > 0x7F)
        {
            return true;
        }
    }

    return false;
}

static bool ContainsNonAsciiSimd(ReadOnlySpan<byte> block)
{
    if (Vector128.IsHardwareAccelerated && Sse2.IsSupported)
    {
        var bytes = MemoryMarshal.Read<Vector128<byte>>(block);
        return Sse2.MoveMask(bytes.AsSByte()) != 0;
    }

    return ContainsNonAscii(block);
}

static bool TryFoldAsciiBlockSimd(ReadOnlySpan<byte> block, Span<byte> destination)
{
    if (!(Vector128.IsHardwareAccelerated && Sse2.IsSupported))
    {
        if (ContainsNonAscii(block))
        {
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            destination[i] = FoldAscii(block[i]);
        }

        return true;
    }

    var bytes = MemoryMarshal.Read<Vector128<byte>>(block);
    if (Sse2.MoveMask(bytes.AsSByte()) != 0)
    {
        return false;
    }

    var asciiBias = Vector128.Create((byte)0x80);
    var upperA = Vector128.Create((byte)('A' ^ 0x80));
    var upperZPlus1 = Vector128.Create((byte)(('Z' + 1) ^ 0x80));
    var lowerBit = Vector128.Create((byte)0x20);
    var biased = Sse2.Xor(bytes, asciiBias);
    var geA = Sse2.CompareGreaterThan(biased.AsSByte(), Sse2.Subtract(upperA, Vector128.Create((byte)1)).AsSByte()).AsByte();
    var ltZPlus1 = Sse2.CompareGreaterThan(upperZPlus1.AsSByte(), biased.AsSByte()).AsByte();
    var upperMask = Sse2.And(geA, ltZPlus1);
    var lowered = Sse2.Or(bytes, Sse2.And(upperMask, lowerBit));
    MemoryMarshal.Write(destination, lowered);
    return true;
}

static byte FoldAscii(byte value)
{
    return (byte)(value is >= (byte)'A' and <= (byte)'Z' ? value + 32 : value);
}

static void RunPostingsMicroBenchmarks(BenchmarkOptions options)
{
    var plainOrdinals = BuildArithmeticOrdinals(2_048, 3);
    var checkpointedOrdinals = BuildArithmeticOrdinals(200_000, 3);
    var checkpointedWidth8Ordinals = BuildArithmeticOrdinals(200_000, 2);
    var plainPayload = PostingsCodec.Encode(plainOrdinals);
    var checkpointedPayload = PostingsCodec.Encode(checkpointedOrdinals);
    var checkpointedWidth8Payload = PostingsCodec.Encode(checkpointedWidth8Ordinals);
    var blockPayload = BlockPostingsCodec.Encode(checkpointedOrdinals);
    var packedBlockPayload = PackedBlockPostingsCodec.Encode(checkpointedOrdinals);
    var bp128Payload = Bp128PostingsCodec.Encode(checkpointedOrdinals);
    var bp128Width8Payload = Bp128PostingsCodec.Encode(checkpointedWidth8Ordinals);
    var streamVBytePayload = StreamVBytePostingsCodec.Encode(checkpointedOrdinals);
    var streamVByteWidth8Payload = StreamVBytePostingsCodec.Encode(checkpointedWidth8Ordinals);
    var target1KOrdinals = BuildOrdinalsForEncodedSizeTarget(1_024);
    var target2KOrdinals = BuildOrdinalsForEncodedSizeTarget(2_048);
    var target1KCheckpointedPayload = PostingsCodec.Encode(target1KOrdinals);
    var target2KCheckpointedPayload = PostingsCodec.Encode(target2KOrdinals);
    var target1KBlockPayload = BlockPostingsCodec.Encode(target1KOrdinals);
    var target2KBlockPayload = BlockPostingsCodec.Encode(target2KOrdinals);
    var target1KPackedBlockPayload = PackedBlockPostingsCodec.Encode(target1KOrdinals);
    var target2KPackedBlockPayload = PackedBlockPostingsCodec.Encode(target2KOrdinals);
    var target1KBp128Payload = Bp128PostingsCodec.Encode(target1KOrdinals);
    var target2KBp128Payload = Bp128PostingsCodec.Encode(target2KOrdinals);
    var target1KStreamVBytePayload = StreamVBytePostingsCodec.Encode(target1KOrdinals);
    var target2KStreamVBytePayload = StreamVBytePostingsCodec.Encode(target2KOrdinals);
    var target1KLeft = BuildArithmeticOrdinals(Math.Max(64, target1KOrdinals.Length / 2), 5);
    var target2KLeft = BuildArithmeticOrdinals(Math.Max(128, target2KOrdinals.Length / 2), 5);
    var balancedLeft = BuildArithmeticOrdinals(80_000, 5);
    var skewedLeft = BuildArithmeticOrdinals(8_000, 97);
    var denseLeft = BuildArithmeticOrdinals(160_000, 4);
    var subsetHalfLeft = PostingsBenchmarkData.BuildSubsetOrdinals(checkpointedOrdinals, 2);
    var subsetSparseLeft = PostingsBenchmarkData.BuildSubsetOrdinals(checkpointedOrdinals, 16);
    var disjointLeft = PostingsBenchmarkData.BuildDisjointOrdinals(checkpointedOrdinals, 80_000);
    var tailMissLeft = PostingsBenchmarkData.BuildTailMissOrdinals(checkpointedOrdinals, 80_000);
    var subsetHalfWidth8Left = PostingsBenchmarkData.BuildSubsetOrdinals(checkpointedWidth8Ordinals, 2);
    var disjointWidth8Left = PostingsBenchmarkData.BuildDisjointOrdinals(checkpointedWidth8Ordinals, 80_000);

    Console.WriteLine($"postings-micro: checkpointedBytes={checkpointedPayload.Length} blockBytes={blockPayload.Length} packedBlockBytes={packedBlockPayload.Length} bp128Bytes={bp128Payload.Length} streamVByteBytes={streamVBytePayload.Length}");
    Console.WriteLine($"postings-micro: target1k checkpointedBytes={target1KCheckpointedPayload.Length} blockBytes={target1KBlockPayload.Length} packedBlockBytes={target1KPackedBlockPayload.Length} bp128Bytes={target1KBp128Payload.Length} streamVByteBytes={target1KStreamVBytePayload.Length} ordinals={target1KOrdinals.Length}");
    Console.WriteLine($"postings-micro: target2k checkpointedBytes={target2KCheckpointedPayload.Length} blockBytes={target2KBlockPayload.Length} packedBlockBytes={target2KPackedBlockPayload.Length} bp128Bytes={target2KBp128Payload.Length} streamVByteBytes={target2KStreamVBytePayload.Length} ordinals={target2KOrdinals.Length}");

    var results = new List<BenchmarkResult>
    {
        Run("postings-encode-plain", options, () =>
        {
            _ = PostingsCodec.Encode(plainOrdinals);
        }),
        Run("postings-encode-checkpointed", options, () =>
        {
            _ = PostingsCodec.Encode(checkpointedOrdinals);
        }),
        Run("postings-decode-plain", options, () =>
        {
            _ = PostingsCodec.Decode(plainPayload);
        }),
        Run("postings-decode-checkpointed", options, () =>
        {
            _ = PostingsCodec.Decode(checkpointedPayload);
        }),
        Run("postings-decode-checkpointed-adjacent2-shuffle", options, () =>
        {
            _ = AdjacentVarIntDecode.DecodeCheckpointedU32Adjacent2(checkpointedPayload);
        }),
        Run("postings-decode-checkpointed-adjacent4-shuffle", options, () =>
        {
            _ = AdjacentVarIntDecode.DecodeCheckpointedU32Adjacent4(checkpointedPayload);
        }),
        Run("postings-encode-block", options, () =>
        {
            _ = BlockPostingsCodec.Encode(checkpointedOrdinals);
        }),
        Run("postings-encode-packed-block", options, () =>
        {
            _ = PackedBlockPostingsCodec.Encode(checkpointedOrdinals);
        }),
        Run("postings-encode-bp128", options, () =>
        {
            _ = Bp128PostingsCodec.Encode(checkpointedOrdinals);
        }),
        Run("postings-encode-streamvbyte", options, () =>
        {
            _ = StreamVBytePostingsCodec.Encode(checkpointedOrdinals);
        }),
        Run("postings-decode-block", options, () =>
        {
            _ = BlockPostingsCodec.Decode(blockPayload);
        }),
        Run("postings-decode-packed-block", options, () =>
        {
            _ = PackedBlockPostingsCodec.Decode(packedBlockPayload);
        }),
        Run("postings-decode-bp128", options, () =>
        {
            _ = Bp128PostingsCodec.Decode(bp128Payload);
        }),
        Run("postings-decode-streamvbyte", options, () =>
        {
            _ = StreamVBytePostingsCodec.Decode(streamVBytePayload);
        }),
        Run("postings-decode-checkpointed-target1k", options, () =>
        {
            _ = PostingsCodec.Decode(target1KCheckpointedPayload);
        }),
        Run("postings-decode-block-target1k", options, () =>
        {
            _ = BlockPostingsCodec.Decode(target1KBlockPayload);
        }),
        Run("postings-decode-packed-block-target1k", options, () =>
        {
            _ = PackedBlockPostingsCodec.Decode(target1KPackedBlockPayload);
        }),
        Run("postings-decode-bp128-target1k", options, () =>
        {
            _ = Bp128PostingsCodec.Decode(target1KBp128Payload);
        }),
        Run("postings-decode-streamvbyte-target1k", options, () =>
        {
            _ = StreamVBytePostingsCodec.Decode(target1KStreamVBytePayload);
        }),
        Run("postings-decode-checkpointed-target2k", options, () =>
        {
            _ = PostingsCodec.Decode(target2KCheckpointedPayload);
        }),
        Run("postings-decode-checkpointed-target2k-adjacent2-shuffle", options, () =>
        {
            _ = AdjacentVarIntDecode.DecodeCheckpointedU32Adjacent2(target2KCheckpointedPayload);
        }),
        Run("postings-decode-checkpointed-target2k-adjacent4-shuffle", options, () =>
        {
            _ = AdjacentVarIntDecode.DecodeCheckpointedU32Adjacent4(target2KCheckpointedPayload);
        }),
        Run("postings-decode-block-target2k", options, () =>
        {
            _ = BlockPostingsCodec.Decode(target2KBlockPayload);
        }),
        Run("postings-decode-packed-block-target2k", options, () =>
        {
            _ = PackedBlockPostingsCodec.Decode(target2KPackedBlockPayload);
        }),
        Run("postings-decode-bp128-target2k", options, () =>
        {
            _ = Bp128PostingsCodec.Decode(target2KBp128Payload);
        }),
        Run("postings-decode-streamvbyte-target2k", options, () =>
        {
            _ = StreamVBytePostingsCodec.Decode(target2KStreamVBytePayload);
        }),
        Run("postings-intersect-plain-balanced", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(balancedLeft, plainPayload, plainOrdinals.Length);
        }),
        Run("postings-intersect-blockmerge-balanced", options, () =>
        {
            _ = IntersectBlockMerge(balancedLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-simdblockmerge-balanced", options, () =>
        {
            _ = IntersectSimdBlockMerge(balancedLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-adaptive-balanced", options, () =>
        {
            _ = IntersectAdaptive(balancedLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-adaptivesimd-balanced", options, () =>
        {
            _ = IntersectAdaptiveSimd(balancedLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-balanced", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(balancedLeft, checkpointedPayload, checkpointedOrdinals.Length);
        }),
        Run("postings-intersect-checkpointed-blockdecode-balanced", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSorted(balancedLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-simd-balanced", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedSimd(balancedLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent2-shuffle-simd-balanced", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent2ShuffleSimd(balancedLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent4-shuffle-simd-balanced", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent4ShuffleSimd(balancedLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-skewed", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(skewedLeft, checkpointedPayload, checkpointedOrdinals.Length);
        }),
        Run("postings-intersect-checkpointed-blockdecode-skewed", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSorted(skewedLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-simd-skewed", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedSimd(skewedLeft, checkpointedPayload);
        }),
        Run("postings-intersect-blockmerge-skewed", options, () =>
        {
            _ = IntersectBlockMerge(skewedLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-simdblockmerge-skewed", options, () =>
        {
            _ = IntersectSimdBlockMerge(skewedLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-adaptive-skewed", options, () =>
        {
            _ = IntersectAdaptive(skewedLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-adaptivesimd-skewed", options, () =>
        {
            _ = IntersectAdaptiveSimd(skewedLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-dense", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(denseLeft, checkpointedPayload, checkpointedOrdinals.Length);
        }),
        Run("postings-intersect-checkpointed-blockdecode-dense", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSorted(denseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-simd-dense", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedSimd(denseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent2-shuffle-simd-dense", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent2ShuffleSimd(denseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent4-shuffle-simd-dense", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent4ShuffleSimd(denseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-blockmerge-dense", options, () =>
        {
            _ = IntersectBlockMerge(denseLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-simdblockmerge-dense", options, () =>
        {
            _ = IntersectSimdBlockMerge(denseLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-adaptive-dense", options, () =>
        {
            _ = IntersectAdaptive(denseLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-adaptivesimd-dense", options, () =>
        {
            _ = IntersectAdaptiveSimd(denseLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-block-balanced", options, () =>
        {
            _ = BlockPostingsCodec.IntersectSorted(balancedLeft, blockPayload);
        }),
        Run("postings-intersect-block-skewed", options, () =>
        {
            _ = BlockPostingsCodec.IntersectSorted(skewedLeft, blockPayload);
        }),
        Run("postings-intersect-block-dense", options, () =>
        {
            _ = BlockPostingsCodec.IntersectSorted(denseLeft, blockPayload);
        }),
        Run("postings-intersect-packed-block-balanced", options, () =>
        {
            _ = PackedBlockPostingsCodec.IntersectSorted(balancedLeft, packedBlockPayload);
        }),
        Run("postings-intersect-packed-block-skewed", options, () =>
        {
            _ = PackedBlockPostingsCodec.IntersectSorted(skewedLeft, packedBlockPayload);
        }),
        Run("postings-intersect-packed-block-dense", options, () =>
        {
            _ = PackedBlockPostingsCodec.IntersectSorted(denseLeft, packedBlockPayload);
        }),
        Run("postings-intersect-bp128-balanced", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(balancedLeft, bp128Payload);
        }),
        Run("postings-intersect-bp128-skewed", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(skewedLeft, bp128Payload);
        }),
        Run("postings-intersect-bp128-dense", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(denseLeft, bp128Payload);
        }),
        Run("postings-intersect-streamvbyte-balanced", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(balancedLeft, streamVBytePayload);
        }),
        Run("postings-intersect-streamvbyte-skewed", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(skewedLeft, streamVBytePayload);
        }),
        Run("postings-intersect-streamvbyte-dense", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(denseLeft, streamVBytePayload);
        }),
        Run("postings-intersect-checkpointed-subset-half", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(subsetHalfLeft, checkpointedPayload, checkpointedOrdinals.Length);
        }),
        Run("postings-intersect-checkpointed-blockdecode-subset-half", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSorted(subsetHalfLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-simd-subset-half", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedSimd(subsetHalfLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent2-shuffle-simd-subset-half", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent2ShuffleSimd(subsetHalfLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent4-shuffle-simd-subset-half", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent4ShuffleSimd(subsetHalfLeft, checkpointedPayload);
        }),
        Run("postings-intersect-blockmerge-subset-half", options, () =>
        {
            _ = IntersectBlockMerge(subsetHalfLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-simdblockmerge-subset-half", options, () =>
        {
            _ = IntersectSimdBlockMerge(subsetHalfLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-adaptive-subset-half", options, () =>
        {
            _ = IntersectAdaptive(subsetHalfLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-adaptivesimd-subset-half", options, () =>
        {
            _ = IntersectAdaptiveSimd(subsetHalfLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-bp128-subset-half", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(subsetHalfLeft, bp128Payload);
        }),
        Run("postings-intersect-streamvbyte-subset-half", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(subsetHalfLeft, streamVBytePayload);
        }),
        Run("postings-intersect-checkpointed-subset-sparse", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(subsetSparseLeft, checkpointedPayload, checkpointedOrdinals.Length);
        }),
        Run("postings-intersect-checkpointed-blockdecode-subset-sparse", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSorted(subsetSparseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-simd-subset-sparse", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedSimd(subsetSparseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent2-shuffle-simd-subset-sparse", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent2ShuffleSimd(subsetSparseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent4-shuffle-simd-subset-sparse", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent4ShuffleSimd(subsetSparseLeft, checkpointedPayload);
        }),
        Run("postings-intersect-blockmerge-subset-sparse", options, () =>
        {
            _ = IntersectBlockMerge(subsetSparseLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-simdblockmerge-subset-sparse", options, () =>
        {
            _ = IntersectSimdBlockMerge(subsetSparseLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-adaptive-subset-sparse", options, () =>
        {
            _ = IntersectAdaptive(subsetSparseLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-adaptivesimd-subset-sparse", options, () =>
        {
            _ = IntersectAdaptiveSimd(subsetSparseLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-bp128-subset-sparse", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(subsetSparseLeft, bp128Payload);
        }),
        Run("postings-intersect-streamvbyte-subset-sparse", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(subsetSparseLeft, streamVBytePayload);
        }),
        Run("postings-intersect-checkpointed-disjoint", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(disjointLeft, checkpointedPayload, checkpointedOrdinals.Length);
        }),
        Run("postings-intersect-checkpointed-blockdecode-disjoint", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSorted(disjointLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-simd-disjoint", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedSimd(disjointLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent2-shuffle-simd-disjoint", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent2ShuffleSimd(disjointLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-adjacent4-shuffle-simd-disjoint", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedAdjacent4ShuffleSimd(disjointLeft, checkpointedPayload);
        }),
        Run("postings-intersect-blockmerge-disjoint", options, () =>
        {
            _ = IntersectBlockMerge(disjointLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-simdblockmerge-disjoint", options, () =>
        {
            _ = IntersectSimdBlockMerge(disjointLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-adaptive-disjoint", options, () =>
        {
            _ = IntersectAdaptive(disjointLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-adaptivesimd-disjoint", options, () =>
        {
            _ = IntersectAdaptiveSimd(disjointLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-bp128-disjoint", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(disjointLeft, bp128Payload);
        }),
        Run("postings-intersect-streamvbyte-disjoint", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(disjointLeft, streamVBytePayload);
        }),
        Run("postings-intersect-checkpointed-tail-miss", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(tailMissLeft, checkpointedPayload, checkpointedOrdinals.Length);
        }),
        Run("postings-intersect-checkpointed-blockdecode-tail-miss", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSorted(tailMissLeft, checkpointedPayload);
        }),
        Run("postings-intersect-checkpointed-blockdecode-simd-tail-miss", options, () =>
        {
            _ = CheckpointedBlockIntersection.IntersectSortedSimd(tailMissLeft, checkpointedPayload);
        }),
        Run("postings-intersect-blockmerge-tail-miss", options, () =>
        {
            _ = IntersectBlockMerge(tailMissLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-simdblockmerge-tail-miss", options, () =>
        {
            _ = IntersectSimdBlockMerge(tailMissLeft, checkpointedOrdinals);
        }),
        Run("postings-intersect-adaptive-tail-miss", options, () =>
        {
            _ = IntersectAdaptive(tailMissLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-adaptivesimd-tail-miss", options, () =>
        {
            _ = IntersectAdaptiveSimd(tailMissLeft, checkpointedOrdinals, checkpointedPayload);
        }),
        Run("postings-intersect-bp128-tail-miss", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(tailMissLeft, bp128Payload);
        }),
        Run("postings-intersect-streamvbyte-tail-miss", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(tailMissLeft, streamVBytePayload);
        }),
        Run("postings-intersect-checkpointed-width8-subset-half", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(subsetHalfWidth8Left, checkpointedWidth8Payload, checkpointedWidth8Ordinals.Length);
        }),
        Run("postings-intersect-blockmerge-width8-subset-half", options, () =>
        {
            _ = IntersectBlockMerge(subsetHalfWidth8Left, checkpointedWidth8Ordinals);
        }),
        Run("postings-intersect-simdblockmerge-width8-subset-half", options, () =>
        {
            _ = IntersectSimdBlockMerge(subsetHalfWidth8Left, checkpointedWidth8Ordinals);
        }),
        Run("postings-intersect-adaptive-width8-subset-half", options, () =>
        {
            _ = IntersectAdaptive(subsetHalfWidth8Left, checkpointedWidth8Ordinals, checkpointedWidth8Payload);
        }),
        Run("postings-intersect-adaptivesimd-width8-subset-half", options, () =>
        {
            _ = IntersectAdaptiveSimd(subsetHalfWidth8Left, checkpointedWidth8Ordinals, checkpointedWidth8Payload);
        }),
        Run("postings-intersect-bp128-width8-subset-half", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(subsetHalfWidth8Left, bp128Width8Payload);
        }),
        Run("postings-intersect-streamvbyte-width8-subset-half", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(subsetHalfWidth8Left, streamVByteWidth8Payload);
        }),
        Run("postings-intersect-checkpointed-width8-disjoint", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(disjointWidth8Left, checkpointedWidth8Payload, checkpointedWidth8Ordinals.Length);
        }),
        Run("postings-intersect-blockmerge-width8-disjoint", options, () =>
        {
            _ = IntersectBlockMerge(disjointWidth8Left, checkpointedWidth8Ordinals);
        }),
        Run("postings-intersect-simdblockmerge-width8-disjoint", options, () =>
        {
            _ = IntersectSimdBlockMerge(disjointWidth8Left, checkpointedWidth8Ordinals);
        }),
        Run("postings-intersect-adaptive-width8-disjoint", options, () =>
        {
            _ = IntersectAdaptive(disjointWidth8Left, checkpointedWidth8Ordinals, checkpointedWidth8Payload);
        }),
        Run("postings-intersect-adaptivesimd-width8-disjoint", options, () =>
        {
            _ = IntersectAdaptiveSimd(disjointWidth8Left, checkpointedWidth8Ordinals, checkpointedWidth8Payload);
        }),
        Run("postings-intersect-bp128-width8-disjoint", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(disjointWidth8Left, bp128Width8Payload);
        }),
        Run("postings-intersect-streamvbyte-width8-disjoint", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(disjointWidth8Left, streamVByteWidth8Payload);
        }),
        Run("postings-intersect-checkpointed-target1k", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(target1KLeft, target1KCheckpointedPayload, target1KOrdinals.Length);
        }),
        Run("postings-intersect-block-target1k", options, () =>
        {
            _ = BlockPostingsCodec.IntersectSorted(target1KLeft, target1KBlockPayload);
        }),
        Run("postings-intersect-packed-block-target1k", options, () =>
        {
            _ = PackedBlockPostingsCodec.IntersectSorted(target1KLeft, target1KPackedBlockPayload);
        }),
        Run("postings-intersect-bp128-target1k", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(target1KLeft, target1KBp128Payload);
        }),
        Run("postings-intersect-streamvbyte-target1k", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(target1KLeft, target1KStreamVBytePayload);
        }),
        Run("postings-intersect-checkpointed-target2k", options, () =>
        {
            _ = PostingsCodec.IntersectSorted(target2KLeft, target2KCheckpointedPayload, target2KOrdinals.Length);
        }),
        Run("postings-intersect-block-target2k", options, () =>
        {
            _ = BlockPostingsCodec.IntersectSorted(target2KLeft, target2KBlockPayload);
        }),
        Run("postings-intersect-packed-block-target2k", options, () =>
        {
            _ = PackedBlockPostingsCodec.IntersectSorted(target2KLeft, target2KPackedBlockPayload);
        }),
        Run("postings-intersect-bp128-target2k", options, () =>
        {
            _ = Bp128PostingsCodec.IntersectSorted(target2KLeft, target2KBp128Payload);
        }),
        Run("postings-intersect-streamvbyte-target2k", options, () =>
        {
            _ = StreamVBytePostingsCodec.IntersectSorted(target2KLeft, target2KStreamVBytePayload);
        })
    };

    foreach (var result in results)
    {
        Console.WriteLine($"{result.Name}: median={result.MedianMilliseconds:F2} ms min={result.MinMilliseconds:F2} ms p95={result.P95Milliseconds:F2} ms samples={result.Iterations}");
    }

    if (options.JsonOutputPath is not null)
    {
        var json = JsonSerializer.Serialize(new BenchmarkRun(
            options.DocumentCount,
            options.WarmupIterations,
            options.MeasurementIterations,
            results.ToArray()), CreateJsonOptions());
        File.WriteAllText(options.JsonOutputPath, json);
    }

    ApplyThresholds(options, results);
}

static void RunPostingsStats(BenchmarkOptions options)
{
    var repoRoot = GetRepositoryRoot();
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "sift", "bench", "indexes");
    var corpora = DiscoverCachedIndexes(indexCacheRoot, options.CorpusIds);
    var results = new List<PostingsStatsCorpusResult>(corpora.Count);
    foreach (var corpus in corpora)
    {
        Console.WriteLine($"postings-stats: {corpus.CorpusId} root={corpus.CachePath}");
        results.Add(AnalyzeCachedIndex(corpus));
    }

    var run = new PostingsStatsRun(
        "postings-stats",
        DateTimeOffset.UtcNow,
        results.ToArray());

    if (options.JsonOutputPath is not null)
    {
        EnsureParentDirectory(options.JsonOutputPath);
        File.WriteAllText(options.JsonOutputPath, JsonSerializer.Serialize(run, CreateJsonOptions()));
    }
}

static void RunPostingsBenchmarkDotNet(BenchmarkOptions options)
{
    var config = ManualConfig
        .Create(DefaultConfig.Instance)
        .AddJob(Job.Default
            .WithWarmupCount(Math.Max(3, options.WarmupIterations))
            .WithIterationCount(Math.Max(8, options.MeasurementIterations)))
        .AddColumn(TargetMethodColumn.Method, StatisticColumn.Mean, StatisticColumn.Median, StatisticColumn.P95)
        .AddExporter(JsonExporter.BriefCompressed)
        .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest))
        .WithArtifactsPath(Path.Combine(GetRepositoryRoot(), "sift", "benchmarks", "out", "bdn"));

    var benchmark = new PostingsCodecBenchmarks();
    if (options.PostingsBdnFilter is { Length: > 0 })
    {
        config = config.WithOption(ConfigOptions.DisableOptimizationsValidator, true);
        foreach (var filter in options.PostingsBdnFilter)
        {
            config.AddFilter(new GlobFilter([filter]));
        }
    }

    BenchmarkRunner.Run<PostingsCodecBenchmarks>(config);
    BenchmarkRunner.Run<PostingsIntersectionBenchmarks>(config);
}

static void RunRealCorpusBenchmarks(BenchmarkOptions options)
{
    var repoRoot = GetRepositoryRoot();
    var corpusRoot = options.RealCorpusRoot ?? Path.Combine(repoRoot, "sift", "bench", "repos");
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "sift", "bench", "indexes");
    var queryPackPath = options.RealQueryPackJsonPath ?? Path.Combine(repoRoot, "src", "Lokad.Sift.Benchmarks", "Assets", "real-diverse.json");
    var queryPack = LoadRealQueryPack(queryPackPath);
    if (!string.IsNullOrWhiteSpace(options.QueryId))
    {
        queryPack = queryPack.Filter(options.QueryId);
    }

    var corpora = DiscoverCorpora(corpusRoot, options.CorpusIds);
    var machine = new MachineInfo(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        GetProcessorName(),
        Environment.ProcessorCount,
        RuntimeInformation.FrameworkDescription);

    var results = new List<RealCorpusBenchmarkResult>(corpora.Count);
    foreach (var corpus in corpora)
    {
        Console.WriteLine($"real-corpus: {corpus.Id} root={corpus.Path}");
        results.Add(RunRealCorpus(corpus, queryPack, options, indexCacheRoot));
    }

    var run = new RealCorpusBenchmarkRun(
        "real-corpus",
        DateTimeOffset.UtcNow,
        machine,
        queryPack.Id,
        options.MaxFiles,
        results.ToArray());

    if (options.JsonOutputPath is not null)
    {
        EnsureParentDirectory(options.JsonOutputPath);
        File.WriteAllText(options.JsonOutputPath, JsonSerializer.Serialize(run, CreateJsonOptions()));
    }
}

static RealCorpusBenchmarkResult RunRealCorpus(DiscoveredCorpus corpus, RealQueryPack pack, BenchmarkOptions options, string indexCacheRoot)
{
    if (options.UseIndexCache)
    {
        return RunRealCorpusWithCachedIndex(corpus, pack, options, indexCacheRoot);
    }

    var benchmarkRoot = Path.Combine(Path.GetTempPath(), "sift-real-bench", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(benchmarkRoot);
    try
    {
        var buildMetrics = BuildRealCorpusIndex(benchmarkRoot, corpus, options);

        var mappedBytesBefore = MappedSegment.CurrentMappedBytes;
        var openStart = Stopwatch.GetTimestamp();
        using var index = new Sift(benchmarkRoot, new SiftOptions());
        using var snapshot = index.OpenSnapshot(new SearchOptions(BenchmarkSearchParallelism));
        var openElapsed = Stopwatch.GetElapsedTime(openStart);
        var mappedBytesDelta = MappedSegment.CurrentMappedBytes - mappedBytesBefore;

        var queryResults = options.PrepareIndexOnly
            ? []
            : RunRealQueryPack(snapshot, pack, options);

        return new RealCorpusBenchmarkResult(
            corpus.Id,
            corpus.Path,
            buildMetrics,
            new RealSnapshotMetrics(openElapsed.TotalMilliseconds, mappedBytesDelta, buildMetrics.SegmentCount),
            queryResults.ToArray(),
            BuildPlannerSummary(queryResults));
    }
    finally
    {
        if (Directory.Exists(benchmarkRoot))
        {
            Directory.Delete(benchmarkRoot, recursive: true);
        }
    }
}

static RealCorpusBenchmarkResult RunRealCorpusWithCachedIndex(DiscoveredCorpus corpus, RealQueryPack pack, BenchmarkOptions options, string indexCacheRoot)
{
    var cachedIndex = EnsureCachedIndex(corpus, options, indexCacheRoot);
    var cachePath = cachedIndex.CachePath;
    var metadata = cachedIndex.Metadata;

    var mappedBytesBefore = MappedSegment.CurrentMappedBytes;
    var openStart = Stopwatch.GetTimestamp();
    using var index = new Sift(cachePath, new SiftOptions());
    using var snapshot = index.OpenSnapshot(new SearchOptions(BenchmarkSearchParallelism));
    var openElapsed = Stopwatch.GetElapsedTime(openStart);
    var mappedBytesDelta = MappedSegment.CurrentMappedBytes - mappedBytesBefore;

    var queryResults = options.PrepareIndexOnly
        ? []
        : RunRealQueryPack(snapshot, pack, options);

    return new RealCorpusBenchmarkResult(
        corpus.Id,
        corpus.Path,
        metadata.Build,
        new RealSnapshotMetrics(openElapsed.TotalMilliseconds, mappedBytesDelta, metadata.Build.SegmentCount),
        queryResults.ToArray(),
        BuildPlannerSummary(queryResults));
}

static RealBuildMetrics BuildRealCorpusIndex(string indexRoot, DiscoveredCorpus corpus, BenchmarkOptions options)
{
    var buildStart = Stopwatch.GetTimestamp();
    using var index = new Sift(indexRoot, new SiftOptions());
    var source = new FileSystemDocumentSource(corpus.Path, options.MaxFiles);
    var buildMeasurement = MeasureBuildMetrics(() => index.Build(source).GetAwaiter().GetResult());
    var buildStats = buildMeasurement.Stats;
    var buildElapsed = Stopwatch.GetElapsedTime(buildStart);
    var indexBytes = CountIndexBytes(indexRoot);
    var inflationRatio = buildStats.ContentBytes == 0 ? 0d : (double)indexBytes / buildStats.ContentBytes;
    var inflationPercent = buildStats.ContentBytes == 0 ? 0d : ((double)indexBytes - buildStats.ContentBytes) / buildStats.ContentBytes * 100d;
    var buildDocsPerSecond = buildElapsed.TotalSeconds <= 0 ? 0d : buildStats.IndexedDocuments / buildElapsed.TotalSeconds;
    var buildBytesPerSecond = buildElapsed.TotalSeconds <= 0 ? 0d : buildStats.ContentBytes / buildElapsed.TotalSeconds;

    return new RealBuildMetrics(
        buildStats.SubmittedDocuments,
        buildStats.IndexedDocuments,
        buildStats.RejectedDocuments,
        buildStats.SubmittedBytes,
        buildStats.ContentBytes,
        indexBytes,
        inflationRatio,
        inflationPercent,
        buildElapsed.TotalMilliseconds,
        buildDocsPerSecond,
        buildBytesPerSecond,
        buildMeasurement.PeakManagedBytes,
        options.MaxFiles,
        CountSegments(indexRoot));
}

static UpdateBenchmarkCorpusResult RunUpdateBenchmarksForCorpus(
    DiscoveredCorpus corpus,
    IReadOnlyList<UpdateScenarioDefinition> scenarios,
    BenchmarkOptions options,
    string indexCacheRoot)
{
    var cachedIndex = EnsureCachedIndex(corpus, options, indexCacheRoot);
    var corpusFiles = EnumerateIndexedDocuments(cachedIndex.CachePath);
    var scenarioResults = new UpdateScenarioResult[scenarios.Count];
    for (var i = 0; i < scenarios.Count; i++)
    {
        scenarioResults[i] = RunUpdateScenario(cachedIndex, corpusFiles, scenarios[i], options);
    }

    return new UpdateBenchmarkCorpusResult(
        corpus.Id,
        corpus.Path,
        cachedIndex.Metadata.Build,
        scenarioResults);
}

static CompactBenchmarkCorpusResult RunCompactBenchmarksForCorpus(
    DiscoveredCorpus corpus,
    IReadOnlyList<CompactScenarioDefinition> scenarios,
    BenchmarkOptions options,
    string indexCacheRoot)
{
    var cachedIndex = EnsureCachedIndex(corpus, options, indexCacheRoot);
    var corpusFiles = EnumerateIndexedDocuments(cachedIndex.CachePath);
    var scenarioResults = new CompactScenarioResult[scenarios.Count];
    for (var i = 0; i < scenarios.Count; i++)
    {
        scenarioResults[i] = RunCompactScenario(cachedIndex, corpusFiles, scenarios[i], options);
    }

    return new CompactBenchmarkCorpusResult(
        corpus.Id,
        corpus.Path,
        cachedIndex.Metadata.Build,
        scenarioResults);
}

static UpdateScenarioResult RunUpdateScenario(
    CachedIndexState cachedIndex,
    CorpusFileEntry[] corpusFiles,
    UpdateScenarioDefinition scenario,
    BenchmarkOptions options)
{
    return options.MaintenanceSteadyState
        ? RunUpdateScenarioSteadyState(cachedIndex, corpusFiles, scenario, options)
        : RunUpdateScenarioIsolated(cachedIndex, corpusFiles, scenario, options);
}

static UpdateScenarioResult RunUpdateScenarioIsolated(
    CachedIndexState cachedIndex,
    CorpusFileEntry[] corpusFiles,
    UpdateScenarioDefinition scenario,
    BenchmarkOptions options)
{
    var samples = GC.AllocateUninitializedArray<double>(options.MeasurementIterations);
    UpdateScenarioMetrics metrics = default;

    for (var iteration = -options.WarmupIterations; iteration < options.MeasurementIterations; iteration++)
    {
        var workRoot = CreateTempBenchmarkWorkspace("sift-update-bench");
        try
        {
            CopyDirectory(cachedIndex.CachePath, workRoot);
            var files = BuildScenarioFiles(
                corpusFiles,
                scenario.UpsertCount,
                scenario.DeleteCount,
                scenario.StartOffset + iteration + options.WarmupIterations);
            var storage = new CorpusStorage(workRoot);
            using var index = new Sift(workRoot, new SiftOptions());
            var beforeManifest = storage.ReadManifest();
            var beforeManifestBytes = new FileInfo(storage.ManifestPath).Length;
            var source = new MemoryDocumentSource(files.Upserts);

            var start = Stopwatch.GetTimestamp();
            var updateStats = index.Update(source, files.Deletions).GetAwaiter().GetResult();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var afterManifest = storage.ReadManifest();
            var afterManifestBytes = new FileInfo(storage.ManifestPath).Length;

            if (iteration < 0)
            {
                continue;
            }

            samples[iteration] = elapsed;
            if (iteration == samples.Length / 2)
            {
                metrics = BuildUpdateScenarioMetrics(updateStats, beforeManifest, afterManifest, beforeManifestBytes, afterManifestBytes);
            }
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    Array.Sort(samples);
    return new UpdateScenarioResult(
        scenario.Id,
        scenario.UpsertCount,
        scenario.DeleteCount,
        ToLatencyMetrics(samples),
        metrics);
}

static UpdateScenarioResult RunUpdateScenarioSteadyState(
    CachedIndexState cachedIndex,
    CorpusFileEntry[] corpusFiles,
    UpdateScenarioDefinition scenario,
    BenchmarkOptions options)
{
    var samples = GC.AllocateUninitializedArray<double>(options.MeasurementIterations);
    UpdateScenarioMetrics metrics = default;
    var workRoot = CreateTempBenchmarkWorkspace("sift-update-bench");
    try
    {
        CopyDirectory(cachedIndex.CachePath, workRoot);
        var storage = new CorpusStorage(workRoot);
        using var index = new Sift(workRoot, new SiftOptions());

        for (var iteration = -options.WarmupIterations; iteration < options.MeasurementIterations; iteration++)
        {
            var files = BuildScenarioFiles(
                corpusFiles,
                scenario.UpsertCount,
                scenario.DeleteCount,
                scenario.StartOffset + iteration + options.WarmupIterations);
            var beforeManifest = storage.ReadManifest();
            var beforeManifestBytes = new FileInfo(storage.ManifestPath).Length;
            var source = new MemoryDocumentSource(files.Upserts);

            var start = Stopwatch.GetTimestamp();
            var updateStats = index.Update(source, files.Deletions).GetAwaiter().GetResult();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var afterManifest = storage.ReadManifest();
            var afterManifestBytes = new FileInfo(storage.ManifestPath).Length;

            if (iteration < 0)
            {
                continue;
            }

            samples[iteration] = elapsed;
            if (iteration == samples.Length / 2)
            {
                metrics = BuildUpdateScenarioMetrics(updateStats, beforeManifest, afterManifest, beforeManifestBytes, afterManifestBytes);
            }
        }
    }
    finally
    {
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    Array.Sort(samples);
    return new UpdateScenarioResult(
        scenario.Id,
        scenario.UpsertCount,
        scenario.DeleteCount,
        ToLatencyMetrics(samples),
        metrics);
}

static CompactScenarioResult RunCompactScenario(
    CachedIndexState cachedIndex,
    CorpusFileEntry[] corpusFiles,
    CompactScenarioDefinition scenario,
    BenchmarkOptions options)
{
    return options.MaintenanceSteadyState
        ? RunCompactScenarioSteadyState(cachedIndex, corpusFiles, scenario, options)
        : RunCompactScenarioIsolated(cachedIndex, corpusFiles, scenario, options);
}

static CompactScenarioResult RunCompactScenarioIsolated(
    CachedIndexState cachedIndex,
    CorpusFileEntry[] corpusFiles,
    CompactScenarioDefinition scenario,
    BenchmarkOptions options)
{
    var samples = GC.AllocateUninitializedArray<double>(options.MeasurementIterations);
    CompactScenarioMetrics metrics = default;

    for (var iteration = -options.WarmupIterations; iteration < options.MeasurementIterations; iteration++)
    {
        var workRoot = CreateTempBenchmarkWorkspace("sift-compact-bench");
        try
        {
            CopyDirectory(cachedIndex.CachePath, workRoot);
            var storage = new CorpusStorage(workRoot);
            using var index = new Sift(workRoot, new SiftOptions());
            for (var round = 0; round < scenario.ChurnRounds; round++)
            {
                var files = BuildScenarioFiles(
                    corpusFiles,
                    scenario.UpsertCountPerRound,
                    scenario.DeleteCountPerRound,
                    scenario.StartOffset + (round * (scenario.UpsertCountPerRound + scenario.DeleteCountPerRound)));
                var source = new MemoryDocumentSource(files.Upserts);
                _ = index.Update(source, files.Deletions).GetAwaiter().GetResult();
            }

            var beforeManifest = storage.ReadManifest();
            var beforeManifestBytes = new FileInfo(storage.ManifestPath).Length;
            var beforeSegmentSizes = GetSegmentSizes(storage, beforeManifest);
            var start = Stopwatch.GetTimestamp();
            var compactMetrics = MeasureCompactionMetrics(() => index.Compact(new CompactOptions
            {
                MaxMergedSegments = 16,
                DeadFractionThreshold = 0.0,
                SmallSegmentMergeThreshold = 1,
                MetadataBloatDeadOrdinalsThreshold = 1
            }).GetAwaiter().GetResult());
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var afterManifest = storage.ReadManifest();
            var afterManifestBytes = new FileInfo(storage.ManifestPath).Length;
            var afterSegmentSizes = GetSegmentSizes(storage, afterManifest);

            if (iteration < 0)
            {
                continue;
            }

            samples[iteration] = elapsed;
            if (iteration == samples.Length / 2)
            {
                metrics = BuildCompactScenarioMetrics(
                    compactMetrics.Stats,
                    beforeManifest,
                    afterManifest,
                    beforeManifestBytes,
                    afterManifestBytes,
                    beforeSegmentSizes,
                    afterSegmentSizes,
                    compactMetrics.PeakManagedBytes);
            }
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    Array.Sort(samples);
    return new CompactScenarioResult(
        scenario.Id,
        scenario.ChurnRounds,
        scenario.UpsertCountPerRound,
        scenario.DeleteCountPerRound,
        ToLatencyMetrics(samples),
        metrics);
}

static CompactScenarioResult RunCompactScenarioSteadyState(
    CachedIndexState cachedIndex,
    CorpusFileEntry[] corpusFiles,
    CompactScenarioDefinition scenario,
    BenchmarkOptions options)
{
    var samples = GC.AllocateUninitializedArray<double>(options.MeasurementIterations);
    CompactScenarioMetrics metrics = default;
    var workRoot = CreateTempBenchmarkWorkspace("sift-compact-bench");
    try
    {
        CopyDirectory(cachedIndex.CachePath, workRoot);
        var storage = new CorpusStorage(workRoot);
        using var index = new Sift(workRoot, new SiftOptions());

        for (var iteration = -options.WarmupIterations; iteration < options.MeasurementIterations; iteration++)
        {
            for (var round = 0; round < scenario.ChurnRounds; round++)
            {
                var files = BuildScenarioFiles(
                    corpusFiles,
                    scenario.UpsertCountPerRound,
                    scenario.DeleteCountPerRound,
                    scenario.StartOffset + ((iteration + options.WarmupIterations) * scenario.ChurnRounds + round) * (scenario.UpsertCountPerRound + scenario.DeleteCountPerRound));
                var source = new MemoryDocumentSource(files.Upserts);
                _ = index.Update(source, files.Deletions).GetAwaiter().GetResult();
            }

            var beforeManifest = storage.ReadManifest();
            var beforeManifestBytes = new FileInfo(storage.ManifestPath).Length;
            var beforeSegmentSizes = GetSegmentSizes(storage, beforeManifest);
            var start = Stopwatch.GetTimestamp();
            var compactMetrics = MeasureCompactionMetrics(() => index.Compact(new CompactOptions
            {
                MaxMergedSegments = 16,
                DeadFractionThreshold = 0.0,
                SmallSegmentMergeThreshold = 1,
                MetadataBloatDeadOrdinalsThreshold = 1
            }).GetAwaiter().GetResult());
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var afterManifest = storage.ReadManifest();
            var afterManifestBytes = new FileInfo(storage.ManifestPath).Length;
            var afterSegmentSizes = GetSegmentSizes(storage, afterManifest);

            if (iteration < 0)
            {
                continue;
            }

            samples[iteration] = elapsed;
            if (iteration == samples.Length / 2)
            {
                metrics = BuildCompactScenarioMetrics(
                    compactMetrics.Stats,
                    beforeManifest,
                    afterManifest,
                    beforeManifestBytes,
                    afterManifestBytes,
                    beforeSegmentSizes,
                    afterSegmentSizes,
                    compactMetrics.PeakManagedBytes);
            }
        }
    }
    finally
    {
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    Array.Sort(samples);
    return new CompactScenarioResult(
        scenario.Id,
        scenario.ChurnRounds,
        scenario.UpsertCountPerRound,
        scenario.DeleteCountPerRound,
        ToLatencyMetrics(samples),
        metrics);
}

static UpdateScenarioMetrics BuildUpdateScenarioMetrics(
    UpdateStats stats,
    CorpusManifest beforeManifest,
    CorpusManifest afterManifest,
    long beforeManifestBytes,
    long afterManifestBytes)
{
    var bytesPerSecond = stats.ElapsedMilliseconds <= 0 ? 0d : stats.ContentBytes / (stats.ElapsedMilliseconds / 1000d);
    return new UpdateScenarioMetrics(
        stats.UpsertedDocuments,
        stats.DeletedDocuments,
        stats.PublishedSegments,
        stats.ContentBytes,
        bytesPerSecond,
        SumDeadOrdinals(beforeManifest),
        SumDeadOrdinals(afterManifest),
        afterManifest.Segments.Count - beforeManifest.Segments.Count,
        beforeManifestBytes,
        afterManifestBytes);
}

static CompactScenarioMetrics BuildCompactScenarioMetrics(
    CompactStats stats,
    CorpusManifest beforeManifest,
    CorpusManifest afterManifest,
    long beforeManifestBytes,
    long afterManifestBytes,
    Dictionary<ulong, long> beforeSegmentSizes,
    Dictionary<ulong, long> afterSegmentSizes,
    long peakManagedBytes)
{
    var bytesPerSecond = stats.ElapsedMilliseconds <= 0 ? 0d : stats.ContentBytes / (stats.ElapsedMilliseconds / 1000d);
    var liveDocsPerSecond = stats.ElapsedMilliseconds <= 0 ? 0d : stats.MergedDocuments / (stats.ElapsedMilliseconds / 1000d);
    var bytesReadFromOldSegments = SumRemovedSegmentBytes(beforeManifest, afterManifest, beforeSegmentSizes);
    var bytesWrittenToNewSegments = SumAddedSegmentBytes(beforeManifest, afterManifest, afterSegmentSizes);
    return new CompactScenarioMetrics(
        stats.InputSegments,
        stats.OutputSegments,
        stats.MergedDocuments,
        stats.ContentBytes,
        bytesPerSecond,
        liveDocsPerSecond,
        SumDeadOrdinals(beforeManifest),
        SumDeadOrdinals(afterManifest),
        beforeManifestBytes,
        afterManifestBytes,
        peakManagedBytes,
        bytesReadFromOldSegments,
        bytesWrittenToNewSegments);
}

static CompactionMeasurement MeasureCompactionMetrics(Func<CompactStats> run)
{
    var peakManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
    using var cts = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            if (managedBytes > peakManagedBytes)
            {
                peakManagedBytes = managedBytes;
            }

            try
            {
                await Task.Delay(5, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    });

    try
    {
        var stats = run();
        peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
        return new CompactionMeasurement(stats, peakManagedBytes);
    }
    finally
    {
        cts.Cancel();
        try
        {
            sampler.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is TaskCanceledException or OperationCanceledException))
        {
        }
    }
}

static BuildMeasurement MeasureBuildMetrics(Func<BuildStats> run)
{
    var peakManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
    using var cts = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            if (managedBytes > peakManagedBytes)
            {
                peakManagedBytes = managedBytes;
            }

            try
            {
                await Task.Delay(5, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    });

    try
    {
        var stats = run();
        peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
        return new BuildMeasurement(stats, peakManagedBytes);
    }
    finally
    {
        cts.Cancel();
        try
        {
            sampler.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is TaskCanceledException or OperationCanceledException))
        {
        }
    }
}

static Dictionary<ulong, long> GetSegmentSizes(CorpusStorage storage, CorpusManifest manifest)
{
    var sizes = new Dictionary<ulong, long>(manifest.Segments.Count);
    for (var i = 0; i < manifest.Segments.Count; i++)
    {
        var entry = manifest.Segments[i];
        var path = Path.Combine(storage.SegmentsPath, entry.SegmentId.ToString("X16"), "segment.pack");
        sizes[entry.SegmentId] = File.Exists(path) ? new FileInfo(path).Length : 0L;
    }

    return sizes;
}

static long SumRemovedSegmentBytes(CorpusManifest beforeManifest, CorpusManifest afterManifest, Dictionary<ulong, long> beforeSegmentSizes)
{
    var afterIds = new HashSet<ulong>(afterManifest.Segments.Count);
    for (var i = 0; i < afterManifest.Segments.Count; i++)
    {
        afterIds.Add(afterManifest.Segments[i].SegmentId);
    }

    long total = 0;
    for (var i = 0; i < beforeManifest.Segments.Count; i++)
    {
        var id = beforeManifest.Segments[i].SegmentId;
        if (!afterIds.Contains(id) && beforeSegmentSizes.TryGetValue(id, out var bytes))
        {
            total += bytes;
        }
    }

    return total;
}

static long SumAddedSegmentBytes(CorpusManifest beforeManifest, CorpusManifest afterManifest, Dictionary<ulong, long> afterSegmentSizes)
{
    var beforeIds = new HashSet<ulong>(beforeManifest.Segments.Count);
    for (var i = 0; i < beforeManifest.Segments.Count; i++)
    {
        beforeIds.Add(beforeManifest.Segments[i].SegmentId);
    }

    long total = 0;
    for (var i = 0; i < afterManifest.Segments.Count; i++)
    {
        var id = afterManifest.Segments[i].SegmentId;
        if (!beforeIds.Contains(id) && afterSegmentSizes.TryGetValue(id, out var bytes))
        {
            total += bytes;
        }
    }

    return total;
}

static QueryLatencyMetrics ToLatencyMetrics(double[] sortedSamples)
{
    var medianIndex = sortedSamples.Length / 2;
    var p95Index = Math.Min(sortedSamples.Length - 1, (int)Math.Ceiling(sortedSamples.Length * 0.95) - 1);
    return new QueryLatencyMetrics(
        sortedSamples[0],
        sortedSamples[medianIndex],
        sortedSamples[p95Index],
        sortedSamples.Length);
}

static long SumDeadOrdinals(CorpusManifest manifest)
{
    long total = 0;
    for (var i = 0; i < manifest.Segments.Count; i++)
    {
        total += manifest.Segments[i].DeadOrdinals.Length;
    }

    return total;
}

static ScenarioFiles BuildScenarioFiles(CorpusFileEntry[] files, int upsertCount, int deleteCount, int startOffset)
{
    if (files.Length == 0)
    {
        return new ScenarioFiles([], []);
    }

    var upserts = new SubmittedDocument[upsertCount];
    for (var i = 0; i < upsertCount; i++)
    {
        var file = files[(startOffset + i) % files.Length];
        upserts[i] = BuildUpdatedDocument(file, startOffset + i);
    }

    var deletions = new DocumentKey[deleteCount];
    for (var i = 0; i < deleteCount; i++)
    {
        var file = files[(startOffset + upsertCount + i) % files.Length];
        deletions[i] = new DocumentKey(file.RelativePath);
    }

    return new ScenarioFiles(upserts, deletions);
}

static SubmittedDocument BuildUpdatedDocument(CorpusFileEntry file, int seed)
{
    var suffix = Encoding.UTF8.GetBytes($"\n// sift-bench-update {seed}\n");
    var content = GC.AllocateUninitializedArray<byte>(file.Content.Length + suffix.Length);
    file.Content.CopyTo(content, 0);
    suffix.CopyTo(content, file.Content.Length);
    return new SubmittedDocument(file.RelativePath, content, file.LastModifiedUtc.AddSeconds(1));
}

static CorpusFileEntry[] EnumerateIndexedDocuments(string cachePath)
{
    var files = new List<CorpusFileEntry>();
    var storage = new CorpusStorage(cachePath);
    var manifest = storage.ReadManifest();
    for (var segmentIndex = 0; segmentIndex < manifest.Segments.Count; segmentIndex++)
    {
        var entry = manifest.Segments[segmentIndex];
        using var segment = storage.OpenSegment(entry.SegmentId);
        var dead = new HashSet<uint>(entry.DeadOrdinals);
        for (uint ordinal = 0; ordinal < segment.DocumentCount; ordinal++)
        {
            if (dead.Contains(ordinal))
            {
                continue;
            }

            var document = segment.GetDocument(ordinal);
            files.Add(new CorpusFileEntry(
                segment.GetPath(document),
                segment.GetContentBytes(document).ToArray(),
                new DateTimeOffset(document.MTimeUtcTicks, TimeSpan.Zero)));
        }
    }

    files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
    return [.. files];
}

static string CreateTempBenchmarkWorkspace(string prefix)
{
    var path = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void CopyDirectory(string sourcePath, string destinationPath)
{
    Directory.CreateDirectory(destinationPath);
    foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourcePath, directory);
        Directory.CreateDirectory(Path.Combine(destinationPath, relative));
    }

    foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourcePath, file);
        var destinationFile = Path.Combine(destinationPath, relative);
        EnsureParentDirectory(destinationFile);
        File.Copy(file, destinationFile, overwrite: true);
    }
}

static List<RealQueryResult> RunRealQueryPack(IIndexSnapshot snapshot, RealQueryPack pack, BenchmarkOptions options)
{
    var queryResults = new List<RealQueryResult>(pack.Queries.Length);
    foreach (var query in pack.Queries)
    {
        queryResults.Add(RunRealQuery(snapshot, query, options));
    }

    return queryResults;
}

static RealQueryResult RunRealQuery(IIndexSnapshot snapshot, RealQueryDefinition query, BenchmarkOptions options)
{
    var searchQuery = query.ToSearchQuery();
    var pathFilter = query.ToPathFilter();
    var explain = snapshot.Explain(searchQuery, pathFilter);

    for (var i = 0; i < options.WarmupIterations; i++)
    {
        if (!TryRunSearch(snapshot, searchQuery, pathFilter, options.QueryTimeoutMilliseconds, out _))
        {
            return CreateTimedOutQueryResult(query, explain, options.QueryTimeoutMilliseconds);
        }
    }

    var samples = GC.AllocateUninitializedArray<double>(options.MeasurementIterations);
    SearchStats? lastStats = null;
    for (var i = 0; i < samples.Length; i++)
    {
        var start = Stopwatch.GetTimestamp();
        if (!TryRunSearch(snapshot, searchQuery, pathFilter, options.QueryTimeoutMilliseconds, out var stats))
        {
            return CreateTimedOutQueryResult(query, explain, options.QueryTimeoutMilliseconds);
        }

        lastStats = stats;
        samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    var completedStats = lastStats ?? throw new InvalidOperationException("At least one measurement iteration is required.");
    Array.Sort(samples);
    var medianIndex = samples.Length / 2;
    var p95Index = Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1);
    return new RealQueryResult(
        query.Id,
        query.Intent,
        new QueryLatencyMetrics(samples[0], samples[medianIndex], samples[p95Index], samples.Length),
        GetPlanKey(explain),
        explain.CandidateKind.ToString(),
        explain.VerifierKind.ToString(),
        explain.EstimatedPathFilterCost,
        explain.EstimatedDirectScanCost,
        explain.EstimatedIndexedCost,
        explain.EstimatedCandidateDocuments,
        explain.EstimatedCost,
        explain.RunnerUp is null ? null : GetAlternativePlanKey(explain.PatternKind, explain.RunnerUp),
        explain.RunnerUp?.CandidateKind.ToString(),
        explain.RunnerUp?.VerifierKind.ToString(),
        explain.RunnerUpEstimatedCostDelta,
        explain.PlannerAlternatives.Select(alternative => new RealQueryPlannerAlternative(
            GetAlternativePlanKey(explain.PatternKind, alternative),
            alternative.CandidateKind.ToString(),
            alternative.VerifierKind.ToString(),
            alternative.UsesIndex,
            alternative.EstimatedDirectScanCost,
            alternative.EstimatedIndexedCost,
            alternative.EstimatedCandidateDocuments,
            alternative.EstimatedCost,
            alternative.SegmentCount)).ToArray(),
        completedStats.ScopeDocuments,
        completedStats.ScopeBytes,
        completedStats.PostingsBytesRead,
        completedStats.CandidateDocumentsAfterIndex,
        completedStats.CandidateDocumentsAfterPrecheck,
        completedStats.VerifiedDocuments,
        completedStats.BytesScanned,
        completedStats.DecodedChars,
        completedStats.RegexInvocations,
        completedStats.Utf8RegexInvocations,
        completedStats.FallbackRegexInvocations,
        completedStats.Utf8RegexMatchedDocuments,
        completedStats.Utf8RegexMatchesEnumerated,
        completedStats.Utf8RegexCacheHits,
        completedStats.Utf8RegexCacheMisses,
        completedStats.HitsReturned,
        completedStats.StopReason.ToString(),
        false);
}

static bool TryRunSearch(
    IIndexSnapshot snapshot,
    in SearchQuery query,
    in PathFilter pathFilter,
    int? timeoutMilliseconds,
    [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchStats? stats)
{
    using var cts = timeoutMilliseconds is int timeout && timeout > 0 ? new CancellationTokenSource(timeout) : null;
    try
    {
        stats = snapshot.Search(query, pathFilter, new NullCollector(), cts?.Token ?? default);
        return true;
    }
    catch (OperationCanceledException) when (cts is not null && cts.IsCancellationRequested)
    {
        stats = default;
        return false;
    }
}

static RealQueryResult CreateTimedOutQueryResult(RealQueryDefinition query, SearchPlan explain, int? timeoutMilliseconds)
{
    var timeout = timeoutMilliseconds ?? 0;
    return new RealQueryResult(
        query.Id,
        query.Intent,
        new QueryLatencyMetrics(timeout, timeout, timeout, 0),
        GetPlanKey(explain),
        explain.CandidateKind.ToString(),
        explain.VerifierKind.ToString(),
        explain.EstimatedPathFilterCost,
        explain.EstimatedDirectScanCost,
        explain.EstimatedIndexedCost,
        explain.EstimatedCandidateDocuments,
        explain.EstimatedCost,
        explain.RunnerUp is null ? null : GetAlternativePlanKey(explain.PatternKind, explain.RunnerUp),
        explain.RunnerUp?.CandidateKind.ToString(),
        explain.RunnerUp?.VerifierKind.ToString(),
        explain.RunnerUpEstimatedCostDelta,
        explain.PlannerAlternatives.Select(alternative => new RealQueryPlannerAlternative(
            GetAlternativePlanKey(explain.PatternKind, alternative),
            alternative.CandidateKind.ToString(),
            alternative.VerifierKind.ToString(),
            alternative.UsesIndex,
            alternative.EstimatedDirectScanCost,
            alternative.EstimatedIndexedCost,
            alternative.EstimatedCandidateDocuments,
            alternative.EstimatedCost,
            alternative.SegmentCount)).ToArray(),
        explain.ScopeDocumentCount,
        explain.ScopeByteCount,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        SearchStopReason.Cancelled.ToString(),
        true);
}

static string GetPlanKey(SearchPlan plan)
{
    return $"{plan.PatternKind}:{plan.CandidateKind}:{plan.CandidateCombination}:{plan.VerifierKind}";
}

static string GetAlternativePlanKey(SearchPatternKind patternKind, PlannerAlternative alternative)
{
    return $"{patternKind}:{alternative.CandidateKind}:{alternative.CandidateCombination}:{alternative.VerifierKind}";
}

static RealCorpusPlannerSummary BuildPlannerSummary(List<RealQueryResult> queries)
{
    var mispredictions = queries
        .Where(static query => query.EstimatedCost > 0d)
        .Select(static query => CreateMispredictionSummary(query))
        .ToArray();

    return new RealCorpusPlannerSummary(
        CountBy(queries, static query => query.PlanFamily),
        CountBy(queries, static query => query.CandidateKind),
        CountBy(queries, static query => query.VerifierKind),
        queries
            .OrderByDescending(static query => query.Latency.MedianMilliseconds)
            .Take(10)
            .Select(static query => new PlannerSlowQuerySummary(
                query.Id,
                query.Latency.MedianMilliseconds,
                query.PlanFamily,
                query.CandidateKind,
                query.VerifierKind,
                query.ScopeDocuments,
                query.VerifiedDocuments,
                query.BytesScanned))
            .ToArray(),
        queries
            .Select(static query => GetRunnerUpSummary(query))
            .Where(static summary => summary.HasValue)
            .Select(static summary => summary.GetValueOrDefault())
            .GroupBy(static summary => summary.PlanFamily, StringComparer.Ordinal)
            .Select(static group => new PlannerRunnerUpSummary(
                group.Key,
                group.Count(),
                group.Average(static value => value.EstimatedCostDelta)))
            .OrderByDescending(static entry => entry.Count)
            .ThenBy(static entry => entry.PlanFamily, StringComparer.Ordinal)
            .ToArray(),
        mispredictions
            .OrderByDescending(static summary => summary.Ratio)
            .Take(5)
            .ToArray(),
        mispredictions
            .OrderBy(static summary => summary.Ratio)
            .Take(5)
            .ToArray(),
        queries
            .GroupBy(static query => query.PlanFamily, StringComparer.Ordinal)
            .Select(static group =>
            {
                var latencies = group.Select(static query => query.Latency.MedianMilliseconds).OrderBy(static latency => latency).ToArray();
                return new PlannerFamilyLatencySummary(
                    group.Key,
                    group.Count(),
                    Percentile50(latencies),
                    Percentile95(latencies),
                    group.Sum(static query => query.BytesScanned),
                    group.Sum(static query => query.VerifiedDocuments));
            })
            .OrderByDescending(static entry => entry.MedianLatencyMilliseconds)
            .ThenBy(static entry => entry.PlanFamily, StringComparer.Ordinal)
            .ToArray());
}

static PlannerCountEntry[] CountBy(IEnumerable<RealQueryResult> queries, Func<RealQueryResult, string> selector)
{
    return queries
        .GroupBy(selector, StringComparer.Ordinal)
        .Select(static group => new PlannerCountEntry(group.Key, group.Count()))
        .OrderByDescending(static entry => entry.Count)
        .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
        .ToArray();
}

static PlannerRunnerUpCandidate? GetRunnerUpSummary(RealQueryResult query)
{
    if (string.IsNullOrEmpty(query.RunnerUpPlanFamily) || query.RunnerUpEstimatedCostDelta is not double delta)
    {
        return null;
    }

    return new PlannerRunnerUpCandidate(
        query.RunnerUpPlanFamily,
        Math.Max(0d, delta));
}

static PlannerMispredictionSummary CreateMispredictionSummary(RealQueryResult query)
{
    var estimated = Math.Max(query.EstimatedCost, 1d);
    return new PlannerMispredictionSummary(
        query.Id,
        query.Latency.MedianMilliseconds,
        query.EstimatedCost,
        query.Latency.MedianMilliseconds / estimated,
        query.PlanFamily,
        query.CandidateKind);
}

static double Percentile50(double[] values)
{
    if (values.Length == 0)
    {
        return 0d;
    }

    return values[values.Length / 2];
}

static double Percentile95(double[] values)
{
    if (values.Length == 0)
    {
        return 0d;
    }

    var index = Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * 0.95) - 1);
    return values[index];
}

static void ApplyThresholds(BenchmarkOptions options, List<BenchmarkResult> results)
{
    var failures = results
        .Where(result => options.MedianThresholds.TryGetValue(result.Name, out var threshold) && result.MedianMilliseconds > threshold)
        .Select(result => $"{result.Name} median {result.MedianMilliseconds:F2} ms exceeded threshold {options.MedianThresholds[result.Name]:F2} ms")
        .ToArray();
    if (failures.Length > 0)
    {
        foreach (var failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        Environment.ExitCode = 1;
    }
}

static BenchmarkResult Run(string name, BenchmarkOptions options, Action action)
{
    var innerRepeat = options.Mode == BenchmarkMode.PostingsMicro
        ? options.PostingsInnerRepeat
        : 1;
    for (var i = 0; i < options.WarmupIterations; i++)
    {
        RunRepeated(action, innerRepeat);
    }

    var samples = GC.AllocateUninitializedArray<double>(options.MeasurementIterations);
    for (var i = 0; i < samples.Length; i++)
    {
        var start = Stopwatch.GetTimestamp();
        RunRepeated(action, innerRepeat);
        var elapsed = Stopwatch.GetElapsedTime(start);
        samples[i] = elapsed.TotalMilliseconds / innerRepeat;
    }

    Array.Sort(samples);
    var medianIndex = samples.Length / 2;
    var p95Index = Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1);
    return new BenchmarkResult(
        name,
        samples[0],
        samples[medianIndex],
        samples[p95Index],
        samples.Length);
}

static CachedIndexState EnsureCachedIndex(DiscoveredCorpus corpus, BenchmarkOptions options, string indexCacheRoot)
{
    var cachePath = Path.Combine(indexCacheRoot, GetIndexCacheKey(corpus.Id, options.MaxFiles));
    Directory.CreateDirectory(indexCacheRoot);

    CachedIndexMetadata metadata = default;
    var needsBuild = options.RefreshIndex || !TryReadCachedIndexMetadata(cachePath, out metadata);
    if (options.QueryOnly && needsBuild)
    {
        throw new InvalidOperationException($"Cached benchmark index '{cachePath}' is missing. Run with index preparation first.");
    }

    if (needsBuild)
    {
        if (Directory.Exists(cachePath))
        {
            Directory.Delete(cachePath, recursive: true);
        }

        Directory.CreateDirectory(cachePath);
        var buildMetrics = BuildRealCorpusIndex(cachePath, corpus, options);
        metadata = new CachedIndexMetadata(
            corpus.Id,
            corpus.Path,
            options.MaxFiles,
            DateTimeOffset.UtcNow,
            buildMetrics);
        WriteCachedIndexMetadata(cachePath, metadata);
    }

    return new CachedIndexState(cachePath, metadata);
}

static void RunUpdateBenchmarks(BenchmarkOptions options)
{
    var repoRoot = Directory.GetCurrentDirectory();
    var corpusRoot = options.RealCorpusRoot ?? Path.Combine(repoRoot, "sift", "bench", "repos");
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "sift", "bench", "indexes");
    var corpora = DiscoverCorpora(corpusRoot, options.CorpusIds);
    var machine = new MachineInfo(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        GetProcessorName(),
        Environment.ProcessorCount,
        RuntimeInformation.FrameworkDescription);
    var scenarios =
        new[]
        {
            new UpdateScenarioDefinition("upsert-10", 10, 0, 0),
            new UpdateScenarioDefinition("upsert-100", 100, 0, 32),
            new UpdateScenarioDefinition("mixed-50-50", 50, 50, 128)
        };

    var results = new List<UpdateBenchmarkCorpusResult>(corpora.Count);
    foreach (var corpus in corpora)
    {
        Console.WriteLine($"update-bench: {corpus.Id} root={corpus.Path}");
        results.Add(RunUpdateBenchmarksForCorpus(corpus, scenarios, options, indexCacheRoot));
    }

    var run = new UpdateBenchmarkRun(
        "update-bench",
        DateTimeOffset.UtcNow,
        machine,
        options.MaxFiles,
        results.ToArray());

    if (options.JsonOutputPath is not null)
    {
        EnsureParentDirectory(options.JsonOutputPath);
        File.WriteAllText(options.JsonOutputPath, JsonSerializer.Serialize(run, CreateJsonOptions()));
    }
}

static void RunCompactBenchmarks(BenchmarkOptions options)
{
    var repoRoot = Directory.GetCurrentDirectory();
    var corpusRoot = options.RealCorpusRoot ?? Path.Combine(repoRoot, "sift", "bench", "repos");
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "sift", "bench", "indexes");
    var corpora = DiscoverCorpora(corpusRoot, options.CorpusIds);
    var machine = new MachineInfo(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        GetProcessorName(),
        Environment.ProcessorCount,
        RuntimeInformation.FrameworkDescription);
    var scenarios =
        new[]
        {
            new CompactScenarioDefinition("compact-after-mixed-1x100", 50, 50, 1, 0),
            new CompactScenarioDefinition("compact-after-mixed-5x100", 50, 50, 5, 256)
        };

    var results = new List<CompactBenchmarkCorpusResult>(corpora.Count);
    foreach (var corpus in corpora)
    {
        Console.WriteLine($"compact-bench: {corpus.Id} root={corpus.Path}");
        results.Add(RunCompactBenchmarksForCorpus(corpus, scenarios, options, indexCacheRoot));
    }

    var run = new CompactBenchmarkRun(
        "compact-bench",
        DateTimeOffset.UtcNow,
        machine,
        options.MaxFiles,
        results.ToArray());

    if (options.JsonOutputPath is not null)
    {
        EnsureParentDirectory(options.JsonOutputPath);
        File.WriteAllText(options.JsonOutputPath, JsonSerializer.Serialize(run, CreateJsonOptions()));
    }
}

static void RunRepeated(Action action, int repeatCount)
{
    for (var i = 0; i < repeatCount; i++)
    {
        action();
    }
}

static RealQueryPack LoadRealQueryPack(string path)
{
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<RealQueryPack>(json, CreateJsonOptions())
        ?? throw new InvalidOperationException($"Failed to deserialize real query pack '{path}'.");
}

static List<DiscoveredCorpus> DiscoverCorpora(string root, IReadOnlyList<string>? requestedIds)
{
    if (!Directory.Exists(root))
    {
        return [];
    }

    var requested = requestedIds is null || requestedIds.Count == 0
        ? null
        : new HashSet<string>(requestedIds, StringComparer.OrdinalIgnoreCase);
    return Directory.EnumerateDirectories(root)
        .Select(static path => new DiscoveredCorpus(Path.GetFileName(path), path))
        .Where(corpus => requested is null || requested.Contains(corpus.Id))
        .OrderBy(static corpus => corpus.Id, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<CachedCorpusIndex> DiscoverCachedIndexes(string root, IReadOnlyList<string>? requestedIds)
{
    if (!Directory.Exists(root))
    {
        return [];
    }

    var requested = requestedIds is null || requestedIds.Count == 0
        ? null
        : new HashSet<string>(requestedIds, StringComparer.OrdinalIgnoreCase);
    return Directory.EnumerateDirectories(root)
        .Select(path => (Path: path, Name: Path.GetFileName(path)))
        .Where(static item => item.Name.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
        .Select(static item => new CachedCorpusIndex(item.Name[..^5], item.Path))
        .Where(corpus => requested is null || requested.Contains(corpus.CorpusId))
        .OrderBy(static corpus => corpus.CorpusId, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static PostingsStatsCorpusResult AnalyzeCachedIndex(CachedCorpusIndex cached)
{
    var storage = new CorpusStorage(cached.CachePath);
    var manifest = storage.ReadManifest();
    var accumulator = new PostingsStatsAccumulator();
    foreach (var entry in manifest.Segments)
    {
        using var segment = storage.OpenSegment(entry.SegmentId);
        AnalyzeRegion(segment, new IndexRegionPair(SegmentRegionId.ContentTriCsLex, SegmentRegionId.ContentTriCsPost), "contentTriCs", accumulator);
        AnalyzeRegion(segment, new IndexRegionPair(SegmentRegionId.ContentTriIcLex, SegmentRegionId.ContentTriIcPost), "contentTriIc", accumulator);
        AnalyzeRegion(segment, new IndexRegionPair(SegmentRegionId.ContentBiCsLex, SegmentRegionId.ContentBiCsPost), "contentBiCs", accumulator);
        AnalyzeRegion(segment, new IndexRegionPair(SegmentRegionId.ContentBiIcLex, SegmentRegionId.ContentBiIcPost), "contentBiIc", accumulator);
        AnalyzeRegion(segment, new IndexRegionPair(SegmentRegionId.PathTriCsLex, SegmentRegionId.PathTriCsPost), "pathTriCs", accumulator);
        AnalyzeRegion(segment, new IndexRegionPair(SegmentRegionId.PathTriIcLex, SegmentRegionId.PathTriIcPost), "pathTriIc", accumulator);
    }

    return accumulator.ToResult(cached.CorpusId, cached.CachePath, manifest.Segments.Count);
}

static void AnalyzeRegion(MappedSegment segment, IndexRegionPair pair, string regionName, PostingsStatsAccumulator accumulator)
{
    segment.VisitPostings(pair, (payload, docFreq) =>
    {
        var ordinals = PostingsCodec.Decode(payload);
        var bp128Bytes = Bp128PostingsCodec.Encode(ordinals).Length;
        var streamVByteBytes = StreamVBytePostingsCodec.Encode(ordinals).Length;
        accumulator.Add(regionName, docFreq, payload.Length, bp128Bytes, streamVByteBytes);
    });
}

static int CountSegments(string corpusPath)
{
    var segmentsPath = Path.Combine(corpusPath, "segments");
    return Directory.Exists(segmentsPath)
        ? Directory.EnumerateFiles(segmentsPath, "segment.pack", SearchOption.AllDirectories).Count()
        : 0;
}

static long CountIndexBytes(string corpusPath)
{
    var segmentsPath = Path.Combine(corpusPath, "segments");
    return Directory.Exists(segmentsPath)
        ? Directory.EnumerateFiles(segmentsPath, "segment.pack", SearchOption.AllDirectories).Sum(static path => new FileInfo(path).Length)
        : 0L;
}

static string GetIndexCacheKey(string corpusId, int? maxFiles)
{
    return maxFiles is int capped
        ? $"{corpusId}-{capped}"
        : $"{corpusId}-full";
}

static bool TryReadCachedIndexMetadata(string cachePath, out CachedIndexMetadata metadata)
{
    var metadataPath = Path.Combine(cachePath, "bench-index.json");
    if (!File.Exists(metadataPath))
    {
        metadata = default;
        return false;
    }

    var metadataValue = JsonSerializer.Deserialize<CachedIndexMetadata>(File.ReadAllText(metadataPath), CreateJsonOptions());
    if (metadataValue.Equals(default(CachedIndexMetadata)))
    {
        throw new InvalidOperationException($"Failed to deserialize cached benchmark metadata '{metadataPath}'.");
    }

    metadata = metadataValue;

    var manifestPath = Path.Combine(cachePath, "manifest.bin");
    return File.Exists(manifestPath);
}

static void WriteCachedIndexMetadata(string cachePath, CachedIndexMetadata metadata)
{
    Directory.CreateDirectory(cachePath);
    File.WriteAllText(
        Path.Combine(cachePath, "bench-index.json"),
        JsonSerializer.Serialize(metadata, CreateJsonOptions()));
}

static uint[] BuildSparseOrdinals(int size, int stride)
{
    var values = new uint[(size + stride - 1) / stride];
    var index = 0;
    for (var i = 0; i < size; i += stride)
    {
        values[index++] = (uint)i;
    }

    return values;
}

static uint[] BuildArithmeticOrdinals(int count, int stride)
{
    var values = new uint[count];
    for (var i = 0; i < count; i++)
    {
        values[i] = (uint)(i * stride);
    }

    return values;
}

static uint[] BuildOrdinalsForEncodedSizeTarget(int targetBytes)
{
    var count = 512;
    while (true)
    {
        var ordinals = BuildArithmeticOrdinals(count, 3);
        var payload = PostingsCodec.Encode(ordinals);
        if (payload.Length >= targetBytes)
        {
            return ordinals;
        }

        count += 128;
    }
}

static uint[] Intersect(uint[] left, uint[] right)
{
    var result = new uint[Math.Min(left.Length, right.Length)];
    var count = 0;
    var i = 0;
    var j = 0;
    while (i < left.Length && j < right.Length)
    {
        if (left[i] == right[j])
        {
            result[count++] = left[i];
            i++;
            j++;
        }
        else if (left[i] < right[j])
        {
            i++;
        }
        else
        {
            j++;
        }
    }

    Array.Resize(ref result, count);
    return result;
}

static uint[] IntersectBlockMerge(uint[] left, uint[] right)
{
    return BlockMergeIntersection.IntersectSorted(left, right);
}

static uint[] IntersectSimdBlockMerge(uint[] left, uint[] right)
{
    return BlockMergeIntersection.IntersectSortedSimd(left, right);
}

static uint[] IntersectAdaptive(uint[] left, uint[] right, byte[] checkpointedPayload)
{
    return AdaptiveIntersection.IntersectSorted(left, right, checkpointedPayload, right.Length);
}

static uint[] IntersectAdaptiveSimd(uint[] left, uint[] right, byte[] checkpointedPayload)
{
    return AdaptiveIntersection.IntersectSortedSimd(left, right, checkpointedPayload, right.Length);
}

static string GetRepositoryRoot()
{
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
}

static string GetProcessorName()
{
    return RuntimeInformation.ProcessArchitecture.ToString();
}

static JsonSerializerOptions CreateJsonOptions()
{
    return new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

static void EnsureParentDirectory(string path)
{
    var parentDirectory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(parentDirectory))
    {
        Directory.CreateDirectory(parentDirectory);
    }
}

internal enum BenchmarkMode
{
    Synthetic,
    RealCorpus,
    UpdateBench,
    CompactBench,
    PostingsMicro,
    PostingsStats,
    PostingsBdn
}

internal readonly record struct BenchmarkResult(
    string Name,
    double MinMilliseconds,
    double MedianMilliseconds,
    double P95Milliseconds,
    int Iterations);

internal readonly record struct BenchmarkRun(
    int DocumentCount,
    int WarmupIterations,
    int MeasurementIterations,
    BenchmarkResult[] Results);

internal readonly record struct PostingsStatsRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    PostingsStatsCorpusResult[] Corpora);

internal readonly record struct MachineInfo(
    string Os,
    string Architecture,
    string Cpu,
    int LogicalCores,
    string Runtime);

internal readonly record struct RealCorpusBenchmarkRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    MachineInfo Machine,
    string QueryPackId,
    int? MaxFiles,
    RealCorpusBenchmarkResult[] Corpora);

internal readonly record struct UpdateBenchmarkRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    MachineInfo Machine,
    int? MaxFiles,
    UpdateBenchmarkCorpusResult[] Corpora);

internal readonly record struct CompactBenchmarkRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    MachineInfo Machine,
    int? MaxFiles,
    CompactBenchmarkCorpusResult[] Corpora);

internal readonly record struct RealCorpusBenchmarkResult(
    string Id,
    string Path,
    RealBuildMetrics Build,
    RealSnapshotMetrics Snapshot,
    RealQueryResult[] Queries,
    RealCorpusPlannerSummary PlannerSummary);

internal readonly record struct UpdateBenchmarkCorpusResult(
    string Id,
    string Path,
    RealBuildMetrics Build,
    UpdateScenarioResult[] Scenarios);

internal readonly record struct CompactBenchmarkCorpusResult(
    string Id,
    string Path,
    RealBuildMetrics Build,
    CompactScenarioResult[] Scenarios);

internal readonly record struct UpdateScenarioResult(
    string Id,
    int UpsertCount,
    int DeleteCount,
    QueryLatencyMetrics Latency,
    UpdateScenarioMetrics Metrics);

internal readonly record struct CompactScenarioResult(
    string Id,
    int ChurnRounds,
    int UpsertCountPerRound,
    int DeleteCountPerRound,
    QueryLatencyMetrics Latency,
    CompactScenarioMetrics Metrics);

internal readonly record struct UpdateScenarioMetrics(
    int UpsertedDocuments,
    int DeletedDocuments,
    int PublishedSegments,
    long ContentBytes,
    double ContentBytesPerSecond,
    long DeadOrdinalsBefore,
    long DeadOrdinalsAfter,
    int SegmentCountDelta,
    long ManifestBytesBefore,
    long ManifestBytesAfter);

internal readonly record struct CompactScenarioMetrics(
    int InputSegments,
    int OutputSegments,
    int MergedDocuments,
    long ContentBytes,
    double ContentBytesPerSecond,
    double LiveDocumentsPerSecond,
    long DeadOrdinalsBefore,
    long DeadOrdinalsAfter,
    long ManifestBytesBefore,
    long ManifestBytesAfter,
    long PeakManagedBytes,
    long BytesReadFromOldSegments,
    long BytesWrittenToNewSegments);

internal readonly record struct CompactionMeasurement(
    CompactStats Stats,
    long PeakManagedBytes);

internal readonly record struct BuildMeasurement(
    BuildStats Stats,
    long PeakManagedBytes);

internal readonly record struct CachedIndexMetadata(
    string CorpusId,
    string CorpusPath,
    int? MaxFiles,
    DateTimeOffset BuiltAtUtc,
    RealBuildMetrics Build);

internal readonly record struct RealBuildMetrics(
    int SubmittedDocuments,
    int IndexedDocuments,
    int RejectedDocuments,
    long SubmittedBytes,
    long ContentBytesOriginal,
    long IndexBytesOnDisk,
    double IndexInflationRatio,
    double IndexInflationPercent,
    double BuildElapsedMilliseconds,
    double BuildDocsPerSecond,
    double BuildBytesPerSecond,
    long PeakManagedBytes,
    int? MaxFiles,
    int SegmentCount);

internal readonly record struct RealSnapshotMetrics(
    double OpenElapsedMilliseconds,
    long MappedBytesDelta,
    int SegmentCount);

internal readonly record struct PostingsStatsCorpusResult(
    string Id,
    string Path,
    int SegmentCount,
    long TotalPostingLists,
    long TotalEncodedBytes,
    long TotalBp128Bytes,
    long TotalStreamVByteBytes,
    long CheckpointedPostingLists,
    long CheckpointedEncodedBytes,
    double TotalBp128InflationPercent,
    double TotalStreamVByteInflationPercent,
    PostingsStatsBucket[] EncodedByteBuckets,
    PostingsStatsBucket[] DocumentFrequencyBuckets,
    PostingsStatsThresholdSummary[] ThresholdSummaries,
    PostingsStatsRegionSummary[] Regions);

internal readonly record struct PostingsStatsBucket(
    string Key,
    long PostingLists,
    long EncodedBytes,
    double PostingListPercent,
    double EncodedBytesPercent);

internal readonly record struct PostingsStatsThresholdSummary(
    int ThresholdBytes,
    long PostingLists,
    long EncodedBytes,
    long Bp128Bytes,
    long StreamVByteBytes,
    double PostingListPercent,
    double EncodedBytesPercent,
    double Bp128InflationPercent,
    double StreamVByteInflationPercent);

internal readonly record struct PostingsStatsRegionSummary(
    string Region,
    long PostingLists,
    long EncodedBytes,
    long Bp128Bytes,
    long StreamVByteBytes,
    long CheckpointedPostingLists,
    long CheckpointedEncodedBytes,
    double Bp128InflationPercent,
    double StreamVByteInflationPercent);

internal readonly record struct RealQueryResult(
    string Id,
    string Intent,
    QueryLatencyMetrics Latency,
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    double EstimatedPathFilterCost,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    double EstimatedCost,
    string? RunnerUpPlanFamily,
    string? RunnerUpCandidateKind,
    string? RunnerUpVerifierKind,
    double? RunnerUpEstimatedCostDelta,
    RealQueryPlannerAlternative[] PlannerAlternatives,
    int ScopeDocuments,
    long ScopeBytes,
    long PostingsBytesRead,
    int CandidateDocumentsAfterIndex,
    int CandidateDocumentsAfterPrecheck,
    int VerifiedDocuments,
    long BytesScanned,
    long DecodedChars,
    int RegexInvocations,
    int Utf8RegexInvocations,
    int FallbackRegexInvocations,
    int Utf8RegexMatchedDocuments,
    long Utf8RegexMatchesEnumerated,
    long Utf8RegexCacheHits,
    long Utf8RegexCacheMisses,
    int HitsReturned,
    string StopReason,
    bool TimedOut);

internal readonly record struct RealQueryPlannerAlternative(
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    bool UsesIndex,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    double EstimatedCost,
    int SegmentCount);

internal readonly record struct QueryLatencyMetrics(
    double MinMilliseconds,
    double MedianMilliseconds,
    double P95Milliseconds,
    int Iterations);

internal readonly record struct CachedIndexState(
    string CachePath,
    CachedIndexMetadata Metadata);

internal readonly record struct UpdateScenarioDefinition(
    string Id,
    int UpsertCount,
    int DeleteCount,
    int StartOffset);

internal readonly record struct CompactScenarioDefinition(
    string Id,
    int UpsertCountPerRound,
    int DeleteCountPerRound,
    int ChurnRounds,
    int StartOffset);

internal readonly record struct CorpusFileEntry(
    string RelativePath,
    byte[] Content,
    DateTimeOffset LastModifiedUtc);

internal readonly record struct ScenarioFiles(
    SubmittedDocument[] Upserts,
    DocumentKey[] Deletions);

internal readonly record struct RealCorpusPlannerSummary(
    PlannerCountEntry[] PlanFamilies,
    PlannerCountEntry[] CandidateKinds,
    PlannerCountEntry[] VerifierKinds,
    PlannerSlowQuerySummary[] SlowQueries,
    PlannerRunnerUpSummary[] RunnerUpLosses,
    PlannerMispredictionSummary[] WorstUnderestimates,
    PlannerMispredictionSummary[] WorstOverestimates,
    PlannerFamilyLatencySummary[] FamilyLatency);

internal readonly record struct PlannerCountEntry(
    string Key,
    int Count);

internal readonly record struct PlannerSlowQuerySummary(
    string Id,
    double MedianMilliseconds,
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    int ScopeDocuments,
    int VerifiedDocuments,
    long BytesScanned);

internal readonly record struct PlannerRunnerUpSummary(
    string PlanFamily,
    int Count,
    double AverageEstimatedCostDelta);

internal readonly record struct PlannerMispredictionSummary(
    string Id,
    double MedianMilliseconds,
    double EstimatedCost,
    double Ratio,
    string PlanFamily,
    string CandidateKind);

internal readonly record struct PlannerFamilyLatencySummary(
    string PlanFamily,
    int QueryCount,
    double MedianLatencyMilliseconds,
    double P95LatencyMilliseconds,
    long TotalBytesScanned,
    int TotalVerifiedDocuments);

internal readonly record struct PlannerRunnerUpCandidate(
    string PlanFamily,
    double EstimatedCostDelta);

internal sealed class BenchmarkOptions
{
    public BenchmarkMode Mode { get; init; } = BenchmarkMode.Synthetic;
    public int DocumentCount { get; init; } = 50_000;
    public int WarmupIterations { get; init; } = 2;
    public int MeasurementIterations { get; init; } = 7;
    public string? JsonOutputPath { get; init; }
    public Dictionary<string, double> MedianThresholds { get; init; } = new(StringComparer.Ordinal);
    public string? ProfileJsonPath { get; init; }
    public string? RealCorpusRoot { get; init; }
    public string? IndexCacheRoot { get; init; }
    public string? RealQueryPackJsonPath { get; init; }
    public string? QueryId { get; init; }
    public string[]? CorpusIds { get; init; }
    public int? MaxFiles { get; init; }
    public bool UseIndexCache { get; init; }
    public bool QueryOnly { get; init; }
    public bool RefreshIndex { get; init; }
    public bool PrepareIndexOnly { get; init; }
    public bool MaintenanceSteadyState { get; init; }
    public int? QueryTimeoutMilliseconds { get; init; }
    public string[]? PostingsBdnFilter { get; init; }
    public int PostingsInnerRepeat { get; init; } = 1;

    public static BenchmarkOptions Parse(string[] args)
    {
        string? profileJsonPath = null;
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        var mode = BenchmarkMode.Synthetic;
        string? realCorpusRoot = null;
        string? indexCacheRoot = null;
        string? realQueryPackJsonPath = null;
        string? queryId = null;
        string[]? corpusIds = null;
        int? maxFiles = null;
        var useIndexCache = false;
        var queryOnly = false;
        var refreshIndex = false;
        var prepareIndexOnly = false;
        var maintenanceSteadyState = false;
        int? queryTimeoutMilliseconds = null;
        string[]? postingsBdnFilter = null;
        var postingsInnerRepeat = 1;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--profile-json=", StringComparison.Ordinal))
            {
                profileJsonPath = arg["--profile-json=".Length..];
                continue;
            }

            if (arg.StartsWith("--json=", StringComparison.Ordinal))
            {
                overrides[arg] = string.Empty;
                continue;
            }

            if (arg.StartsWith("--assert-median-ms=", StringComparison.Ordinal))
            {
                overrides[arg] = string.Empty;
                continue;
            }

            if (arg.StartsWith("--documents=", StringComparison.Ordinal) ||
                arg.StartsWith("--warmup=", StringComparison.Ordinal) ||
                arg.StartsWith("--iterations=", StringComparison.Ordinal))
            {
                overrides[arg] = string.Empty;
                continue;
            }

            if (arg.Equals("--real-corpus", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.RealCorpus;
                continue;
            }

            if (arg.Equals("--update-bench", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.UpdateBench;
                continue;
            }

            if (arg.Equals("--compact-bench", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.CompactBench;
                continue;
            }

            if (arg.Equals("--postings-micro", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsMicro;
                continue;
            }

            if (arg.Equals("--postings-stats", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsStats;
                continue;
            }

            if (arg.Equals("--postings-bdn", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsBdn;
                continue;
            }

            if (arg.StartsWith("--bdn-filter=", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsBdn;
                postingsBdnFilter =
                    [.. arg["--bdn-filter=".Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                continue;
            }

            if (arg.StartsWith("--real-corpus-root=", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                realCorpusRoot = arg["--real-corpus-root=".Length..];
                continue;
            }

            if (arg.StartsWith("--real-query-pack-json=", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.RealCorpus;
                realQueryPackJsonPath = arg["--real-query-pack-json=".Length..];
                continue;
            }

            if (arg.StartsWith("--query-id=", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.RealCorpus;
                queryId = arg["--query-id=".Length..];
                continue;
            }

            if (arg.StartsWith("--index-cache-root=", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                indexCacheRoot = arg["--index-cache-root=".Length..];
                continue;
            }

            if (arg.Equals("--use-index-cache", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                continue;
            }

            if (arg.Equals("--query-only", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                queryOnly = true;
                continue;
            }

            if (arg.Equals("--refresh-index", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                refreshIndex = true;
                continue;
            }

            if (arg.Equals("--prepare-index-only", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                prepareIndexOnly = true;
                continue;
            }

            if (arg.Equals("--maintenance-steady-state", StringComparison.Ordinal))
            {
                maintenanceSteadyState = true;
                continue;
            }

            if (arg.StartsWith("--corpora=", StringComparison.Ordinal))
            {
                corpusIds = arg["--corpora=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                continue;
            }

            if (TryReadInt32(arg, "--query-timeout-ms=", out var parsedQueryTimeoutMilliseconds))
            {
                if (parsedQueryTimeoutMilliseconds <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "Query timeout must be positive.");
                }

                queryTimeoutMilliseconds = parsedQueryTimeoutMilliseconds;
                continue;
            }

            if (TryReadInt32(arg, "--max-files=", out var parsedMaxFiles))
            {
                if (parsedMaxFiles <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "Max files must be positive.");
                }

                maxFiles = parsedMaxFiles;
                continue;
            }

            if (TryReadInt32(arg, "--postings-inner-repeat=", out var parsedPostingsInnerRepeat))
            {
                if (parsedPostingsInnerRepeat <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "Postings inner repeat must be positive.");
                }

                postingsInnerRepeat = parsedPostingsInnerRepeat;
                continue;
            }

            throw new ArgumentException($"Unknown benchmark argument '{arg}'.");
        }

        var profile = string.IsNullOrWhiteSpace(profileJsonPath)
            ? new BenchmarkProfile()
            : LoadProfile(profileJsonPath);
        var documentCount = profile.DocumentCount ?? 50_000;
        var warmupIterations = profile.WarmupIterations ?? 2;
        var measurementIterations = profile.MeasurementIterations ?? 7;
        var jsonOutputPath = profile.JsonOutputPath;
        var thresholds = profile.MedianThresholds is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(profile.MedianThresholds, StringComparer.Ordinal);

        foreach (var key in overrides.Keys)
        {
            if (TryReadInt32(key, "--documents=", out var parsedDocumentCount))
            {
                documentCount = parsedDocumentCount;
                continue;
            }

            if (TryReadInt32(key, "--warmup=", out var parsedWarmup))
            {
                warmupIterations = parsedWarmup;
                continue;
            }

            if (TryReadInt32(key, "--iterations=", out var parsedIterations))
            {
                measurementIterations = parsedIterations;
                continue;
            }

            if (key.StartsWith("--json=", StringComparison.Ordinal))
            {
                jsonOutputPath = key["--json=".Length..];
                continue;
            }

            if (key.StartsWith("--assert-median-ms=", StringComparison.Ordinal))
            {
                var payload = key["--assert-median-ms=".Length..];
                var separator = payload.IndexOf(':');
                if (separator <= 0 ||
                    separator == payload.Length - 1 ||
                    !double.TryParse(payload[(separator + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var threshold) ||
                    threshold < 0)
                {
                    throw new ArgumentException($"Invalid benchmark threshold argument '{key}'.");
                }

                thresholds[payload[..separator]] = threshold;
            }
        }

        if (documentCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Document count must be positive.");
        }

        if (warmupIterations < 0 || measurementIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Warmup must be non-negative and iterations must be positive.");
        }

        return new BenchmarkOptions
        {
            Mode = mode,
            DocumentCount = documentCount,
            WarmupIterations = warmupIterations,
            MeasurementIterations = measurementIterations,
            JsonOutputPath = string.IsNullOrWhiteSpace(jsonOutputPath) ? null : jsonOutputPath,
            MedianThresholds = thresholds,
            ProfileJsonPath = string.IsNullOrWhiteSpace(profileJsonPath) ? null : profileJsonPath,
            RealCorpusRoot = realCorpusRoot,
            IndexCacheRoot = indexCacheRoot,
            RealQueryPackJsonPath = realQueryPackJsonPath,
            QueryId = string.IsNullOrWhiteSpace(queryId) ? null : queryId,
            CorpusIds = corpusIds,
            MaxFiles = maxFiles,
            UseIndexCache = useIndexCache,
            QueryOnly = queryOnly,
            RefreshIndex = refreshIndex,
            PrepareIndexOnly = prepareIndexOnly,
            MaintenanceSteadyState = maintenanceSteadyState
            ,
            QueryTimeoutMilliseconds = queryTimeoutMilliseconds
            ,
            PostingsBdnFilter = postingsBdnFilter
            ,
            PostingsInnerRepeat = postingsInnerRepeat
        };
    }

    private static BenchmarkProfile LoadProfile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BenchmarkProfile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new BenchmarkProfile();
    }

    private static bool TryReadInt32(string arg, string prefix, out int value)
    {
        if (!arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = default;
            return false;
        }

        if (!int.TryParse(arg[prefix.Length..], out value))
        {
            throw new ArgumentException($"Invalid integer argument '{arg}'.");
        }

        return true;
    }

}

internal sealed class BenchmarkProfile
{
    public int? DocumentCount { get; init; }
    public int? WarmupIterations { get; init; }
    public int? MeasurementIterations { get; init; }
    public string? JsonOutputPath { get; init; }
    public Dictionary<string, double>? MedianThresholds { get; init; }
}

internal sealed class BenchmarkSource : IDocumentSource
{
    private readonly int _count;
    private int _index;

    public BenchmarkSource(int count)
    {
        _count = count;
    }

    public bool TryReadNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SubmittedDocument? document)
    {
        if (_index >= _count)
        {
            document = default;
            return false;
        }

        var i = _index++;
        var text = $"alpha beta gamma needle {i}\nalpha beta gamma delta\nALPHA beta gamma NEEDLE {i}\n";
        document = new SubmittedDocument(
            $"src/{i:D6}.txt",
            Encoding.UTF8.GetBytes(text),
            lastModifiedUtc: null,
            contentLifetime: DocumentContentLifetime.UntilOperationCompletes);
        return true;
    }
}

internal sealed class FileSystemDocumentSource : IDocumentSource
{
    internal static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".svn",
        "node_modules",
        "bin",
        "obj"
    };

    private readonly string _rootPath;
    private readonly Queue<string> _pendingDirectories = new();
    private readonly Queue<string> _pendingFiles = new();
    private readonly int? _maxFiles;
    private int _emittedFiles;

    public FileSystemDocumentSource(string rootPath, int? maxFiles)
    {
        _rootPath = rootPath;
        _maxFiles = maxFiles;
        _pendingDirectories.Enqueue(rootPath);
    }

    public bool TryReadNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SubmittedDocument? document)
    {
        if (_maxFiles is int maxFiles && _emittedFiles >= maxFiles)
        {
            document = default;
            return false;
        }

        while (true)
        {
            if (_pendingFiles.Count > 0)
            {
                var path = _pendingFiles.Dequeue();
                var relative = Path.GetRelativePath(_rootPath, path).Replace('\\', '/');
                _emittedFiles++;
                document = new SubmittedDocument(
                    relative,
                    File.ReadAllBytes(path),
                    new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                    DocumentContentLifetime.UntilOperationCompletes);
                return true;
            }

            if (_pendingDirectories.Count == 0)
            {
                document = default;
                return false;
            }

            var directory = _pendingDirectories.Dequeue();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(childDirectory)))
                {
                    _pendingDirectories.Enqueue(childDirectory);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                _pendingFiles.Enqueue(file);
            }
        }
    }
}

internal sealed class MemoryDocumentSource : IDocumentSource
{
    private readonly SubmittedDocument[] _documents;
    private int _index;

    public MemoryDocumentSource(SubmittedDocument[] documents)
    {
        _documents = documents;
    }

    public bool TryReadNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SubmittedDocument? document)
    {
        if (_index >= _documents.Length)
        {
            document = default;
            return false;
        }

        document = _documents[_index++] with
        {
            ContentLifetime = DocumentContentLifetime.UntilOperationCompletes
        };
        return true;
    }
}

internal sealed class NullCollector : IHitCollector
{
    public bool OnHit(in RawHit hit) => true;
}

internal sealed class FirstHitCollector : IHitCollector
{
    public bool HasHit { get; private set; }
    public RawHit Hit { get; private set; }

    public bool OnHit(in RawHit hit)
    {
        if (!HasHit)
        {
            Hit = hit;
            HasHit = true;
        }

        return true;
    }
}

internal sealed class RealQueryPack
{
    public string Id { get; init; } = "real-default";
    public RealQueryDefinition[] Queries { get; init; } = [];

    public RealQueryPack Filter(string queryId)
    {
        var filtered = Queries.Where(query => string.Equals(query.Id, queryId, StringComparison.Ordinal)).ToArray();
        if (filtered.Length == 0)
        {
            throw new ArgumentException($"Unknown real query id '{queryId}'.");
        }

        return new RealQueryPack
        {
            Id = $"{Id}:{queryId}",
            Queries = filtered
        };
    }
}

internal sealed class RealQueryDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public string PatternMode { get; init; } = "Literal";
    public string CaseMode { get; init; } = "Smart";
    public bool WholeWord { get; init; }
    public bool WholeLine { get; init; }
    public bool Multiline { get; init; }
    public bool DotAll { get; init; }
    public string? PathPrefix { get; init; }
    public string[]? IncludeGlobs { get; init; }
    public string[]? ExcludeGlobs { get; init; }
    public string? PathRegex { get; init; }
    public string Intent { get; init; } = string.Empty;

    public SearchQuery ToSearchQuery()
    {
        return new SearchQuery(
            Pattern,
            Enum.Parse<PatternMode>(PatternMode, ignoreCase: true),
            Enum.Parse<CaseMode>(CaseMode, ignoreCase: true))
        {
            WholeWord = WholeWord,
            WholeLine = WholeLine,
            Multiline = Multiline,
            DotAll = DotAll,
            MaxHits = int.MaxValue,
            MaxDocuments = int.MaxValue,
            MaxBytesScanned = long.MaxValue
        };
    }

    public PathFilter ToPathFilter()
    {
        return new PathFilter
        {
            PathPrefix = PathPrefix,
            IncludeGlobs = IncludeGlobs,
            ExcludeGlobs = ExcludeGlobs,
            PathRegex = PathRegex
        };
    }
}

internal readonly record struct DiscoveredCorpus(string Id, string Path);
internal readonly record struct CachedCorpusIndex(string CorpusId, string CachePath);

internal static class PostingsBenchmarkData
{
    public static uint[] BuildArithmeticOrdinals(int count, int stride)
    {
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (uint)(i * stride);
        }

        return values;
    }

    public static uint[] BuildOrdinalsForEncodedSizeTarget(int targetBytes)
    {
        var count = 512;
        while (true)
        {
            var ordinals = BuildArithmeticOrdinals(count, 3);
            var payload = PostingsCodec.Encode(ordinals);
            if (payload.Length >= targetBytes)
            {
                return ordinals;
            }

            count += 128;
        }
    }

    public static uint[] BuildSubsetOrdinals(ReadOnlySpan<uint> source, int step)
    {
        if (source.IsEmpty)
        {
            return [];
        }

        var count = (source.Length + step - 1) / step;
        var values = new uint[count];
        var index = 0;
        for (var i = 0; i < source.Length; i += step)
        {
            values[index++] = source[i];
        }

        return values;
    }

    public static uint[] BuildDisjointOrdinals(ReadOnlySpan<uint> source, int count)
    {
        if (source.IsEmpty || count <= 0)
        {
            return [];
        }

        var stride = source.Length > 1 ? source[1] - source[0] : 1u;
        var offset = source[0] + 1u;
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = offset + ((uint)i * stride);
        }

        return values;
    }

    public static uint[] BuildTailMissOrdinals(ReadOnlySpan<uint> source, int count)
    {
        if (source.IsEmpty || count <= 0)
        {
            return [];
        }

        var stride = source.Length > 1 ? source[1] - source[0] : 1u;
        var start = source[^1] + stride;
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = start + ((uint)i * stride);
        }

        return values;
    }
}

internal static class BlockMergeIntersection
{
    private const int ScalarBlockSize = 4;

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        var result = new uint[Math.Min(left.Length, right.Length)];
        var count = 0;
        var ai = 0;
        var bi = 0;
        while (ai < left.Length && bi < right.Length)
        {
            var aCount = Math.Min(ScalarBlockSize, left.Length - ai);
            var bCount = Math.Min(ScalarBlockSize, right.Length - bi);

            for (var i = 0; i < aCount; i++)
            {
                var value = left[ai + i];
                for (var j = 0; j < bCount; j++)
                {
                    if (value == right[bi + j])
                    {
                        result[count++] = value;
                        break;
                    }
                }
            }

            var aLast = left[ai + aCount - 1];
            var bLast = right[bi + bCount - 1];
            if (aLast <= bLast)
            {
                ai += aCount;
            }

            if (bLast <= aLast)
            {
                bi += bCount;
            }
        }

        Array.Resize(ref result, count);
        return result;
    }

    public static uint[] IntersectSortedSimd(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        if (!Vector.IsHardwareAccelerated || Vector<uint>.Count < 4)
        {
            return IntersectSorted(left, right);
        }

        var blockSize = Vector<uint>.Count;
        var result = new uint[Math.Min(left.Length, right.Length)];
        var count = 0;
        var ai = 0;
        var bi = 0;
        var zero = Vector<uint>.Zero;
        while (ai < left.Length && bi < right.Length)
        {
            var aCount = Math.Min(blockSize, left.Length - ai);
            var bCount = Math.Min(blockSize, right.Length - bi);

            if (aCount == blockSize && bCount == blockSize)
            {
                var blockB = new Vector<uint>(right.Slice(bi, blockSize));
                for (var i = 0; i < blockSize; i++)
                {
                    var value = left[ai + i];
                    var eq = Vector.Equals(new Vector<uint>(value), blockB);
                    if (!Vector.EqualsAll(eq, zero))
                    {
                        result[count++] = value;
                    }
                }
            }
            else
            {
                for (var i = 0; i < aCount; i++)
                {
                    var value = left[ai + i];
                    for (var j = 0; j < bCount; j++)
                    {
                        if (value == right[bi + j])
                        {
                            result[count++] = value;
                            break;
                        }
                    }
                }
            }

            var aLast = left[ai + aCount - 1];
            var bLast = right[bi + bCount - 1];
            if (aLast <= bLast)
            {
                ai += aCount;
            }

            if (bLast <= aLast)
            {
                bi += bCount;
            }
        }

        Array.Resize(ref result, count);
        return result;
    }
}

internal static class AdaptiveIntersection
{
    private const int TinyListThreshold = 256;
    private const int SimdBlockThreshold = 1024;

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, ReadOnlySpan<byte> checkpointedPayload, int rightCount)
    {
        return IntersectCore(left, right, checkpointedPayload, rightCount, useSimd: false);
    }

    public static uint[] IntersectSortedSimd(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, ReadOnlySpan<byte> checkpointedPayload, int rightCount)
    {
        return IntersectCore(left, right, checkpointedPayload, rightCount, useSimd: true);
    }

    private static uint[] IntersectCore(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, ReadOnlySpan<byte> checkpointedPayload, int rightCount, bool useSimd)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        if (left[0] > right[^1] || right[0] > left[^1])
        {
            return [];
        }

        var leftLength = left.Length;
        var smaller = Math.Min(leftLength, rightCount);
        if (smaller <= TinyListThreshold)
        {
            return ScalarIntersection.IntersectSorted(left, right);
        }

        if (smaller < SimdBlockThreshold)
        {
            return BlockMergeIntersection.IntersectSorted(left, right);
        }

        return useSimd
            ? BlockMergeIntersection.IntersectSortedSimd(left, right)
            : BlockMergeIntersection.IntersectSorted(left, right);
    }
}

internal static class CheckpointedBlockIntersection
{
    private const int DecodedBlockSize = 128;

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCore(left, checkpointedPayload, useSimd: false);
    }

    public static uint[] IntersectSortedSimd(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCore(left, checkpointedPayload, useSimd: true);
    }

    public static uint[] IntersectSortedAdjacent2ShuffleSimd(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCoreAdjacent(left, checkpointedPayload, AdjacentDecodeMode.Adjacent2Shuffle);
    }

    public static uint[] IntersectSortedAdjacent4ShuffleSimd(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCoreAdjacent(left, checkpointedPayload, AdjacentDecodeMode.Adjacent4Shuffle);
    }

    private static uint[] IntersectCore(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload, bool useSimd)
    {
        if (left.IsEmpty || checkpointedPayload.IsEmpty)
        {
            return [];
        }

        var view = PostingsCodec.Parse(checkpointedPayload);
        if (view.Data.IsEmpty)
        {
            return [];
        }

        if (TryGetApproximateRange(view, out var first, out var last) &&
            (left[0] > last || left[^1] < first))
        {
            return [];
        }

        var result = new uint[left.Length];
        var resultCount = 0;
        var rightBuffer = ArrayPool<uint>.Shared.Rent(DecodedBlockSize);
        try
        {
            var leftIndex = AdvanceLeft(left, 0, first);
            uint current = 0;
            var offset = 0;
            while (leftIndex < left.Length && offset < view.Data.Length)
            {
                var decodedCount = DecodeBlock(view.Data, ref offset, ref current, rightBuffer);
                if (decodedCount == 0)
                {
                    break;
                }

                var rightFirst = rightBuffer[0];
                var rightLast = rightBuffer[decodedCount - 1];
                leftIndex = AdvanceLeft(left, leftIndex, rightFirst);
                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] > rightLast)
                {
                    continue;
                }

                var leftEnd = leftIndex;
                while (leftEnd < left.Length && left[leftEnd] <= rightLast)
                {
                    leftEnd++;
                }

                if (leftEnd > leftIndex)
                {
                    resultCount = useSimd
                        ? AppendIntersectSortedBlockMergeSimd(left.Slice(leftIndex, leftEnd - leftIndex), rightBuffer.AsSpan(0, decodedCount), result, resultCount)
                        : AppendIntersectSortedBlockMerge(left.Slice(leftIndex, leftEnd - leftIndex), rightBuffer.AsSpan(0, decodedCount), result, resultCount);
                    leftIndex = leftEnd;
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rightBuffer);
        }

        Array.Resize(ref result, resultCount);
        return result;
    }

    private static uint[] IntersectCoreAdjacent(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload, AdjacentDecodeMode mode)
    {
        if (left.IsEmpty || checkpointedPayload.IsEmpty)
        {
            return [];
        }

        var view = PostingsCodec.Parse(checkpointedPayload);
        if (view.Data.IsEmpty)
        {
            return [];
        }

        if (TryGetApproximateRange(view, out var first, out var last) &&
            (left[0] > last || left[^1] < first))
        {
            return [];
        }

        var result = new uint[left.Length];
        var resultCount = 0;
        var rightBuffer = ArrayPool<uint>.Shared.Rent(DecodedBlockSize);
        try
        {
            var leftIndex = AdvanceLeft(left, 0, first);
            uint current = 0;
            var offset = 0;
            while (leftIndex < left.Length && offset < view.Data.Length)
            {
                var decodedCount = AdjacentVarIntDecode.DecodeBlockDeltaU32(view.Data, ref offset, ref current, rightBuffer, DecodedBlockSize, mode);
                if (decodedCount == 0)
                {
                    break;
                }

                var rightFirst = rightBuffer[0];
                var rightLast = rightBuffer[decodedCount - 1];
                leftIndex = AdvanceLeft(left, leftIndex, rightFirst);
                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] > rightLast)
                {
                    continue;
                }

                var leftEnd = leftIndex;
                while (leftEnd < left.Length && left[leftEnd] <= rightLast)
                {
                    leftEnd++;
                }

                if (leftEnd > leftIndex)
                {
                    resultCount = AppendIntersectSortedBlockMergeSimd(left.Slice(leftIndex, leftEnd - leftIndex), rightBuffer.AsSpan(0, decodedCount), result, resultCount);
                    leftIndex = leftEnd;
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rightBuffer);
        }

        Array.Resize(ref result, resultCount);
        return result;
    }

    private static int DecodeBlock(ReadOnlySpan<byte> data, ref int offset, ref uint current, uint[] buffer)
    {
        var count = 0;
        while (offset < data.Length && count < DecodedBlockSize)
        {
            current += VarInt.ReadUInt32(data, ref offset);
            buffer[count++] = current;
        }

        return count;
    }

    private static int AdvanceLeft(ReadOnlySpan<uint> left, int start, uint target)
    {
        while (start < left.Length && left[start] < target)
        {
            start++;
        }

        return start;
    }

    private static bool TryGetApproximateRange(PostingsView view, out uint first, out uint last)
    {
        if (!view.IsCheckpointed || view.Checkpoints.Length < 8)
        {
            first = 0;
            last = 0;
            return false;
        }

        first = BinaryPrimitives.ReadUInt32LittleEndian(view.Checkpoints);
        var lastCheckpointOffset = view.Checkpoints.Length - 8;
        last = BinaryPrimitives.ReadUInt32LittleEndian(view.Checkpoints.Slice(lastCheckpointOffset, 4));
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(view.Checkpoints.Slice(lastCheckpointOffset + 4, 4));
        while (dataOffset < view.Data.Length)
        {
            last += VarInt.ReadUInt32(view.Data, ref dataOffset);
        }

        return true;
    }

    private static int AppendIntersectSortedBlockMerge(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, uint[] destination, int destinationCount)
    {
        var ai = 0;
        var bi = 0;
        while (ai < left.Length && bi < right.Length)
        {
            var aCount = Math.Min(4, left.Length - ai);
            var bCount = Math.Min(4, right.Length - bi);

            for (var i = 0; i < aCount; i++)
            {
                var value = left[ai + i];
                for (var j = 0; j < bCount; j++)
                {
                    if (value == right[bi + j])
                    {
                        destination[destinationCount++] = value;
                        break;
                    }
                }
            }

            var aLast = left[ai + aCount - 1];
            var bLast = right[bi + bCount - 1];
            if (aLast <= bLast)
            {
                ai += aCount;
            }

            if (bLast <= aLast)
            {
                bi += bCount;
            }
        }

        return destinationCount;
    }

    private static int AppendIntersectSortedBlockMergeSimd(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, uint[] destination, int destinationCount)
    {
        if (!Vector.IsHardwareAccelerated || Vector<uint>.Count < 4)
        {
            return AppendIntersectSortedBlockMerge(left, right, destination, destinationCount);
        }

        var blockSize = Vector<uint>.Count;
        var ai = 0;
        var bi = 0;
        var zero = Vector<uint>.Zero;
        while (ai < left.Length && bi < right.Length)
        {
            var aCount = Math.Min(blockSize, left.Length - ai);
            var bCount = Math.Min(blockSize, right.Length - bi);

            if (aCount == blockSize && bCount == blockSize)
            {
                var blockB = new Vector<uint>(right.Slice(bi, blockSize));
                for (var i = 0; i < blockSize; i++)
                {
                    var value = left[ai + i];
                    var eq = Vector.Equals(new Vector<uint>(value), blockB);
                    if (!Vector.EqualsAll(eq, zero))
                    {
                        destination[destinationCount++] = value;
                    }
                }
            }
            else
            {
                for (var i = 0; i < aCount; i++)
                {
                    var value = left[ai + i];
                    for (var j = 0; j < bCount; j++)
                    {
                        if (value == right[bi + j])
                        {
                            destination[destinationCount++] = value;
                            break;
                        }
                    }
                }
            }

            var aLast = left[ai + aCount - 1];
            var bLast = right[bi + bCount - 1];
            if (aLast <= bLast)
            {
                ai += aCount;
            }

            if (bLast <= aLast)
            {
                bi += bCount;
            }
        }

        return destinationCount;
    }
}

internal enum AdjacentDecodeMode
{
    Adjacent2Shuffle,
    Adjacent4Shuffle
}

internal static class AdjacentVarIntDecode
{
    private static readonly Vector128<byte>[] s_twoShuffleMasks = BuildTwoShuffleMasks();
    private static readonly Vector128<byte>[] s_fourShuffleMasks = BuildFourShuffleMasks();

    public static uint[] DecodeCheckpointedU32Adjacent2(ReadOnlySpan<byte> payload)
    {
        return DecodeCheckpointedU32(payload, AdjacentDecodeMode.Adjacent2Shuffle);
    }

    public static uint[] DecodeCheckpointedU32Adjacent4(ReadOnlySpan<byte> payload)
    {
        return DecodeCheckpointedU32(payload, AdjacentDecodeMode.Adjacent4Shuffle);
    }

    private static uint[] DecodeCheckpointedU32(ReadOnlySpan<byte> payload, AdjacentDecodeMode mode)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var view = PostingsCodec.Parse(payload);
        if (view.Data.IsEmpty)
        {
            return [];
        }

        var values = new uint[EstimateCount(view)];
        uint current = 0;
        var offset = 0;
        var count = 0;
        while (offset < view.Data.Length)
        {
            if (count == values.Length)
            {
                Array.Resize(ref values, Math.Max(4, values.Length * 2));
            }

            var remaining = values.Length - count;
            count += DecodeBlockDeltaU32(view.Data, ref offset, ref current, values.AsSpan(count), remaining, mode);
        }

        Array.Resize(ref values, count);
        return values;
    }

    public static int DecodeBlockDeltaU32(ReadOnlySpan<byte> data, ref int offset, ref uint current, Span<uint> destination, int maxCount, AdjacentDecodeMode mode)
    {
        var count = 0;
        if (Ssse3.IsSupported && Sse2.IsSupported)
        {
            while (count < maxCount && offset + 16 <= data.Length)
            {
                var written = 0;
                var consumed4 = 0;
                if (mode == AdjacentDecodeMode.Adjacent4Shuffle)
                {
                    written = TryDecodeFour(data.Slice(offset), ref current, destination.Slice(count, maxCount - count), out consumed4);
                }

                if (written > 0)
                {
                    count += written;
                    offset += consumed4;
                    continue;
                }

                written = TryDecodeTwo(data.Slice(offset), ref current, destination.Slice(count, maxCount - count), out var consumed2);
                if (written > 0)
                {
                    count += written;
                    offset += consumed2;
                    continue;
                }

                break;
            }
        }

        while (count < maxCount && offset < data.Length)
        {
            current += VarInt.ReadUInt32(data, ref offset);
            destination[count++] = current;
        }

        return count;
    }

    private static int TryDecodeTwo(ReadOnlySpan<byte> data, ref uint current, Span<uint> destination, out int consumed)
    {
        consumed = 0;
        if (destination.Length < 2 || data.Length < 16)
        {
            return 0;
        }

        var vector = MemoryMarshal.Read<Vector128<byte>>(data);
        var continuationMask = Sse2.MoveMask(vector);
        var stopMask = (~continuationMask) & 0x3FF;
        if (stopMask == 0)
        {
            return 0;
        }

        var firstLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(firstLen - 1) >= 5u)
        {
            return 0;
        }

        var secondStopMask = stopMask >> firstLen;
        if (secondStopMask == 0)
        {
            return 0;
        }

        var secondLen = BitOperations.TrailingZeroCount((uint)secondStopMask) + 1;
        if ((uint)(secondLen - 1) >= 5u)
        {
            return 0;
        }

        consumed = firstLen + secondLen;
        var mask = s_twoShuffleMasks[(firstLen - 1) * 5 + (secondLen - 1)];
        var shuffled = Ssse3.Shuffle(vector, mask);
        Span<byte> packed = stackalloc byte[16];
        MemoryMarshal.Write(packed, in shuffled);

        current += DecodeU32ByLength(packed, firstLen);
        destination[0] = current;
        current += DecodeU32ByLength(packed.Slice(8), secondLen);
        destination[1] = current;
        return 2;
    }

    private static int TryDecodeFour(ReadOnlySpan<byte> data, ref uint current, Span<uint> destination, out int consumed)
    {
        consumed = 0;
        if (destination.Length < 4 || data.Length < 16)
        {
            return 0;
        }

        var vector = MemoryMarshal.Read<Vector128<byte>>(data);
        var continuationMask = Sse2.MoveMask(vector);
        var stopMask = (~continuationMask) & 0xFFFF;
        if (stopMask == 0)
        {
            return 0;
        }

        var firstLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(firstLen - 1) >= 4u)
        {
            return 0;
        }

        stopMask >>= firstLen;
        if (stopMask == 0)
        {
            return 0;
        }

        var secondLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(secondLen - 1) >= 4u)
        {
            return 0;
        }

        stopMask >>= secondLen;
        if (stopMask == 0)
        {
            return 0;
        }

        var thirdLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(thirdLen - 1) >= 4u)
        {
            return 0;
        }

        stopMask >>= thirdLen;
        if (stopMask == 0)
        {
            return 0;
        }

        var fourthLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(fourthLen - 1) >= 4u)
        {
            return 0;
        }

        consumed = firstLen + secondLen + thirdLen + fourthLen;
        if (consumed > 16)
        {
            return 0;
        }

        var maskIndex = (((firstLen - 1) * 4 + (secondLen - 1)) * 4 + (thirdLen - 1)) * 4 + (fourthLen - 1);
        var mask = s_fourShuffleMasks[maskIndex];
        var shuffled = Ssse3.Shuffle(vector, mask);
        Span<byte> packed = stackalloc byte[16];
        MemoryMarshal.Write(packed, in shuffled);

        current += DecodeU32ByLength(packed, firstLen);
        destination[0] = current;
        current += DecodeU32ByLength(packed.Slice(4), secondLen);
        destination[1] = current;
        current += DecodeU32ByLength(packed.Slice(8), thirdLen);
        destination[2] = current;
        current += DecodeU32ByLength(packed.Slice(12), fourthLen);
        destination[3] = current;
        return 4;
    }

    private static Vector128<byte>[] BuildTwoShuffleMasks()
    {
        var masks = new Vector128<byte>[25];
        Span<byte> bytes = stackalloc byte[16];
        for (var firstLen = 1; firstLen <= 5; firstLen++)
        {
            for (var secondLen = 1; secondLen <= 5; secondLen++)
            {
                bytes.Fill(0x80);
                for (var i = 0; i < firstLen; i++)
                {
                    bytes[i] = (byte)i;
                }

                for (var i = 0; i < secondLen; i++)
                {
                    bytes[8 + i] = (byte)(firstLen + i);
                }

                masks[(firstLen - 1) * 5 + (secondLen - 1)] = MemoryMarshal.Read<Vector128<byte>>(bytes);
            }
        }

        return masks;
    }

    private static Vector128<byte>[] BuildFourShuffleMasks()
    {
        var masks = new Vector128<byte>[256];
        Span<byte> bytes = stackalloc byte[16];
        for (var firstLen = 1; firstLen <= 4; firstLen++)
        {
            for (var secondLen = 1; secondLen <= 4; secondLen++)
            {
                for (var thirdLen = 1; thirdLen <= 4; thirdLen++)
                {
                    for (var fourthLen = 1; fourthLen <= 4; fourthLen++)
                    {
                        bytes.Fill(0x80);
                        var source = 0;
                        for (var i = 0; i < firstLen; i++)
                        {
                            bytes[i] = (byte)(source + i);
                        }

                        source += firstLen;
                        for (var i = 0; i < secondLen; i++)
                        {
                            bytes[4 + i] = (byte)(source + i);
                        }

                        source += secondLen;
                        for (var i = 0; i < thirdLen; i++)
                        {
                            bytes[8 + i] = (byte)(source + i);
                        }

                        source += thirdLen;
                        for (var i = 0; i < fourthLen; i++)
                        {
                            bytes[12 + i] = (byte)(source + i);
                        }

                        var index = (((firstLen - 1) * 4 + (secondLen - 1)) * 4 + (thirdLen - 1)) * 4 + (fourthLen - 1);
                        masks[index] = MemoryMarshal.Read<Vector128<byte>>(bytes);
                    }
                }
            }
        }

        return masks;
    }

    private static uint DecodeU32ByLength(ReadOnlySpan<byte> data, int length)
    {
        return length switch
        {
            1 => (uint)(data[0] & 0x7F),
            2 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7),
            3 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7) | ((uint)(data[2] & 0x7F) << 14),
            4 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7) | ((uint)(data[2] & 0x7F) << 14) | ((uint)(data[3] & 0x7F) << 21),
            5 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7) | ((uint)(data[2] & 0x7F) << 14) | ((uint)(data[3] & 0x7F) << 21) | ((uint)(data[4] & 0x0F) << 28),
            _ => throw new ArgumentOutOfRangeException(nameof(length))
        };
    }

    private static int EstimateCount(PostingsView view)
    {
        if (view.IsCheckpointed && view.Checkpoints.Length >= 8)
        {
            var checkpointCount = view.Checkpoints.Length / 8;
            return Math.Max(4, checkpointCount * PostingsCodec.CheckpointInterval);
        }

        return Math.Max(4, view.Data.Length);
    }
}

internal static class ScalarIntersection
{
    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        var result = new uint[Math.Min(left.Length, right.Length)];
        var count = 0;
        var i = 0;
        var j = 0;
        while (i < left.Length && j < right.Length)
        {
            var leftValue = left[i];
            var rightValue = right[j];
            if (leftValue == rightValue)
            {
                result[count++] = leftValue;
                i++;
                j++;
            }
            else if (leftValue < rightValue)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        Array.Resize(ref result, count);
        return result;
    }
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostingsCodecBenchmarks
{
    private uint[] _ordinals = [];
    private byte[] _checkpointedPayload = [];
    private byte[] _blockPayload = [];
    private byte[] _packedBlockPayload = [];
    private byte[] _bp128Payload = [];
    private byte[] _streamVBytePayload = [];
    private uint[] _left = [];

    [ParamsSource(nameof(Cases))]
    public string CaseName { get; set; } = string.Empty;

    public IEnumerable<string> Cases => ["Target1K", "Target2K", "Target4K", "Target8K", "Large200K", "Large200KWidth8"];

    [GlobalSetup]
    public void Setup()
    {
        _ordinals = CaseName switch
        {
            "Target1K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(1_024),
            "Target2K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(2_048),
            "Target4K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(4_096),
            "Target8K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(8_192),
            "Large200K" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 3),
            "Large200KWidth8" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 2),
            _ => throw new InvalidOperationException($"Unknown postings benchmark case '{CaseName}'.")
        };
        _checkpointedPayload = PostingsCodec.Encode(_ordinals);
        _blockPayload = BlockPostingsCodec.Encode(_ordinals);
        _packedBlockPayload = PackedBlockPostingsCodec.Encode(_ordinals);
        _bp128Payload = Bp128PostingsCodec.Encode(_ordinals);
        _streamVBytePayload = StreamVBytePostingsCodec.Encode(_ordinals);
        _left = PostingsBenchmarkData.BuildArithmeticOrdinals(Math.Max(64, _ordinals.Length / 2), 5);
    }

    [Benchmark]
    public byte[] EncodeCheckpointed() => PostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodeBlock() => BlockPostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodePackedBlock() => PackedBlockPostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodeBp128() => Bp128PostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodeStreamVByte() => StreamVBytePostingsCodec.Encode(_ordinals);

    [Benchmark]
    public uint[] DecodeCheckpointed() => PostingsCodec.Decode(_checkpointedPayload);

    [Benchmark]
    public uint[] DecodeBlock() => BlockPostingsCodec.Decode(_blockPayload);

    [Benchmark]
    public uint[] DecodePackedBlock() => PackedBlockPostingsCodec.Decode(_packedBlockPayload);

    [Benchmark]
    public uint[] DecodeBp128() => Bp128PostingsCodec.Decode(_bp128Payload);

    [Benchmark]
    public uint[] DecodeStreamVByte() => StreamVBytePostingsCodec.Decode(_streamVBytePayload);

    [Benchmark]
    public uint[] IntersectCheckpointed() => PostingsCodec.IntersectSorted(_left, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectBlock() => BlockPostingsCodec.IntersectSorted(_left, _blockPayload);

    [Benchmark]
    public uint[] IntersectPackedBlock() => PackedBlockPostingsCodec.IntersectSorted(_left, _packedBlockPayload);

    [Benchmark]
    public uint[] IntersectBp128() => Bp128PostingsCodec.IntersectSorted(_left, _bp128Payload);

    [Benchmark]
    public uint[] IntersectStreamVByte() => StreamVBytePostingsCodec.IntersectSorted(_left, _streamVBytePayload);

    [Benchmark]
    public uint[] IntersectAdaptive() => AdaptiveIntersection.IntersectSorted(_left, _ordinals, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectAdaptiveSimd() => AdaptiveIntersection.IntersectSortedSimd(_left, _ordinals, _checkpointedPayload, _ordinals.Length);
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostingsIntersectionBenchmarks
{
    private uint[] _ordinals = [];
    private byte[] _checkpointedPayload = [];
    private byte[] _blockPayload = [];
    private byte[] _bp128Payload = [];
    private byte[] _streamVBytePayload = [];
    private uint[] _left = [];

    [ParamsSource(nameof(Cases))]
    public string CaseName { get; set; } = string.Empty;

    [ParamsSource(nameof(LeftShapes))]
    public string LeftShape { get; set; } = string.Empty;

    public IEnumerable<string> Cases => ["Target8K", "Large200K", "Large200KWidth8"];

    public IEnumerable<string> LeftShapes => ["SubsetHalf", "SubsetSparse", "DenseMixed", "Disjoint", "TailMiss"];

    [GlobalSetup]
    public void Setup()
    {
        _ordinals = CaseName switch
        {
            "Target8K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(8_192),
            "Large200K" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 3),
            "Large200KWidth8" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 2),
            _ => throw new InvalidOperationException($"Unknown postings benchmark case '{CaseName}'.")
        };

        _checkpointedPayload = PostingsCodec.Encode(_ordinals);
        _blockPayload = BlockPostingsCodec.Encode(_ordinals);
        _bp128Payload = Bp128PostingsCodec.Encode(_ordinals);
        _streamVBytePayload = StreamVBytePostingsCodec.Encode(_ordinals);
        _left = LeftShape switch
        {
            "SubsetHalf" => PostingsBenchmarkData.BuildSubsetOrdinals(_ordinals, 2),
            "SubsetSparse" => PostingsBenchmarkData.BuildSubsetOrdinals(_ordinals, 16),
            "DenseMixed" => PostingsBenchmarkData.BuildArithmeticOrdinals(Math.Max(64, (_ordinals.Length * 4) / 5), 4),
            "Disjoint" => PostingsBenchmarkData.BuildDisjointOrdinals(_ordinals, Math.Max(64, _ordinals.Length / 2)),
            "TailMiss" => PostingsBenchmarkData.BuildTailMissOrdinals(_ordinals, Math.Max(64, _ordinals.Length / 2)),
            _ => throw new InvalidOperationException($"Unknown left shape '{LeftShape}'.")
        };
    }

    [Benchmark]
    public uint[] IntersectCheckpointed() => PostingsCodec.IntersectSorted(_left, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectBlock() => BlockPostingsCodec.IntersectSorted(_left, _blockPayload);

    [Benchmark]
    public uint[] IntersectBp128() => Bp128PostingsCodec.IntersectSorted(_left, _bp128Payload);

    [Benchmark]
    public uint[] IntersectStreamVByte() => StreamVBytePostingsCodec.IntersectSorted(_left, _streamVBytePayload);

    [Benchmark]
    public uint[] IntersectAdaptive() => AdaptiveIntersection.IntersectSorted(_left, _ordinals, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectAdaptiveSimd() => AdaptiveIntersection.IntersectSortedSimd(_left, _ordinals, _checkpointedPayload, _ordinals.Length);
}

internal static class StreamVBytePostingsCodec
{
    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.IsEmpty)
        {
            return [];
        }

        var count = ordinals.Length;
        var controlCount = (count + 3) >> 2;
        var payload = new ArrayBufferWriter<byte>(controlCount + (count * sizeof(uint)));
        var header = payload.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(header, count);
        payload.Advance(4);

        var control = payload.GetSpan(controlCount);
        control.Slice(0, controlCount).Clear();
        payload.Advance(controlCount);

        var previous = 0u;
        for (var group = 0; group < controlCount; group++)
        {
            byte controlByte = 0;
            for (var lane = 0; lane < 4; lane++)
            {
                var index = (group << 2) + lane;
                if (index >= count)
                {
                    break;
                }

                var delta = ordinals[index] - previous;
                previous = ordinals[index];
                var byteCount = RequiredByteCount(delta);
                controlByte |= (byte)((byteCount - 1) << (lane * 2));
                var span = payload.GetSpan(byteCount);
                WriteLittleEndian(span, delta, byteCount);
                payload.Advance(byteCount);
            }

            control[group] = controlByte;
        }

        return payload.WrittenSpan.ToArray();
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        if (count <= 0)
        {
            return [];
        }

        var controlCount = (count + 3) >> 2;
        var control = payload.Slice(4, controlCount);
        var data = payload.Slice(4 + controlCount);
        var result = GC.AllocateUninitializedArray<uint>(count);
        var cursor = 0;
        var previous = 0u;
        for (var group = 0; group < controlCount; group++)
        {
            var controlByte = control[group];
            for (var lane = 0; lane < 4; lane++)
            {
                var index = (group << 2) + lane;
                if (index >= count)
                {
                    break;
                }

                var byteCount = ((controlByte >> (lane * 2)) & 0x3) + 1;
                var delta = ReadLittleEndian(data.Slice(cursor, byteCount), byteCount);
                cursor += byteCount;
                previous += delta;
                result[index] = previous;
            }
        }

        return result;
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.IsEmpty || payload.IsEmpty)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        if (count <= 0)
        {
            return [];
        }

        var controlCount = (count + 3) >> 2;
        var control = payload.Slice(4, controlCount);
        var data = payload.Slice(4 + controlCount);
        var result = new uint[Math.Min(left.Length, count)];
        var leftIndex = 0;
        var matchCount = 0;
        var cursor = 0;
        var previous = 0u;
        for (var group = 0; group < controlCount && leftIndex < left.Length; group++)
        {
            var controlByte = control[group];
            for (var lane = 0; lane < 4 && leftIndex < left.Length; lane++)
            {
                var index = (group << 2) + lane;
                if (index >= count)
                {
                    break;
                }

                var byteCount = ((controlByte >> (lane * 2)) & 0x3) + 1;
                var delta = ReadLittleEndian(data.Slice(cursor, byteCount), byteCount);
                cursor += byteCount;
                previous += delta;

                while (leftIndex < left.Length && left[leftIndex] < previous)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == previous)
                {
                    result[matchCount++] = previous;
                    leftIndex++;
                }
            }
        }

        Array.Resize(ref result, matchCount);
        return result;
    }

    private static int RequiredByteCount(uint value)
    {
        if (value <= byte.MaxValue)
        {
            return 1;
        }

        if (value <= ushort.MaxValue)
        {
            return 2;
        }

        return value <= 0x00FF_FFFFu ? 3 : 4;
    }

    private static void WriteLittleEndian(Span<byte> destination, uint value, int byteCount)
    {
        for (var i = 0; i < byteCount; i++)
        {
            destination[i] = (byte)(value >> (i * 8));
        }
    }

    private static uint ReadLittleEndian(ReadOnlySpan<byte> source, int byteCount)
    {
        var value = 0u;
        for (var i = 0; i < byteCount; i++)
        {
            value |= (uint)source[i] << (i * 8);
        }

        return value;
    }
}

internal static class Bp128PostingsCodec
{
    private const int BlockSize = 128;

    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.IsEmpty)
        {
            return [];
        }

        var payload = new ArrayBufferWriter<byte>(EstimateCapacity(ordinals.Length));
        var deltas = new uint[BlockSize - 1];
        var words = new uint[BlockSize * sizeof(uint)];
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            var first = ordinals[offset];
            var maxDelta = 0u;
            for (var i = 1; i < count; i++)
            {
                var delta = ordinals[offset + i] - first;
                deltas[i - 1] = delta;
                maxDelta |= delta;
            }

            var bitWidth = maxDelta == 0 ? 0 : BitOperations.Log2(maxDelta) + 1;
            var wordCount = ((count - 1) * bitWidth + 31) >> 5;
            Array.Clear(words, 0, wordCount);
            PackWords(deltas.AsSpan(0, count - 1), bitWidth, words.AsSpan(0, wordCount));

            var header = payload.GetSpan(8);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)count);
            header[2] = (byte)bitWidth;
            header[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), first);
            payload.Advance(8);

            var body = payload.GetSpan(wordCount * sizeof(uint));
            MemoryMarshal.Cast<byte, uint>(body.Slice(0, wordCount * sizeof(uint))).Clear();
            words.AsSpan(0, wordCount).CopyTo(MemoryMarshal.Cast<byte, uint>(body));
            payload.Advance(wordCount * sizeof(uint));
        }

        return payload.WrittenSpan.ToArray();
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var values = new List<uint>(payload.Length / 2);
        var unpacked = new uint[BlockSize - 1];
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var wordCount = ((count - 1) * bitWidth + 31) >> 5;
            UnpackWords(MemoryMarshal.Cast<byte, uint>(payload.Slice(cursor, wordCount * sizeof(uint))), bitWidth, unpacked.AsSpan(0, count - 1));
            cursor += wordCount * sizeof(uint);

            values.Add(first);
            for (var i = 0; i < count - 1; i++)
            {
                values.Add(first + unpacked[i]);
            }
        }

        return values.ToArray();
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.IsEmpty || payload.IsEmpty)
        {
            return [];
        }

        var result = new uint[left.Length];
        var matchCount = 0;
        var leftIndex = 0;
        var cursor = 0;
        var unpacked = new uint[BlockSize - 1];
        while (leftIndex < left.Length && cursor < payload.Length)
        {
            var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var wordCount = ((blockCount - 1) * bitWidth + 31) >> 5;
            UnpackWords(MemoryMarshal.Cast<byte, uint>(payload.Slice(cursor, wordCount * sizeof(uint))), bitWidth, unpacked.AsSpan(0, blockCount - 1));
            cursor += wordCount * sizeof(uint);

            var last = blockCount == 1 ? first : first + unpacked[blockCount - 2];
            while (leftIndex < left.Length && left[leftIndex] < first)
            {
                leftIndex++;
            }

            if (leftIndex >= left.Length)
            {
                break;
            }

            if (left[leftIndex] > last)
            {
                continue;
            }

            if (left[leftIndex] == first)
            {
                result[matchCount++] = first;
                leftIndex++;
                if (leftIndex >= left.Length)
                {
                    break;
                }
            }

            for (var i = 0; i < blockCount - 1 && leftIndex < left.Length; i++)
            {
                var ordinal = first + unpacked[i];
                while (leftIndex < left.Length && left[leftIndex] < ordinal)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == ordinal)
                {
                    result[matchCount++] = ordinal;
                    leftIndex++;
                }
            }
        }

        Array.Resize(ref result, matchCount);
        return result;
    }

    private static int EstimateCapacity(int ordinalCount)
    {
        var blocks = (ordinalCount + BlockSize - 1) / BlockSize;
        return blocks * (8 + BlockSize * sizeof(uint));
    }

    private static void PackWords(ReadOnlySpan<uint> values, int bitWidth, Span<uint> words)
    {
        if (bitWidth == 0 || values.IsEmpty)
        {
            return;
        }

        var bitOffset = 0;
        foreach (var value in values)
        {
            var wordIndex = bitOffset >> 5;
            var bitIndex = bitOffset & 31;
            words[wordIndex] |= value << bitIndex;
            if (bitIndex + bitWidth > 32)
            {
                words[wordIndex + 1] |= value >> (32 - bitIndex);
            }

            bitOffset += bitWidth;
        }
    }

    private static void UnpackWords(ReadOnlySpan<uint> words, int bitWidth, Span<uint> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        if (bitWidth == 32)
        {
            words[..destination.Length].CopyTo(destination);
            return;
        }

        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        var bitOffset = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            var wordIndex = bitOffset >> 5;
            var bitIndex = bitOffset & 31;
            var value = words[wordIndex] >> bitIndex;
            if (bitIndex + bitWidth > 32)
            {
                value |= words[wordIndex + 1] << (32 - bitIndex);
            }

            destination[i] = value & mask;
            bitOffset += bitWidth;
        }
    }
}

internal static class PackedBlockPostingsCodec
{
    private const int BlockSize = 128;

    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.Length == 0)
        {
            return [];
        }

        var payload = new ArrayBufferWriter<byte>(EstimateCapacity(ordinals.Length));
        var blockBuffer = new uint[BlockSize - 1];
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            var first = ordinals[offset];
            var maxDelta = 0u;
            for (var i = 1; i < count; i++)
            {
                var delta = ordinals[offset + i] - first;
                blockBuffer[i - 1] = delta;
                maxDelta |= delta;
            }

            var bitWidth = maxDelta == 0 ? 0 : BitOperations.Log2(maxDelta) + 1;
            var packedBytes = ((count - 1) * bitWidth + 7) >> 3;

            var header = payload.GetSpan(8);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)count);
            header[2] = (byte)bitWidth;
            header[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), first);
            payload.Advance(8);

            var body = payload.GetSpan(packedBytes);
            body.Slice(0, packedBytes).Clear();
            Pack(blockBuffer.AsSpan(0, count - 1), bitWidth, body);
            payload.Advance(packedBytes);
        }

        return payload.WrittenSpan.ToArray();
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var values = new List<uint>(payload.Length / 2);
        var unpacked = new uint[BlockSize - 1];
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var packedBytes = ((count - 1) * bitWidth + 7) >> 3;
            Unpack(payload.Slice(cursor, packedBytes), bitWidth, unpacked.AsSpan(0, count - 1));
            cursor += packedBytes;

            values.Add(first);
            for (var i = 0; i < count - 1; i++)
            {
                values.Add(first + unpacked[i]);
            }
        }

        return values.ToArray();
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.IsEmpty || payload.IsEmpty)
        {
            return [];
        }

        var result = new uint[left.Length];
        var count = 0;
        var leftIndex = 0;
        var cursor = 0;
        var unpacked = new uint[BlockSize - 1];
        while (leftIndex < left.Length && cursor < payload.Length)
        {
            var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var packedBytes = ((blockCount - 1) * bitWidth + 7) >> 3;
            Unpack(payload.Slice(cursor, packedBytes), bitWidth, unpacked.AsSpan(0, blockCount - 1));
            cursor += packedBytes;

            var last = blockCount == 1 ? first : first + unpacked[blockCount - 2];
            while (leftIndex < left.Length && left[leftIndex] < first)
            {
                leftIndex++;
            }

            if (leftIndex >= left.Length)
            {
                break;
            }

            if (left[leftIndex] > last)
            {
                continue;
            }

            if (left[leftIndex] == first)
            {
                result[count++] = first;
                leftIndex++;
                if (leftIndex >= left.Length)
                {
                    break;
                }
            }

            for (var i = 0; i < blockCount - 1 && leftIndex < left.Length; i++)
            {
                var ordinal = first + unpacked[i];
                while (leftIndex < left.Length && left[leftIndex] < ordinal)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == ordinal)
                {
                    result[count++] = ordinal;
                    leftIndex++;
                }
            }
        }

        Array.Resize(ref result, count);
        return result;
    }

    private static int EstimateCapacity(int ordinalCount)
    {
        var blocks = (ordinalCount + BlockSize - 1) / BlockSize;
        return blocks * (8 + (BlockSize - 1) * sizeof(uint));
    }

    private static void Pack(ReadOnlySpan<uint> values, int bitWidth, Span<byte> destination)
    {
        if (bitWidth == 0 || values.IsEmpty)
        {
            return;
        }

        var bitOffset = 0;
        foreach (var value in values)
        {
            WriteBits(destination, bitOffset, value, bitWidth);
            bitOffset += bitWidth;
        }
    }

    private static void Unpack(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        var bitOffset = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadBits(source, bitOffset, bitWidth);
            bitOffset += bitWidth;
        }
    }

    private static void WriteBits(Span<byte> destination, int bitOffset, uint value, int bitWidth)
    {
        var remaining = bitWidth;
        var localOffset = bitOffset;
        var shifted = value;
        while (remaining > 0)
        {
            var byteIndex = localOffset >> 3;
            var bitIndex = localOffset & 7;
            var bitsThisByte = Math.Min(remaining, 8 - bitIndex);
            var mask = (1u << bitsThisByte) - 1u;
            destination[byteIndex] |= (byte)((shifted & mask) << bitIndex);
            shifted >>= bitsThisByte;
            localOffset += bitsThisByte;
            remaining -= bitsThisByte;
        }
    }

    private static uint ReadBits(ReadOnlySpan<byte> source, int bitOffset, int bitWidth)
    {
        var remaining = bitWidth;
        var localOffset = bitOffset;
        var result = 0u;
        var shift = 0;
        while (remaining > 0)
        {
            var byteIndex = localOffset >> 3;
            var bitIndex = localOffset & 7;
            var bitsThisByte = Math.Min(remaining, 8 - bitIndex);
            var mask = (1u << bitsThisByte) - 1u;
            var chunk = (uint)((source[byteIndex] >> bitIndex) & mask);
            result |= chunk << shift;
            localOffset += bitsThisByte;
            remaining -= bitsThisByte;
            shift += bitsThisByte;
        }

        return result;
    }
}

internal static class BlockPostingsCodec
{
    private const int BlockSize = 128;

    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.Length == 0)
        {
            return [];
        }

        var totalBytes = 0;
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            totalBytes += 2 + 4 + 4 + ((count - 1) * 4);
        }

        var payload = GC.AllocateUninitializedArray<byte>(totalBytes);
        var cursor = 0;
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            var first = ordinals[offset];
            var last = ordinals[offset + count - 1];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(cursor, 2), (ushort)count);
            cursor += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(cursor, 4), first);
            cursor += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(cursor, 4), last);
            cursor += 4;
            for (var i = 1; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(cursor, 4), ordinals[offset + i] - first);
                cursor += 4;
            }
        }

        return payload;
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        var values = new List<uint>(payload.Length / 4);
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            _ = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            values.Add(first);
            for (var i = 1; i < count; i++)
            {
                values.Add(first + BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4)));
                cursor += 4;
            }
        }

        return values.ToArray();
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.Length == 0 || payload.Length == 0)
        {
            return [];
        }

        var result = new uint[left.Length];
        var count = 0;
        var leftIndex = 0;
        var cursor = 0;
        while (leftIndex < left.Length && cursor < payload.Length)
        {
            var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var last = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var blockCursor = cursor;
            var blockEnd = cursor + ((blockCount - 1) * 4);

            while (leftIndex < left.Length && left[leftIndex] < first)
            {
                leftIndex++;
            }

            if (leftIndex >= left.Length)
            {
                break;
            }

            if (left[leftIndex] > last)
            {
                cursor = blockEnd;
                continue;
            }

            if (left[leftIndex] == first)
            {
                result[count++] = first;
                leftIndex++;
                if (leftIndex >= left.Length)
                {
                    break;
                }
            }

            for (var i = 1; i < blockCount && leftIndex < left.Length; i++)
            {
                var ordinal = first + BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(blockCursor, 4));
                blockCursor += 4;
                while (leftIndex < left.Length && left[leftIndex] < ordinal)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == ordinal)
                {
                    result[count++] = ordinal;
                    leftIndex++;
                }
            }

            cursor = blockEnd;
        }

        Array.Resize(ref result, count);
        return result;
    }
}

internal sealed class PostingsStatsAccumulator
{
    private static readonly (string Key, int MinInclusive, int MaxExclusive)[] EncodedByteBucketDefinitions =
    [
        ("1-31", 1, 32),
        ("32-127", 32, 128),
        ("128-511", 128, 512),
        ("512-2047", 512, 2048),
        ("2048-8191", 2048, 8192),
        ("8192+", 8192, int.MaxValue)
    ];

    private static readonly (string Key, int MinInclusive, int MaxExclusive)[] DocumentFrequencyBucketDefinitions =
    [
        ("1-31", 1, 32),
        ("32-127", 32, 128),
        ("128-511", 128, 512),
        ("512-2047", 512, 2048),
        ("2048-8191", 2048, 8192),
        ("8192+", 8192, int.MaxValue)
    ];

    private static readonly int[] Thresholds = [1024, 2048, 4096, 8192, 16384, 32768];

    private readonly long[] _encodedByteBucketCounts = new long[EncodedByteBucketDefinitions.Length];
    private readonly long[] _encodedByteBucketBytes = new long[EncodedByteBucketDefinitions.Length];
    private readonly long[] _docFreqBucketCounts = new long[DocumentFrequencyBucketDefinitions.Length];
    private readonly long[] _docFreqBucketBytes = new long[DocumentFrequencyBucketDefinitions.Length];
    private readonly long[] _thresholdCounts = new long[Thresholds.Length];
    private readonly long[] _thresholdBytes = new long[Thresholds.Length];
    private readonly long[] _thresholdBp128Bytes = new long[Thresholds.Length];
    private readonly long[] _thresholdStreamVByteBytes = new long[Thresholds.Length];
    private readonly Dictionary<string, RegionTotals> _regions = new(StringComparer.Ordinal);
    private long _totalPostingLists;
    private long _totalEncodedBytes;
    private long _totalBp128Bytes;
    private long _totalStreamVByteBytes;
    private long _checkpointedPostingLists;
    private long _checkpointedEncodedBytes;

    public void Add(string region, int documentFrequency, int encodedBytes, int bp128Bytes, int streamVByteBytes)
    {
        _totalPostingLists++;
        _totalEncodedBytes += encodedBytes;
        _totalBp128Bytes += bp128Bytes;
        _totalStreamVByteBytes += streamVByteBytes;
        if (encodedBytes >= PostingsCodec.CheckpointThreshold)
        {
            _checkpointedPostingLists++;
            _checkpointedEncodedBytes += encodedBytes;
        }

        var encodedBucket = FindBucket(encodedBytes, EncodedByteBucketDefinitions);
        _encodedByteBucketCounts[encodedBucket]++;
        _encodedByteBucketBytes[encodedBucket] += encodedBytes;

        var docFreqBucket = FindBucket(documentFrequency, DocumentFrequencyBucketDefinitions);
        _docFreqBucketCounts[docFreqBucket]++;
        _docFreqBucketBytes[docFreqBucket] += encodedBytes;

        for (var i = 0; i < Thresholds.Length; i++)
        {
            if (encodedBytes >= Thresholds[i])
            {
                _thresholdCounts[i]++;
                _thresholdBytes[i] += encodedBytes;
                _thresholdBp128Bytes[i] += bp128Bytes;
                _thresholdStreamVByteBytes[i] += streamVByteBytes;
            }
        }

        ref var totals = ref CollectionsMarshal.GetValueRefOrAddDefault(_regions, region, out _);
        totals.PostingLists++;
        totals.EncodedBytes += encodedBytes;
        totals.Bp128Bytes += bp128Bytes;
        totals.StreamVByteBytes += streamVByteBytes;
        if (encodedBytes >= PostingsCodec.CheckpointThreshold)
        {
            totals.CheckpointedPostingLists++;
            totals.CheckpointedEncodedBytes += encodedBytes;
        }
    }

    public PostingsStatsCorpusResult ToResult(string corpusId, string path, int segmentCount)
    {
        var encodedBuckets = new PostingsStatsBucket[EncodedByteBucketDefinitions.Length];
        for (var i = 0; i < encodedBuckets.Length; i++)
        {
            encodedBuckets[i] = new PostingsStatsBucket(
                EncodedByteBucketDefinitions[i].Key,
                _encodedByteBucketCounts[i],
                _encodedByteBucketBytes[i],
                Percent(_encodedByteBucketCounts[i], _totalPostingLists),
                Percent(_encodedByteBucketBytes[i], _totalEncodedBytes));
        }

        var docFreqBuckets = new PostingsStatsBucket[DocumentFrequencyBucketDefinitions.Length];
        for (var i = 0; i < docFreqBuckets.Length; i++)
        {
            docFreqBuckets[i] = new PostingsStatsBucket(
                DocumentFrequencyBucketDefinitions[i].Key,
                _docFreqBucketCounts[i],
                _docFreqBucketBytes[i],
                Percent(_docFreqBucketCounts[i], _totalPostingLists),
                Percent(_docFreqBucketBytes[i], _totalEncodedBytes));
        }

        var thresholds = new PostingsStatsThresholdSummary[Thresholds.Length];
        for (var i = 0; i < Thresholds.Length; i++)
        {
            thresholds[i] = new PostingsStatsThresholdSummary(
                Thresholds[i],
                _thresholdCounts[i],
                _thresholdBytes[i],
                _thresholdBp128Bytes[i],
                _thresholdStreamVByteBytes[i],
                Percent(_thresholdCounts[i], _totalPostingLists),
                Percent(_thresholdBytes[i], _totalEncodedBytes),
                InflationPercent(_thresholdBp128Bytes[i], _thresholdBytes[i]),
                InflationPercent(_thresholdStreamVByteBytes[i], _thresholdBytes[i]));
        }

        var regions = _regions
            .OrderByDescending(static pair => pair.Value.EncodedBytes)
            .Select(static pair => new PostingsStatsRegionSummary(
                pair.Key,
                pair.Value.PostingLists,
                pair.Value.EncodedBytes,
                pair.Value.Bp128Bytes,
                pair.Value.StreamVByteBytes,
                pair.Value.CheckpointedPostingLists,
                pair.Value.CheckpointedEncodedBytes,
                InflationPercent(pair.Value.Bp128Bytes, pair.Value.EncodedBytes),
                InflationPercent(pair.Value.StreamVByteBytes, pair.Value.EncodedBytes)))
            .ToArray();

        return new PostingsStatsCorpusResult(
            corpusId,
            path,
            segmentCount,
            _totalPostingLists,
            _totalEncodedBytes,
            _totalBp128Bytes,
            _totalStreamVByteBytes,
            _checkpointedPostingLists,
            _checkpointedEncodedBytes,
            InflationPercent(_totalBp128Bytes, _totalEncodedBytes),
            InflationPercent(_totalStreamVByteBytes, _totalEncodedBytes),
            encodedBuckets,
            docFreqBuckets,
            thresholds,
            regions);
    }

    private static int FindBucket(int value, (string Key, int MinInclusive, int MaxExclusive)[] definitions)
    {
        for (var i = 0; i < definitions.Length; i++)
        {
            if (value >= definitions[i].MinInclusive && value < definitions[i].MaxExclusive)
            {
                return i;
            }
        }

        return definitions.Length - 1;
    }

    private static double Percent(long part, long whole)
    {
        return whole == 0 ? 0d : (double)part / whole * 100d;
    }

    private static double InflationPercent(long alternativeBytes, long baselineBytes)
    {
        return baselineBytes == 0 ? 0d : ((double)alternativeBytes - baselineBytes) / baselineBytes * 100d;
    }

    private struct RegionTotals
    {
        public long PostingLists;
        public long EncodedBytes;
        public long Bp128Bytes;
        public long StreamVByteBytes;
        public long CheckpointedPostingLists;
        public long CheckpointedEncodedBytes;
    }
}


