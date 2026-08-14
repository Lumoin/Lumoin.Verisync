using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Validation and equality coverage for the <see cref="ReconciliationDrop"/> remove-push payload. The drop is
/// a validating-message record carrying the dots whose entries the receiver must drop, so the tests pin its
/// construction guards (a default, empty, mis-sized-replica, sub-one-counter, or duplicate-bearing dot array
/// each fails closed) and its custom ORDER-INDEPENDENT value equality: two drops built from the same set of
/// dots in different orders, across independently allocated replica buffers, are equal and hash-equal, while
/// drops over different dot sets are unequal. Equality is over the set of (replica bytes, counter) pairs, not
/// the array's reference or order, because the synthesized record equality would compare the array by reference.
/// </summary>
[TestClass]
internal sealed class ReconciliationDropTests
{
    [TestMethod]
    public void ConstructionRejectsBadDotArrays()
    {
        //A default array is not a constructed message and fails closed, as an empty fetch does.
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDrop(default));

        //An empty drop carries nothing to remove and is not a valid message.
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDrop([]));

        //A dot whose replica is not the fixed 32-byte width cannot name a replica.
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDrop([new DotState(ReplicaBytes(1, 31), 1)]));

        //A counter below one is never a minted dot; a dot is minted past zero.
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDrop([new DotState(ReplicaBytes(1), 0)]));

        //Two dots with the same replica AND counter are the same dot; a duplicate fails closed.
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDrop([new DotState(ReplicaBytes(1), 7), new DotState(ReplicaBytes(1), 7)]));
    }


    [TestMethod]
    public void ValidDropExposesItsDots()
    {
        DotState first = new(ReplicaBytes(1), 1);
        DotState second = new(ReplicaBytes(2), 4);

        ReconciliationDrop drop = new([first, second]);

        Assert.HasCount(2, drop.Dots);
        Assert.Contains(first, drop.Dots);
        Assert.Contains(second, drop.Dots);
    }


    [TestMethod]
    public void EqualityIsOrderIndependentAndAcrossIndependentBuffers()
    {
        //Two drops built from the SAME set of dots in DIFFERENT ORDER, each dot over an independently allocated
        //replica buffer, are equal and hash-equal: equality is the set of (replica, counter) pairs, not order.
        ReconciliationDrop forward = new(
        [
            new DotState(ReplicaBytes(1), 3),
            new DotState(ReplicaBytes(2), 5),
            new DotState(ReplicaBytes(3), 9)
        ]);

        ReconciliationDrop reordered = new(
        [
            new DotState(ReplicaBytes(3), 9),
            new DotState(ReplicaBytes(1), 3),
            new DotState(ReplicaBytes(2), 5)
        ]);

        Assert.AreEqual(forward, reordered);
        Assert.AreEqual(forward.GetHashCode(), reordered.GetHashCode());
    }


    [TestMethod]
    public void DropsOverDifferentDotSetsAreUnequal()
    {
        ReconciliationDrop drop = new([new DotState(ReplicaBytes(1), 3), new DotState(ReplicaBytes(2), 5)]);

        //A different counter on a shared replica is a different dot, so the sets differ.
        ReconciliationDrop differentCounter = new([new DotState(ReplicaBytes(1), 3), new DotState(ReplicaBytes(2), 6)]);
        Assert.AreNotEqual(drop, differentCounter);

        //A different replica is a different dot, so the sets differ.
        ReconciliationDrop differentReplica = new([new DotState(ReplicaBytes(1), 3), new DotState(ReplicaBytes(4), 5)]);
        Assert.AreNotEqual(drop, differentReplica);

        //A subset is not the same set, so a drop missing a dot differs.
        ReconciliationDrop subset = new([new DotState(ReplicaBytes(1), 3)]);
        Assert.AreNotEqual(drop, subset);
    }


    /// <summary>
    /// Builds the fixed 32-byte (ReplicaId.Size) replica bytes for a deterministic id, without System.Random
    /// (CA5394): the seed byte sits at position zero so distinct seeds yield distinct replicas.
    /// </summary>
    private static ImmutableArray<byte> ReplicaBytes(byte seed)
    {
        return ReplicaBytes(seed, ReplicaId.Size);
    }


    /// <summary>
    /// Builds replica bytes of an arbitrary length for the width-validation case; only ReplicaId.Size is valid.
    /// </summary>
    private static ImmutableArray<byte> ReplicaBytes(byte seed, int length)
    {
        byte[] bytes = new byte[length];
        bytes[0] = seed;

        return ImmutableArray.Create(bytes);
    }
}
