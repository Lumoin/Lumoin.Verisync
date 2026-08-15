using Lumoin.Verisync.Core;
using System.Globalization;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Covers the acceptor's durable seam: <see cref="FastAcceptor{TValue}.ToState"/> snapshots the promise, the
/// accepted ballot and the accepted value, <see cref="FastAcceptor{TValue}.FromState"/> rebuilds an acceptor
/// a proposer cannot distinguish from the one that crashed, and every rule that refuses a state no acceptor
/// can hold.
/// </summary>
/// <remarks>
/// Every rejection below derives its state with <see langword="with"/> from a snapshot a live acceptor
/// produced, because a rule exists precisely for the states no honest history reaches: the mutation names the
/// one field that makes the state impossible, and the state stays otherwise well formed so exactly one rule
/// can be what refuses it. Each rejected state is built outside the throwing call, which pins that the record
/// itself validates nothing — the rules live in <see cref="FastAcceptor{TValue}.FromState"/> alone.
/// </remarks>
[TestClass]
internal sealed class FastAcceptorStateTests
{
    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);


    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void FromStateRefusesAPromiseBelowTheInitialFastBallot()
    {
        //The zero-ballot promise is what an all-zero page or a zeroed record reads back as, so this vector is
        //the practically important corruption and not a curiosity. The accepted ballot is zero and the value
        //default, so the promise floor is the one rule that can fire.
        FastAcceptorState<string> zeroed = FastAcceptor<string>.Initial.ToState() with { Promised = FastBallot.Zero };

        StateRestoreException zeroedRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(zeroed));
        Assert.AreEqual(StateRestoreRefusal.AcceptorPromiseBelowInitialBallot, zeroedRefusal.Refusal);
        Assert.AreEqual("state", zeroedRefusal.ParamName);

        //A round-zero ballot owning a proposer is not the zero ballot yet still orders below the initial fast
        //ballot; the raw ballot constructor admits it, so the floor must refuse it by ordering and not by IsZero.
        FastAcceptorState<string> roundZeroClassic = FastAcceptor<string>.Initial.ToState() with { Promised = new FastBallot(0, R1) };

        StateRestoreException roundZeroRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(roundZeroClassic));
        Assert.AreEqual(StateRestoreRefusal.AcceptorPromiseBelowInitialBallot, roundZeroRefusal.Refusal);
        Assert.AreEqual("state", roundZeroRefusal.ParamName);

        //A negative-round promise cannot fire this rule alone — the zero accepted ballot then orders above the
        //promise and the trailing-promise rule fires too — so negative rounds are swept for refusal only. The
        //row names no refusal because AcceptorPromiseBelowInitialBallot and AcceptorPromiseTrailsAcceptedBallot
        //are both reachable on this state and only the order the rules are stated in decides between them.
        foreach(int round in (int[])[-1, int.MinValue])
        {
            FastAcceptorState<string> negative = FastAcceptor<string>.Initial.ToState() with { Promised = new FastBallot(round, null) };

            Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(negative));
        }
    }


    [TestMethod]
    public void FromStateRefusesAnAcceptedBallotBelowTheInitialFastBallotThatIsNotZero()
    {
        //An accepted ballot is written only by an accept whose ballot stood at or above the promise, and the
        //promise never stands below the initial fast ballot, so the gap between the zero ballot and the
        //initial fast ballot holds no accepted ballot. The raw ballot constructor reaches that gap with a
        //round-zero ballot owning a proposer, which the zero-ballot exemption must not cover.
        FastAcceptorState<string> roundZeroClassic = FastAcceptor<string>.Initial.ToState() with { AcceptedBallot = new FastBallot(0, R1) };

        StateRestoreException roundZeroRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(roundZeroClassic));
        Assert.AreEqual(StateRestoreRefusal.AcceptorAcceptedBallotBelowInitialBallot, roundZeroRefusal.Refusal);
        Assert.AreEqual("state", roundZeroRefusal.ParamName);

        FastAcceptorState<string> negativeFast = FastAcceptor<string>.Initial.ToState() with { AcceptedBallot = new FastBallot(-2, null) };

        StateRestoreException negativeRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(negativeFast));
        Assert.AreEqual(StateRestoreRefusal.AcceptorAcceptedBallotBelowInitialBallot, negativeRefusal.Refusal);
        Assert.AreEqual("state", negativeRefusal.ParamName);
    }


    [TestMethod]
    public void FromStateRefusesAPromiseThatTrailsTheAcceptedBallot()
    {
        //The same-round shape is where the rule earns its keep: at one round the fast ballot orders before
        //any classic ballot, so a classic accepted ballot at round one stands above the initial fast promise
        //without either slot leaving its own legal range.
        FastAcceptorState<string> sameRound = FastAcceptor<string>.Initial.ToState() with
        {
            AcceptedBallot = FastBallot.Classic(1, R1),
            AcceptedValue = "v",
        };

        StateRestoreException sameRoundRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(sameRound));
        Assert.AreEqual(StateRestoreRefusal.AcceptorPromiseTrailsAcceptedBallot, sameRoundRefusal.Refusal);
        Assert.AreEqual("state", sameRoundRefusal.ParamName);

        //The across-round shape mutates a snapshot an accept produced, raising only the accepted ballot.
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(2, R1), "v");
        FastAcceptorState<string> acrossRounds = accepted.ToState() with { AcceptedBallot = FastBallot.Classic(3, R1) };

        StateRestoreException acrossRoundsRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(acrossRounds));
        Assert.AreEqual(StateRestoreRefusal.AcceptorPromiseTrailsAcceptedBallot, acrossRoundsRefusal.Refusal);
        Assert.AreEqual("state", acrossRoundsRefusal.ParamName);
    }


    [TestMethod]
    public void FromStateRefusesAValueUnderTheZeroAcceptedBallot()
    {
        //The accepted ballot and the accepted value are assigned together and only by an accept, so a value
        //beside the zero ballot is a mix no acceptor wrote — the signature of a torn first accept that landed
        //the value and lost the ballots.
        FastAcceptorState<string> ghost = FastAcceptor<string>.Initial.ToState() with { AcceptedValue = "v" };

        StateRestoreException ghostRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<string>.FromState(ghost));
        Assert.AreEqual(StateRestoreRefusal.AcceptorValueWithoutAcceptedBallot, ghostRefusal.Refusal);
        Assert.AreEqual("state", ghostRefusal.ParamName);

        //The struct vector pins the comparer form of the rule: a boxed null test would refuse every struct
        //value including the initial acceptor's default, where the comparer refuses exactly the non-default.
        FastAcceptorState<int> structGhost = FastAcceptor<int>.Initial.ToState() with { AcceptedValue = 7 };

        StateRestoreException structRefusal = Assert.ThrowsExactly<StateRestoreException>(() => FastAcceptor<int>.FromState(structGhost));
        Assert.AreEqual(StateRestoreRefusal.AcceptorValueWithoutAcceptedBallot, structRefusal.Refusal);
        Assert.AreEqual("state", structRefusal.ParamName);
    }


    [TestMethod]
    public void FromStateRefusesANullState()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => FastAcceptor<string>.FromState(null!));
    }


    /// <summary>
    /// The pair is an inverse at the bottom of the range, unlike the recorder's, whose unwritten state the
    /// restore refuses: the initial acceptor is not unwritten but pre-promised, and a node that lost
    /// everything returns exactly there, so refusing it would refuse the state a fresh node stands in.
    /// </summary>
    [TestMethod]
    public void ToStateAndFromStateRoundTripTheInitialAcceptor()
    {
        FastAcceptorState<string> snapshot = FastAcceptor<string>.Initial.ToState();
        FastAcceptor<string> restored = FastAcceptor<string>.FromState(snapshot);

        Assert.AreEqual(snapshot, restored.ToState());

        //The restored acceptor answers exactly as the initial one does: the same promise to a classic
        //prepare, and the same acceptance of the pre-promised initial fast ballot.
        (FastAcceptor<string> originalPrepared, FastPrepareResponse<string> originalResponse) = FastAcceptor<string>.Initial.Prepare(FastBallot.Classic(2, R1));
        (FastAcceptor<string> restoredPrepared, FastPrepareResponse<string> restoredResponse) = restored.Prepare(FastBallot.Classic(2, R1));

        Assert.AreEqual(originalResponse, restoredResponse);
        Assert.AreEqual(originalPrepared.ToState(), restoredPrepared.ToState());

        (FastAcceptor<string> originalAccepted, bool originalAccepts) = FastAcceptor<string>.Initial.Accept(FastBallot.InitialFast(), "v");
        (FastAcceptor<string> restoredAccepted, bool restoredAccepts) = restored.Accept(FastBallot.InitialFast(), "v");

        Assert.IsTrue(originalAccepts);
        Assert.IsTrue(restoredAccepts);
        Assert.AreEqual(originalAccepted.ToState(), restoredAccepted.ToState());

        //The value-type half: the initial int acceptor carries the struct default under the zero ballot, and
        //a restore keyed on a boxed null test instead of the comparer would refuse exactly this state.
        FastAcceptorState<int> intSnapshot = FastAcceptor<int>.Initial.ToState();

        Assert.AreEqual(intSnapshot, FastAcceptor<int>.FromState(intSnapshot).ToState());
    }


    [TestMethod]
    public void APromiseRaisedWithNothingAcceptedRestores()
    {
        //A classic promise far above the initial round with nothing accepted is the ordinary trace of
        //prepares that never led to an accept.
        (FastAcceptor<string> prepared, _) = FastAcceptor<string>.Initial.Prepare(FastBallot.Classic(9, R1));
        FastAcceptorState<string> classicSnapshot = prepared.ToState();

        Assert.AreEqual(classicSnapshot, FastAcceptor<string>.FromState(classicSnapshot).ToState());

        //A fast promise above the initial round with nothing accepted has exactly one route: a zero-ballot
        //idempotent retry carrying the piggybacked next ballot, which raises the promise while leaving the
        //accepted pair untouched. A rule reading "a fast promise above the initial round implies an accept"
        //would refuse a state the type produces.
        (FastAcceptor<string> raised, bool retried) = FastAcceptor<string>.Initial.Accept(FastBallot.Zero, default!, next: FastBallot.Fast(9));
        FastAcceptorState<string> fastSnapshot = raised.ToState();

        Assert.IsTrue(retried);
        Assert.AreEqual(FastBallot.Fast(9), fastSnapshot.Promised);
        Assert.AreEqual(FastBallot.Zero, fastSnapshot.AcceptedBallot);
        Assert.AreEqual(fastSnapshot, FastAcceptor<string>.FromState(fastSnapshot).ToState());
    }


    /// <summary>
    /// The accepted ballot here equals the initial fast ballot exactly, so this test is also what refuses an
    /// accepted-ballot floor written as strictly-above rather than at-or-above.
    /// </summary>
    [TestMethod]
    public void AFastAcceptedValueUnderALaterClassicPromiseRestores()
    {
        //A fast write followed by a classic recovery prepare is the protocol's ordinary contention shape, and
        //it parts every field: a fast accepted ballot under a classic promise two rounds up.
        (FastAcceptor<string> fastWritten, _) = FastAcceptor<string>.Initial.Accept(FastBallot.InitialFast(), "v");
        (FastAcceptor<string> prepared, _) = fastWritten.Prepare(FastBallot.Classic(3, R1));
        FastAcceptorState<string> snapshot = prepared.ToState();

        Assert.AreEqual(FastBallot.Classic(3, R1), snapshot.Promised);
        Assert.AreEqual(FastBallot.InitialFast(), snapshot.AcceptedBallot);
        Assert.AreEqual(snapshot, FastAcceptor<string>.FromState(snapshot).ToState());
    }


    /// <summary>
    /// The class remark on <see cref="FastAcceptor{TValue}"/> states that "a fast round beyond the initial
    /// one is never blind-writable", which invites the rule "an accepted fast ballot must be the initial
    /// one"; the same sentence's escape clause — or through a piggybacked next ballot — is easy to read
    /// past. This test keeps that tempting wrong rule dead.
    /// </summary>
    [TestMethod]
    public void AFastRoundAboveTheFirstCanBeTheAcceptedBallot()
    {
        //A piggybacked next fast round becomes the promise, so a fast ballot above round one satisfies the
        //equality rule and is accepted without a prepare.
        (FastAcceptor<string> armed, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(2, R1), "v", next: FastBallot.Fast(5));
        (FastAcceptor<string> fastAccepted, bool accepted) = armed.Accept(FastBallot.Fast(5), "w");
        FastAcceptorState<string> snapshot = fastAccepted.ToState();

        Assert.IsTrue(accepted);
        Assert.AreEqual(FastBallot.Fast(5), snapshot.AcceptedBallot);
        Assert.AreEqual(snapshot, FastAcceptor<string>.FromState(snapshot).ToState());

        //Two distinct fast rounds in one state: a retry of the accepted fast pair carries a further next
        //ballot, raising the promise past the accepted round.
        (FastAcceptor<string> lower, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Zero, default!, next: FastBallot.Fast(3));
        (FastAcceptor<string> lowerAccepted, _) = lower.Accept(FastBallot.Fast(3), "v");
        (FastAcceptor<string> raised, _) = lowerAccepted.Accept(FastBallot.Fast(3), "v", next: FastBallot.Fast(5));
        FastAcceptorState<string> twoRounds = raised.ToState();

        Assert.AreEqual(FastBallot.Fast(5), twoRounds.Promised);
        Assert.AreEqual(FastBallot.Fast(3), twoRounds.AcceptedBallot);
        Assert.AreEqual(twoRounds, FastAcceptor<string>.FromState(twoRounds).ToState());
    }


    [TestMethod]
    public void AnAcceptedBallotEqualToThePromiseRestores()
    {
        //An accept with no piggyback lands with the promise equal to the accepted ballot, which is the most
        //ordinary accepted state there is; a trailing-promise rule written at-or-above would refuse it.
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(2, R1), "v");
        FastAcceptorState<string> snapshot = accepted.ToState();

        Assert.AreEqual(snapshot.Promised, snapshot.AcceptedBallot);
        Assert.AreEqual(snapshot, FastAcceptor<string>.FromState(snapshot).ToState());
    }


    [TestMethod]
    public void TheDefaultValueAtARealAcceptedBallotRestores()
    {
        //Accept validates nothing about its value, so a null reference is acceptable and durable; a rule
        //requiring a value beside a real accepted ballot would refuse it.
        (FastAcceptor<string> nullAccepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(2, R1), null!);
        FastAcceptorState<string> nullSnapshot = nullAccepted.ToState();

        Assert.AreEqual(FastBallot.Classic(2, R1), nullSnapshot.AcceptedBallot);
        Assert.IsNull(nullSnapshot.AcceptedValue);
        Assert.AreEqual(nullSnapshot, FastAcceptor<string>.FromState(nullSnapshot).ToState());

        //The struct default at a real ballot is a legitimate accepted value, told apart from nothing-accepted
        //by the ballot and never by the value; a rule refusing the default under the comparer would refuse it.
        (FastAcceptor<int> zeroAccepted, _) = FastAcceptor<int>.Initial.Accept(FastBallot.Classic(2, R1), 0);
        FastAcceptorState<int> zeroSnapshot = zeroAccepted.ToState();

        Assert.AreEqual(FastBallot.Classic(2, R1), zeroSnapshot.AcceptedBallot);
        Assert.AreEqual(0, zeroSnapshot.AcceptedValue);
        Assert.AreEqual(zeroSnapshot, FastAcceptor<int>.FromState(zeroSnapshot).ToState());
    }


    /// <summary>
    /// The over-refusal sweep: a fixed script drives an acceptor through a fast write, a recovery prepare,
    /// a classic accept, a piggybacked retry, a chained fast round and a classic piggyback, and drives a
    /// second acceptor from initial through the zero-ballot retry that arms a fast round with nothing
    /// accepted; after every step the state must restore and re-snapshot equal. Any rule that refuses a
    /// reachable state fails here at the step that reaches it.
    /// </summary>
    [TestMethod]
    public void EveryStateAnAcceptorReachesRoundTrips()
    {
        List<(string Label, FastAcceptor<string> Acceptor)> stops = [("initial", FastAcceptor<string>.Initial)];

        (FastAcceptor<string> fastWritten, _) = FastAcceptor<string>.Initial.Accept(FastBallot.InitialFast(), "fast");
        stops.Add(("fast write at the initial round", fastWritten));

        (FastAcceptor<string> prepared, _) = fastWritten.Prepare(FastBallot.Classic(3, R1));
        stops.Add(("classic recovery prepare over the fast value", prepared));

        (FastAcceptor<string> classicAccepted, _) = prepared.Accept(FastBallot.Classic(3, R1), "classic");
        stops.Add(("classic accept at the promised round", classicAccepted));

        (FastAcceptor<string> retryRaised, _) = classicAccepted.Accept(FastBallot.Classic(3, R1), "classic", next: FastBallot.Fast(6));
        stops.Add(("idempotent retry arming the next fast round", retryRaised));

        (FastAcceptor<string> chainedFast, _) = retryRaised.Accept(FastBallot.Fast(6), "chained");
        stops.Add(("blind write at the armed fast round", chainedFast));

        (FastAcceptor<string> classicPiggyback, _) = chainedFast.Accept(FastBallot.Classic(7, R2), "carried", next: FastBallot.Classic(9, R2));
        stops.Add(("classic piggyback raising the promise", classicPiggyback));

        (FastAcceptor<string> zeroRetryRaised, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Zero, default!, next: FastBallot.Fast(9));
        stops.Add(("zero-ballot retry arming a fast round with nothing accepted", zeroRetryRaised));

        foreach((string label, FastAcceptor<string> acceptor) in stops)
        {
            FastAcceptorState<string> snapshot = acceptor.ToState();
            FastAcceptor<string> restored = FastAcceptor<string>.FromState(snapshot);

            Assert.AreEqual(snapshot, restored.ToState(), $"The state at '{label}' must restore and re-snapshot equal.");
        }

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{stops.Count} reachable states restored"));
    }


    /// <summary>
    /// The executable backing for the whole-write obligation on
    /// <see cref="PersistAcceptorDelegate{TValue}"/>: a per-field mix of two faithful snapshots passes every
    /// rule the restore owns and contradicts a reply already sent, so no rule substitutes for the store
    /// landing the write whole.
    /// </summary>
    [TestMethod]
    public void ATornMixOfTwoFaithfulSnapshotsRestoresAndContradictsAnAnsweredBallot()
    {
        //Two accepts, each answered, each snapshot faithful on its own.
        (FastAcceptor<string> first, bool firstAccepted) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(2, R1), "v");
        (FastAcceptor<string> second, bool secondAccepted) = first.Accept(FastBallot.Classic(3, R2), "w");

        Assert.IsTrue(firstAccepted);
        Assert.IsTrue(secondAccepted);

        //The store lands the second accept's ballots and loses its value: the newer accepted ballot beside
        //the older accepted value.
        FastAcceptorState<string> torn = second.ToState() with { AcceptedValue = first.ToState().AcceptedValue };

        FastAcceptor<string> restoredTorn = FastAcceptor<string>.FromState(torn);
        FastAcceptor<string> restoredWhole = FastAcceptor<string>.FromState(second.ToState());

        //Both restores answer a later recovery prepare, and they report the same accepted ballot carrying two
        //different values — the torn one contradicts the accept reply the proposer already acted on, and a
        //ballot may carry only one value.
        (_, FastPrepareResponse<string> tornResponse) = restoredTorn.Prepare(FastBallot.Classic(4, R3));
        (_, FastPrepareResponse<string> wholeResponse) = restoredWhole.Prepare(FastBallot.Classic(4, R3));

        Assert.IsTrue(tornResponse.Promised);
        Assert.IsTrue(wholeResponse.Promised);
        Assert.AreEqual(wholeResponse.AcceptedBallot, tornResponse.AcceptedBallot);
        Assert.AreEqual("v", tornResponse.AcceptedValue);
        Assert.AreEqual("w", wholeResponse.AcceptedValue);
    }


    /// <summary>
    /// The restored acceptor's value is load-bearing through a real recovery: one restored node among three
    /// carries the only accepted value, and the recovering proposer commits it rather than its own fallback.
    /// </summary>
    [TestMethod]
    public async Task AFastRoundSnapshotRestoresAndARecoveringProposerReadsItsValue()
    {
        //The snapshot is the exact durable state the fast path leaves behind: the pre-promised initial fast
        //ballot accepted with a value, nothing else moved.
        (FastAcceptor<string> fastWritten, _) = FastAcceptor<string>.Initial.Accept(FastBallot.InitialFast(), "fast");

        ConsensusNode<string> restored = new(FastAcceptor<string>.FromState(fastWritten.ToState()));
        ConsensusNode<string> freshA = new();
        ConsensusNode<string> freshB = new();

        ConsensusEndpointDelegate<string>[] endpoints =
        [
            (request, _) => ValueTask.FromResult(restored.Handle(request)),
            (request, _) => ValueTask.FromResult(freshA.Handle(request)),
            (request, _) => ValueTask.FromResult(freshB.Handle(request)),
        ];

        FastProposer<string> proposer = new(endpoints);
        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(2, R2), value => value ?? "fallback", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("fast", outcome.Value);
    }


    /// <summary>
    /// The retry-arm inertness the durability gate reads survives a restore: an identical redelivery to a
    /// node seeded with the restored acceptor answers from the same instance and costs no durable write.
    /// </summary>
    [TestMethod]
    public async Task AnIdenticalRetryAcrossARestoreStaysInertAndCostsNoWrite()
    {
        AcceptRequest<string> request = new(FastBallot.Classic(2, R1), "v");
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(request.Ballot, request.Value);

        FastAcceptor<string> restored = FastAcceptor<string>.FromState(accepted.ToState());
        ConsensusNode<string> node = new(restored);
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<FastAcceptor<string>> persisted = [];
        List<ConsensusReply<string>> replies = [];

        PersistAcceptorDelegate<string> persist = (acceptor, _) =>
        {
            persisted.Add(acceptor);

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        //The redelivery answered from the restored instance itself, so the reply cost nothing durable.
        Assert.AreSame(restored, node.Acceptor);
        Assert.IsEmpty(persisted);
        Assert.HasCount(1, replies);
        Assert.IsTrue(((AcceptReply<string>)replies[0]).Accepted);
    }


    /// <summary>
    /// The safety consequence of the restore, stated as protocol behavior: a restored promise still refuses
    /// the ballots it superseded, exactly as the acceptor that crashed would have. A restore that repaired or
    /// normalized the promise instead of restoring it would re-open the window the persist exists to close.
    /// </summary>
    [TestMethod]
    public void ARestoredPromiseStillRefusesTheBallotsItSuperseded()
    {
        (FastAcceptor<string> prepared, _) = FastAcceptor<string>.Initial.Prepare(FastBallot.Classic(5, R1));
        FastAcceptor<string> restored = FastAcceptor<string>.FromState(prepared.ToState());

        (FastAcceptor<string> afterStalePrepare, FastPrepareResponse<string> response) = restored.Prepare(FastBallot.Classic(3, R1));

        Assert.IsFalse(response.Promised);
        Assert.AreEqual(FastBallot.Classic(5, R1), response.ConflictingBallot);
        Assert.AreSame(restored, afterStalePrepare);

        (FastAcceptor<string> afterStaleAccept, bool accepted) = restored.Accept(FastBallot.Classic(3, R1), "x");

        Assert.IsFalse(accepted);
        Assert.AreSame(restored, afterStaleAccept);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
