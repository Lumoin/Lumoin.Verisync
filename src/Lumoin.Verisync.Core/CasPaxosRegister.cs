using System;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A classic CASPaxos register: a value replicated across acceptors and mutated by a change function
/// applied under two-phase quorum consensus. This is the small, readable base that the value-agnostic Fast
/// CASPaxos register builds on.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// <see cref="Change(Ballot, Func{TValue, TValue})"/> runs the two CASPaxos phases over the acceptors:
/// a prepare phase that promises the ballot on a majority and recovers any in-progress value, then an
/// accept phase that commits the new value (the change function applied to the recovered value) on a
/// majority. A value is chosen only when both phases reach a quorum.
/// </para>
/// <para>
/// This model is in-memory and contacts every acceptor synchronously — it omits message loss, retries,
/// and proposer failure that the networked Fast CASPaxos layer handles. It exists to make the safety
/// argument legible: a higher ballot always supersedes a lower one, and a chosen value is never lost
/// because the next proposer recovers it during prepare.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class CasPaxosRegister<TValue>
{
    private ImmutableArray<Acceptor<TValue>> Acceptors { get; }


    private CasPaxosRegister(ImmutableArray<Acceptor<TValue>> acceptors)
    {
        Acceptors = acceptors;
    }


    /// <summary>
    /// Creates a register with <paramref name="acceptorCount"/> acceptors, each in its initial state.
    /// </summary>
    /// <param name="acceptorCount">The number of acceptors. A quorum is a strict majority of this count.</param>
    /// <returns>A new register.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="acceptorCount"/> is less than one.</exception>
    public static CasPaxosRegister<TValue> WithAcceptors(int acceptorCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(acceptorCount, 1);

        ImmutableArray<Acceptor<TValue>>.Builder builder = ImmutableArray.CreateBuilder<Acceptor<TValue>>(acceptorCount);
        for(int i = 0; i < acceptorCount; i++)
        {
            builder.Add(Acceptor<TValue>.Initial);
        }

        return new CasPaxosRegister<TValue>(builder.ToImmutable());
    }


    /// <summary>The number of acceptors in the register.</summary>
    public int AcceptorCount => Acceptors.Length;


    /// <summary>
    /// Attempts to change the register value under <paramref name="ballot"/> by applying
    /// <paramref name="update"/> to the value recovered during prepare.
    /// </summary>
    /// <param name="ballot">The proposing ballot. Must be higher than any ballot a quorum has already promised.</param>
    /// <param name="update">
    /// The change function, applied to the current value (the default when the register holds no value yet).
    /// </param>
    /// <returns>The register after the attempt and the outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="update"/> is <see langword="null"/>.</exception>
    public (CasPaxosRegister<TValue> Register, ChangeOutcome<TValue> Outcome) Change(Ballot ballot, Func<TValue?, TValue> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        ImmutableArray<Acceptor<TValue>>.Builder working = Acceptors.ToBuilder();

        int promises = 0;
        Ballot? highestAccepted = null;
        TValue? recovered = default;
        for(int i = 0; i < working.Count; i++)
        {
            (Acceptor<TValue> acceptor, PrepareResponse<TValue> response) = working[i].Prepare(ballot);
            working[i] = acceptor;
            if(response.Promised)
            {
                promises++;
                if(response.AcceptedBallot is Ballot accepted && (highestAccepted is null || accepted > highestAccepted.Value))
                {
                    highestAccepted = accepted;
                    recovered = response.AcceptedValue;
                }
            }
        }

        if(promises < Quorum)
        {
            return (new CasPaxosRegister<TValue>(working.ToImmutable()), new ChangeOutcome<TValue>(false, default));
        }

        TValue newValue = update(recovered);

        int accepts = 0;
        for(int i = 0; i < working.Count; i++)
        {
            (Acceptor<TValue> acceptor, bool accepted) = working[i].Accept(ballot, newValue);
            working[i] = acceptor;
            if(accepted)
            {
                accepts++;
            }
        }

        ChangeOutcome<TValue> outcome = accepts >= Quorum
            ? new ChangeOutcome<TValue>(true, newValue)
            : new ChangeOutcome<TValue>(false, default);

        return (new CasPaxosRegister<TValue>(working.ToImmutable()), outcome);
    }


    private int Quorum => (Acceptors.Length / 2) + 1;


    private string DebuggerDisplay => $"CasPaxosRegister: {Acceptors.Length} acceptors, quorum {Quorum}";
}
