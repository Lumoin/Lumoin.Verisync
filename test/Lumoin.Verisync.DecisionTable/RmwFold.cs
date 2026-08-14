namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The read-modify-write workload's value, the change function every writer applies to it, and the oracle that
/// says whether what the replicas ended up holding is the sequential fold of the changes that committed.
/// </summary>
/// <remarks>
/// <para>
/// THE VALUE IS THE FOLD'S OWN TRACE. A writer's change appends its token to the value it read, so the
/// committed value is the ordered record of which changes were applied and in which order, and the oracle is a
/// property of that value alone rather than of anything the clients reported. A counter would fold to a number
/// that no longer says which writer contributed to it, and an oracle over a number cannot separate a lost
/// change from a change applied twice.
/// </para>
/// <para>
/// THE CHANGE IS READ-MODIFY-WRITE AND NOT A BLIND WRITE. The value proposed is a function of the value read,
/// so a proposer that loses cannot re-send what it computed before: the token it would append belongs after
/// the winner's, and a proposal computed against a superseded value names an order that never happened. That
/// is the whole quantity this arm measures, and it is why the workload is not interchangeable in the sense the
/// settled rules give the word.
/// </para>
/// <para>
/// THE CHANGE CARRIES ITS OWN APPLY-ONCE TOKEN, WHICH IS A PROTOCOL REQUIREMENT AND NOT A CONVENIENCE. Fast
/// CASPaxos recovers the highest accepted value and applies the change to it, and a writer's own partially
/// accepted value can be the value recovered, so a change written as a plain append would compose on top of
/// itself and count one writer twice. QuePaxa reaches the same hazard by the other route and far more rarely:
/// a superseded proposal is discarded whole and never composed, but an attempt that reached no decision may
/// still be carried by another proposer and decided afterwards, and the writer then recomputes against a value
/// its own change is already inside. Both arms run this one change function, and the rate at which the token
/// fires is reported per arm, because the gap between the two rates is the semantic difference the
/// interchangeability boundary is drawn along.
/// </para>
/// </remarks>
internal static class RmwFold
{
    /// <summary>The first token of the alphabet, which writer zero holds.</summary>
    private const char FirstToken = 'a';


    /// <summary>The number of writers the token alphabet addresses.</summary>
    public const int MaximumWriters = 26;


    /// <summary>The token writer <paramref name="writer"/> appends.</summary>
    /// <param name="writer">The zero-based writer index. Must be below <see cref="MaximumWriters"/>.</param>
    /// <returns>The token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="writer"/> is negative or not below <see cref="MaximumWriters"/>.</exception>
    public static char Token(int writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(writer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(writer, MaximumWriters);

        return (char)(FirstToken + writer);
    }


    /// <summary>The writer holding <paramref name="token"/>, which is negative for a token no writer holds.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The writer index, or a negative value when the token is outside the alphabet.</returns>
    public static int WriterOf(char token)
    {
        int writer = token - FirstToken;

        return writer >= 0 && writer < MaximumWriters ? writer : -1;
    }


    /// <summary>Whether <paramref name="value"/> already carries <paramref name="token"/>.</summary>
    /// <param name="value">The value to inspect, which is the unwritten register when it is <see langword="null"/>.</param>
    /// <param name="token">The token to look for.</param>
    /// <returns><see langword="true"/> when the token is present.</returns>
    public static bool Carries(string? value, char token)
    {
        if(value is null)
        {
            return false;
        }

        foreach(char held in value)
        {
            if(held == token)
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// The value a writer holding <paramref name="token"/> proposes, computed from the value it read.
    /// </summary>
    /// <param name="current">The value read, which is the unwritten register when it is <see langword="null"/>.</param>
    /// <param name="token">The writer's token.</param>
    /// <returns>The value to propose.</returns>
    /// <remarks>
    /// Appending is what makes this a read-modify-write: the result names an order, so it cannot be computed
    /// without the read. The apply-once test is what keeps a change that is recovered back into its own round
    /// from being counted twice, and it is a no-op on every write whose value the writer has not already
    /// reached.
    /// </remarks>
    public static string Apply(string? current, char token)
    {
        string held = current ?? string.Empty;

        return Carries(held, token) ? held : held + token;
    }


    /// <summary>
    /// Whether <paramref name="finalValue"/> is the sequential fold of the changes that committed.
    /// </summary>
    /// <param name="finalValue">The value the replicas hold at the end of the trial, read from the replicas rather than from any client.</param>
    /// <param name="committedTokens">The tokens of the writers whose own change reported committed.</param>
    /// <param name="writerCount">How many writers the trial ran.</param>
    /// <returns>Whether the fold holds, and what broke it when it does not.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committedTokens"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="writerCount"/> is not positive.</exception>
    /// <remarks>
    /// <para>
    /// THREE FAILURES AND NOT ONE. A token appearing twice is a change applied twice, which is what a
    /// recovered-own-value composition produces. A committed writer's token missing is a change lost, which is
    /// what a proposer re-proposing a stale value or applying its change to a stale base produces. A token no
    /// writer holds is a corrupted value. Each is reported by name, so a vector can require the one it was
    /// written for rather than any refusal at all.
    /// </para>
    /// <para>
    /// A writer that spent its budget without committing is not required to be absent. Its proposal may have
    /// been decided at a version it never learned the decision of, and an oracle that demanded its absence
    /// would report a protocol violation for a client-side timeout. What is required in both directions is
    /// that a committed change is present and that nothing is present twice.
    /// </para>
    /// </remarks>
    public static RmwFoldVerdict Check(string? finalValue, IReadOnlyList<char> committedTokens, int writerCount)
    {
        ArgumentNullException.ThrowIfNull(committedTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(writerCount, 1);

        string held = finalValue ?? string.Empty;

        for(int index = 0; index < held.Length; index++)
        {
            char token = held[index];
            int writer = WriterOf(token);
            if(writer < 0 || writer >= writerCount)
            {
                return RmwFoldVerdict.Broken($"The committed value '{held}' carries the token '{token}', which belongs to no writer of a trial that ran {writerCount}.");
            }

            for(int other = index + 1; other < held.Length; other++)
            {
                if(held[other] == token)
                {
                    return RmwFoldVerdict.Broken($"The committed value '{held}' carries the token '{token}' more than once, so one writer's change was applied twice.");
                }
            }
        }

        foreach(char token in committedTokens)
        {
            if(!Carries(held, token))
            {
                return RmwFoldVerdict.Broken($"The writer holding the token '{token}' reported its change committed and the committed value '{held}' does not carry it, so a change was lost.");
            }
        }

        return RmwFoldVerdict.Sound;
    }
}
