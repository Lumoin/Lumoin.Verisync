namespace Lumoin.Verisync.Tests;

/// <summary>
/// Rewrites a reply frame's payload bytes after they arrive from the socket and before the reply
/// deserializer reads them, which is what makes a transport-corruption test wire-level.
/// </summary>
/// <param name="host">The index of the recorder host whose reply this is.</param>
/// <param name="payload">The payload exactly as it came off the wire.</param>
/// <returns>The bytes handed to the deserializer in its place.</returns>
/// <remarks>
/// A test that wraps an endpoint and edits the reply it already deserialized never puts the corruption on
/// the wire, so it shows only that the caller checks a field. Substituting bytes here sends the corruption
/// through the real deserializer, which is where a version-swapping transport would actually be met.
/// </remarks>
internal delegate byte[] TamperReplyPayloadDelegate(int host, byte[] payload);
