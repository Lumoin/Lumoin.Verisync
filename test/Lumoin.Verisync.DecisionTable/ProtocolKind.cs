namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// Which protocol a measured configuration belongs to.
/// </summary>
/// <remarks>
/// The verdict's first gate is stated per protocol rather than per configuration - a protocol left without a
/// surviving configuration loses the cell unconditionally - so the protocol is carried as a value rather than
/// recovered from a printed name.
/// </remarks>
internal enum ProtocolKind
{
    /// <summary>The QuePaxa versioned register.</summary>
    QuePaxa,

    /// <summary>Fast CASPaxos.</summary>
    FastCasPaxos
}
