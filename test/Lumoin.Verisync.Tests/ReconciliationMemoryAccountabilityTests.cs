using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The fail-closed governance gate for the reconciliation tier's pooled memory: a <see cref="MeterListener"/>
/// subscribed to <see cref="BaseMemoryPoolMetrics.MeterName"/> sums the pool's rent and return counters across
/// a full session, and after every pooled object is disposed the rented count must equal the returned count and
/// exceed zero, so every pooled backing was returned. One test drives a direct encode, stream, decode exchange
/// over a fresh <see cref="BaseMemoryPool"/>; the other drives the two-session in-memory reconcile with a pool
/// injected into both sessions. A rented/returned imbalance is a leaked rental — a hard CI failure, never
/// tolerated.
/// </summary>
/// <remarks>
/// Both tests observe the pool's process-global rental instruments, so the class is marked
/// <see cref="DoNotParallelizeAttribute"/> to keep their measurement totals free of rentals emitted by other
/// pool-using tests running concurrently — the same isolation the other metric-observing suites use.
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class ReconciliationMemoryAccountabilityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int TriggerCap = 100;

    private const int DefaultBatchSize = 4;

    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    private static ReconciliationContract ContentHashContract { get; } = ReconciliationContract.ContentHashDefault;

    private static byte[] A1 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] A2 { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] A3 { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

    private static byte[] B1 { get; } = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];

    private static string[] ExpectedConverged { get; } = [.. new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" }.Order()];

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void OneEncodeStreamDecodeSessionLeaksNoPooledRentals()
    {
        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            //A small structural difference over the well-known key: the left set holds A1, A2, B1, the right
            //holds A1, A2, A3, so the symmetric difference is the two items B1 and A3. Both encoders and the
            //decoder rent their cell backings from the same pool inside this scope.
            using ReconciliationEncoder left = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, cellCapacityHint: 0);
            left.Add(A1);
            left.Add(A2);
            left.Add(B1);

            using ReconciliationEncoder right = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, cellCapacityHint: 0);
            right.Add(A1);
            right.Add(A2);
            right.Add(A3);

            using ReconciliationDecoder decoder = new(StructuralContract, pool, cellCapacityHint: 0);

            //Absorb the symbol-wise difference until the peel completes; the cap is generous for a difference of
            //two and the loop stops the moment cell zero clears.
            const int Cap = 200;
            for(int n = 0; n < Cap && !decoder.IsComplete; n++)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            }

            Assert.IsTrue(decoder.IsComplete);
            Assert.HasCount(2, decoder.DecodedItems);
        }

        //After the using-scope disposes both encoders, the decoder, and the pool, the rental ledger must
        //balance: the net active gauge returns to zero and the rented count equals the returned count and is
        //strictly positive, proving every pooled cell backing was returned.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public void AThrowingSecondRentReturnsTheFirstRentalRatherThanLeakingIt()
    {
        //The cell buffer rents its two backings as a pair. A capacity strategy that yields a valid slab for the
        //sum backing's rent size but zero segments for the checksum backing's size forces the SECOND rent to
        //throw after the first has already taken a live segment. The buffer must return that first rental on the
        //failure path rather than orphan it, or the accountability invariant the whole tier rests on is broken.
        const int SumWidth = 16;
        const int ChecksumWidth = 8;
        const int InitialCapacity = 4;
        const int ChecksumRentSize = InitialCapacity * ChecksumWidth;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using var meter = new Meter(BaseMemoryPoolMetrics.MeterName);
            using BaseMemoryPool pool = new(meter, capacityStrategy: size => size == ChecksumRentSize ? 0 : 4);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                using ReconciliationCellBuffer buffer = new(SumWidth, ChecksumWidth, pool, cellCapacityHint: 0);
            });
        }

        //The first (sum) rental succeeded and the second (checksum) rent threw during slab construction; the net
        //active gauge must still balance to zero and the rented count equal the returned count, proving the first
        //rental was returned on the exception path instead of leaking.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public void EncoderAndDecoderArenaGrowthLeavesNoActiveRentals()
    {
        const int Stride = 8;

        //A shared corpus plus a per-side surplus, all distinct: the left set is the shared corpus and a few
        //hundred left-only items, the right set is the shared corpus and a few hundred right-only items, so the
        //symmetric difference the decoder recovers is the few-hundred-item left surplus and the few-hundred-item
        //right surplus. A corpus and difference this large force both the cell buffers AND the arenas across
        //several doubling block grows on the pooled path, with a zero hint pinning the small initial blocks.
        const int SharedCount = 200;
        const int LeftOnlyCount = 200;
        const int RightOnlyCount = 200;
        const int ExpectedDecoded = LeftOnlyCount + RightOnlyCount;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            using ReconciliationEncoder left = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, cellCapacityHint: 0);
            using ReconciliationEncoder right = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, cellCapacityHint: 0);

            for(int n = 0; n < SharedCount; n++)
            {
                byte[] shared = BuildArenaItem(Stride, 0, n);
                left.Add(shared);
                right.Add(shared);
            }

            for(int n = 0; n < LeftOnlyCount; n++)
            {
                left.Add(BuildArenaItem(Stride, 1, n));
            }

            for(int n = 0; n < RightOnlyCount; n++)
            {
                right.Add(BuildArenaItem(Stride, 2, n));
            }

            using ReconciliationDecoder decoder = new(StructuralContract, pool, cellCapacityHint: 0);

            //The cap is generous for a difference of a few hundred; the loop stops the moment the peel completes.
            const int Cap = 4000;
            for(int n = 0; n < Cap && !decoder.IsComplete; n++)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            }

            Assert.IsTrue(decoder.IsComplete);
            Assert.HasCount(ExpectedDecoded, decoder.DecodedItems);
        }

        //After the using-scope disposes both encoders, the decoder, and the pool, every block both the cell
        //buffers and the arenas grew through must have been returned: the net active gauge balances to zero and
        //the rented count equals the returned count and is strictly positive. This is the explicit arena-growth
        //balance check the two byte-vector tests only cover implicitly.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public async Task TwoSessionReconcileLeavesNoActiveRentalsAfterDisposal()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
            OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
            OrSet<string> responderSet = ancestor.Add("zeta", R3);

            ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
            ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

            //Inject the pool into both sessions through the pool-bearing constructor; each session builds its
            //encoder (both roles) and decoder (initiator) over the pool and disposes them when the session is
            //disposed at the end of this scope.
            using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, DefaultBatchSize, pool);
            using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, DefaultBatchSize, pool);

            Dictionary<string, string> initiatorDirectory = BuildHashDirectory(initiatorSet);
            Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);
            HashSet<string> initiatorHexes = [.. initiatorItems.Select(item => Convert.ToHexString(item.Span))];

            //The initiator partitions the decoded difference into a fetch for digests it lacks and a push for
            //digests it holds in surplus; the responder serves fetches and both sides apply received elements.
            ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            {
                ImmutableArray<ReadOnlyMemory<byte>>.Builder fetch = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
                ImmutableArray<ReconciliationElementEntry<string>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<string>>();
                foreach(ReadOnlyMemory<byte> item in decoded)
                {
                    string hex = Convert.ToHexString(item.Span);
                    if(initiatorHexes.Contains(hex))
                    {
                        push.Add(new ReconciliationElementEntry<string>(item, initiatorDirectory[hex]));
                    }
                    else
                    {
                        fetch.Add(item);
                    }
                }

                return new ReconciliationDifferenceResolution<string>(fetch.ToImmutable(), push.ToImmutable());
            };

            ServeReconciliationFetchDelegate<string> serve = items =>
                [.. items.Select(item => new ReconciliationElementEntry<string>(item, responderDirectory[Convert.ToHexString(item.Span)]))];

            OrSet<string> initiatorResult = initiatorSet;
            ApplyReconciliationElementsDelegate<string> applyToInitiator = (entries, _, ct) =>
            {
                foreach(ReconciliationElementEntry<string> entry in entries)
                {
                    initiatorResult = initiatorResult.Add(entry.Element, R2);
                }

                return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
            };

            OrSet<string> responderResult = responderSet;
            ApplyReconciliationElementsDelegate<string> applyToResponder = (entries, _, ct) =>
            {
                foreach(ReconciliationElementEntry<string> entry in entries)
                {
                    responderResult = responderResult.Add(entry.Element, R3);
                }

                return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
            };

            Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, applyToInitiator, cancellationToken: cancellationToken);
            Task responderRun = responder.RunAsync(Forward(initiator), null, serve, applyToResponder, cancellationToken: cancellationToken);

            await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

            responder.Complete();
            await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

            //The exchange converges, which proves the pooled cell backings carried the reconciliation correctly,
            //not merely that they balanced.
            Assert.AreSequenceEqual(ExpectedConverged, Sorted(initiatorResult));
            Assert.AreSequenceEqual(ExpectedConverged, Sorted(responderResult));
            Assert.HasCount(3, initiator.DecodedItems);
        }

        //Both sessions and the pool are disposed at the end of the scope, so every rental the sessions took for
        //their encoders and decoder is returned: the net active gauge balances to zero.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    private static async Task PaceUntilInitiatorCompletesAsync(AntiEntropySession<string> initiator, AntiEntropySession<string> responder, CancellationToken cancellationToken)
    {
        int triggers = 0;
        while(initiator.State != AntiEntropySessionState.Completed)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            triggers++;
            Assert.IsLessThan(TriggerCap, triggers, "The initiator never completed within the trigger cap.");
        }
    }


    private static SendReconciliationEnvelopeDelegate<string> Forward(AntiEntropySession<string> peer)
    {
        return (envelope, cancellationToken) => ForwardTo(peer, envelope, cancellationToken);
    }


    private static ValueTask ForwardTo(AntiEntropySession<string> peer, ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        try
        {
            return peer.SubmitAsync(envelope, cancellationToken);
        }
        catch(ChannelClosedException)
        {
            //A completed peer is a wound-down session; dropping the late send is exactly the transport's behaviour.
            return ValueTask.CompletedTask;
        }
    }


    private static ReadOnlyMemory<byte>[] ProjectHashes(OrSet<string> set)
    {
        List<ReadOnlyMemory<byte>> items = [];
        foreach(string element in set.Elements)
        {
            items.Add(SHA256.HashData(Encoding.UTF8.GetBytes(element)));
        }

        return [.. items];
    }


    private static Dictionary<string, string> BuildHashDirectory(OrSet<string> set)
    {
        Dictionary<string, string> directory = [];
        foreach(string element in set.Elements)
        {
            directory[Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element)))] = element;
        }

        return directory;
    }


    private static string[] Sorted(OrSet<string> set)
    {
        return [.. set.Elements.Order()];
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// Builds a deterministic, distinct item of the given width without System.Random (CA5394).
    /// </summary>
    /// <remarks>
    /// The group tag in the leading byte separates the shared, left-only, and right-only corpora, and the
    /// little-endian index in the following bytes makes every item within a group distinct across the
    /// few-hundred-item range; the remaining stride carries a position-derived tail. Distinct (group, index)
    /// pairs therefore yield distinct full-width items, so no enforcement is needed to keep them apart.
    /// </remarks>
    private static byte[] BuildArenaItem(int stride, int group, int index)
    {
        byte[] item = new byte[stride];
        item[0] = (byte)(0xA0 + group);
        long value = index;
        for(int b = 1; b < stride; b++)
        {
            item[b] = (byte)((value & 0xFF) ^ (byte)(b * 13));
            value >>= 8;
        }

        return item;
    }


}
