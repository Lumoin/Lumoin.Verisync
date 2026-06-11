using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class StabilityFrontierTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);
    private static ReplicaId R3 { get; } = Replica(3);


    [TestMethod]
    public void FrontierIsTheElementWiseMinimum()
    {
        //R1 has seen (R1:3, R2:1); R2 has seen (R1:2, R2:4): the group floor is (R1:2, R2:1).
        GossipDigest first = new(R1, Clock((R1, 3), (R2, 1)));
        GossipDigest second = new(R2, Clock((R1, 2), (R2, 4)));

        VectorClock frontier = StabilityFrontier.Compute([first, second]);

        Assert.AreEqual(2, frontier[R1]);
        Assert.AreEqual(1, frontier[R2]);
    }


    [TestMethod]
    public void ASilentMemberPinsTheFloorAtZero()
    {
        //R3 has observed nothing of R2, so nothing of R2 is stable, no matter how far others have seen.
        GossipDigest first = new(R1, Clock((R1, 5), (R2, 7)));
        GossipDigest second = new(R2, Clock((R1, 5), (R2, 7)));
        GossipDigest silent = new(R3, Clock((R1, 5)));

        VectorClock frontier = StabilityFrontier.Compute([first, second, silent]);

        Assert.AreEqual(5, frontier[R1]);
        Assert.AreEqual(0, frontier[R2]);
    }


    [TestMethod]
    public void ASingleMemberGroupIsItsOwnFrontier()
    {
        GossipDigest only = new(R1, Clock((R1, 4), (R2, 2)));

        VectorClock frontier = StabilityFrontier.Compute([only]);

        Assert.AreEqual(only.Summary, frontier);
    }


    [TestMethod]
    public void DuplicateMemberDigestsOnlyLowerTheFrontier()
    {
        //An older duplicate from the same member is harmless: the minimum stays conservative.
        GossipDigest newer = new(R1, Clock((R1, 6)));
        GossipDigest older = new(R1, Clock((R1, 2)));
        GossipDigest peer = new(R2, Clock((R1, 9)));

        VectorClock frontier = StabilityFrontier.Compute([newer, older, peer]);

        Assert.AreEqual(2, frontier[R1]);
    }


    [TestMethod]
    public void ComputeRejectsInvalidInput()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => StabilityFrontier.Compute(null!));
        Assert.ThrowsExactly<ArgumentException>(() => StabilityFrontier.Compute([]));
        Assert.ThrowsExactly<ArgumentNullException>(() => StabilityFrontier.Compute([null!]));
    }


    private static VectorClock Clock(params (ReplicaId Replica, int Count)[] entries)
    {
        VectorClock clock = VectorClock.Empty;
        foreach((ReplicaId replica, int count) in entries)
        {
            for(int i = 0; i < count; i++)
            {
                clock = clock.Increment(replica);
            }
        }

        return clock;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
