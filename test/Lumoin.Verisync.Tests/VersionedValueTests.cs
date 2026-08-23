using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The decided record's own guards: the version it refuses and the configuration it refuses, each pinned from
/// the constructor and from a <c>with</c> expression.
/// </summary>
/// <remarks>
/// <para>
/// THE <c>with</c> VECTORS ARE NOT A DUPLICATE OF THE CONSTRUCTION ONES. A positional record's initializer
/// writes the backing field directly while a <c>with</c> expression runs the <c>init</c> accessor, so the two
/// are separate paths and a field validated on one of them alone accepts through the other. Constructing
/// leaves an ordinary auto-property indistinguishable from a validated one.
/// </para>
/// <para>
/// EACH REJECTION VECTOR IS AN OTHERWISE-VALID RECORD differing in exactly the field under test, so the guard
/// that fires is the one the vector was written for and no sibling guard can answer for it.
/// </para>
/// </remarks>
[TestClass]
internal sealed class VersionedValueTests
{
    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);

    /// <summary>The membership a record carries here.</summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third));

    /// <summary>The membership that same chain reaches by admitting a fourth replica.</summary>
    private static QuePaxaConfiguration Grown { get; } = Configuration.With(Membership.Member(Replica(4)));


    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void ARecordCarriesTheVersionTheWriterTheConfigurationAndTheValue()
    {
        VersionedValue<string> record = new(new RegisterVersion(3UL), Second, Configuration, "v");

        Assert.AreEqual(new RegisterVersion(3UL), record.Version);
        Assert.AreEqual(Second, record.Writer);
        Assert.AreEqual(Configuration, record.NextConfiguration);
        Assert.AreEqual("v", record.Value);
    }


    [TestMethod]
    public void TheUnwrittenVersionIsRefusedOnConstructionAndOnAWithExpression()
    {
        ArgumentOutOfRangeException constructed = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = new VersionedValue<string>(RegisterVersion.Unwritten, Second, Configuration, "v"));

        Assert.AreEqual("Version", constructed.ParamName);

        VersionedValue<string> valid = new(new RegisterVersion(3UL), Second, Configuration, "v");

        ArgumentOutOfRangeException rewritten = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = valid with { Version = RegisterVersion.Unwritten });

        Assert.AreEqual("Version", rewritten.ParamName);
    }


    [TestMethod]
    public void AMissingConfigurationIsRefusedOnConstructionAndOnAWithExpression()
    {
        //A record with no membership names no recorder set for the version after it, and every other field of
        //this vector is valid, so the null configuration is the only rule it can trip.
        ArgumentNullException constructed = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = new VersionedValue<string>(new RegisterVersion(3UL), Second, null!, "v"));

        Assert.AreEqual("NextConfiguration", constructed.ParamName);

        VersionedValue<string> valid = new(new RegisterVersion(3UL), Second, Configuration, "v");

        //The initializer wrote the backing field directly, so this is the only path the init accessor runs on
        //and the only vector an unvalidated auto-property is visible from.
        ArgumentNullException rewritten = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = valid with { NextConfiguration = null! });

        Assert.AreEqual("NextConfiguration", rewritten.ParamName);
    }


    [TestMethod]
    public void AWithExpressionOverTheConfigurationKeepsEveryOtherFieldAndTakesTheNewMembership()
    {
        VersionedValue<string> record = new(new RegisterVersion(3UL), Second, Configuration, "v");

        VersionedValue<string> reconfigured = record with { NextConfiguration = Grown };

        TestContext.WriteLine($"{record.NextConfiguration.Members.Length} members became {reconfigured.NextConfiguration.Members.Length}");

        Assert.AreEqual(Grown, reconfigured.NextConfiguration);
        Assert.HasCount(4, reconfigured.NextConfiguration.Members);
        Assert.AreEqual(record.Version, reconfigured.Version);
        Assert.AreEqual(record.Writer, reconfigured.Writer);
        Assert.AreEqual(record.Value, reconfigured.Value);

        //The membership is part of the decided value, so two records agreeing on everything else and differing
        //on it are different records and whole-proposal comparison separates them.
        Assert.AreNotEqual(record, reconfigured);
        Assert.AreEqual(Configuration, record.NextConfiguration);
    }


    [TestMethod]
    public void TwoRecordsOverIndependentlyBuiltEqualConfigurationsAreEqual()
    {
        //The register compares whole proposals, so a record's equality has to read the configuration's members
        //rather than the identity of the array holding them.
        VersionedValue<string> left = new(new RegisterVersion(3UL), Second, QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third)), "v");
        VersionedValue<string> right = new(new RegisterVersion(3UL), Second, QuePaxaConfiguration.CreateGenesis(Membership.Of(Replica(1), Replica(2), Replica(3))), "v");

        Assert.AreNotSame(left.NextConfiguration, right.NextConfiguration);
        Assert.AreEqual(left, right);
        Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
