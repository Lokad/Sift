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

try
{
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
    return Environment.ExitCode;
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void RunSyntheticBenchmarks(BenchmarkOptions options)
{
    var corpusPath = Path.Combine(Path.GetTempPath(), "sift-bench", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(corpusPath);

    try
    {
        using var index = new SiftIndex(corpusPath, new SiftOptions
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
                overlay.Update(
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
                        MaxMatchedDocuments = int.MaxValue,
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
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "bench", "indexes");
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
        .WithArtifactsPath(Path.Combine(GetRepositoryRoot(), "bench", "out", "bdn"));

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
    var corpusRoot = options.RealCorpusRoot ?? Path.Combine(repoRoot, "bench", "repos");
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "bench", "indexes");
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
        results.ToArray(),
        GetBenchmarkAssemblyHash());

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

        var mappedBytesBefore = SiftProcessMetrics.MappedSegmentBytes;
        var openStart = Stopwatch.GetTimestamp();
        using var index = new SiftIndex(benchmarkRoot, new SiftOptions());
        using var snapshot = index.OpenSnapshot(new SearchOptions(BenchmarkSearchParallelism));
        var openElapsed = Stopwatch.GetElapsedTime(openStart);
        var mappedBytesDelta = SiftProcessMetrics.MappedSegmentBytes - mappedBytesBefore;

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

    var mappedBytesBefore = SiftProcessMetrics.MappedSegmentBytes;
    var openStart = Stopwatch.GetTimestamp();
    using var index = new SiftIndex(cachePath, new SiftOptions());
    using var snapshot = index.OpenSnapshot(new SearchOptions(BenchmarkSearchParallelism));
    var openElapsed = Stopwatch.GetElapsedTime(openStart);
    var mappedBytesDelta = SiftProcessMetrics.MappedSegmentBytes - mappedBytesBefore;

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
    var sourceRevision = GetSourceRevision(corpus.Path);
    var builderAssemblyHash = GetBenchmarkAssemblyHash();
    var buildStart = Stopwatch.GetTimestamp();
    var siftOptions = new SiftOptions();
    using var source = new FileSystemDocumentSource(
        corpus.Path, indexRoot, siftOptions.MaxDocumentBytes, options.MaxFiles,
        new HashSet<string>([".git", ".hg", ".svn", "node_modules", "bin", "obj"], StringComparer.OrdinalIgnoreCase));
    using var index = new SiftIndex(indexRoot, siftOptions);
    var buildMeasurement = MeasureManagedMemory(() => index.Build(source).GetAwaiter().GetResult());
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
        buildStats.SubmittedBytes + source.UnmaterializedRejectedBytes,
        buildStats.ContentBytes,
        indexBytes,
        inflationRatio,
        inflationPercent,
        buildElapsed.TotalMilliseconds,
        buildDocsPerSecond,
        buildBytesPerSecond,
        buildMeasurement.PeakManagedBytes,
        options.MaxFiles,
        CountSegments(indexRoot),
        sourceRevision,
        builderAssemblyHash);
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
            using var index = new SiftIndex(workRoot, new SiftOptions());
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
        using var index = new SiftIndex(workRoot, new SiftOptions());

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
            using var index = new SiftIndex(workRoot, new SiftOptions());
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
            var compactMetrics = MeasureManagedMemory(() => index.Compact(new CompactOptions
            {
                MaxMergedSegments = 16,
                DeadFractionThreshold = 0.0,
        MinimumSegmentCountForSmallMerge = 1,
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
        using var index = new SiftIndex(workRoot, new SiftOptions());

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
            var compactMetrics = MeasureManagedMemory(() => index.Compact(new CompactOptions
            {
                MaxMergedSegments = 16,
                DeadFractionThreshold = 0.0,
                MinimumSegmentCountForSmallMerge = 1,
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
    Dictionary<SegmentId, long> beforeSegmentSizes,
    Dictionary<SegmentId, long> afterSegmentSizes,
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

static (T Stats, long PeakManagedBytes) MeasureManagedMemory<T>(Func<T> run)
{
    var sampledPeak = GC.GetTotalMemory(forceFullCollection: false);
    using var cts = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            sampledPeak = Math.Max(sampledPeak, GC.GetTotalMemory(forceFullCollection: false));
            try { await Task.Delay(5, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
        }
    });

    T stats;
    long finalBytes;
    try
    {
        stats = run();
        finalBytes = GC.GetTotalMemory(forceFullCollection: false);
    }
    finally
    {
        cts.Cancel();
        sampler.GetAwaiter().GetResult();
    }
    return (stats, Math.Max(sampledPeak, finalBytes));
}

static Dictionary<SegmentId, long> GetSegmentSizes(CorpusStorage storage, CorpusManifest manifest)
{
    var sizes = new Dictionary<SegmentId, long>(manifest.Segments.Count);
    for (var i = 0; i < manifest.Segments.Count; i++)
    {
        var entry = manifest.Segments[i];
        var path = Path.Combine(storage.SegmentsPath, entry.SegmentId.ToString("X16"), "segment.pack");
        sizes[entry.SegmentId] = File.Exists(path) ? new FileInfo(path).Length : 0L;
    }

    return sizes;
}

static long SumRemovedSegmentBytes(CorpusManifest beforeManifest, CorpusManifest afterManifest, Dictionary<SegmentId, long> beforeSegmentSizes)
{
    var afterIds = new HashSet<SegmentId>(afterManifest.Segments.Count);
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

static long SumAddedSegmentBytes(CorpusManifest beforeManifest, CorpusManifest afterManifest, Dictionary<SegmentId, long> afterSegmentSizes)
{
    var beforeIds = new HashSet<SegmentId>(beforeManifest.Segments.Count);
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
        for (uint ordinal = 0; ordinal < segment.DocumentCount; ordinal++)
        {
            if (entry.DeadOrdinals.Contains(ordinal))
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
        explain.RegexRouteReason.ToString(),
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
        completedStats.NonBacktrackingRegexInvocations,
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
    SearchQuery query,
    PathFilter pathFilter,
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
        explain.RegexRouteReason.ToString(),
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
        0,
        SearchStopReason.CollectorStopped.ToString(),
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

    if (!needsBuild)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (metadata.CacheVersion != 1 || metadata.CorpusId != corpus.Id || metadata.MaxFiles != options.MaxFiles ||
            string.IsNullOrWhiteSpace(metadata.CorpusPath) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(metadata.CorpusPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpus.Path)), comparison) ||
            metadata.Build.SourceRevision != GetSourceRevision(corpus.Path))
        {
            throw new InvalidOperationException(
                $"Cached benchmark index '{cachePath}' has different or obsolete source provenance. " +
                "Rebuild with --refresh-index without --query-only.");
        }
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
            buildMetrics,
            CacheVersion: 1);
        WriteCachedIndexMetadata(cachePath, metadata);
    }

    return new CachedIndexState(cachePath, metadata);
}

static void RunUpdateBenchmarks(BenchmarkOptions options)
{
    var repoRoot = GetRepositoryRoot();
    var corpusRoot = options.RealCorpusRoot ?? Path.Combine(repoRoot, "bench", "repos");
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "bench", "indexes");
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
        results.ToArray(),
        GetBenchmarkAssemblyHash());

    if (options.JsonOutputPath is not null)
    {
        EnsureParentDirectory(options.JsonOutputPath);
        File.WriteAllText(options.JsonOutputPath, JsonSerializer.Serialize(run, CreateJsonOptions()));
    }
}

static void RunCompactBenchmarks(BenchmarkOptions options)
{
    var repoRoot = GetRepositoryRoot();
    var corpusRoot = options.RealCorpusRoot ?? Path.Combine(repoRoot, "bench", "repos");
    var indexCacheRoot = options.IndexCacheRoot ?? Path.Combine(repoRoot, "bench", "indexes");
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
        results.ToArray(),
        GetBenchmarkAssemblyHash());

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

static string GetBenchmarkAssemblyHash()
{
    using var stream = File.OpenRead(typeof(SiftIndex).Assembly.Location);
    return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
}

static string? GetSourceRevision(string corpusPath)
{
    var start = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in new[] { "-C", Path.GetFullPath(corpusPath), "rev-parse", "HEAD" })
        start.ArgumentList.Add(argument);
    try
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("Timed out reading benchmark source revision.");
        }
        _ = error.GetAwaiter().GetResult();
        return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
    }
    catch (System.ComponentModel.Win32Exception) { return null; } // Git is optional for non-repository corpora.
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
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
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

