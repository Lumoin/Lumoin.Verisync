using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Adversarial probe for the finding that "a walk-divergent peer peels shared items past the checksum gate",
/// producing a decode that IsComplete accepts yet is NOT the true symmetric difference (a wrong complete
/// decode short of a checksum collision, violating the C1 claim).
///
/// The finding concedes it is not triggerable within one build: the shipped encoder and decoder both call the
/// single static <see cref="ReconciliationIndexWalk"/>, so no data input forks the walk. This probe therefore
/// SIMULATES a hypothetical second build whose index walk uses a different geometric-gap constant (the finding's
/// own "1.5 -> 2.0" example) while sharing the checksum key and field widths, so <see cref="ReconciliationOffer.Matches"/>
/// still returns true. Peer A (local) and the decoder run the real shipped walk; peer B is hand-encoded with the
/// forked walk.
///
/// The load-bearing question is whether the divergence yields a WRONG COMPLETE decode (the finding's claim) or
/// merely an INCOMPLETE decode (a legal, safe outcome). Because every item's walk visits index zero, the decoder
/// folds every item it decodes - including any spuriously-peeled shared item - back into cell zero. A decode that
/// leaked shared items therefore leaves cell zero non-neutral (its sum is the XOR of the leaked items), so
/// <see cref="ReconciliationDecoder.IsComplete"/> cannot fire on it. The safe failure mode is non-completion,
/// not a wrong accept.
/// </summary>
[TestClass]
internal sealed class WalkVersionDivergenceProbe
{
    private static ReconciliationContract Contract { get; } = ReconciliationContract.ContentHashDefault;

    /// <summary>
    /// The real walk key, reused unchanged by the forked walk: the finding's scenario is a peer that shares the
    /// checksum key and the walk key but differs only in the gap constant.
    /// </summary>
    private const ulong WalkKeyLow = 0x636E797369726576UL;
    private const ulong WalkKeyHigh = 0x31302D6B6C61772DUL;

    private static byte[] HashA { get; } = Digest("a");
    private static byte[] HashB { get; } = Digest("b");
    private static byte[] HashC { get; } = Digest("c");
    private static byte[] HashD { get; } = Digest("d");
    private static byte[] HashE { get; } = Digest("e");


    [TestMethod]
    public void SharedWalkDecodesExactlyTheSymmetricDifference()
    {
        //Positive control: the shipped single-walk build. Both peers use the real encoder, so shared items map
        //to identical cells on both sides and cancel; only the genuine differences d and e are decoded.
        byte[][] left = [HashA, HashB, HashC, HashD];
        byte[][] right = [HashA, HashB, HashC, HashE];

        using ReconciliationEncoder a = LoadEncoder(Contract, left);
        using ReconciliationEncoder b = LoadEncoder(Contract, right);
        using ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);

        const int cap = 256;
        for(int i = 0; i < cap && !decoder.IsComplete; i++)
        {
            decoder.Absorb(a.ProduceNext().Combine(b.ProduceNext()));
        }

        Assert.IsTrue(decoder.IsComplete, "The shipped single-walk reconciliation completes.");

