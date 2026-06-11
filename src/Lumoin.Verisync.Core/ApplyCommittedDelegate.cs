using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Applies a newly committed log entry to the host's application state machine: the apply edge of a
/// <see cref="RaftRunner{TCommand}"/>. The runner invokes it for each entry as it crosses the commit
/// threshold, in strictly increasing index order, so the application observes the totally ordered command
/// stream the log defines.
/// </summary>
/// <typeparam name="TCommand">The application command type replicated by the log.</typeparam>
/// <param name="index">The 1-based protocol index of the committed entry.</param>
/// <param name="command">The committed command to apply.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once the command has been applied.</returns>
/// <remarks>
/// <para>
/// Within one process lifetime the runner invokes this exactly once per committed index, in order, with no
/// gaps. Across a restart it is at-least-once: the commit index is volatile by Figure 2 and is rediscovered
/// from the leader, so a node restored from its durable state replays from a commit index of zero and may
/// re-apply entries it applied before the crash. A host that needs exactly-once application persists its own
/// applied watermark alongside the application state and ignores any index at or below it.
/// </para>
/// <para>
/// Throwing fails closed: the exception propagates out of <see cref="RaftRunner{TCommand}.RunAsync"/> and
/// ends the runner loop, since a node whose state machine has rejected a committed entry cannot safely
/// continue.
/// </para>
/// </remarks>
public delegate ValueTask ApplyCommittedDelegate<TCommand>(long index, TCommand command, CancellationToken cancellationToken);
