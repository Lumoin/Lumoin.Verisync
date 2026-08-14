using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Coverage for <see cref="ClusterId"/>, the chain identity a QuePaxa versioned register's configurations
/// carry. The fixed-width shape is pinned as <see cref="ReplicaId"/>'s is, and the genesis derivation is
/// pinned on the three properties the fail-closed genesis argument rests on: it is ORDER-SENSITIVE over the
/// member array, it is a function of the member bytes rather than of the buffers holding them, and its
/// canonical encoding is fixed, because two hosts that encode the same member list differently mint different
/// identities and block each other permanently.
/// </summary>
[TestClass]
internal sealed class ClusterIdTests
{
    /// <summary>
    /// The digest of the three-member genesis list below under the pinned canonical encoding: the domain
    /// separator, the member count as four big-endian bytes, then every member's bytes in array order.
    /// </summary>
    private const string PinnedGenesisDigest = "a8403cc0ad331152143610bca35b37125c0ec07895b99a241104cf3672412d56";


    [TestMethod]
    public void FromSpanRejectsWrongLength()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ClusterId.FromSpan([1, 2, 3]));
    }


    [TestMethod]
    public void FromSpanRoundTripsBytes()
    {
        byte[] bytes = new byte[ClusterId.Size];
        for(int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }

        ClusterId cluster = ClusterId.FromSpan(bytes);

        Assert.AreSequenceEqual(bytes, cluster.AsSpan().ToArray());
        Assert.AreSequenceEqual(bytes, cluster.ToArray());

        Span<byte> destination = stackalloc byte[ClusterId.Size];
        cluster.CopyTo(destination);
        Assert.IsTrue(destination.SequenceEqual(cluster.AsSpan()));
    }


    [TestMethod]
    public void EqualityIsByByteContent()
    {
        ClusterId left = Cluster(7, 7, 7);
        ClusterId right = Cluster(7, 7, 7);

        Assert.IsTrue(left.Equals(right));
        Assert.IsTrue(left == right);
        Assert.AreEqual(left.GetHashCode(), right.GetHashCode());

        Assert.AreNotEqual(Cluster(1), Cluster(2));
        Assert.IsTrue(Cluster(1) != Cluster(2));
    }


    [TestMethod]
    public void OrderingIsLexicographic()
    {
        ClusterId a = Cluster(0, 1);
        ClusterId b = Cluster(0, 2);
        ClusterId c = Cluster(1, 0);

        Assert.IsLessThan(0, a.CompareTo(b));
        Assert.IsLessThan(0, b.CompareTo(c));

        Assert.IsTrue(a < b);
        Assert.IsTrue(a <= b);
        Assert.IsFalse(a > b);
        Assert.IsFalse(a >= b);
    }


    [TestMethod]
    public void TheNonStrictComparisonsAreReflexiveAndTheStrictOnesAreNot()
    {
        //The EQUAL ARM is the only place the non-strict comparisons differ from the strict ones, so it is
        //where an ordering that answered both alike would place two equal identifiers as though one preceded
        //the other. The two values are filled and copied through separate FromSpan calls, so each holds its
        //own bytes and the comparison is over content.
        ClusterId left = Cluster(5, 6, 7);
        ClusterId right = Cluster(5, 6, 7);

        Assert.AreEqual(0, left.CompareTo(right));

        Assert.IsTrue(left <= right);
        Assert.IsTrue(left >= right);
        Assert.IsFalse(left < right);
        Assert.IsFalse(left > right);
    }


    [TestMethod]
    public void TheGenesisDigestIsOrderSensitive()
    {
        //The order is load-bearing, because the first member is the bootstrap leader. Two operators who wrote
        //the same replicas in different orders must mint DIFFERENT identities and block each other, rather
        //than agree on one identity while disagreeing on who bootstraps.
        ImmutableArray<ReplicaId> forward = [Replica(1), Replica(2), Replica(3)];
        ImmutableArray<ReplicaId> reordered = [Replica(2), Replica(1), Replica(3)];

        Assert.AreNotEqual(ClusterId.FromGenesisMembers(forward), ClusterId.FromGenesisMembers(reordered));
    }


    [TestMethod]
    public void TheGenesisDigestReadsTheMemberBytesAndNotTheirBuffers()
    {
        //Two independently built member arrays holding the same bytes in the same order are the same genesis,
        //so every host that reads one genesis file mints one identity.
        ImmutableArray<ReplicaId> first = [Replica(1), Replica(2), Replica(3)];
        ImmutableArray<ReplicaId> second = [Replica(1), Replica(2), Replica(3)];

        Assert.AreEqual(ClusterId.FromGenesisMembers(first), ClusterId.FromGenesisMembers(second));
    }


    [TestMethod]
    public void TheGenesisDigestIsPinnedToItsCanonicalEncoding()
    {
        //The encoding is a permanent contract and not an implementation detail: a build that encodes the
        //domain separator, the member count or the member order differently mints a different identity for an
        //existing chain, and every host on the changed build is declined by every host that was already
        //running. The pinned value is what makes such a change visible here instead of in a deployment.
        ClusterId minted = ClusterId.FromGenesisMembers([Replica(1), Replica(2), Replica(3)]);

        Assert.AreEqual(PinnedGenesisDigest, Convert.ToHexStringLower(minted.AsSpan()));
    }


    [TestMethod]
    public void AnEmptyGenesisMemberListIsRefused()
    {
        //A chain with no members has nothing to identify. Both shapes of "no members" fail closed.
        Assert.ThrowsExactly<ArgumentException>(() => ClusterId.FromGenesisMembers([]));
        Assert.ThrowsExactly<ArgumentException>(() => ClusterId.FromGenesisMembers(default));
    }


    [TestMethod]
    public void ToStringShowsSizeAndHexPreview()
    {
        ClusterId cluster = Cluster(0x01, 0x02, 0x03);

        string text = cluster.ToString();

        Assert.Contains("32 bytes", text);
        Assert.Contains("010203", text);
    }


    private static ClusterId Cluster(params byte[] prefix)
    {
        Span<byte> buffer = stackalloc byte[ClusterId.Size];
        prefix.AsSpan().CopyTo(buffer);

        return ClusterId.FromSpan(buffer);
    }


    private static ReplicaId Replica(params byte[] prefix)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        prefix.AsSpan().CopyTo(buffer);

        return ReplicaId.FromSpan(buffer);
    }
}
