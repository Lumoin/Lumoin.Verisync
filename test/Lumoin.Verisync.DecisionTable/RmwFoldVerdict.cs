namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What the read-modify-write correctness oracle found in one trial.
/// </summary>
/// <param name="Holds">Whether the committed value is the sequential fold of the changes that committed.</param>
/// <param name="Reason">Why it does not hold, or what holding means when it does.</param>
/// <remarks>
/// The reason is carried rather than derived at the printing site, because a broken fold is the one result of
/// this arm that voids a configuration and an operator reading the void needs to know which of the three
/// failures produced it.
/// </remarks>
internal sealed record RmwFoldVerdict(bool Holds, string Reason)
{
    /// <summary>The verdict of a trial whose fold holds.</summary>
    public static RmwFoldVerdict Sound { get; } = new(true, "the committed value is the sequential fold of every committed change, each applied exactly once");


    /// <summary>The verdict of a trial whose fold broke.</summary>
    /// <param name="reason">What broke it.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reason"/> is <see langword="null"/>.</exception>
    public static RmwFoldVerdict Broken(FormattableString reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new RmwFoldVerdict(false, FormattableString.Invariant(reason));
    }
}
