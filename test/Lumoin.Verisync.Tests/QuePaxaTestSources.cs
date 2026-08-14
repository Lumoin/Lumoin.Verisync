using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A deterministic <see cref="ProposalPrioritySourceDelegate"/> for tests that need an unbounded stream of
/// ordinary priorities.
/// </summary>
/// <remarks>
/// Xorshift64 rather than the cryptographic source: every priority in a run is reproducible from its seed, so
/// a failing scenario replays the identical draws on any runtime. It is shared rather than nested in one test
/// class because a scenario that runs a proposer to its step budget draws once per recorder at every
/// phase-zero step, which is far past the end of any scripted sequence.
/// </remarks>
internal sealed class SeededPrioritySource
{
    private ulong state;

    public SeededPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


    public int DrawCount { get; private set; }


    public ProposalPriority Next()
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        DrawCount++;

        //The two reserved endpoints are excluded, so the source honours the delegate's contract exactly.
        ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

        return new ProposalPriority(value);
    }
}