        HashSet<string> decoded = [.. decoder.DecodedItems.Select(m => ToHex(m.Span))];
        HashSet<string> trueDiff = [ToHex(HashD), ToHex(HashE)];
        Assert.IsTrue(decoded.SetEquals(trueDiff), "The shipped build decodes exactly the true symmetric difference; shared items never leak.");
    }


    [TestMethod]
    public void ForkedWalkPeerNeverProducesAWrongCompleteDecode()
    {
        //Simulate a walk-divergent SECOND BUILD: peer B is hand-encoded with a forked gap constant while
        //sharing the checksum key and widths, so the wire negotiation cannot see the fork.
        Assert.IsTrue(ReconciliationOffer.FromContract(Contract).Matches(Contract), "A shared checksum key makes the offer match; the walk fork is invisible to the negotiation.");

        byte[][] left = [HashA, HashB, HashC, HashD];
        byte[][] right = [HashA, HashB, HashC, HashE];
        HashSet<string> trueDiff = [ToHex(HashD), ToHex(HashE)];
        HashSet<string> sharedHex = [ToHex(HashA), ToHex(HashB), ToHex(HashC)];

        const int cap = 512;
        using ReconciliationEncoder a = LoadEncoder(Contract, left);
        ReconciliationSymbol[] b = ForkedEncode(Contract, right, cap, gapConstant: 3.0);
        using ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);

        bool everCompletedWrong = false;
        bool sharedLeaked = false;
        bool completed = false;
        for(int i = 0; i < cap; i++)
        {
            decoder.Absorb(a.ProduceNext().Combine(b[i]));

            HashSet<string> decodedNow = [.. decoder.DecodedItems.Select(m => ToHex(m.Span))];
            if(decodedNow.Overlaps(sharedHex))
            {
                //The forked walk did cause at least one shared item to peel as a spurious difference: the
                //finding's mechanism fires. The question is whether completion can accept it.
                sharedLeaked = true;
            }

            if(decoder.IsComplete)
            {
                completed = true;
                if(!decodedNow.SetEquals(trueDiff))
                {
                    everCompletedWrong = true;
                }

                break;
            }
        }

        //The core soundness claim: a wrong decode never completes. Under the forked walk the decoder either
        //stalls incomplete (legal, safe) or - if it completes - completes with exactly the true difference.
        Assert.IsFalse(everCompletedWrong, "A forked walk must never yield a WRONG complete decode; that would violate C1.");

        //Demonstrate the divergence is real and the safety comes from cell-zero pollution, not from the fork
        //being a no-op: the forked walk leaks shared items into the running decoded set, yet the decode does
        //NOT complete, because every leaked item re-touches cell zero and blocks IsComplete.
        Assert.IsTrue(sharedLeaked, "The forked walk actually diverges: shared items peel as spurious differences.");
        Assert.IsFalse(completed, "The forked walk cannot complete: a leaked shared item leaves cell zero non-neutral, so IsComplete stays false.");
    }


    private static ReconciliationSymbol[] ForkedEncode(ReconciliationContract contract, byte[][] items, int count, double gapConstant)
    {
        int itemWidth = contract.ItemWidth;
        int checksumWidth = contract.ChecksumWidth;

        byte[][] sums = new byte[count][];
        byte[][] checksums = new byte[count][];
        for(int i = 0; i < count; i++)
        {
            sums[i] = new byte[itemWidth];
            checksums[i] = new byte[checksumWidth];
        }

        foreach(byte[] item in items)
        {
            byte[] checksumBytes = new byte[checksumWidth];
            ulong checksum = ReconciliationChecksum.Compute(contract.ChecksumKeyLow, contract.ChecksumKeyHigh, item);
            ReconciliationChecksum.Write(checksum, checksumBytes);

            //The forked walk: identical start (index zero, same walk key), a different gap constant thereafter.
            long index = 0;
            ulong state = ReconciliationChecksum.Compute(WalkKeyLow, WalkKeyHigh, item);
            while(index < count)
            {
                int cell = (int)index;
                ReconciliationXor.Fold(sums[cell], item);
                ReconciliationXor.Fold(checksums[cell], checksumBytes);

                (index, state) = ForkedNext(index, state, gapConstant);
            }
        }

        ReconciliationSymbol[] symbols = new ReconciliationSymbol[count];
        for(int i = 0; i < count; i++)
        {
            symbols[i] = new ReconciliationSymbol(sums[i], checksums[i]);
        }

        return symbols;
    }


    private static (long Index, ulong State) ForkedNext(long index, ulong state, double gapConstant)
    {
        ulong newState = unchecked(state + 0x9E3779B97F4A7C15UL);
        ulong t = newState;
        t ^= t >> 30;
        t = unchecked(t * 0xBF58476D1CE4E5B9UL);
        t ^= t >> 27;
        t = unchecked(t * 0x94D049BB133111EBUL);
        t ^= t >> 31;
        double r = (t >> 11) * (1.0 / 9007199254740992.0);
        long gap = Math.Max(1L, (long)Math.Ceiling((gapConstant + index) * ((1.0 / Math.Sqrt(1.0 - r)) - 1.0)));

        return (index + gap, newState);
    }


    private static ReconciliationEncoder LoadEncoder(ReconciliationContract contract, byte[][] items)
    {
        ReconciliationEncoder encoder = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(byte[] item in items)
        {
            encoder.Add(item);
        }

        return encoder;
    }


    private static byte[] Digest(string element)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(element));
    }


    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes);
    }
}
