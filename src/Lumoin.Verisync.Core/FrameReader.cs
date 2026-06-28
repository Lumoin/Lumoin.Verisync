using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Shared length-prefixed framing for the message-channel readers
/// (<see cref="MessageChannelReader{TMessage}"/>, <see cref="OwnedMessageChannelReader{TMessage}"/>, and
/// <see cref="ItemStreamChannelReader{TItem}"/>). It centralizes the two attacker-facing bounds — the outer
/// payload-length cap and the inner padded real-length check — so every reader enforces them identically and
/// the security-critical arithmetic lives in one place. See <see cref="MessageChannelReader{TMessage}"/> for
/// the unpadded wire format and <see cref="FramePadding"/> for the padded variant.
/// </summary>
internal static class FrameReader
{
    /// <summary>The four-byte big-endian length prefix that opens every frame, and the padded inner prefix.</summary>
    public const int FrameHeaderLength = 4;


    /// <summary>
    /// Tries to slice one complete frame off the front of <paramref name="buffer"/>, advancing it past the
    /// frame's header and payload on success.
    /// </summary>
    /// <param name="buffer">The unconsumed channel bytes; advanced past the frame on success, left untouched otherwise.</param>
    /// <param name="maxFrameLength">The largest payload accepted, in bytes — the declared length is attacker-controlled and is never trusted past this bound.</param>
    /// <param name="frame">The framed payload bytes, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a whole frame was available; <see langword="false"/> when more bytes are needed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a frame declares a payload longer than <paramref name="maxFrameLength"/>.</exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, int maxFrameLength, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        if(buffer.Length < FrameHeaderLength)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        buffer.Slice(0, FrameHeaderLength).CopyTo(header);
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header);

        if(payloadLength > (uint)maxFrameLength)
        {
            throw new InvalidOperationException($"A frame declares a payload of {payloadLength} bytes, above the maximum of {maxFrameLength}; the peer is faulty, hostile, or speaking another protocol.");
        }

        if(buffer.Length < FrameHeaderLength + payloadLength)
        {
            return false;
        }

        frame = buffer.Slice(FrameHeaderLength, payloadLength);
        buffer = buffer.Slice(FrameHeaderLength + payloadLength);

        return true;
    }


    /// <summary>
    /// Returns the real payload of a frame: the frame itself when <paramref name="padding"/> is
    /// <see langword="null"/>, or the slice declared by the frame's inner real-length prefix when a padding
    /// policy is configured.
    /// </summary>
    /// <param name="frame">The framed payload bytes from <see cref="TryReadFrame"/>.</param>
    /// <param name="padding">The padding policy shared with the writing peer, or <see langword="null"/> for the unpadded format.</param>
    /// <returns>The real payload to hand to the deserializer.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a padded frame is shorter than its inner prefix, or its inner length reaches past the frame bounds.</exception>
    public static ReadOnlySequence<byte> RealPayload(ReadOnlySequence<byte> frame, FramePadding? padding)
    {
        if(padding is null)
        {
            return frame;
        }

        //A padded frame begins with a four-byte real-length prefix. With padding configured every frame
        //carries it, so a frame too short even for the prefix is a faulty or unpadding peer.
        if(frame.Length < FrameHeaderLength)
        {
            throw new InvalidOperationException("A padded frame is shorter than its inner length prefix; the peer is faulty, hostile, or not padding.");
        }

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        frame.Slice(0, FrameHeaderLength).CopyTo(header);
        uint realLength = BinaryPrimitives.ReadUInt32BigEndian(header);

        //The inner length is attacker-influenced and is never trusted past the frame bounds.
        if(realLength > frame.Length - FrameHeaderLength)
        {
            throw new InvalidOperationException("The inner length exceeds the padded frame; the peer is faulty, hostile, or not padding.");
        }

        return frame.Slice(FrameHeaderLength, realLength);
    }
}
