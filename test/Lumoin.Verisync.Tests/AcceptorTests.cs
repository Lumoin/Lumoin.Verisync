using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class AcceptorTests
{
    private static ReplicaId R1 { get; } = Replica(1);


    [TestMethod]
    public void InitialHasNoPromiseOrValue()
    {
        Assert.IsNull(Acceptor<string>.Initial.Promise);
        Assert.IsNull(Acceptor<string>.Initial.AcceptedBallot);
    }


    [TestMethod]
    public void PrepareHigherBallotPromises()
    {
        (Acceptor<string> acceptor, PrepareResponse<string> response) = Acceptor<string>.Initial.Prepare(new Ballot(1, R1));

        Assert.IsTrue(response.Promised);
        Assert.IsNull(response.AcceptedBallot);
        Assert.AreEqual(new Ballot(1, R1), acceptor.Promise);
    }


    [TestMethod]
    public void PrepareLowerBallotIsRejected()
    {
        (Acceptor<string> promised, _) = Acceptor<string>.Initial.Prepare(new Ballot(2, R1));

        (Acceptor<string> after, PrepareResponse<string> response) = promised.Prepare(new Ballot(1, R1));

        Assert.IsFalse(response.Promised);
        Assert.AreEqual(new Ballot(2, R1), after.Promise);
    }


    [TestMethod]
    public void AcceptStoresBallotAndValue()
    {
        (Acceptor<string> promised, _) = Acceptor<string>.Initial.Prepare(new Ballot(1, R1));

        (Acceptor<string> accepted, bool ok) = promised.Accept(new Ballot(1, R1), "x");

        Assert.IsTrue(ok);
        Assert.AreEqual(new Ballot(1, R1), accepted.AcceptedBallot);
        Assert.AreEqual("x", accepted.AcceptedValue);
    }


    [TestMethod]
    public void AcceptBelowPromiseIsRejected()
    {
        (Acceptor<string> promised, _) = Acceptor<string>.Initial.Prepare(new Ballot(2, R1));

        (Acceptor<string> after, bool ok) = promised.Accept(new Ballot(1, R1), "x");

        Assert.IsFalse(ok);
        Assert.IsNull(after.AcceptedValue);
    }


    [TestMethod]
    public void PrepareReturnsPreviouslyAcceptedValue()
    {
        (Acceptor<string> promised, _) = Acceptor<string>.Initial.Prepare(new Ballot(1, R1));
        (Acceptor<string> accepted, _) = promised.Accept(new Ballot(1, R1), "x");

        (_, PrepareResponse<string> response) = accepted.Prepare(new Ballot(2, R1));

        Assert.IsTrue(response.Promised);
        Assert.AreEqual(new Ballot(1, R1), response.AcceptedBallot);
        Assert.AreEqual("x", response.AcceptedValue);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
