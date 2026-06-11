using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class FastAcceptorTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void InitialIsPrePromisedToFastBallot()
    {
        Assert.AreEqual(FastBallot.InitialFast(), FastAcceptor<string>.Initial.Promised);
        Assert.IsTrue(FastAcceptor<string>.Initial.AcceptedBallot.IsZero);
    }


    [TestMethod]
    public void FastAcceptSucceedsWithoutPrepare()
    {
        (FastAcceptor<string> acceptor, bool accepted) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x");

        Assert.IsTrue(accepted);
        Assert.AreEqual("x", acceptor.AcceptedValue);
        Assert.AreEqual(FastBallot.Fast(1), acceptor.AcceptedBallot);
    }


    [TestMethod]
    public void AcceptRetryOfSamePairIsIdempotent()
    {
        (FastAcceptor<string> once, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x");

        (FastAcceptor<string> twice, bool accepted) = once.Accept(FastBallot.Fast(1), "x");

        Assert.IsTrue(accepted);
        Assert.AreEqual("x", twice.AcceptedValue);
    }


    [TestMethod]
    public void DifferentValueAtSameBallotIsRejected()
    {
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x");

        (FastAcceptor<string> after, bool ok) = accepted.Accept(FastBallot.Fast(1), "y");

        Assert.IsFalse(ok);
        Assert.AreEqual("x", after.AcceptedValue);
    }


    [TestMethod]
    public void AcceptBelowPromiseIsRejected()
    {
        (FastAcceptor<string> promised, _) = FastAcceptor<string>.Initial.Prepare(FastBallot.Classic(2, R1));

        (_, bool ok) = promised.Accept(FastBallot.Fast(1), "x");

        Assert.IsFalse(ok);
    }


    [TestMethod]
    public void AcceptRaisesThePromiseToTheAcceptedBallot()
    {
        //An accept without a preceding prepare must lift the promise with it; otherwise the promise
        //trails the accepted ballot and a stale lower-ballot accept could later overwrite this value.
        (FastAcceptor<string> accepted, bool ok) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(1, R1), "v");

        Assert.IsTrue(ok);
        Assert.AreEqual(FastBallot.Classic(1, R1), accepted.Promised);
    }


    [TestMethod]
    public void StaleFastAcceptAfterAClassicAcceptIsRejected()
    {
        //The acceptor took a classic ballot it was never prepared for — the normal accept-without-prepare
        //case. A delayed duplicate of the earlier fast write must not regress its accepted state.
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(1, R1), "chosen");

        (FastAcceptor<string> after, bool ok) = accepted.Accept(FastBallot.Fast(1), "stale");

        Assert.IsFalse(ok);
        Assert.AreEqual("chosen", after.AcceptedValue);
        Assert.AreEqual(FastBallot.Classic(1, R1), after.AcceptedBallot);
    }


    [TestMethod]
    public void StaleLowerClassicAcceptIsRejected()
    {
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(1, R2), "chosen");

        (FastAcceptor<string> after, bool ok) = accepted.Accept(FastBallot.Classic(1, R1), "stale");

        Assert.IsFalse(ok);
        Assert.AreEqual("chosen", after.AcceptedValue);
    }


    [TestMethod]
    public void FastAcceptAboveThePromiseIsRejected()
    {
        //Only the pre-promised initial fast round is blind-writable. A higher fast round has had no
        //coordinating recovery, so writing it blindly could overwrite a value chosen below.
        (_, bool ok) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(2), "y");

        Assert.IsFalse(ok);
    }


    [TestMethod]
    public void SuccessfulAcceptWithNextRaisesThePromiseToNext()
    {
        //The piggyback establishes the next fast round: the promise rises past the accepted ballot to next,
        //while the accepted pair records the value actually taken.
        (FastAcceptor<string> accepted, bool ok) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x", FastBallot.Fast(2));

        Assert.IsTrue(ok);
        Assert.AreEqual(FastBallot.Fast(2), accepted.Promised);
        Assert.AreEqual(FastBallot.Fast(1), accepted.AcceptedBallot);
        Assert.AreEqual("x", accepted.AcceptedValue);
    }


    [TestMethod]
    public void NextBelowTheResultingPromiseDoesNotLowerIt()
    {
        //An accept at classic(3) leaves the promise at classic(3); a piggybacked fast(2) below it is taken
        //as a maximum and must not regress the promise.
        (FastAcceptor<string> accepted, bool ok) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(3, R1), "v", FastBallot.Fast(2));

        Assert.IsTrue(ok);
        Assert.AreEqual(FastBallot.Classic(3, R1), accepted.Promised);
        Assert.AreEqual(FastBallot.Classic(3, R1), accepted.AcceptedBallot);
    }


    [TestMethod]
    public void RejectedAcceptIgnoresNextEntirely()
    {
        //The acceptor promised classic(2); a fast(1) accept below the promise is rejected and the piggybacked
        //fast(5) must not raise the promise — only a successful accept ever applies the next-raise.
        (FastAcceptor<string> promised, _) = FastAcceptor<string>.Initial.Prepare(FastBallot.Classic(2, R1));

        (FastAcceptor<string> after, bool ok) = promised.Accept(FastBallot.Fast(1), "x", FastBallot.Fast(5));

        Assert.IsFalse(ok);
        Assert.AreEqual(FastBallot.Classic(2, R1), after.Promised);
    }


    [TestMethod]
    public void IdempotentRetryWithNextRaisesThePromise()
    {
        //A retried accept of the already-accepted pair stays idempotent, but a piggybacked next ballot must
        //still raise the promise so a delayed duplicate can establish the next fast round.
        (FastAcceptor<string> once, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x");

        (FastAcceptor<string> twice, bool ok) = once.Accept(FastBallot.Fast(1), "x", FastBallot.Fast(2));

        Assert.IsTrue(ok);
        Assert.AreEqual(FastBallot.Fast(2), twice.Promised);
        Assert.AreEqual(FastBallot.Fast(1), twice.AcceptedBallot);
        Assert.AreEqual("x", twice.AcceptedValue);
    }


    [TestMethod]
    public void PiggybackedRaiseMakesTheNextFastRoundWritableButTheHoleStaysClosed()
    {
        //An acceptor that took the piggyback to fast(2) now satisfies the equality rule at fast(2) and
        //accepts it without a prepare — the recurring fast round.
        (FastAcceptor<string> raised, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x", FastBallot.Fast(2));

        (FastAcceptor<string> afterRaise, bool raisedAccepts) = raised.Accept(FastBallot.Fast(2), "y");

        Assert.IsTrue(raisedAccepts);
        Assert.AreEqual("y", afterRaise.AcceptedValue);
        Assert.AreEqual(FastBallot.Fast(2), afterRaise.AcceptedBallot);

        //An acceptor that never saw the piggyback still rejects fast(2) via the equality rule: a blind write
        //at an un-established fast round remains impossible, so the hole stays closed.
        (FastAcceptor<string> withoutRaise, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x");

        (_, bool unraisedAccepts) = withoutRaise.Accept(FastBallot.Fast(2), "y");

        Assert.IsFalse(unraisedAccepts);
    }


    [TestMethod]
    public void FastPrepareIsRejected()
    {
        //Promising a fast ballot would re-open uncoordinated blind writes at that round; prepares are
        //classic-only.
        (FastAcceptor<string> after, FastPrepareResponse<string> response) = FastAcceptor<string>.Initial.Prepare(FastBallot.Fast(2));

        Assert.IsFalse(response.Promised);
        Assert.AreEqual(FastAcceptor<string>.Initial.Promised, after.Promised);
    }


    [TestMethod]
    public void PrepareReturnsPreviouslyAcceptedValue()
    {
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Fast(1), "x");

        (_, FastPrepareResponse<string> response) = accepted.Prepare(FastBallot.Classic(1, R1));

        Assert.IsTrue(response.Promised);
        Assert.AreEqual(FastBallot.Fast(1), response.AcceptedBallot);
        Assert.AreEqual("x", response.AcceptedValue);
    }


    [TestMethod]
    public void PrepareBelowPromiseIsRejected()
    {
        (FastAcceptor<string> promised, _) = FastAcceptor<string>.Initial.Prepare(FastBallot.Classic(2, R1));

        (_, FastPrepareResponse<string> response) = promised.Prepare(FastBallot.Classic(1, R1));

        Assert.IsFalse(response.Promised);
        Assert.AreEqual(FastBallot.Classic(2, R1), response.ConflictingBallot);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
