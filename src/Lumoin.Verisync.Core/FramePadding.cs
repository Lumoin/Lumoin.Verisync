using System;
using System.Numerics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// An immutable policy that rounds a serialized payload up to one of a fixed ladder of size buckets, so a
/// network observer sees only the bucket a frame fell into rather than its true length. Frame sizes
/// otherwise leak message types and content sizes — a prepare, an accept, and a state transfer are
/// distinguishable by length alone — so quantizing the wire length to coarse buckets is the metadata-privacy
/// measure of the project's sealed-segments/privacy design.
/// </summary>
/// <remarks>
/// <para>
/// A policy is built through one of the factory methods and is otherwise opaque: callers configure both
/// endpoints of a <see cref="MessageChannelWriter{TMessage}"/>/<see cref="MessageChannelReader{TMessage}"/>
/// pair with the same policy and read it back as a padded length.
/// </para>
/// <para>
/// <b>Wire format.</b> A padded frame keeps the channel's outer four-byte big-endian length prefix, but that
/// prefix now declares the <em>padded</em> payload length — a bucket size — rather than the real length. The
/// padded payload is itself laid out as a four-byte big-endian <em>real</em> length prefix, the real payload
/// bytes, then zero fill to the bucket boundary:
/// </para>
/// <code>
/// +-----------------------+-------------------+-------------------------+----------------+
/// | outer length (4 bytes)| inner length      | real payload            | zero fill      |
/// | = padded length       | (4 bytes)         | (inner-length bytes)    | to the bucket  |
/// +-----------------------+-------------------+-------------------------+----------------+
///  &lt;-- bucket size = outer length, the only quantity an observer can measure ----------&gt;
/// </code>
/// <para>
/// The smallest bucket therefore admits a real payload of <c>bucket - 4</c> bytes, the four bytes spent on
/// the inner length prefix. The reader treats the inner length as attacker-influenced and never trusts it
/// past the frame bounds.
/// </para>
/// <para>
/// Both endpoints must agree on the policy. A reader configured with a different policy — or none — still
/// frames correctly off the trusted outer prefix, but deserializes the wrong span and so fails the same way
/// a maximum-frame-length mismatch does.
/// </para>
/// </remarks>
public sealed class FramePadding
{
    /// <summary>The four-byte big-endian inner length prefix that precedes the real payload inside a bucket.</summary>
    private const int InnerLengthPrefixLength = 4;

    /// <summary>The smallest bucket; every bucket is a positive multiple or power-of-two scaling of this.</summary>
    private int MinimumBucket { get; }

    /// <summary>
    /// When <see langword="true"/>, buckets double — <c>MinimumBucket, 2x, 4x, ...</c>; when
    /// <see langword="false"/>, buckets step by a fixed size — <c>MinimumBucket, 2*MinimumBucket, ...</c>.
    /// </summary>
    private bool DoublesEachStep { get; }


    private FramePadding(int minimumBucket, bool doublesEachStep)
    {
        MinimumBucket = minimumBucket;
        DoublesEachStep = doublesEachStep;
    }


    /// <summary>
    /// Creates a policy whose buckets are powers of two scaled from <paramref name="minimumBucket"/>:
    /// <c>minimumBucket, 2 * minimumBucket, 4 * minimumBucket, ...</c>. Doubling keeps the number of distinct
    /// observable sizes logarithmic in the payload length, at the cost of up to nearly doubling the smallest
    /// admissible frame.
    /// </summary>
    /// <param name="minimumBucket">The smallest bucket size in bytes. Must be at least eight and a power of two.</param>
    /// <returns>The padding policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="minimumBucket"/> is less than eight or is not a power of two.</exception>
    public static FramePadding PowersOfTwo(int minimumBucket)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumBucket, 8);
        if(!BitOperations.IsPow2(minimumBucket))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumBucket), minimumBucket, "The minimum bucket must be a power of two.");
        }

        return new FramePadding(minimumBucket, doublesEachStep: true);
    }


    /// <summary>
    /// Creates a policy whose buckets are fixed multiples of <paramref name="bucketSize"/>:
    /// <c>bucketSize, 2 * bucketSize, 3 * bucketSize, ...</c>. Even steps make every observable size a
    /// multiple of one quantum, at the cost of more distinct sizes than the doubling ladder.
    /// </summary>
    /// <param name="bucketSize">The bucket quantum in bytes. Must be at least eight.</param>
    /// <returns>The padding policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="bucketSize"/> is less than eight.</exception>
    public static FramePadding FixedBuckets(int bucketSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketSize, 8);

        return new FramePadding(bucketSize, doublesEachStep: false);
    }


    /// <summary>
    /// Returns the smallest bucket large enough to hold <paramref name="payloadLength"/> real payload bytes
    /// together with the four-byte inner length prefix — that is, the smallest bucket at or above
    /// <c><paramref name="payloadLength"/> + 4</c>.
    /// </summary>
    /// <param name="payloadLength">The real serialized payload length in bytes.</param>
    /// <returns>The padded payload length in bytes, a bucket size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="payloadLength"/> is negative.</exception>
    public int PaddedLength(int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        //The inner length prefix shares the bucket with the real payload, so this is the quantity to round up.
        int required = payloadLength + InnerLengthPrefixLength;
        if(required <= MinimumBucket)
        {
            return MinimumBucket;
        }

        if(DoublesEachStep)
        {
            //Smallest power-of-two multiple of MinimumBucket at or above the requirement: scale the minimum
            //by the next power of two that covers the ratio. RoundUpToPowerOf2 of the ceiling ratio gives it.
            uint ratio = (uint)((required + MinimumBucket - 1) / MinimumBucket);
            uint factor = BitOperations.RoundUpToPowerOf2(ratio);

            return MinimumBucket * (int)factor;
        }

        //Smallest fixed multiple of MinimumBucket at or above the requirement.
        int multiples = (required + MinimumBucket - 1) / MinimumBucket;

        return MinimumBucket * multiples;
    }
}
