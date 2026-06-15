using Lumoin.Verisync.Core;
using System;
using System.Formats.Cbor;

namespace Lumoin.Verisync.Cbor;

/// <summary>
/// Wraps a CBOR <see cref="DeserializeMessageDelegate{TMessage}"/> so a malformed or non-canonical payload
/// surfaces as the encoding-agnostic <see cref="MessageDeserializationException"/>, keeping the underlying
/// <see cref="CborContentException"/> (or the raw reader exception a wrong-typed item throws) as the inner
/// exception. This mirrors <c>JsonMessageGuard</c> so a channel consumer catches one type whether the wire
/// is CBOR or JSON.
/// </summary>
internal static class CborMessageGuard
{
    public static DeserializeMessageDelegate<TMessage> FailClosed<TMessage>(DeserializeMessageDelegate<TMessage> deserialize)
    {
        ArgumentNullException.ThrowIfNull(deserialize);

        return payload =>
        {
            try
            {
                return deserialize(payload);
            }
            catch(Exception exception) when(IsPayloadFailure(exception))
            {
                throw new MessageDeserializationException("The CBOR payload could not be deserialized into a message.", exception);
            }
        };
    }


    private static bool IsPayloadFailure(Exception exception)
    {
        //Everything a CBOR decode can throw on hostile bytes: malformed or non-canonical content is a
        //CborContentException, reading the wrong major type an InvalidOperationException, and a caller decoder
        //may surface a FormatException, OverflowException, ArgumentException, or NotSupportedException. A fatal
        //condition (out-of-memory, cancellation) is not in this set and propagates unwrapped.
        return exception is CborContentException
            or FormatException
            or OverflowException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException;
    }
}
