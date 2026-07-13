using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The public addressing type of the checkpoint-offset sequence strategy: a structural
/// <see cref="OffsetAnchor"/> paired with the base generation it was read at. This is what a peer or an
/// editor holds, exchanges, and presents across compaction generations, while <see cref="OffsetAnchor"/>
/// itself stays internal to the strategy and its state model.
/// </summary>
/// <remarks>
/// <para>
/// A reference type is load-bearing: the anchor-translation seam returns <c>TAnchor?</c> for an
/// unconstrained <c>TAnchor</c>, which is a real nullable only when the type is a reference type, so a
/// class is what carries the fail-closed <see langword="null"/> through the seam and keeps every null
/// assertion meaningful.
/// </para>
/// <para>
/// Every construction path is canonical, so structural equality is meaningful for every shape: a base
/// anchor carries a non-negative generation and two base addresses of one offset differ exactly when
/// their generations differ, while a live or head anchor is exact across generations and carries the
/// single canonical generation zero, so two addresses of one live element are equal regardless of when
/// they were read. A <see langword="with"/> expression re-validates each changed member against the
/// retained other in written order, so the copy path can never yield an address the constructor refuses.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed record OffsetAddress
{
    private readonly OffsetAnchor anchor;
    private readonly int generation;


    /// <summary>Initializes a canonical address from the anchor and the base generation it was read at.</summary>
    /// <param name="anchor">The structural anchor this address carries.</param>
    /// <param name="generation">The base generation the anchor was read at: any non-negative value for a base anchor, exactly zero for a live or head anchor.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="anchor"/> is a base anchor and <paramref name="generation"/> is negative.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="anchor"/> is a live or head anchor and <paramref name="generation"/> is not zero.</exception>
    public OffsetAddress(OffsetAnchor anchor, int generation)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ValidateCanonical(anchor, generation);
        this.anchor = anchor;
        this.generation = generation;
    }


    /// <summary>The structural anchor this address carries.</summary>
    public OffsetAnchor Anchor
    {
        get => anchor;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateCanonical(value, generation);
            anchor = value;
        }
    }

    /// <summary>The base generation the anchor was read at; zero for a live or head anchor.</summary>
    public int Generation
    {
        get => generation;
        init
        {
            ValidateCanonical(anchor, value);
            generation = value;
        }
    }


    /// <summary>Deconstructs the address into its anchor and generation.</summary>
    /// <param name="anchor">The structural anchor.</param>
    /// <param name="generation">The base generation.</param>
    public void Deconstruct(out OffsetAnchor anchor, out int generation)
    {
        anchor = Anchor;
        generation = Generation;
    }


    //Enforces the canonical shape the address's structural equality depends on: a base anchor carries a
    //non-negative generation, the head or a live anchor carries exactly zero. Both init accessors run this
    //against the retained other member, so the with-expression copy path is validated exactly as the
    //constructor is.
    private static void ValidateCanonical(OffsetAnchor anchor, int generation)
    {
        if(anchor.IsLive || anchor.BaseOffset < 0)
        {
            if(generation != 0)
            {
                throw new ArgumentException($"A live or head address is exact across generations and carries generation 0, got {generation}.", nameof(generation));
            }
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfNegative(generation);
        }
    }


    private string DebuggerDisplay => Anchor.IsLive
        ? $"OffsetAddress: live {Anchor.LiveId} @generation {Generation}"
        : Anchor.BaseOffset < 0
            ? $"OffsetAddress: head @generation {Generation}"
            : $"OffsetAddress: base[{Anchor.BaseOffset}] @generation {Generation}";
}
