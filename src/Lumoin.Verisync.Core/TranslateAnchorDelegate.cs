namespace Lumoin.Verisync.Core;

/// <summary>
/// Translates an anchor that may refer to compacted state into its current equivalent.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type, which carries the translation maps.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type.</typeparam>
/// <param name="sequence">The compacted sequence whose translation state services the anchor.</param>
/// <param name="anchor">The possibly stale anchor.</param>
/// <returns>
/// The current anchor — the input itself when no translation is needed — or <see langword="null"/>
/// when the anchor is unservable.
/// </returns>
/// <remarks>
/// <para>
/// The translation maps are compacted-sequence state, so the delegate takes the sequence: an anchor is
/// resolved against the maps the receiving generation actually holds, not against ambient strategy
/// state.
/// </para>
/// <para>
/// A DOT translation is PERMANENT: compaction reclaims payload, not identity, so a dropped dot keeps its
/// translation entry for the life of the sequence — one entry per dropped dot, forever (coalesced in the run
/// shape) — because dots are globally unique and the map that serves them is never trimmed, so a dot anchor
/// stays translatable across arbitrarily many generations.
/// </para>
/// <para>
/// A BASE translation is bounded: the base-offset map is REPLACED on each base-changing compaction, so it
/// serves exactly the immediately preceding generation. An addressing type that distinguishes generations
/// carries which generation an incoming base anchor belongs to, and the seam decides on it — the current
/// generation resolves as identity, the one immediately preceding generation resolves through the map, and
/// any older or newer generation resolves to nothing.
/// </para>
/// <para>
/// On every path an anchor that resolves to neither a live vertex nor a servable translation indicates a
/// contract violation by the peer (it referenced state its own advertised frontier had passed, or a forged
/// identity) — return <see langword="null"/> and fail closed rather than guessing.
/// </para>
/// <para>
/// Strategies whose anchors survive compaction unchanged use the identity translation; strategies that
/// re-anchor onto checkpoint positions serve stale anchors from the same translation state.
/// </para>
/// </remarks>
public delegate TAnchor? TranslateAnchorDelegate<TSequence, TAnchor>(TSequence sequence, TAnchor anchor);
