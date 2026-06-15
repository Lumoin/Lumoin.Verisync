using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Produces the canonical bytes of a value, over which its dotted item digest is committed. The bytes join the
/// dot (replica and counter) in the pinned frame fed to the digest, so the projection commits to both the dot
/// and the value the dot tags.
/// </summary>
/// <typeparam name="T">The value type framed into its dotted item.</typeparam>
/// <param name="value">The value to canonicalize.</param>
/// <returns>The canonical byte representation of <paramref name="value"/>; may be empty.</returns>
/// <remarks>
/// The canonicalization must be a pure, deterministic, replica-independent and time-independent function of the
/// value, so two replicas frame a shared <c>(Dot, value)</c> entry identically; otherwise their shared entries
/// project to different items and the symmetric difference is wrong. The bytes may be empty — the dot alone
/// still distinguishes the entry. Serialization is a caller concern (a JSON or CBOR project, or any caller
/// encoder), as elsewhere in the core; the core orchestrates the framing but does not embed a value encoding.
/// </remarks>
public delegate ReadOnlyMemory<byte> CanonicalizeReconciliationValueDelegate<in T>(T value);
