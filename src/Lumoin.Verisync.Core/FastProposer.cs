using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Drives the Fast CASPaxos protocol from the proposer side over a set of acceptor endpoints. It depends only
/// on <see cref="ConsensusEndpointDelegate{TValue}"/>, so the same proposer runs over in-process calls, in-memory
/// channels, or sockets.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <see cref="TryFastWriteAsync"/> sends an accept directly to every acceptor (the leaderless fast path) and
/// reports whether a fast quorum accepted. <see cref="RecoverAsync"/> runs a classic ballot — prepare, recover
/// the value (tallying the fast-round winner), apply the change, accept — when the fast round was contended.
/// </remarks>
public sealed class FastProposer<TValue>
{
    private IReadOnlyList<ConsensusEndpointDelegate<TValue>> Acceptors { get; }


    /// <summary>
    /// Initializes a proposer over <paramref name="acceptors"/>.
    /// </summary>
    /// <param name="acceptors">The acceptor endpoints.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="acceptors"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="acceptors"/> is empty.</exception>
    public FastProposer(IReadOnlyList<ConsensusEndpointDelegate<TValue>> acceptors)
    {
        ArgumentNullException.ThrowIfNull(acceptors);
        if(acceptors.Count == 0)
        {
            throw new ArgumentException("At least one acceptor is required.", nameof(acceptors));
        }

        Acceptors = acceptors;
    }


    /// <summary>The fast-quorum size: a supermajority of <c>(3N + 3) / 4</c>.</summary>
    public int FastQuorum => ((3 * Acceptors.Count) + 3) / 4;

    /// <summary>The classic-quorum size: a strict majority.</summary>
    public int ClassicQuorum => (Acceptors.Count / 2) + 1;


    /// <summary>
    /// Proposes <paramref name="value"/> on the fast path to every acceptor and reports whether a fast quorum accepted.
    /// </summary>
    /// <param name="fastBallot">The fast-round ballot. Must be a fast ballot.</param>
    /// <param name="value">The value to propose.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of acceptors that accepted and whether that is a fast quorum.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="fastBallot"/> is not a fast ballot.</exception>
    public async Task<(int AcceptedCount, bool IsCommitted)> TryFastWriteAsync(FastBallot fastBallot, TValue value, CancellationToken cancellationToken)
    {
        if(!fastBallot.IsFast)
        {
            throw new ArgumentException("A fast write requires a fast ballot.", nameof(fastBallot));
        }

        ConsensusReply<TValue>?[] replies = await RequestAllAsync(new AcceptRequest<TValue>(fastBallot, value), cancellationToken).ConfigureAwait(false);

        int accepted = CountAccepts(replies);

        return (accepted, (4 * accepted) >= (3 * Acceptors.Count));
    }


    /// <summary>
    /// Runs a classic recovery round under <paramref name="classicBallot"/>: prepares a majority, recovers the
    /// value (tallying the fast-round winner when the highest accepted ballot is a fast ballot), applies
    /// <paramref name="update"/>, and accepts the result.
    /// </summary>
    /// <param name="classicBallot">The classic recovery ballot. Must be a proposer-owned (non-fast) ballot.</param>
    /// <param name="update">The change function applied to the recovered value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The change outcome.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="classicBallot"/> is a fast ballot.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="update"/> is <see langword="null"/>.</exception>
    public async Task<ChangeOutcome<TValue>> RecoverAsync(FastBallot classicBallot, Func<TValue?, TValue> update, CancellationToken cancellationToken)
    {
        if(classicBallot.IsFast)
        {
            throw new ArgumentException("Recovery requires a classic (proposer-owned) ballot.", nameof(classicBallot));
        }

        ArgumentNullException.ThrowIfNull(update);

        ConsensusReply<TValue>?[] prepareReplies = await RequestAllAsync(new PrepareRequest<TValue>(classicBallot), cancellationToken).ConfigureAwait(false);

        int promises = 0;
        FastBallot highestAccepted = FastBallot.Zero;
        TValue? recovered = default;
        var promised = new List<PrepareReply<TValue>>(prepareReplies.Length);
        foreach(ConsensusReply<TValue>? reply in prepareReplies)
        {
            if(reply is PrepareReply<TValue> { Promised: true } prepareReply)
            {
                promises++;
                promised.Add(prepareReply);
                if(prepareReply.AcceptedBallot > highestAccepted)
                {
                    highestAccepted = prepareReply.AcceptedBallot;
                    recovered = prepareReply.AcceptedValue;
                }
            }
        }

        if(promises < ClassicQuorum)
        {
            return new ChangeOutcome<TValue>(false, default);
        }

        if(!highestAccepted.IsZero && highestAccepted.IsFast)
        {
            recovered = TallyFastWinner(promised, highestAccepted, recovered);
        }

        TValue newValue = update(recovered);
        ConsensusReply<TValue>?[] acceptReplies = await RequestAllAsync(new AcceptRequest<TValue>(classicBallot, newValue), cancellationToken).ConfigureAwait(false);

        int accepts = CountAccepts(acceptReplies);

        return accepts >= ClassicQuorum
            ? new ChangeOutcome<TValue>(true, newValue)
            : new ChangeOutcome<TValue>(false, default);
    }


    private async Task<ConsensusReply<TValue>?[]> RequestAllAsync(ConsensusRequest<TValue> request, CancellationToken cancellationToken)
    {
        var tasks = new Task<ConsensusReply<TValue>?>[Acceptors.Count];
        for(int i = 0; i < Acceptors.Count; i++)
        {
            tasks[i] = SafeRequestAsync(Acceptors[i], request, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }


    private static async Task<ConsensusReply<TValue>?> SafeRequestAsync(ConsensusEndpointDelegate<TValue> endpoint, ConsensusRequest<TValue> request, CancellationToken cancellationToken)
    {
        try
        {
            return await endpoint(request, cancellationToken).ConfigureAwait(false);
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch
        {
            //An unreachable or faulty acceptor counts as no response — neither a promise nor an accept.
            return null;
        }
    }


    private static int CountAccepts(ConsensusReply<TValue>?[] replies)
    {
        int accepted = 0;
        foreach(ConsensusReply<TValue>? reply in replies)
        {
            if(reply is AcceptReply<TValue> { Accepted: true })
            {
                accepted++;
            }
        }

        return accepted;
    }


    private static TValue? TallyFastWinner(List<PrepareReply<TValue>> replies, FastBallot highestAccepted, TValue? fallback)
    {
        var counts = new List<(TValue? Value, int Count)>();
        foreach(PrepareReply<TValue> reply in replies)
        {
            if(reply.AcceptedBallot != highestAccepted)
            {
                continue;
            }

            bool found = false;
            for(int i = 0; i < counts.Count; i++)
            {
                if(EqualityComparer<TValue>.Default.Equals(counts[i].Value, reply.AcceptedValue))
                {
                    counts[i] = (counts[i].Value, counts[i].Count + 1);
                    found = true;
                    break;
                }
            }

            if(!found)
            {
                counts.Add((reply.AcceptedValue, 1));
            }
        }

        if(counts.Count == 0)
        {
            return fallback;
        }

        (TValue? Value, int Count) winner = counts[0];
        for(int i = 1; i < counts.Count; i++)
        {
            if(counts[i].Count > winner.Count)
            {
                winner = counts[i];
            }
        }

        return winner.Value;
    }
}
