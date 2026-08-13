namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The work one scheduled event performs when the virtual clock reaches its instant.
/// </summary>
/// <remarks>
/// The delegate runs on the pump's own thread, so whatever it starts or completes is enqueued before the
/// pump looks at its schedule again. It returns nothing and takes nothing: an event's operands are captured
/// when it is scheduled, which is what keeps the schedule a function of the seed rather than of the order the
/// harness happened to build it in.
/// </remarks>
internal delegate void PumpEventDelegate();
