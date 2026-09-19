using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Verifies bit-for-bit agreement of the three decode branches within
/// ChunkDataLayer.CopyBlocksToUnsafe: the scalar fallback, AVX2 and AVX-512.
///
/// The scalar implementation is duplicated here as a reference; the two SIMD methods are invoked
/// via reflection on the private statics DecodePlanesAvx2 / DecodePlanesAvx512,
/// because the test project references only VintagestoryAPI and does not pull in VintagestoryLib
/// into signatures that xUnit reflects over during discovery.
///
/// Comparisons requiring an ISA absent from the current machine are skipped, so the test
/// stays green even on a CPU without AVX2/AVX-512. The scenario's goal is to catch divergence
/// between implementations rather than require a specific processor.
/// </summary>
public class ChunkDataLayerDecodeEquivalenceScenarios : AtlasScenarioBase
{
    private const int PlaneLength = 1024;   // 32768 / 32
    private const int OutputLength = 32768; // 32 * 32 * 32
    private const int BitsizeMin = 1;
    private const int BitsizeMax = 15;      // dataBits — int[15][], num limited to 15

    [AtlasScenario(TimeoutMs = 180_000)]
    public void CopyBlocksToUnsafe_DecodePaths_Should_Agree_BitForBit()
    {
        Type? layerType = Type.GetType("Vintagestory.Common.ChunkDataLayer, VintagestoryLib");
        Assert.True(layerType != null,
            "ChunkDataLayer not found in VintagestoryLib.");

        MethodInfo? avx2 = layerType!.GetMethod(
            "DecodePlanesAvx2", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? avx512 = layerType.GetMethod(
            "DecodePlanesAvx512", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(avx2 != null,
            "DecodePlanesAvx2 is missing in ChunkDataLayer: the SIMD branch was renamed or removed.");
        Assert.True(avx512 != null,
            "DecodePlanesAvx512 is missing in ChunkDataLayer: the SIMD branch was renamed or removed.");

        // The same set of cases is run for both available ISAs. The values don't depend on what
        // the CPU actually supports — the reference always holds.
        bool anyIsaChecked = false;

        foreach (DecodeCase c in BuildCases())
        {
            int[] reference = ScalarDecode(c.Planes, c.Palette, c.BitSize);

            if (Avx2.IsSupported)
            {
                int[] viaAvx2 = new int[OutputLength];
                // Signature: private static unsafe void DecodePlanesAvx2(int[] dst, int[][] planes, int[] palette, int num)
                avx2!.Invoke(null, new object[] { viaAvx2, c.Planes, c.Palette, c.BitSize });
                AssertSame(reference, viaAvx2, c.Label, "AVX2");
                anyIsaChecked = true;
            }

            if (Avx512F.IsSupported)
            {
                int[] viaAvx512 = new int[OutputLength];
                // Signature: private static unsafe void DecodePlanesAvx512(int[] dst, int[][] planes, int[] palette, int num)
                avx512!.Invoke(null, new object[] { viaAvx512, c.Planes, c.Palette, c.BitSize });
                AssertSame(reference, viaAvx512, c.Label, "AVX-512");
                anyIsaChecked = true;
            }
        }

        // Intentionally no Assert.Fail: on a CPU without SIMD the test must pass, just without comparisons.
        // We keep the message so the "quiet green" on a scalar machine stands out in the log.
        if (!anyIsaChecked)
        {
            Console.WriteLine(
                "[ChunkDataLayerDecodeEquivalence] neither AVX2 nor AVX-512 is supported: " +
                "SIMD comparisons skipped, only the scalar reference was run.");
        }
    }

    private static void AssertSame(int[] expected, int[] actual, string label, string variant)
    {
        Assert.True(expected.Length == actual.Length,
            $"[{label}] {variant} returned {actual.Length} values, expected {expected.Length}");

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.True(false,
                    $"decode divergence [{label}] {variant} vs scalar " +
                    $"at index {i} (word {i / 32}, bit {i % 32}): " +
                    $"scalar={expected[i]}, {variant}={actual[i]}");
            }
        }
    }

    // ==== Input generation ==================================================

