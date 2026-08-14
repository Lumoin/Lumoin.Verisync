using Lumoin.Base;
using Lumoin.Verisync.Core;
using System;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Pins the decoder's per-decode masquerade-bound surface: <see cref="ReconciliationDecoder.PurityCheckCount"/>
/// counts the non-neutral purity evaluations a decode runs (the union-bound multiplier), and
/// <see cref="ReconciliationDecoder.FalseDecodeProbabilityBound"/> is that count against the per-cell bound
/// <c>2^(−8·ChecksumWidth)</c>, clamped to one. Covers a fresh decoder's zero baseline, a real
/// production-width-eight decode whose bound is the exact <c>PurityCheckCount·2^(−64)</c> and negligibly small,
/// and a deliberately weak factory-width-one decode driven far enough to saturate the bound to one.
/// </summary>
[TestClass]
internal sealed class ReconciliationDecodeBoundTests
{
    private static ReconciliationContract ProductionContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    private static ReconciliationContract NarrowContract { get; } =
        ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 1, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);


    [TestMethod]
    public void FreshDecoderHasNoPurityChecksAndAVacantBound()
    {
        using ReconciliationDecoder decoder = new(ProductionContract, BaseMemoryPool.Shared);

        Assert.AreEqual(0L, decoder.PurityCheckCount);
        Assert.AreEqual(0.0, decoder.FalseDecodeProbabilityBound);
    }


    [TestMethod]
    public void ProductionWidthDecodeExposesAnExactNegligibleBound()
    {
        //A five-item difference against an empty peer, decoded to completion at the production eight-byte width,
        //runs at least one non-neutral purity check and stays exact.
        using ReconciliationDecoder decoder = DecodeOverOneSidedDifference(ProductionContract, itemCount: 5, absorbCount: 200, stopAtCompletion: true);

        Assert.IsGreaterThan(0L, decoder.PurityCheckCount);

        //The bound is exactly the purity-check count scaled by the eight-byte per-cell bound, and at this width
        //it is far below any threshold a consumer would act on.
        Assert.AreEqual(Math.ScaleB((double)decoder.PurityCheckCount, -64), decoder.FalseDecodeProbabilityBound);
        Assert.IsLessThan(1e-12, decoder.FalseDecodeProbabilityBound);
    }


    [TestMethod]
    public void NarrowWidthDecodeSaturatesTheBoundToOne()
    {
        //At the factory one-byte width the per-cell bound is 2^-8, so once more than 256 purity checks run the
        //union bound reaches one and the clamp holds it there. Over-absorbing a fixed prefix is legal and drives
        //the count well past the clamp point.
        using ReconciliationDecoder decoder = DecodeOverOneSidedDifference(NarrowContract, itemCount: 300, absorbCount: 1000, stopAtCompletion: false);

        Assert.IsGreaterThan(256L, decoder.PurityCheckCount);
        Assert.AreEqual(1.0, decoder.FalseDecodeProbabilityBound);
    }


    /// <summary>
    /// Encodes a one-sided difference of itemCount distinct eight-byte items against an empty peer and absorbs
    /// the difference stream into a fresh decoder, either to completion or for a fixed prefix.
    /// </summary>
    /// <remarks>
    /// The encoders are disposed here because the decoder has already copied every absorbed symbol into its own
    /// cells and arena.
    /// </remarks>
    private static ReconciliationDecoder DecodeOverOneSidedDifference(ReconciliationContract contract, int itemCount, int absorbCount, bool stopAtCompletion)
    {
        using ReconciliationEncoder left = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        for(int i = 0; i < itemCount; i++)
        {
            left.Add(BitConverter.GetBytes((long)(i + 1)));
        }

        using ReconciliationEncoder empty = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);

        ReconciliationDecoder decoder = new(contract, BaseMemoryPool.Shared);
        for(int n = 0; n < absorbCount; n++)
        {
            decoder.Absorb(left.ProduceNext().Combine(empty.ProduceNext()));
            if(stopAtCompletion && decoder.IsComplete)
            {
                break;
            }
        }

        return decoder;
    }
}
