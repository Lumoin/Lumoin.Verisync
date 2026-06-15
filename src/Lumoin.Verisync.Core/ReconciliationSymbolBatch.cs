using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A contiguous run of coded symbols a responder streams to an initiator. The symbols occupy consecutive
/// stream indices <see cref="StartIndex"/>, <see cref="StartIndex"/> + 1, and so on, so the consumer can
/// absorb them in order without reordering or gaps.
/// </summary>
/// <remarks>
/// All symbols in a batch share the first symbol's sum length and checksum length, the field widths fixed by
/// the contract. The consumer must verify that <see cref="StartIndex"/> equals the count of symbols it has
/// already absorbed — in-order, gap-free streaming — and fail closed otherwise.
/// </remarks>
public sealed record ReconciliationSymbolBatch
{
    /// <summary>
    /// Initializes a batch from the stream index of its first symbol and the symbols themselves, validating
    /// that the run is non-empty, has no null element, and shares one pair of field widths.
    /// </summary>
    /// <param name="startIndex">The stream index of the first symbol, the consumer's absorbed count.</param>
    /// <param name="symbols">The symbols of the batch, occupying consecutive stream indices from <paramref name="startIndex"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="startIndex"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="symbols"/> is default or empty, when any element is <see langword="null"/>,
    /// or when the symbols do not all share the first symbol's sum length and checksum length.
    /// </exception>
    public ReconciliationSymbolBatch(int startIndex, ImmutableArray<ReconciliationSymbol> symbols)
    {
        if(startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), startIndex, "A start index cannot be negative.");
        }

        if(symbols.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A symbol batch must carry at least one symbol.", nameof(symbols));
        }

        ReconciliationSymbol first = symbols[0];
        if(first is null)
        {
            throw new ArgumentException("A symbol batch cannot carry a null symbol.", nameof(symbols));
        }

        int sumLength = first.Sum.Length;
        int checksumLength = first.Checksum.Length;
        for(int i = 1; i < symbols.Length; i++)
        {
            ReconciliationSymbol symbol = symbols[i];
            if(symbol is null)
            {
                throw new ArgumentException("A symbol batch cannot carry a null symbol.", nameof(symbols));
            }

            if(symbol.Sum.Length != sumLength || symbol.Checksum.Length != checksumLength)
            {
                throw new ArgumentException("Every symbol in a batch must share the first symbol's sum and checksum widths.", nameof(symbols));
            }
        }

        StartIndex = startIndex;
        Symbols = symbols;
    }


    /// <summary>The stream index of the first symbol; the consumer must verify it equals its absorbed count.</summary>
    public int StartIndex { get; }

    /// <summary>The symbols of the batch, occupying consecutive stream indices from <see cref="StartIndex"/>.</summary>
    public ImmutableArray<ReconciliationSymbol> Symbols { get; }


    /// <summary>Determines whether <paramref name="other"/> has the same start index and element-wise equal symbols.</summary>
    /// <param name="other">The batch to compare with.</param>
    /// <returns><see langword="true"/> when the start index matches and the symbols are element-wise equal.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ImmutableArray{T}"/>
    /// by reference identity; batch equality is the start index plus element-wise symbol equality.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationSymbolBatch? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(StartIndex != other.StartIndex || Symbols.Length != other.Symbols.Length)
        {
            return false;
        }

        for(int i = 0; i < Symbols.Length; i++)
        {
            if(!Symbols[i].Equals(other.Symbols[i]))
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StartIndex);
        foreach(ReconciliationSymbol symbol in Symbols)
        {
            hash.Add(symbol);
        }

        return hash.ToHashCode();
    }
}
