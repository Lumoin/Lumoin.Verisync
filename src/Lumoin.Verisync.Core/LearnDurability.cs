namespace Lumoin.Verisync.Core;

/// <summary>
/// How far a learn must get before <see cref="QuePaxaVersionedRunner{TValue}.LearnAsync"/> completes.
/// </summary>
/// <remarks>
/// The parameter naming this is required rather than defaulted. A learn's crash safety is a decision the
/// caller makes, and eager persistence is not the answer for every host: one write per disseminated record on
/// every host is a demotion, while a caller that must acknowledge a dissemination as durable needs the write
/// before its call completes.
/// </remarks>
public enum LearnDurability
{
    /// <summary>
    /// The record is adopted in memory and becomes durable with the first reply that depends on it or with a
    /// checkpoint. It is the default behaviour of the surface and the zero value.
    /// </summary>
    InMemory = 0,

    /// <summary>
    /// The host's state is made durable before the call completes, so a crash after it cannot lose the record.
    /// </summary>
    Durable = 1
}
