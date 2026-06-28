using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Deserializes one framed message into an <em>owned</em> value whose byte buffers are rented from
/// <paramref name="pool"/> rather than allocated on the GC heap. This is the pool-aware companion to
/// <see cref="DeserializeMessageDelegate{TMessage}"/>: where that seam returns a self-contained managed value
/// (a record, an immutable struct), this one returns a value that <em>owns</em> pooled memory and so must be
/// disposed. Use it for the "one framed blob in, one owned payload out" case — a sketch image, any single
/// borrowed-then-kept byte payload — that would otherwise force a <c>ToArray</c> onto the managed heap.
/// </summary>
/// <typeparam name="TMessage">
/// The owned message type. It holds buffers rented from the supplied pool and is the consumer's to dispose;
/// for a bare byte payload this is simply <see cref="System.Buffers.IMemoryOwner{T}"/> of <see cref="byte"/>.
/// </typeparam>
/// <param name="payload">The complete payload bytes of one framed message. Valid only for the duration of the call; copy what the result must retain into <paramref name="pool"/>-backed memory.</param>
/// <param name="pool">The pool the result rents its backing from. Required and non-null — provenance for the returned memory is explicit at the call site, exactly as it is across the reconciliation tier.</param>
/// <returns>
/// The deserialized message, owning any pooled buffers it holds. Ownership transfers to the caller of
/// <see cref="OwnedMessageChannelReader{TMessage}.ReadAllAsync"/>, which never disposes a yielded value.
/// </returns>
/// <exception cref="MessageDeserializationException">
/// Thrown when the payload cannot be deserialized into a valid message — a malformed encoding, a missing or
/// rejected field, or a failed verification. This is the uniform failure across every encoding; the
/// encoding-specific cause is carried as the inner exception. An implementation that rents from the pool
/// before it detects the failure must return that rental before it throws, so a rejected frame leaks nothing.
/// </exception>
public delegate TMessage DeserializeOwnedMessageDelegate<out TMessage>(ReadOnlySequence<byte> payload, MemoryPool<byte> pool);
