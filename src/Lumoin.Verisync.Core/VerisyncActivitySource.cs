using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The single <see cref="ActivitySource"/> through which Verisync emits OpenTelemetry trace activities.
/// </summary>
/// <remarks>
/// <para>
/// Activity creation is gated by <see cref="ActivitySource.HasListeners"/>. When no listener is
/// attached, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns
/// <see langword="null"/> and the activity path is zero-cost. Metric emission is independent of
/// listener attachment.
/// </para>
/// <para>
/// Subscribe in application startup to receive spans:
/// </para>
/// <code>
/// using var tracerProvider = Sdk.CreateTracerProviderBuilder()
///     .AddSource(VerisyncActivitySource.Name)
///     .AddOtlpExporter()
///     .Build();
/// </code>
/// </remarks>
public static class VerisyncActivitySource
{
    /// <summary>
    /// The name of the activity source. Use this value when configuring OpenTelemetry to
    /// subscribe to Verisync lifetime spans.
    /// </summary>
    public const string Name = "Lumoin.Verisync";

    /// <summary>
    /// The shared <see cref="ActivitySource"/> instance used by all Verisync components.
    /// </summary>
    public static ActivitySource Instance { get; } = new(Name);
}
