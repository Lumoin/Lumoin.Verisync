namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The seed derivation every jitter draw and every trial seed in this harness comes from.
/// </summary>
/// <remarks>
/// It is the splitmix-style finalizer <c>WanFastRoundContentionProbe</c> uses, constant for constant, because
/// the reproduction gate requires this harness to draw the published rows' exact jitter patterns. A different
/// finalizer would re-roll every measured number, which makes the gate unreachable rather than failing it.
/// The function is stateless, so a draw depends on what it is for and never on the order the harness happened
/// to ask in.
/// </remarks>
internal static class SeedMixer
{
    /// <summary>Maps distinct inputs to well-spread values.</summary>
    /// <param name="value">The value to mix.</param>
    /// <returns>The mixed value.</returns>
    public static ulong Mix(ulong value)
    {
        ulong mixed = value + 0x9E3779B97F4A7C15UL;
        mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
        mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;

        return mixed ^ (mixed >> 31);
    }


    /// <summary>The seed one trial of one configuration runs under.</summary>
    /// <param name="configuration">The configuration base, which separates one sweep's trials from another's.</param>
    /// <param name="trial">The trial index within the configuration.</param>
    /// <returns>The trial seed.</returns>
    public static ulong TrialSeed(int configuration, int trial) => Mix(((ulong)(uint)configuration << 32) | (uint)trial);


    /// <summary>The seed one writer's priority stream runs under within a trial.</summary>
    /// <param name="trialSeed">The trial seed.</param>
    /// <param name="writer">The writer index.</param>
    /// <returns>The stream seed.</returns>
    /// <remarks>
    /// Each writer owns a stream, because a believed leader draws nothing at its first step and a shared
    /// stream would couple the writers through dispatch order.
    /// </remarks>
    public static ulong PriorityStreamSeed(ulong trialSeed, int writer) => Mix(trialSeed ^ ((ulong)(writer + 1) * 0xD1B54A32D192ED03UL));
}
