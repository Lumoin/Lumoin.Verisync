using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Validation, change and equality coverage for <see cref="QuePaxaConfiguration"/>, the membership one
/// versioned register instance runs under.
/// </summary>
/// <remarks>
/// <para>
/// The construction guards are pinned one vector per rule: an empty member list and a duplicate-bearing one
/// each fail on their own guard, and emptying a configuration through
/// <see cref="QuePaxaConfiguration.Without(ReplicaId)"/> fails on a third that no other guard can answer for.
/// The duplicate refusal is load-bearing for quorum injectivity and not hygiene, so its vector stands beside a
/// POSITIVE vector over members whose hashes collide, which is what a duplicate scan comparing anything
/// weaker than the member bytes would wrongly refuse.
/// </para>
/// <para>
/// THE CODEC ROUND TRIP IS THE VECTOR THE SYNTHESIZED EQUALITY CANNOT SURVIVE. A record's synthesized equality
/// would compare <see cref="ImmutableArray{T}"/> by the identity of its backing array, and the defect that
/// causes is silent: whole-proposal comparison fails, phase two never decides, and a writer's own write comes
/// back superseded. The encoding here is the one the record codec carries the configuration in, so the
/// decoded configuration is a genuinely different instance over genuinely different buffers.
/// </para>
/// <para>
/// THE HASH-SET VECTOR REACHES <see cref="QuePaxaConfiguration.GetHashCode"/>'s COLLISION PATH.
/// <see cref="ReplicaId.GetHashCode"/> reads only the leading four bytes, so two replicas differing later hash
/// alike; two configurations built over them hash alike as well, and a hash-keyed collection therefore has to
/// call <see cref="QuePaxaConfiguration.Equals(QuePaxaConfiguration)"/> to separate them. The same collection
/// then rejects an equal configuration built over independent buffers, which a synthesized hash puts in
/// another bucket entirely.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaConfigurationTests
{
    private static ReplicaId A { get; } = Replica(1);
    private static ReplicaId B { get; } = Replica(2);
    private static ReplicaId C { get; } = Replica(3);
    private static ReplicaId D { get; } = Replica(4);
    private static ReplicaId Absent { get; } = Replica(9);

    /// <summary>
    /// Differs from A only past the fourth byte, so it is a different replica that hashes exactly as A does.
    /// </summary>
    private static ReplicaId ATwin { get; } = Replica(1, 0, 0, 0, 9);

    private static ClusterId Chain { get; } = ClusterId.FromGenesisMembers([A, B, C]);
    private static ClusterId OtherChain { get; } = ClusterId.FromGenesisMembers([D, B, C]);


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// The membership delta names who a change admits and who it retires, in the memberships' own orders.
    /// </summary>
    /// <remarks>
    /// The boundary is where an operator acts — the joiners are who an admission disseminates to first and
    /// the leavers are who a readiness gate must stop counting — so the delta is a first-class read rather
    /// than a set walk every consumer rewrites.
    /// </remarks>
    [TestMethod]
    public void TheMembershipDeltaNamesJoinersAndLeavers()
    {
        QuePaxaConfiguration outgoing = QuePaxaConfiguration.Create(Chain, [A, B, C]);
        QuePaxaConfiguration incoming = outgoing.Without(B).With(D);

        Assert.AreSequenceEqual(new[] { D }, outgoing.Joining(incoming));
        Assert.AreSequenceEqual(new[] { B }, outgoing.Leaving(incoming));
        Assert.IsEmpty(outgoing.Joining(outgoing));
        Assert.IsEmpty(outgoing.Leaving(outgoing));
    }


    /// <summary>
    /// A delta against another chain's membership is refused, because the two never were one fleet.
    /// </summary>
    [TestMethod]
    public void TheMembershipDeltaRefusesAnotherChain()
    {
        QuePaxaConfiguration ours = QuePaxaConfiguration.Create(Chain, [A, B, C]);
        QuePaxaConfiguration foreign = QuePaxaConfiguration.Create(OtherChain, [A, B, C]);

        ArgumentException joining = Assert.ThrowsExactly<ArgumentException>(() => ours.Joining(foreign));
        ArgumentException leaving = Assert.ThrowsExactly<ArgumentException>(() => ours.Leaving(foreign));

        Assert.Contains("another chain", joining.Message);
        Assert.Contains("another chain", leaving.Message);
    }


    [TestMethod]
    public void AnEmptyMemberListIsRefused()
    {
        //Neither shape of "no members" can reach the duplicate scan, so this rejection has one firing rule.
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaConfiguration.Create(Chain, []));
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaConfiguration.Create(Chain, default));
    }


    [TestMethod]
    public void ADuplicateMemberIsRefused()
    {
        //A duplicate is refused because a quorum is a count of distinct members: a replica listed twice would
        //answer twice and be counted twice, and the decision would rest on fewer replicas than the arithmetic
        //claims. Both lists are non-empty, so the empty guard cannot answer for either.
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaConfiguration.Create(Chain, [A, A]));

        //Non-adjacent, so the scan has to span the whole tail rather than compare neighbours.
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaConfiguration.Create(Chain, [A, B, A]));
    }


    [TestMethod]
    public void ADuplicateMemberIsRefusedAtGenesis()
    {
        //CreateGenesis is a SECOND PUBLIC ENTRY POINT into the same rule, and a genesis that bypassed the
        //duplicate scan would mint a chain identity for a configuration listing the same replica twice:
        //quorum injectivity broken at the one configuration an operator hand-writes. Both lists are non-empty
        //and neither is default, so neither emptiness guard on the path can answer for either refusal.
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaConfiguration.CreateGenesis([A, A]));

        //Non-adjacent, so the scan has to span the whole tail rather than compare neighbours.
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaConfiguration.CreateGenesis([A, B, A]));
    }


    [TestMethod]
    public void AConfigurationOfDistinctMembersIsAccepted()
    {
        //The positive arm of the duplicate scan, over a member pair that HASHES ALIKE and is not equal: a
        //scan comparing hashes, or comparing a member with itself, refuses this valid configuration.
        Assert.AreEqual(A.GetHashCode(), ATwin.GetHashCode());
        Assert.AreNotEqual(A, ATwin);

        QuePaxaConfiguration configuration = QuePaxaConfiguration.Create(Chain, [A, ATwin, B]);

        Assert.AreSequenceEqual(new[] { A, ATwin, B }, configuration.Members);
        Assert.AreEqual(Chain, configuration.Cluster);
        Assert.AreEqual(2, configuration.Quorum);
        Assert.IsTrue(configuration.Contains(ATwin));
        Assert.IsFalse(configuration.Contains(Absent));
    }


    [TestMethod]
    public void QuorumIsAMajorityOfTheMemberCount()
    {
        //No safety floor is imposed: majorities intersect at every size, so one and two members are legal
        //configurations with, respectively, no redundancy and no fault tolerance.
        Assert.AreEqual(1, Configuration(A).Quorum);
        Assert.AreEqual(2, Configuration(A, B).Quorum);
        Assert.AreEqual(2, Configuration(A, B, C).Quorum);
        Assert.AreEqual(3, Configuration(A, B, C, D).Quorum);
    }


    [TestMethod]
    public void AddingAMemberTwiceIsIdempotent()
    {
        //A change is re-applied against the winning configuration when a reconfiguring write is superseded,
        //so adding a member that is already listed must be the identity rather than an error or a duplicate.
        QuePaxaConfiguration three = Configuration(A, B, C);

        QuePaxaConfiguration grown = three.With(D);
        Assert.AreSequenceEqual(new[] { A, B, C, D }, grown.Members);

        Assert.AreSame(grown, grown.With(D));
        Assert.AreSame(three, three.With(A));
    }


    [TestMethod]
    public void RemovingAMemberTwiceIsIdempotent()
    {
        //The same re-application rule from the other side: removing a member that is not listed is the
        //identity, and the survivors keep their order so the bootstrap position does not shift underneath.
        QuePaxaConfiguration three = Configuration(A, B, C);

        QuePaxaConfiguration shrunk = three.Without(B);
        Assert.AreSequenceEqual(new[] { A, C }, shrunk.Members);

        Assert.AreSame(shrunk, shrunk.Without(B));
        Assert.AreSame(three, three.Without(Absent));
    }


    [TestMethod]
    public void RemovingTheLastMemberIsRefused()
    {
        //A register with no members can neither decide nor be reconfigured back into existence. The refusal
        //is its own rule and not the construction guard reached indirectly, and the same one-member
        //configuration still answers a removal of a replica it does not list with the identity.
        QuePaxaConfiguration single = Configuration(A);

        Assert.ThrowsExactly<InvalidOperationException>(() => single.Without(A));
        Assert.AreSame(single, single.Without(Absent));
    }


    [TestMethod]
    public void AChangeCarriesTheChainIdentityForwardWithoutMintingANewOne()
    {
        //A membership change stays on the chain it changes. Re-minting the identity from the changed member
        //list would make every reconfiguration a fork that every unchanged host declines.
        QuePaxaConfiguration genesis = QuePaxaConfiguration.CreateGenesis([A, B, C]);

        QuePaxaConfiguration grown = genesis.With(D);
        QuePaxaConfiguration shrunk = genesis.Without(C);

        Assert.AreEqual(genesis.Cluster, grown.Cluster);
        Assert.AreEqual(genesis.Cluster, shrunk.Cluster);
        Assert.AreNotEqual(ClusterId.FromGenesisMembers(grown.Members), grown.Cluster);
    }


    [TestMethod]
    public void GenesisMintsTheChainIdentityFromItsOwnMemberList()
    {
        QuePaxaConfiguration genesis = QuePaxaConfiguration.CreateGenesis([A, B, C]);

        Assert.AreEqual(ClusterId.FromGenesisMembers([A, B, C]), genesis.Cluster);
        Assert.AreSequenceEqual(new[] { A, B, C }, genesis.Members);

        //An operator who wrote the same replicas in another order bootstrapped another chain.
        Assert.AreNotEqual(genesis.Cluster, QuePaxaConfiguration.CreateGenesis([B, A, C]).Cluster);
    }


    [TestMethod]
    public void EqualityIsElementWiseAndOrderSensitive()
    {
        //The deliberate contrast with the order-INDEPENDENT equality of a reconciliation drop: here the order
        //is the hedging order and the first position is the bootstrap leader, so a reorder is a different
        //configuration and not a different spelling of one.
        QuePaxaConfiguration forward = Configuration(A, B, C);
        QuePaxaConfiguration reordered = Configuration(B, A, C);

        Assert.AreEqual(forward, Configuration(A, B, C));
        Assert.AreNotEqual(forward, reordered);
        Assert.AreNotEqual(forward, Configuration(A, B));
    }


    [TestMethod]
    public void ConfigurationsOnDifferentChainsAreUnequal()
    {
        QuePaxaConfiguration here = QuePaxaConfiguration.Create(Chain, [A, B, C]);
        QuePaxaConfiguration elsewhere = QuePaxaConfiguration.Create(OtherChain, [A, B, C]);

        Assert.AreNotEqual(here, elsewhere);

        //The object override answers a null, a foreign type and another configuration alike: false, never a
        //throw, because a configuration is compared wherever a record holding one is compared.
        List<object?> notThisConfiguration = [null, "not a configuration", elsewhere];
        foreach(object? other in notThisConfiguration)
        {
            Assert.IsFalse(here.Equals(other));
        }
    }


    [TestMethod]
    public void EqualityHoldsAcrossACodecRoundTrip()
    {
        //A configuration that crossed a codec is a different object over different buffers, which separates
        //element-wise equality from the synthesized equality an ImmutableArray field would get REGARDLESS OF
        //HOW A HELPER ALLOCATES ITS BACKING ARRAYS. The in-memory vectors separate the two only for as long
        //as their helpers keep handing out a fresh array per call, so this is the vector that stays the
        //guaranteed killer if those helpers ever change. The defect it pins is silent rather than thrown:
        //nothing fails to encode, the bytes are byte-perfect, and whole-proposal comparison simply never
        //matches again.
        QuePaxaConfiguration original = QuePaxaConfiguration.CreateGenesis([A, B, C]);

        string encoded = Encode(original);
        TestContext.WriteLine(encoded);

        QuePaxaConfiguration decoded = Decode(encoded);

        Assert.AreNotSame(original, decoded);
        Assert.AreEqual(original, decoded);
        Assert.AreEqual(original.GetHashCode(), decoded.GetHashCode());
        Assert.AreEqual(original.Cluster, decoded.Cluster);
    }


    [TestMethod]
    public void AHashKeyedCollectionSeparatesCollidingConfigurationsAndCollapsesEqualOnes()
    {
        //Two configurations differing only in a member's bytes past the fourth hash identically, because a
        //replica's hash reads only the leading four bytes. A hash-keyed collection therefore has to call
        //Equals to keep them apart, which is the collision path a synthesized hash never reaches.
        QuePaxaConfiguration left = QuePaxaConfiguration.Create(Chain, [A, B, C]);
        QuePaxaConfiguration right = QuePaxaConfiguration.Create(Chain, [ATwin, B, C]);

        Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
        Assert.AreNotEqual(left, right);

        HashSet<QuePaxaConfiguration> configurations = [left, right];
        Assert.HasCount(2, configurations);

        //An equal configuration built over independently allocated buffers finds the stored one, which is
        //what a hash over the backing array's identity cannot do.
        QuePaxaConfiguration equalToLeft = QuePaxaConfiguration.Create(Chain, [Replica(1), Replica(2), Replica(3)]);

        Assert.IsFalse(configurations.Add(equalToLeft));
        Assert.HasCount(2, configurations);
    }


    [TestMethod]
    public void TheScheduleTakesTheMemberOrderAndALocalDelay()
    {
        //The base delay is local tuning and not part of the agreed configuration: it orders sending and
        //settles no protocol rule.
        QuePaxaConfiguration configuration = Configuration(A, B, C);

        HedgingSchedule schedule = configuration.ScheduleWith(TimeSpan.FromMilliseconds(40));

        Assert.AreSequenceEqual(new[] { A, B, C }, schedule.Order);
        Assert.AreEqual(A, schedule.Leader);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), schedule.DelayFor(C));
    }


    private static QuePaxaConfiguration Configuration(params ReplicaId[] members)
    {
        return QuePaxaConfiguration.Create(Chain, [.. members]);
    }


    private static string Encode(QuePaxaConfiguration configuration)
    {
        //The encoding the record codec carries a configuration in: the chain identity and every member as
        //lower-case hex, the members in their configured order.
        var buffer = new ArrayBufferWriter<byte>();
        using(var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("cluster", Convert.ToHexStringLower(configuration.Cluster.AsSpan()));
            writer.WriteStartArray("members");
            foreach(ReplicaId member in configuration.Members)
            {
                writer.WriteStringValue(Convert.ToHexStringLower(member.AsSpan()));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    private static QuePaxaConfiguration Decode(string encoded)
    {
        using JsonDocument document = JsonDocument.Parse(encoded);
        JsonElement root = document.RootElement;

        ClusterId cluster = ClusterId.FromSpan(Convert.FromHexString(root.GetProperty("cluster").GetString()!));

        ImmutableArray<ReplicaId>.Builder members = ImmutableArray.CreateBuilder<ReplicaId>();
        foreach(JsonElement member in root.GetProperty("members").EnumerateArray())
        {
            members.Add(ReplicaId.FromSpan(Convert.FromHexString(member.GetString()!)));
        }

        return QuePaxaConfiguration.Create(cluster, members.ToImmutable());
    }


    private static ReplicaId Replica(params byte[] prefix)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        prefix.AsSpan().CopyTo(buffer);

        return ReplicaId.FromSpan(buffer);
    }
}
