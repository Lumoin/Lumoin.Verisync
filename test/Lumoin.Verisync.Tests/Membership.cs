using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Builds membership lists for tests whose subject is not the store binding.
/// </summary>
/// <remarks>
/// <para>
/// A member pairs a replica with the store incarnation admitted to answer for it. Most vectors here are about
/// something else — a codec, a quorum count, a socket — and only need a membership that is well formed and
/// reproducible, so the incarnation is derived from the replica's own bytes rather than minted. Deriving it
/// keeps a member reconstructible from its replica alone, which is what lets a vector build the same
/// membership twice and compare the two.
/// </para>
/// <para>
/// A derived incarnation is a test convenience and not the shape a deployment uses. A real store mints its
/// incarnation, precisely so that it is not a function of the identity an operator hands out; a vector about
/// the binding itself therefore states its incarnations explicitly instead of calling this.
/// </para>
/// </remarks>
internal static class Membership
{
    /// <summary>The membership listing <paramref name="replicas"/> in the given order.</summary>
    /// <param name="replicas">The replicas to list, in their configured order.</param>
    /// <returns>The member list.</returns>
    internal static ImmutableArray<HostId> Of(params ReplicaId[] replicas)
    {
        ImmutableArray<HostId>.Builder members = ImmutableArray.CreateBuilder<HostId>(replicas.Length);
        foreach(ReplicaId replica in replicas)
        {
            members.Add(Member(replica));
        }

        return members.ToImmutable();
    }


    /// <summary>The member for <paramref name="replica"/> under its derived store incarnation.</summary>
    /// <param name="replica">The replica to admit.</param>
    /// <returns>The member.</returns>
    internal static HostId Member(ReplicaId replica)
    {
        return new HostId(replica, IncarnationFor(replica));
    }


    /// <summary>
    /// A second store for <paramref name="replica"/>: the same identity under an incarnation the derived one
    /// never equals.
    /// </summary>
    /// <param name="replica">The replica whose store is replaced.</param>
    /// <returns>The member naming the replacement store.</returns>
    /// <remarks>
    /// This is what a vector about the binding reaches for: a host holding this member is the same replica as
    /// <see cref="Member(ReplicaId)"/>'s and a different store, which is the whole hazard in one value.
    /// </remarks>
    internal static HostId Restored(ReplicaId replica)
    {
        Span<byte> buffer = stackalloc byte[StoreIncarnation.Size];
        replica.AsSpan()[..StoreIncarnation.Size].CopyTo(buffer);

        //The derived incarnation copies the replica's leading bytes unchanged, so inverting every one of them
        //cannot collide with it whatever the replica holds.
        for(int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (byte)~buffer[index];
        }

        return new HostId(replica, StoreIncarnation.FromSpan(buffer));
    }


    private static StoreIncarnation IncarnationFor(ReplicaId replica)
    {
        return StoreIncarnation.FromSpan(replica.AsSpan()[..StoreIncarnation.Size]);
    }
}
