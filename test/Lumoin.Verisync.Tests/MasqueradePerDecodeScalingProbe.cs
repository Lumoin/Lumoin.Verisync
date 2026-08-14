using Lumoin.Base;
using Lumoin.Verisync.Core;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Adversarial probe for the peel-math finding that the documented masquerade bound is stated per
/// degree-two cell (probability bounded by the checksum width, i.e. 2^-8w) while the operative
/// per-decode false-peel probability scales with the difference size d, because a single decode runs
/// the sole gate <see cref="ReconciliationDecoder"/> IsCellPure on Theta(d.ln d) cell states.
///
/// The probe fixes a deliberately weak width-1 checksum (2^-8 per candidate) under the well-known key,
/// then sweeps the symmetric-difference size d and, for each d, runs many independent decodes over
/// distinct item sets. Each decode's recovered set is compared to the true difference; a decode that
/// emits ANY item not in the true difference is a per-decode false-accept (a wrong item peeled and
/// recorded in DecodedItems). The measured false-accept FRACTION per decode is the observable the
/// finding predicts grows with d, in contradiction to a constant per-decode reading of the width bound.
///
/// If the per-decode probability were "bounded by the checksum width" as a decode-level guarantee, the
/// fraction would sit near 2^-8 (0.39%) independent of d. The finding predicts instead that it climbs
/// roughly linearly with the number of degree-two purity checks. The probe asserts the qualitative
/// scaling (monotone rise with d, and a top-of-sweep fraction far above the per-candidate 2^-8), which
/// is exactly the claim the documentation never states.
/// </summary>
[TestClass]
internal sealed class MasqueradePerDecodeScalingProbe
{
    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// Structural items, 8 bytes wide, a width-1 checksum: the sanctioned adversarial-test width where a
    /// degree-two cell masquerades with per-candidate probability 2^-8, under the well-known (unkeyed) key
    /// so random collisions occur naturally rather than being brute-forced.
    /// </summary>
    private static ReconciliationContract WeakContract { get; } =
        ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 1, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    /// <summary>
    /// The difference sizes swept; d = 1 is the control (no degree-two cell can arise, so no masquerade).
    /// </summary>
    private static int[] DifferenceSizes { get; } = [1, 2, 4, 8, 16, 32];

    /// <summary>
    /// Independent decodes per size, each over a distinct disjoint item range so the sweep is a Monte-Carlo
    /// over which sets happen to carry a masquerade; fully deterministic (no System.Random).
    /// </summary>
    private const int TrialsPerSize = 250;

    private const double PerCandidateBound = 1.0 / 256.0;


    [TestMethod]
    public void PerDecodeFalseAcceptRateScalesWithDifferenceSize()
    {
        var fractions = new List<(int D, int FalseAccepts, int Incomplete, double Fraction)>();
        long rangeBase = 1L;
        foreach(int d in DifferenceSizes)
        {
            int falseAccepts = 0;
            int incomplete = 0;
            for(int trial = 0; trial < TrialsPerSize; trial++)
            {
                //A fresh disjoint block of d counters for this trial; distinct bytes give distinct walks
                //and distinct checksums, so each trial is an independent draw.
                long trialBase = rangeBase + ((long)trial * 1_000L);
                (bool sawPhantom, bool completed) = DecodeOnce(d, trialBase);
                if(sawPhantom)
                {
                    falseAccepts++;
                }

                if(!completed)
                {
                    incomplete++;
                }
            }

            double fraction = (double)falseAccepts / TrialsPerSize;
            fractions.Add((d, falseAccepts, incomplete, fraction));
            rangeBase += (long)TrialsPerSize * 1_000L * (d + 1);
        }

        //The rows are a comma-separated table, so the decimal separator has to be invariant or a locale that
        //writes decimal commas makes every row ambiguous.
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"columns: d, trials, falseAccepts, incomplete, perDecodeFalseAcceptFraction; perCandidate(2^-8)={PerCandidateBound:F5}"));
        foreach((int d, int fa, int inc, double frac) in fractions)
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{d}, {TrialsPerSize}, {fa}, {inc}, {frac:F4}"));
        }

        double controlFraction = fractions[0].Fraction;
        double topFraction = fractions[^1].Fraction;

        //The control (d = 1) never masquerades: a genuine degree-one cell always peels correctly, so
        //there is no false-accept at all when no degree-two cell can form.
        Assert.AreEqual(0.0, controlFraction, "d = 1 has no degree-two cell so must never false-accept.");

        //The top of the sweep must sit far above the per-candidate 2^-8 bound: this is the finding's
        //core claim, that the per-DECODE probability is not bounded by the checksum width but climbs
        //with the Theta(d.ln d) purity checks per decode.
        Assert.IsGreaterThan(10.0 * PerCandidateBound, topFraction,
            $"Per-decode false-accept fraction at d = {DifferenceSizes[^1]} was {topFraction:F4}, expected far above the per-candidate {PerCandidateBound:F5}.");

        //The fraction rises with d (monotone non-decreasing across the sweep once a degree-two cell can
        //form), demonstrating the linear-in-d scaling the documentation omits.
        for(int i = 2; i < fractions.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(fractions[i - 1].Fraction, fractions[i].Fraction,
                $"Fraction must not fall as d grows: d = {fractions[i - 1].D} gave {fractions[i - 1].Fraction:F4}, d = {fractions[i].D} gave {fractions[i].Fraction:F4}.");
        }
    }


    /// <summary>
    /// Encodes a size-d difference (d distinct items present on one side, none on the other), decodes it
    /// to the completion cap, and reports whether any decoded item is NOT a true difference item (a
    /// per-decode false-accept) and whether the decode reported completion.
    /// </summary>
    private static (bool SawPhantom, bool Completed) DecodeOnce(int d, long trialBase)
    {
        var truth = new HashSet<string>(d);
        using ReconciliationEncoder full = new(WeakContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        for(int i = 0; i < d; i++)
        {
            byte[] item = BitConverter.GetBytes(trialBase + i);
            full.Add(item);
            truth.Add(Convert.ToHexString(item));
        }

        using ReconciliationEncoder empty = new(WeakContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);

        int cap = 100 + (20 * d);
        using ReconciliationDecoder decoder = new(WeakContract, BaseMemoryPool.Shared);
        int absorbed = 0;
        while(!decoder.IsComplete && absorbed < cap)
        {
            decoder.Absorb(full.ProduceNext().Combine(empty.ProduceNext()));
            absorbed++;
        }

        bool sawPhantom = false;
        foreach(ReadOnlyMemory<byte> item in decoder.DecodedItems)
        {
            if(!truth.Contains(Convert.ToHexString(item.Span)))
            {
                sawPhantom = true;
                break;
            }
        }

        return (sawPhantom, decoder.IsComplete);
    }
}
