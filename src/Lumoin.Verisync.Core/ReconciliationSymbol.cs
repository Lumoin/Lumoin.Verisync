using System;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A coded symbol (a cell) of a reconciliation stream: a pair of XOR-accumulated fields,
/// <see cref="Sum"/> over the item bytes and <see cref="Checksum"/> over the item checksums of every item
/// whose index walk visits this symbol's index. Both fields accumulate by XOR, so the encoding is linear
/// over GF(2) and the symbol-wise <see cref="Combine(ReconciliationSymbol)"/> of two streams is the stream
/// of their symmetric difference.
/// </summary>
/// <remarks>
/// <para>
/// The constructor copies both arguments into private arrays, so a caller may reuse its buffers freely. A
/// cell is <see cref="IsNeutral"/> when every byte of both fields is zero; the decoder reads out a cell as a
/// decoded item when the cell is not neutral and the checksum of its sum bytes matches its checksum field.
/// </para>
/// <para>
/// The reconciliation tier rents and tracks its SCOPED buffers — the cell store, and the
/// per-add and decoded-item bytes — through the memory pool and releases them deterministically on disposal.
/// A symbol's <see cref="Sum"/> and <see cref="Checksum"/> are an ESCAPED value by contrast: they are handed
/// to callers, held in a batch's immutable array, and read by the wire codec, with no deterministic release
/// point, so the symbol owns its bytes outright as the SANCTIONED EXCEPTION to the tier's no-naked-array rule
/// and is deliberately not <see cref="IDisposable"/>. The partition is explicit — scoped buffers are pooled,
/// tracked, and disposed; escaped symbol bytes are owned and garbage-collected — and only escaped values cross
/// the tier's boundary.
/// </para>
/// </remarks>
public sealed record ReconciliationSymbol
{
    private byte[] SumBytes { get; }
    private byte[] ChecksumBytes { get; }


    /// <summary>
    /// Initializes a symbol from a sum field and a checksum field, copying both so the caller may reuse the
    /// source buffers.
    /// </summary>
    /// <param name="sum">The sum field. Must be non-empty; its length is the contract's item width.</param>
    /// <param name="checksum">The checksum field. Its length must be in the inclusive range one through eight.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sum"/> is empty or when <paramref name="checksum"/>'s length is outside one through eight.
    /// </exception>
    public ReconciliationSymbol(ReadOnlyMemory<byte> sum, ReadOnlyMemory<byte> checksum)
    {
        if(sum.IsEmpty)
        {
            throw new ArgumentException("A symbol's sum field cannot be empty.", nameof(sum));
        }

        if(checksum.Length is < 1 or > 8)
        {
            throw new ArgumentException("A symbol's checksum field must be between one and eight bytes.", nameof(checksum));
        }

        SumBytes = sum.ToArray();
        ChecksumBytes = checksum.ToArray();
    }


    /// <summary>
    /// Initializes a symbol from a sum field and a checksum field expressed as spans, copying both so the
    /// caller may reuse the source buffers. This lets a symbol be snapshotted from a cell buffer's spans in a
    /// single copy with the same validation and semantics as the <see cref="ReadOnlyMemory{T}"/> constructor.
    /// </summary>
    /// <param name="sum">The sum field. Must be non-empty; its length is the contract's item width.</param>
    /// <param name="checksum">The checksum field. Its length must be in the inclusive range one through eight.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sum"/> is empty or when <paramref name="checksum"/>'s length is outside one through eight.
    /// </exception>
    public ReconciliationSymbol(ReadOnlySpan<byte> sum, ReadOnlySpan<byte> checksum)
    {
        if(sum.IsEmpty)
        {
            throw new ArgumentException("A symbol's sum field cannot be empty.", nameof(sum));
        }

        if(checksum.Length is < 1 or > 8)
        {
            throw new ArgumentException("A symbol's checksum field must be between one and eight bytes.", nameof(checksum));
        }

        SumBytes = sum.ToArray();
        ChecksumBytes = checksum.ToArray();
    }


    /// <summary>The XOR-accumulated sum field, the item-width bytes of the items folded into this cell.</summary>
    public ReadOnlyMemory<byte> Sum => SumBytes;

    /// <summary>The XOR-accumulated checksum field, the truncated item checksums of the items folded into this cell.</summary>
    public ReadOnlyMemory<byte> Checksum => ChecksumBytes;

    /// <summary>Whether every byte of both fields is zero, marking a cell with no net contribution.</summary>
    public bool IsNeutral => ReconciliationXor.IsNeutral(SumBytes) && ReconciliationXor.IsNeutral(ChecksumBytes);


    /// <summary>
    /// Returns the byte-wise XOR of this symbol's fields with <paramref name="other"/>'s. Over GF(2) addition
    /// is its own inverse, so this is simultaneously the sum and the difference of the two symbols; applied
    /// index-wise across two streams it yields the stream of their symmetric difference.
    /// </summary>
    /// <param name="other">The symbol to combine with. Must have the same field widths.</param>
    /// <returns>A new symbol holding the byte-wise XOR of both fields.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when either field's length differs from this symbol's.</exception>
    public ReconciliationSymbol Combine(ReconciliationSymbol other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if(other.SumBytes.Length != SumBytes.Length || other.ChecksumBytes.Length != ChecksumBytes.Length)
        {
            throw new ArgumentException("Symbols can only be combined when both fields have matching widths.", nameof(other));
        }

        var sum = new byte[SumBytes.Length];
        ReconciliationXor.Combine(SumBytes, other.SumBytes, sum);

        var checksum = new byte[ChecksumBytes.Length];
        ReconciliationXor.Combine(ChecksumBytes, other.ChecksumBytes, checksum);

        return new ReconciliationSymbol(sum, checksum);
    }


    /// <summary>Determines whether <paramref name="other"/> has byte-identical sum and checksum fields.</summary>
    /// <param name="other">The symbol to compare with.</param>
    /// <returns><see langword="true"/> when both fields match byte-for-byte.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ReadOnlyMemory{T}"/>
    /// fields by reference identity; reconciliation equality is byte-sequence equality over both fields.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationSymbol? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return SumBytes.AsSpan().SequenceEqual(other.SumBytes) && ChecksumBytes.AsSpan().SequenceEqual(other.ChecksumBytes);
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(SumBytes);
        hash.AddBytes(ChecksumBytes);

        return hash.ToHashCode();
    }
}
