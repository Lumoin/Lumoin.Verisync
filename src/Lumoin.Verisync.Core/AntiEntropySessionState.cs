namespace Lumoin.Verisync.Core;

/// <summary>
/// The advisory lifecycle phase of an <see cref="AntiEntropySession{TElement}"/>, written only by the session's
/// single consumer loop and read by hosts to pace their gossip. The phases run in order for the happy path,
/// from <see cref="Created"/> before the loop starts to a terminal state: <see cref="Completed"/> for an
/// exchange that finished, <see cref="Interrupted"/> for a wind-down before one.
/// </summary>
public enum AntiEntropySessionState
{
    /// <summary>Before <see cref="AntiEntropySession{TElement}.RunAsync"/> has started the consumer loop.</summary>
    Created = 0,

    /// <summary>Own offer sent; the peer's offer has not yet been validated against the local contract.</summary>
    Pinning = 1,

    /// <summary>Offers exchanged and matched; coded symbols are flowing between the two sides.</summary>
    Reconciling = 2,

    /// <summary>Initiator: done sent and a fetch answer is outstanding. Responder: done received, serving the fetch or applying elements.</summary>
    Resolving = 3,

    /// <summary>
    /// Terminal: the exchange finished and the consumer loop has returned. For a responder this means the
    /// peer's done signal had arrived before the wind-down; whether the initiator's trailing element and drop
    /// frames all arrived is not verifiable from the responder's side.
    /// </summary>
    Completed = 4,

    /// <summary>
    /// Terminal: the host wound the session down through <see cref="AntiEntropySession{TElement}.Complete"/>
    /// before the exchange finished, and the consumer loop has returned. A remove-aware side reaching this
    /// state has folded no peer context at all — folds ride only the applies of a completed transfer, and an
    /// initiator holds its local drops back while its fetch is outstanding — so an interrupted exchange cannot
    /// poison the local context into covering entries that were never transferred.
    /// </summary>
    Interrupted = 5,
}
