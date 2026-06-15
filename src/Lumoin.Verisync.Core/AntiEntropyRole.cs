using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The fixed role a side plays in a single point-to-point reconciliation session. The initiator decodes the
/// responder's coded stream against its own lockstep encoder and resolves the recovered difference; the
/// responder streams symbol batches on host triggers and serves the initiator's fetches. A session's role is
/// chosen at construction and never changes for its lifetime.
/// </summary>
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "The two roles are an asymmetric protocol pairing; there is no neutral role, and a session must commit to exactly one, so no zero member exists.")]
public enum AntiEntropyRole
{
    /// <summary>The side that decodes the responder's stream, signals done, and classifies and resolves the difference.</summary>
    Initiator = 1,

    /// <summary>The side that streams symbol batches on host triggers, serves fetches, and applies pushed elements.</summary>
    Responder = 2,
}
