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
/// The translation map is PERMANENT: compaction reclaims payload, not identity, so a dropped dot keeps its
/// translation entry for the life of the sequence — one entry per dropped dot, forever (coalesced in the run
/// shape). There is no bounded transition window after which a translation expires; a dot anchor stays
/// translatable indefinitely because dots are globally unique, and the map that serves it is never trimmed.
/// An anchor that resolves to neither a live vertex nor a translation entry indicates a contract violation by
/// the peer (it referenced state its own advertised frontier had passed, or a forged identity) — return
/// <see langword="null"/> and fail closed rather than guessing.
/// </para>
/// <para>
/// Strategies whose anchors survive compaction unchanged use the identity translation; strategies that
/// re-anchor onto checkpoint positions serve stale anchors from the same permanent, never-trimmed
/// translation state.
/// </para>
/// </remarks>
public delegate TAnchor? TranslateAnchorDelegate<TSequence, TAnchor>(TSequence sequence, TAnchor anchor);
