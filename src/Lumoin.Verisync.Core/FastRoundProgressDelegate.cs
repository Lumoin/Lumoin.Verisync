using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Reports whether the host has evidence that an earlier-scheduled writer has already driven
/// <paramref name="fastBallot"/>: a value learned through the application, through an anti-entropy exchange,
/// or through the host's own commit path. A hedged writer that sees such evidence stands down rather than
/// spending a fast round the acceptors would reject.
/// </summary>
/// <param name="fastBallot">The fast round the writer is about to use.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns><see langword="true"/> when the round has already been driven; otherwise <see langword="false"/>.</returns>
/// <remarks>
/// The signal is an optimization and is never consulted for safety. Reporting <see langword="false"/> for a
/// round that was in fact driven costs one rejected fast round; reporting <see langword="true"/> for a round
/// that was not costs one skipped attempt the host must reissue, because a skipped write is not a failed
/// write and carries no outcome. A host with no such signal supplies none, and every scheduled writer then
/// activates on its delay.
/// </remarks>
public delegate ValueTask<bool> FastRoundProgressDelegate(FastBallot fastBallot, CancellationToken cancellationToken);
