using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class CasPaxosRegisterTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void WithAcceptorsRejectsNonPositiveCount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CasPaxosRegister<string>.WithAcceptors(0));
    }


    [TestMethod]
    public void ExposesAcceptorCount()
    {
        Assert.AreEqual(5, CasPaxosRegister<string>.WithAcceptors(5).AcceptorCount);
    }


    [TestMethod]
    public void ChangeRejectsNullUpdate()
    {
        CasPaxosRegister<string> register = CasPaxosRegister<string>.WithAcceptors(3);

        Assert.ThrowsExactly<ArgumentNullException>(() => register.Change(new Ballot(1, R1), null!));
    }


    [TestMethod]
    public void SingleChangeChoosesValue()
    {
        CasPaxosRegister<string> register = CasPaxosRegister<string>.WithAcceptors(3);

        (_, ChangeOutcome<string> outcome) = register.Change(new Ballot(1, R1), _ => "a");

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("a", outcome.Value);
    }


    [TestMethod]
    public void SequentialChangesEvolveValue()
    {
        CasPaxosRegister<string> register0 = CasPaxosRegister<string>.WithAcceptors(3);
        (CasPaxosRegister<string> register1, _) = register0.Change(new Ballot(1, R1), _ => "a");

        (_, ChangeOutcome<string> outcome) = register1.Change(new Ballot(2, R1), current => current + "b");

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("ab", outcome.Value);
    }


    [TestMethod]
    public void ChangeRecoversCurrentValueAcrossProposers()
    {
        CasPaxosRegister<string> register0 = CasPaxosRegister<string>.WithAcceptors(3);
        (CasPaxosRegister<string> register1, _) = register0.Change(new Ballot(1, R1), _ => "x");

        (_, ChangeOutcome<string> outcome) = register1.Change(new Ballot(2, R2), current => current ?? "default");

        Assert.AreEqual("x", outcome.Value);
    }


    [TestMethod]
    public void LowerBallotAfterHigherIsNotChosen()
    {
        CasPaxosRegister<string> register0 = CasPaxosRegister<string>.WithAcceptors(3);
        (CasPaxosRegister<string> register1, _) = register0.Change(new Ballot(2, R1), _ => "a");

        (_, ChangeOutcome<string> outcome) = register1.Change(new Ballot(1, R1), _ => "b");

        Assert.IsFalse(outcome.IsChosen);
    }


    [TestMethod]
    public void ChosenValuePersistsAfterFailedLowerBallot()
    {
        CasPaxosRegister<string> register0 = CasPaxosRegister<string>.WithAcceptors(3);
        (CasPaxosRegister<string> register1, _) = register0.Change(new Ballot(2, R1), _ => "a");
        (CasPaxosRegister<string> register2, _) = register1.Change(new Ballot(1, R1), _ => "b");

        //A later higher ballot still recovers the original chosen value, never the rejected one.
        (_, ChangeOutcome<string> outcome) = register2.Change(new Ballot(3, R2), current => current ?? "lost");

        Assert.AreEqual("a", outcome.Value);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