    private readonly struct DecodeCase
    {
        public readonly string Label;
        public readonly int[][] Planes;
        public readonly int[] Palette;
        public readonly int BitSize;

        public DecodeCase(string label, int[][] planes, int[] palette, int bitsize)
        {
            Label = label;
            Planes = planes;
            Palette = palette;
            BitSize = bitsize;
        }
    }

    private static IEnumerable<DecodeCase> BuildCases()
    {
        // Two independent seeds are cheaper than running the test many times and cover more patterns
        // of mixed words, where the main transpose/gather logic actually lives.
        foreach (int seed in new[] { 0x5EED_5EED, 0x0BADCAFE })
        {
            var rng = new Random(seed);

            for (int bitsize = BitsizeMin; bitsize <= BitsizeMax; bitsize++)
            {
                int paletteLength = 1 << bitsize;
                int[] palette = new int[paletteLength];
                for (int i = 0; i < paletteLength; i++)
                {
                    palette[i] = NextFullRangeInt(rng);
                }

                // Completely random planes cover every edge case of mixed words on both SIMD branches.
                yield return new DecodeCase(
                    $"seed=0x{seed:X8} bitsize={bitsize} random planes",
                    BuildPlanes(bitsize, _ => NextFullRangeInt(rng)),
                    palette,
                    bitsize);

                // All planes = 0: decoding must yield palette[0] everywhere.
                yield return new DecodeCase(
                    $"seed=0x{seed:X8} bitsize={bitsize} all-zero planes",
                    BuildPlanes(bitsize, _ => 0),
                    palette,
                    bitsize);

                // All planes = -1: decoding must yield palette[2^bitsize - 1] everywhere;
                // this also hits the uniform-word fast path on both SIMD branches.
                yield return new DecodeCase(
                    $"seed=0x{seed:X8} bitsize={bitsize} all-ones planes",
                    BuildPlanes(bitsize, _ => -1),
                    palette,
                    bitsize);

                // Per-word uniform planes: each 32-block word yields one palette index, so a single
                // pass exercises both the uniform path and the mixed path.
                yield return new DecodeCase(
                    $"seed=0x{seed:X8} bitsize={bitsize} per-word uniform",
                    BuildPerWordUniformPlanes(bitsize, rng),
                    palette,
                    bitsize);
            }
        }
    }

    private static int[][] BuildPlanes(int bitsize, Func<int, int> wordFactory)
    {
        var planes = new int[bitsize][];
        for (int k = 0; k < bitsize; k++)
        {
            planes[k] = new int[PlaneLength];
            for (int w = 0; w < PlaneLength; w++)
            {
                planes[k][w] = wordFactory(k);
            }
        }
        return planes;
    }

    private static int[][] BuildPerWordUniformPlanes(int bitsize, Random rng)
    {
        var planes = new int[bitsize][];
        for (int k = 0; k < bitsize; k++) planes[k] = new int[PlaneLength];

        for (int w = 0; w < PlaneLength; w++)
        {
            int index = rng.Next(1 << bitsize);
            for (int k = 0; k < bitsize; k++)
            {
                planes[k][w] = ((index >> k) & 1) != 0 ? -1 : 0;
            }
        }
        return planes;
    }

    private static int NextFullRangeInt(Random rng)
    {
        // Random.Next() is non-negative; two long ranges cover the whole int range so that the
        // sign-extension paths in SIMD are exercised too.
        return unchecked((int)rng.NextInt64(int.MinValue, (long)int.MaxValue + 1));
    }

    // ==== Scalar reference =================================================
    /// <summary>
    /// Exact copy of the scalar CopyBlocksToUnsafe branch: word-major, bit-major,
    /// with the same check index &lt; palette.Length ? palette[index] : 0.
    /// Any divergence in the SIMD branches will show up as a byte-level mismatch of the arrays.
    /// </summary>
    private static int[] ScalarDecode(int[][] planes, int[] palette, int num)
    {
        var output = new int[OutputLength];
        int outIdx = 0;

        for (int word = 0; word < PlaneLength; word++)
        {
            for (int j = 0; j < 32; j++)
            {
                int index = 0;
                for (int k = 0; k < num; k++)
                {
                    index |= ((planes[k][word] >> j) & 1) << k;
                }
                output[outIdx++] = index < palette.Length ? palette[index] : 0;
            }
        }

        return output;
    }
}
