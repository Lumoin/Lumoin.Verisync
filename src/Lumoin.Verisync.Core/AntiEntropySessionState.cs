namespace Lumoin.Verisync.Core;

/// <summary>
/// The advisory lifecycle phase of an <see cref="AntiEntropySession{TElement}"/>, written only by the session's
/// single consumer loop and read by hosts to pace their gossip. The phases run in order for the happy path,
/// from <see cref="Created"/> before the loop starts through to the terminal <see cref="Completed"/>.
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

    /// <summary>Terminal: the session has finished and the consumer loop has returned.</summary>
    Completed = 4,
}
