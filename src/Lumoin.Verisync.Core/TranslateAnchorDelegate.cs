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
/// The composition routes here only anchors expressed in the <em>immediately previous</em> compaction
/// generation; current-generation anchors are used directly. An anchor older than one generation can no
/// longer arrive once the stability line passes the previous checkpoint — base anchors are unambiguous
/// only one generation back, while dot anchors stay translatable indefinitely because dots are globally
/// unique. Given that stability rule, an unservable anchor indicates a contract violation by the peer
/// (it referenced state below a frontier it had advertised passing) — return <see langword="null"/> and
/// fail closed rather than guessing.
/// </para>
/// <para>
/// Strategies whose anchors survive compaction unchanged use the identity translation; strategies that
/// re-anchor onto checkpoint positions serve stale anchors from a translation map retained until the
/// frontier passes the checkpoint.
/// </para>
/// </remarks>
public delegate TAnchor? TranslateAnchorDelegate<TSequence, TAnchor>(TSequence sequence, TAnchor anchor);
