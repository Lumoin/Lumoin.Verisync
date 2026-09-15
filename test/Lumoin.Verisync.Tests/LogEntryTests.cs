using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LogEntryTests
{
    [TestMethod]
    public void ExposesRequiredProperties()
    {
        byte[] digest = [1, 2, 3];
        byte[] canonical = [9, 9];
        LogEntry<string, string> entry = new()
        {
            Index = 0,
            PreviousDigest = null,
            Digest = digest,
            CanonicalBytes = canonical,
            Operation = "op",
            Proofs = ["controller"]
        };

        Assert.AreEqual(0UL, entry.Index);
        Assert.IsNull(entry.PreviousDigest);
        Assert.AreEqual("op", entry.Operation);
        Assert.HasCount(1, entry.Proofs);
    }


    [TestMethod]
    public void HeartbeatEntryHasNullOperation()
    {
        LogEntry<string, string> heartbeat = Make(index: 1, operation: null, digest: [4, 5]);

        Assert.IsNull(heartbeat.Operation);
    }


    [TestMethod]
    public void EqualityIsByIndexAndDigests()
    {
        LogEntry<string, string> left = Make(index: 2, operation: "x", digest: [7, 7, 7]);
        LogEntry<string, string> right = Make(index: 2, operation: "x", digest: [7, 7, 7]);

        Assert.AreEqual(left, right);
        Assert.IsTrue(left == right);
    }


    [TestMethod]
    public void EqualityIgnoresOperationAndProofs()
    {
        LogEntry<string, string> left = Make(index: 2, operation: "x", digest: [7, 7, 7]);
        LogEntry<string, string> right = Make(index: 2, operation: "y", digest: [7, 7, 7]);

        Assert.AreEqual(left, right);
    }


    [TestMethod]
    public void EqualityFailsForDifferentDigest()
    {
        LogEntry<string, string> left = Make(index: 2, operation: "x", digest: [7, 7, 7]);
        LogEntry<string, string> right = Make(index: 2, operation: "x", digest: [7, 7, 8]);

        Assert.AreNotEqual(left, right);
    }


    [TestMethod]
    public void EqualityFailsForDifferentIndex()
    {
        LogEntry<string, string> left = Make(index: 1, operation: "x", digest: [7]);
        LogEntry<string, string> right = Make(index: 2, operation: "x", digest: [7]);

        Assert.AreNotEqual(left, right);
    }


    private static LogEntry<string, string> Make(ulong index, string? operation, byte[] digest)
    {
        return new LogEntry<string, string>
        {
            Index = index,
            PreviousDigest = null,
            Digest = digest,
            CanonicalBytes = digest,
            Operation = operation,
            Proofs = ImmutableArray<string>.Empty
        };
    }


    /// <summary>Returns a null reference through an opaque call so equality tests that compare against
    /// null cannot be constant-folded by the compiler's nullable/impossible-condition analysis.</summary>
    private static LogEntry<string, string>? NullEntry() => null;


    /// <summary>Pins that entries differing only in <see cref="LogEntry{TOperation,TProof}.PreviousDigest"/>
    /// (one null, one populated) are not equal.</summary>
    [TestMethod]
    public void PreviousDigestWithOnlyLeftNullAreNotEqual()
    {
        LogEntry<string, string> left = new()
        {
            Index = 3,
            PreviousDigest = null,
            Digest = new byte[] { 5, 5 },
            CanonicalBytes = new byte[] { 5, 5 },
            Operation = "x",
            Proofs = ImmutableArray<string>.Empty
        };
        LogEntry<string, string> right = new()
        {
            Index = 3,
            PreviousDigest = new byte[] { 9, 9 },
            Digest = new byte[] { 5, 5 },
            CanonicalBytes = new byte[] { 5, 5 },
            Operation = "x",
            Proofs = ImmutableArray<string>.Empty
        };

        Assert.AreNotEqual(left, right);
    }


    /// <summary>Pins that <see cref="LogEntry{TOperation,TProof}.GetHashCode"/> reflects the digest bytes
    /// rather than being a function of Index alone.</summary>
    [TestMethod]
    public void GetHashCodeDiffersForDifferentDigest()
    {
        LogEntry<string, string> left = Make(index: 5, operation: "x", digest: [1, 2, 3]);
        LogEntry<string, string> right = Make(index: 5, operation: "x", digest: [4, 5, 6]);

        Assert.AreNotEqual(left.GetHashCode(), right.GetHashCode());
    }


    /// <summary>Pins that <c>operator ==</c> with a null left operand and a non-null right operand
    /// returns false without throwing.</summary>
    [TestMethod]
    public void OperatorEqualityWithNullLeftAndNonNullRightIsFalse()
    {
        LogEntry<string, string>? left = null;
        LogEntry<string, string> right = Make(index: 1, operation: "x", digest: [1]);

        bool result = left == right;

        Assert.IsFalse(result);
    }


    /// <summary>Pins that <c>operator !=</c> returns true for entries with different digests.</summary>
    [TestMethod]
    public void OperatorInequalityReturnsTrueForDifferentDigests()
    {
        LogEntry<string, string> left = Make(index: 1, operation: "x", digest: [1]);
        LogEntry<string, string> right = Make(index: 1, operation: "x", digest: [2]);

        Assert.IsTrue(left != right);
    }


    /// <summary>Pins that <c>operator ==</c> with both operands null returns true.</summary>
    [TestMethod]
    public void OperatorEqualityWithBothNullIsTrue()
    {
        LogEntry<string, string>? left = NullEntry();
        LogEntry<string, string>? right = NullEntry();

        Assert.IsTrue(left == right);
    }


    /// <summary>
    /// Pins that the <c>init</c> accessor on <see cref="RaftLogEntry{TCommand}.Term"/> revalidates and actually
    /// assigns the term on a <c>with</c> expression the same way the primary constructor does: a term below
    /// <see cref="Term.First"/> is rejected, naming the public property, and a valid term is written through to
    /// the cloned record's backing field rather than left at its old value.
    /// </summary>
    [TestMethod]
    public void WithExpressionValidatesAndAssignsTerm()
    {
        RaftLogEntry<string> entry = new(Term.First, "x");

        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = entry with { Term = Term.Zero });
        Assert.AreEqual("Term", exception.ParamName);

        RaftLogEntry<string> advanced = entry with { Term = new Term(2) };
        Assert.AreEqual(new Term(2), advanced.Term);
    }


    /// <summary>Pins that entries with equal non-null <see cref="LogEntry{TOperation,TProof}.PreviousDigest"/>
    /// byte content compare equal.</summary>
    [TestMethod]
    public void PreviousDigestEqualNonNullValuesAreEqual()
    {
        LogEntry<string, string> left = new()
        {
            Index = 4,
            PreviousDigest = new byte[] { 3, 3, 3 },
            Digest = new byte[] { 6, 6 },
            CanonicalBytes = new byte[] { 6, 6 },
            Operation = "x",
            Proofs = ImmutableArray<string>.Empty
        };
        LogEntry<string, string> right = new()
        {
            Index = 4,
            PreviousDigest = new byte[] { 3, 3, 3 },
            Digest = new byte[] { 6, 6 },
            CanonicalBytes = new byte[] { 6, 6 },
            Operation = "x",
            Proofs = ImmutableArray<string>.Empty
        };

        Assert.AreEqual(left, right);
    }


    /// <summary>Pins that <see cref="LogEntry{TOperation,TProof}.Equals(LogEntry{TOperation,TProof}?)"/>
    /// returns true when compared against the same reference (the ReferenceEquals fast path).</summary>
    [TestMethod]
    public void EqualsReturnsTrueForSameReference()
    {
        LogEntry<string, string> entry = Make(index: 1, operation: "x", digest: [1]);

        Assert.IsTrue(entry.Equals(entry));
    }


    /// <summary>
    /// Pins that <see cref="LogEntry{TOperation,TProof}.Equals(LogEntry{TOperation,TProof}?)"/> returns false
    /// for a null other, exercising the overload's own null fast path rather than a comparison of two values.
    /// </summary>
    [TestMethod]
    public void AnEntryIsNotEqualToANullOther()
    {
        LogEntry<string, string> entry = Make(index: 1, operation: "x", digest: [1]);

        Assert.IsFalse(entry.Equals(NullEntry()));
    }
}
