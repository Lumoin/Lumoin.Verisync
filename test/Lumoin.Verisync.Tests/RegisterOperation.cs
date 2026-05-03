namespace Lumoin.Verisync.Tests;

/// <summary>
/// One completed client operation against an append register, recorded for linearizability checking:
/// its label, its real-time interval in virtual clock ticks, and the register values its successful
/// attempt observed and wrote.
/// </summary>
/// <param name="Label">The unique single character this operation appends.</param>
/// <param name="Invoked">The virtual time at which the operation was invoked.</param>
/// <param name="Completed">The virtual time at which the operation completed.</param>
/// <param name="Observed">The register value the successful attempt recovered.</param>
/// <param name="Written">The register value the successful attempt committed.</param>
internal sealed record RegisterOperation(char Label, long Invoked, long Completed, string Observed, string Written);
