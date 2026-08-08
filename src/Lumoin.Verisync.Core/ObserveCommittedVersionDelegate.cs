using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Reports the highest register version the host knows to be committed, so that a writer waiting out its
/// hedging delay can stand down instead of running a consensus instance that is already closed.
/// </summary>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The highest committed version the host knows of, or <see cref="RegisterVersion.Unwritten"/> when it knows of none.</returns>
/// <remarks>
/// <para>
/// This is the versioned register's counterpart to <see cref="FastRoundProgressDelegate"/> and is optional: a
/// host with no learn path supplies none, and every scheduled writer then activates on its delay.
/// </para>
/// <para>
/// It is consulted only where a delay is waited. The schedule's leader has a zero delay and therefore never
/// stands down; a leader that stood down would leave the schedule with no activator. A schedule whose base
/// delay is zero consults this for no replica at all, which reproduces the unhedged behaviour exactly.
/// </para>
/// <para>
/// A stale answer costs a redundant attempt and never costs safety. Reporting a version lower than the truth
/// makes a writer run an instance that is already decided, where it learns the committed record and reports a
/// superseded write; reporting one higher makes it stand down and retry.
/// </para>
/// </remarks>
public delegate ValueTask<RegisterVersion> ObserveCommittedVersionDelegate(CancellationToken cancellationToken);
