namespace Lumoin.Verisync.Core;

/// <summary>
/// The one consensus instance a versioned register's attempt addresses: the version it runs at, the
/// membership it runs under, and the writer whose successor leads it.
/// </summary>
/// <param name="Version">The version the attempt proposes at.</param>
/// <param name="Configuration">The membership the instance runs under, which is the recorder set a quorum is counted over and the hedging order the delay is read from.</param>
/// <param name="PreviousWriter">The writer of the version before it, or <see langword="null"/> when no version has been written.</param>
/// <remarks>
/// <para>
/// The three travel together because they come from one read of one committed record. Three parameters
/// cannot express that they did: a version resolved from one record beside a membership resolved from a
/// newer one addresses a quorum of the wrong recorder set, which is the tear a single capture makes
/// unstateable. A register reads its committed record once per attempt, builds this from that one
/// reference, and everything the attempt does afterwards reads this rather than the record again.
/// </para>
/// <para>
/// It is a capture and never a supplied argument, which is why nothing here is validated. A default value
/// of a struct is always constructible and no accessor can refuse it, so a guard here would be a half
/// guard; the register is the only producer and it produces from state it has already validated.
/// </para>
/// </remarks>
public readonly record struct RegisterInstance(
    RegisterVersion Version,
    QuePaxaConfiguration Configuration,
    ReplicaId? PreviousWriter);
