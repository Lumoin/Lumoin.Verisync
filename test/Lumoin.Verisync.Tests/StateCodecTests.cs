using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class StateCodecTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void GCounterRoundTripsThroughState()
    {
        GCounter counter = GCounter.Empty.Increment(R1, 3).Increment(R2, 2);

        GCounter back = GCounter.FromState(counter.ToState());

        Assert.AreEqual(counter, back);
        Assert.AreEqual(5, back.Value);
    }


    [TestMethod]
    public void VectorClockRoundTripsThroughState()
    {
        VectorClock clock = VectorClock.Empty.Increment(R1).Increment(R1).Increment(R2);

        VectorClock back = VectorClock.FromState(clock.ToState());

        Assert.AreEqual(clock, back);
        Assert.AreEqual(2, back[R1]);
        Assert.AreEqual(1, back[R2]);
    }


    [TestMethod]
    public void OrSetReplicasConvergeAfterEachIsPersistedAndReloaded()
    {
        //The piggyback pattern: each replica's set is serialized to state, persisted by the host, reloaded, then merged.
        OrSet<string> a = OrSet<string>.Empty.Add("x", R1);
        OrSet<string> b = OrSet<string>.Empty.Add("y", R2);

        OrSet<string> aReloaded = OrSet<string>.FromState(a.ToState());
        OrSet<string> bReloaded = OrSet<string>.FromState(b.ToState());

        OrSet<string> merged = aReloaded.Merge(bReloaded);

        Assert.IsTrue(merged.Contains("x"));
        Assert.IsTrue(merged.Contains("y"));
    }


    [TestMethod]
    public void OrSetStatePreservesObservedRemoveAcrossReload()
    {
        OrSet<string> removed = OrSet<string>.Empty.Add("x", R1).Remove("x");

        OrSet<string> reloaded = OrSet<string>.FromState(removed.ToState());

        Assert.IsFalse(reloaded.Contains("x"));

        //The causal context survived the round-trip, so a concurrent add still wins over the observed remove.
        OrSet<string> concurrentAdd = OrSet<string>.Empty.Add("x", R2);
        Assert.IsTrue(reloaded.Merge(concurrentAdd).Contains("x"));
    }


    [TestMethod]
    public void PNCounterRoundTripsThroughState()
    {
        PNCounter counter = PNCounter.Empty.Increment(R1, 3).Decrement(R2, 5);

        PNCounter back = PNCounter.FromState(counter.ToState());

        Assert.AreEqual(counter, back);
        Assert.AreEqual(-2, back.Value);
    }


    [TestMethod]
    public void LwwRegisterRoundTripsThroughState()
    {
        LwwRegister<string> register = LwwRegister<string>.Empty.Write("alpha", new Timestamp(100), R1);

        LwwRegister<string> back = LwwRegister<string>.FromState(register.ToState());

        Assert.AreEqual(register, back);
        Assert.AreEqual("alpha", back.Value);

        //The (timestamp, writer) pair survived, so merge ordering is unchanged: a later write still wins
        //and an equal-timestamp write from a lower replica still loses.
        LwwRegister<string> later = LwwRegister<string>.Empty.Write("beta", new Timestamp(200), R2);
        Assert.AreEqual("beta", back.Merge(later).Value);

        LwwRegister<string> tied = LwwRegister<string>.Empty.Write("gamma", new Timestamp(100), R2);
        Assert.AreEqual("gamma", back.Merge(tied).Value);
    }


    [TestMethod]
    public void EmptyLwwRegisterRoundTripsThroughState()
    {
        LwwRegister<string> back = LwwRegister<string>.FromState(LwwRegister<string>.Empty.ToState());

        Assert.IsFalse(back.HasValue);
        Assert.AreEqual(LwwRegister<string>.Empty, back);
    }


    [TestMethod]
    public void MvRegisterRoundTripsThroughState()
    {
        //Two concurrent writes are both retained; both must survive the round-trip.
        MvRegister<string> concurrent = MvRegister<string>.Empty.Write("x", R1).Merge(MvRegister<string>.Empty.Write("y", R2));

        MvRegister<string> back = MvRegister<string>.FromState(concurrent.ToState());

        Assert.AreEqual(concurrent, back);
        Assert.HasCount(2, back.Values);

        //The causal context survived, so a write after reload observes and supersedes both values.
        MvRegister<string> resolved = back.Write("z", R1);
        Assert.HasCount(1, resolved.Values);
        Assert.Contains("z", resolved.Values);
    }


    [TestMethod]
    public void RgaRoundTripsThroughState()
    {
        (Rga<string> array, Dot first) = Rga<string>.Empty.InsertAtHead("a", R1);
        (array, Dot second) = array.InsertAfter(first, "b", R1);
        (array, _) = array.InsertAfter(second, "c", R1);
        array = array.Remove(second);

        Rga<string> back = Rga<string>.FromState(array.ToState());

        Assert.AreEqual(array, back);
        string[] expected = ["a", "c"];
        CollectionAssert.AreEqual(expected, back.Values.ToArray());

        //The tombstone survived, so merging the pre-removal array does not resurrect the element.
        Assert.HasCount(2, back.Merge(array).Values);

        //The causal context survived, so an insert after reload gets a fresh dot and converges on merge.
        //After "a", the tombstoned "b" (counter 2) still sorts before the new "d" (counter 1), and "c"
        //follows its predecessor "b", so "d" lands after "c".
        (Rga<string> extended, _) = back.InsertAfter(first, "d", R2);
        string[] expectedExtended = ["a", "c", "d"];
        CollectionAssert.AreEqual(expectedExtended, extended.Values.ToArray());
        Assert.AreEqual(extended, extended.Merge(array));
    }


    [TestMethod]
    public void RgaStatePersistsAsJsonAndReloads()
    {
        //The trickiest state shape — nested dot records and a nullable predecessor — through real JSON bytes.
        (Rga<string> array, Dot first) = Rga<string>.Empty.InsertAtHead("a", R1);
        (array, _) = array.InsertAfter(first, "b", R2);

        byte[] persisted = JsonSerializer.SerializeToUtf8Bytes(array.ToState(), SampleJsonContext.Default.RgaStateString);
        RgaState<string> reloadedState = JsonSerializer.Deserialize(persisted, SampleJsonContext.Default.RgaStateString)!;
        Rga<string> back = Rga<string>.FromState(reloadedState);

        Assert.AreEqual(array, back);
        string[] expected = ["a", "b"];
        CollectionAssert.AreEqual(expected, back.Values.ToArray());
    }


    [TestMethod]
    public void GCounterStatePersistsAsJsonAndReloads()
    {
        //The full host-persistence cycle: CRDT -> state -> JSON bytes (what Orleans/Postgres would store) -> state -> CRDT.
        GCounter counter = GCounter.Empty.Increment(R1, 3).Increment(R2, 2);

        byte[] persisted = JsonSerializer.SerializeToUtf8Bytes(counter.ToState(), SampleJsonContext.Default.GCounterState);
        GCounterState reloadedState = JsonSerializer.Deserialize(persisted, SampleJsonContext.Default.GCounterState)!;
        GCounter back = GCounter.FromState(reloadedState);

        Assert.AreEqual(counter, back);
        Assert.AreEqual(5, back.Value);
    }


    [TestMethod]
    public void FromStateRejectsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => GCounter.FromState(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => VectorClock.FromState(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => OrSet<string>.FromState(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PNCounter.FromState(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LwwRegister<string>.FromState(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => MvRegister<string>.FromState(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => Rga<string>.FromState(null!));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
