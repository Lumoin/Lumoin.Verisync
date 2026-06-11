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
}
