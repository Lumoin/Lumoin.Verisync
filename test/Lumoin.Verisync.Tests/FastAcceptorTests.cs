using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class FastAcceptorTests
{
    private static ReplicaId R1 { get; } = Replica(1);


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
