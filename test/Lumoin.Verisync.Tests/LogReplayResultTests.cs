using System.Collections.Immutable;

using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The log replay result's unit suite. A result is compared by value across its entry, state,
/// classification and error, both directly and through the equality operators, which delegate to
/// that same value equality rather than reference identity.
/// </summary>
[TestClass]
internal sealed class LogReplayResultTests
{
    /// <summary>Pins that <c>operator ==</c> delegates to value equality (<c>left.Equals(right)</c>) when both operands are non-null.</summary>
    [TestMethod]
    public void OperatorEqualsReturnsTrueForEqualNonNullInstances()
    {
        LogReplayResult<string, string, string> left = Make(MakeEntry(1, [1]), LogEntryClassification.Update, error: null);
        LogReplayResult<string, string, string> right = Make(MakeEntry(1, [1]), LogEntryClassification.Update, error: null);

        Assert.IsTrue(left == right);
    }


    /// <summary>Pins that <c>operator ==</c> treats two null references as equal without dereferencing either side.</summary>
    [TestMethod]
    public void OperatorEqualsReturnsTrueForBothNull()
    {
        LogReplayResult<string, string, string>? left = NullResult();
        LogReplayResult<string, string, string>? right = NullResult();

        Assert.IsTrue(left == right);
    }


    /// <summary>Pins that <c>operator !=</c> returns false for two value-equal, non-null instances.</summary>
    [TestMethod]
    public void OperatorNotEqualsReturnsFalseForEqualInstances()
    {
        LogReplayResult<string, string, string> left = Make(MakeEntry(1, [1]), LogEntryClassification.Update, error: null);
        LogReplayResult<string, string, string> right = Make(MakeEntry(1, [1]), LogEntryClassification.Update, error: null);

        Assert.IsFalse(left != right);
    }


    /// <summary>Pins that a mismatched <c>Classification</c> alone makes two results unequal, even when Entry, State and Error all match.</summary>
    [TestMethod]
    public void EqualityFailsWhenOnlyClassificationDiffers()
    {
        LogReplayResult<string, string, string> left = Make(MakeEntry(1, [1]), LogEntryClassification.Update, error: "boom");
        LogReplayResult<string, string, string> right = Make(MakeEntry(1, [1]), LogEntryClassification.Genesis, error: "boom");

        Assert.AreNotEqual(left, right);
    }


    /// <summary>Pins that two distinct instances are compared by property value rather than being treated as equal merely for not being the same reference.</summary>
    [TestMethod]
    public void EqualsUsesPropertyComparisonForDistinctInstances()
    {
        LogReplayResult<string, string, string> left = Make(MakeEntry(1, [1]), LogEntryClassification.Update, error: null);
        LogReplayResult<string, string, string> right = Make(MakeEntry(2, [2]), LogEntryClassification.Update, error: null);

        Assert.AreNotEqual(left, right);
    }


    /// <summary>Pins that <see cref="LogReplayResult{TState, TOperation, TProof}.Equals(LogReplayResult{TState, TOperation, TProof}?)"/> returns false, not true or an exception, when the argument is null.</summary>
    [TestMethod]
    public void EqualsReturnsFalseForNullOther()
    {
        LogReplayResult<string, string, string> left = Make(MakeEntry(0, [1]), LogEntryClassification.Update, error: null);

        Assert.IsFalse(left.Equals(null));
    }


    /// <summary>Pins that a result equals itself by reference, per <see cref="IEquatable{T}"/> reflexivity.</summary>
    [TestMethod]
    public void EqualsIsReflexiveForSameReference()
    {
        LogReplayResult<string, string, string> left = Make(MakeEntry(1, [2]), LogEntryClassification.Update, error: null);

        Assert.IsTrue(left.Equals(left));
    }


    /// <summary>Pins that a mismatched <c>Entry</c> alone makes two results unequal, even when State, Classification and Error all match, so a matching Error cannot paper over a differing Entry.</summary>
    [TestMethod]
    public void EqualityFailsWhenOnlyEntryDiffers()
    {
        LogReplayResult<string, string, string> left = Make(MakeEntry(1, [1]), LogEntryClassification.Update, error: "boom");
        LogReplayResult<string, string, string> right = Make(MakeEntry(2, [2]), LogEntryClassification.Update, error: "boom");

        Assert.AreNotEqual(left, right);
    }


    /// <summary>Builds a result over <paramref name="entry"/> with a fixed active state, the given classification and error.</summary>
    private static LogReplayResult<string, string, string> Make(LogEntry<string, string> entry, LogEntryClassification classification, string? error)
    {
        return new LogReplayResult<string, string, string>
        {
            Entry = entry,
            State = new ActiveLogState<string>("s"),
            Classification = classification,
            Error = error
        };
    }


    /// <summary>Builds an entry at <paramref name="index"/> whose digest and canonical bytes are both <paramref name="digest"/>.</summary>
    private static LogEntry<string, string> MakeEntry(ulong index, byte[] digest)
    {
        return new LogEntry<string, string>
        {
            Index = index,
            PreviousDigest = null,
            Digest = digest,
            CanonicalBytes = digest,
            Operation = "op",
            Proofs = ImmutableArray<string>.Empty
        };
    }


    /// <summary>Returns a null reference through an opaque call so equality tests that compare against
    /// null cannot be constant-folded by the compiler's nullable/impossible-condition analysis.</summary>
    private static LogReplayResult<string, string, string>? NullResult() => null;
}
