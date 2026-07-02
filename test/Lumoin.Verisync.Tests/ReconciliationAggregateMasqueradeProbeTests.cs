using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Probe pinning the checksum-review finding "documented masquerade bound is per-cell; reconciliation-level
/// false-accept scales with peel count". The decoder documents a PER-CELL masquerade bound (Decoder.cs:18,
/// Contract.cs:69) and the existing <c>MasqueradeIsWidthBoundedAndKeyRefuses</c> exercises exactly one
/// hand-crafted degree-two collision. This drives a WHOLE decode of a many-item difference under a
/// deliberately weak checksum width (the sanctioned adversarial-test width one, well-known key, honest trust
/// domain — no crafted collision, no secret-key path) and counts decoded items that were in NEITHER set:
/// "phantom" decodes, each the sum of a degree-two-or-higher cell that passed the purity gate at
/// <see cref="ReconciliationDecoder"/> Decoder.cs:204. A per-cell reading predicts about one collision per
/// cell; the union over the O(D ln D) peel produces many, which is the finding's point. The count is
/// deterministic (counter-derived items, no <see cref="Random"/>), so the assertion is not flaky.
/// </summary>
[TestClass]
internal sealed class ReconciliationAggregateMasqueradeProbeTests
{
    private static ReconciliationContract WeakWellKnown { get; } =
        ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 1, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);


    [TestMethod]
    public void AggregateFalseAcceptScalesWithPeelCountAtWeakWidth()
    {
        //The local set is a run of distinct eight-byte items; the peer is empty, so the true symmetric
        //difference is exactly this set and any decoded item outside it is a spurious phantom.
        const int itemCount = 800;
        const int symbolCount = 2400;

        var trueItems = new HashSet<string>(StringComparer.Ordinal);
        using ReconciliationEncoder left = new(WeakWellKnown, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        using ReconciliationEncoder emptyPeer = new(WeakWellKnown, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);

        for(long counter = 1; counter <= itemCount; counter++)
        {
            byte[] item = BitConverter.GetBytes(counter);
            trueItems.Add(Convert.ToHexString(item));
            left.Add(item);
        }

        //Absorb a fixed prefix rather than stopping at completion: phantoms injected mid-decode corrupt the
        //peel, so IsComplete is not a reliable stop, and more absorbed cells means more degree-two purity
        //checks, which is exactly the peel-count union the finding describes. Over-absorbing is legal.
        using ReconciliationDecoder decoder = new(WeakWellKnown, BaseMemoryPool.Shared);
        for(int n = 0; n < symbolCount; n++)
        {
            decoder.Absorb(left.ProduceNext().Combine(emptyPeer.ProduceNext()));
        }

        int phantomCount = 0;
        foreach(ReadOnlyMemory<byte> decoded in decoder.DecodedItems)
        {
            if(!trueItems.Contains(Convert.ToHexString(decoded.Span)))
            {
                phantomCount++;
            }
        }

        string line = $"itemCount={itemCount} symbolCount={symbolCount} decoded={decoder.DecodedItems.Count} phantom={phantomCount}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "verisync-aggregate-masquerade-probe.txt"), line);

        //A phantom is a decoded item present in neither set: the XOR of two-or-more real items whose folded
        //width-one checksum happened to match the checksum of their XOR. The per-cell wording bounds one
        //such cell; this count is the union over the whole decode.
        Assert.IsGreaterThan(0, phantomCount, $"Expected aggregate false-accepts over the decode. {line}");
    }
}
