using Lumoin.Verisync.Core;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Wraps a JSON <see cref="DeserializeMessageDelegate{TMessage}"/> so every way a malformed payload can be
/// rejected surfaces as the encoding-agnostic <see cref="MessageDeserializationException"/>, keeping the
/// original <see cref="JsonException"/> (or the raw accessor exception a less-hardened arm still throws) as
/// the inner exception. The codecs apply this at every deserializer factory so a channel consumer catches
/// one type whether the wire is JSON or CBOR.
/// </summary>
internal static class JsonMessageGuard
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
                throw new MessageDeserializationException("The JSON payload could not be deserialized into a message.", exception);
            }
        };
    }


    private static bool IsPayloadFailure(Exception exception)
    {
        //Everything a JSON deserialize body can throw on hostile bytes: the codecs' own JsonException, the
        //raw accessor exceptions the wrong-kind arms still surface (a value-kind mismatch is an
        //InvalidOperationException, an overflowing or non-hex number a FormatException or OverflowException), a
        //domain constructor's ArgumentException, the unknown-discriminator NotSupportedException, and a stray
        //KeyNotFoundException from any reader not yet routed through RequireProperty. A fatal condition
        //(out-of-memory, cancellation) is not in this set and propagates unwrapped.
        return exception is JsonException
            or FormatException
            or OverflowException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or KeyNotFoundException;
    }
}
