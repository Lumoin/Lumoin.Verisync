using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The wire envelope that carries exactly one Raft protocol message between two replicas, tagged with the
/// sender so the receiving <see cref="RaftRunner{TCommand}"/> knows which peer to attribute it to and where
/// to address the reply.
/// </summary>
/// <typeparam name="TCommand">The application command type replicated by the log.</typeparam>
/// <param name="From">The replica that sent this envelope.</param>
/// <param name="VoteRequest">The carried <see cref="RequestVoteRequest"/>, or <see langword="null"/>.</param>
/// <param name="VoteReply">The carried <see cref="RequestVoteReply"/>, or <see langword="null"/>.</param>
/// <param name="AppendRequest">The carried <see cref="AppendEntriesRequest{TCommand}"/>, or <see langword="null"/>.</param>
/// <param name="AppendReply">The carried <see cref="AppendEntriesReply"/>, or <see langword="null"/>.</param>
/// <remarks>
/// Exactly one of the four payloads is non-null; an envelope carrying none or more than one is not a valid
/// protocol message. The four static factories — <see cref="ForVoteRequest"/>, <see cref="ForVoteReply"/>,
/// <see cref="ForAppendRequest"/>, and <see cref="ForAppendReply"/> — are the only documented construction
/// path, and both the runner and the codec fail closed on an envelope that violates the invariant
/// (<see cref="ArgumentException"/> in process, <c>JsonException</c> on the wire). The primary constructor
/// stays public so the record's value semantics and <c>with</c> expressions work, but constructing a
/// malformed envelope through it is a caller error caught downstream.
/// </remarks>
public sealed record RaftEnvelope<TCommand>(
    ReplicaId From,
    RequestVoteRequest? VoteRequest,
    RequestVoteReply? VoteReply,
    AppendEntriesRequest<TCommand>? AppendRequest,
    AppendEntriesReply? AppendReply)
{
    /// <summary>
    /// Builds an envelope carrying a <see cref="RequestVoteRequest"/> from <paramref name="from"/>.
    /// </summary>
    /// <param name="from">The candidate sending the request.</param>
    /// <param name="request">The vote request to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="request"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    public static RaftEnvelope<TCommand> ForVoteRequest(ReplicaId from, RequestVoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RaftEnvelope<TCommand>(from, request, null, null, null);
    }


    /// <summary>
    /// Builds an envelope carrying a <see cref="RequestVoteReply"/> from <paramref name="from"/>.
    /// </summary>
    /// <param name="from">The voter sending the reply.</param>
    /// <param name="reply">The vote reply to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="reply"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reply"/> is <see langword="null"/>.</exception>
    public static RaftEnvelope<TCommand> ForVoteReply(ReplicaId from, RequestVoteReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        return new RaftEnvelope<TCommand>(from, null, reply, null, null);
    }


    /// <summary>
    /// Builds an envelope carrying an <see cref="AppendEntriesRequest{TCommand}"/> from <paramref name="from"/>.
    /// </summary>
    /// <param name="from">The leader sending the request.</param>
    /// <param name="request">The append request to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="request"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    public static RaftEnvelope<TCommand> ForAppendRequest(ReplicaId from, AppendEntriesRequest<TCommand> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RaftEnvelope<TCommand>(from, null, null, request, null);
    }


    /// <summary>
    /// Builds an envelope carrying an <see cref="AppendEntriesReply"/> from <paramref name="from"/>.
    /// </summary>
    /// <param name="from">The follower sending the reply.</param>
    /// <param name="reply">The append reply to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="reply"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reply"/> is <see langword="null"/>.</exception>
    public static RaftEnvelope<TCommand> ForAppendReply(ReplicaId from, AppendEntriesReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        return new RaftEnvelope<TCommand>(from, null, null, null, reply);
    }


    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless exactly one payload is non-null. The runner calls this
    /// before dispatching an inbound envelope, so a malformed message fails closed rather than being silently
    /// dropped or matching no dispatch arm.
    /// </summary>
    /// <param name="paramName">The parameter name to attribute the exception to.</param>
    /// <exception cref="ArgumentException">Thrown if zero, or more than one, payload is non-null.</exception>
    internal void EnsureSinglePayload(string paramName)
    {
        int payloadCount = 0;
        if(VoteRequest is not null)
        {
            payloadCount++;
        }

        if(VoteReply is not null)
        {
            payloadCount++;
        }

        if(AppendRequest is not null)
        {
            payloadCount++;
        }

        if(AppendReply is not null)
        {
            payloadCount++;
        }

        if(payloadCount != 1)
        {
            throw new ArgumentException($"A Raft envelope must carry exactly one payload, but it carries {payloadCount}.", paramName);
        }
    }
}
