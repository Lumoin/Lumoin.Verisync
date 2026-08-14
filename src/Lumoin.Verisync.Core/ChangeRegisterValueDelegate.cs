namespace Lumoin.Verisync.Core;

/// <summary>
/// Computes the value a CASPaxos change proposes from the value the round recovered.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="current">The value the prepare phase recovered, or the default when no quorum has accepted
/// one.</param>
/// <returns>The value to propose.</returns>
/// <remarks>
/// <para>
/// It runs inside the consensus round, against the value recovered there, which is what separates it from
/// <see cref="ComputeRegisterValueDelegate{TValue}"/>: a CASPaxos change composes with whatever the round
/// found, so a caller's intent survives contention, while a QuePaxa write recomputes outside the round and
/// re-proposes whole.
/// </para>
/// <para>
/// One change applies it once, to the one value that change recovered. A caller that retries a failed change
/// runs it again against a fresh recovery, and the apply-once discipline for an update that must not land
/// twice stays that caller's, because an attempt this host saw fail can still be decided later.
/// </para>
/// </remarks>
public delegate TValue ChangeRegisterValueDelegate<TValue>(TValue? current);
