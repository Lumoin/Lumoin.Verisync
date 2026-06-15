using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Encodes a finite set of fixed-width items into an unbounded stream of coded symbols. Symbol <c>n</c> is
/// the XOR-fold of <c>(item, checksum(item))</c> over every item whose index walk visits <c>n</c>. The
/// encoding is linear over GF(2), so the symbol-wise <see cref="ReconciliationSymbol.Combine(ReconciliationSymbol)"/>
/// of two encoders' streams is the stream of their symmetric difference, which a <see cref="ReconciliationDecoder"/>
/// peels without either side ever estimating the difference size.
/// </summary>
/// <remarks>
/// <para>
/// This is a mutable, single-threaded class and is not safe for concurrent calls. A stream prefix must
/// cover one set version: the session layer pins a snapshot before producing, because a prefix that spans a
/// mutation no longer subtracts cleanly against a peer's prefix.
/// </para>
/// <para>
/// The net set is the set of items whose total operation count — <see cref="Add(ReadOnlySpan{byte})"/> plus
/// <see cref="Remove(ReadOnlySpan{byte})"/>, both self-inverse under XOR — is odd. Because of the
/// history-erasure law, at any moment <see cref="SymbolAt(int)"/> equals the symbol a fresh encoder over the
/// current net set would produce at that index; the incremental machinery (produced cell buffers plus a heap
/// of pending walk cursors) is an implementation detail and never observable.
/// </para>
/// </remarks>
public sealed class ReconciliationEncoder: IDisposable
{
    //The dense, contiguous store of produced cells; symbol n is the snapshot of cell n's sum and checksum.
    private ReconciliationCellBuffer Cells { get; }

    //The per-add item bytes, kept stable so the Members keys and the pending walk cursors can view them
    //without a copy of their own; the arena never relocates a stored item.
    private ReconciliationItemArena Items { get; }

    //Cursors for item contributions not yet folded into produced cells, ordered by the next walk index each
    //will visit. Duplicate cursors for the same bytes are legal and cancel under XOR (set semantics).
    private PriorityQueue<WalkCursor, long> PendingCursors { get; } = new();

    //Membership bookkeeping, populated only under DebugAssert or Strict enforcement.
    private HashSet<ContentAddress> Members { get; } = [];

    private bool disposed;


    /// <summary>
    /// Initializes an encoder over <paramref name="contract"/> with no injectivity enforcement.
    /// </summary>
    /// <param name="contract">The contract pinning item width, checksum width, and checksum key.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> is <see langword="null"/>.</exception>
    public ReconciliationEncoder(ReconciliationContract contract)
        : this(contract, ReconciliationInjectivityEnforcement.None, pool: null, cellCapacityHint: 0)
    {
    }


    /// <summary>
    /// Initializes an encoder over <paramref name="contract"/> with the given injectivity enforcement.
    /// </summary>
    /// <param name="contract">The contract pinning item width, checksum width, and checksum key.</param>
    /// <param name="enforcement">How strictly the encoder polices duplicate adds and unmatched removes.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="enforcement"/> is not a defined value.</exception>
    public ReconciliationEncoder(ReconciliationContract contract, ReconciliationInjectivityEnforcement enforcement)
        : this(contract, enforcement, pool: null, cellCapacityHint: 0)
    {
    }


    /// <summary>
    /// Initializes an encoder over <paramref name="contract"/> with the given injectivity enforcement, renting
    /// the produced-cell store from <paramref name="pool"/> and pre-sizing it with <paramref name="cellCapacityHint"/>.
    /// </summary>
    /// <param name="contract">The contract pinning item width, checksum width, and checksum key.</param>
    /// <param name="enforcement">How strictly the encoder polices duplicate adds and unmatched removes.</param>
    /// <param name="pool">The pool the cell store rents from, or <see langword="null"/> for the heap fallback. The encoder never disposes the pool.</param>
    /// <param name="cellCapacityHint">A lower bound on the cells the session will touch, pre-sizing the store; must not be negative.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="enforcement"/> is not a defined value or <paramref name="cellCapacityHint"/> is negative.</exception>
    public ReconciliationEncoder(ReconciliationContract contract, ReconciliationInjectivityEnforcement enforcement, MemoryPool<byte>? pool, int cellCapacityHint)
    {
        ArgumentNullException.ThrowIfNull(contract);

        if(enforcement is not (ReconciliationInjectivityEnforcement.None or ReconciliationInjectivityEnforcement.DebugAssert or ReconciliationInjectivityEnforcement.Strict))
        {
            throw new ArgumentOutOfRangeException(nameof(enforcement), enforcement, "The enforcement mode must be a defined value.");
        }

        Contract = contract;
        Enforcement = enforcement;
        Cells = new ReconciliationCellBuffer(contract.ItemWidth, contract.ChecksumWidth, pool, cellCapacityHint);

        //No try/catch guard is needed building the arena after the cell buffer: the arena rents nothing here (its
        //first block is lazy), so it has nothing to leak. Neither of its ctor failure modes can fire once the cell
        //buffer succeeded — stride validation cannot trigger (contract.ItemWidth is validated to one through 1024),
        //and its eager hint bound-check rejects on capacity * ItemWidth > Array.MaxLength, the same product the cell
        //buffer already checked first and more strictly (also against the checksum width), so any hint the cell
        //buffer accepted the arena accepts too.
        Items = new ReconciliationItemArena(contract.ItemWidth, pool, cellCapacityHint);
    }


    /// <summary>The contract this encoder produces against.</summary>
    public ReconciliationContract Contract { get; }

    /// <summary>The injectivity enforcement mode in effect.</summary>
    public ReconciliationInjectivityEnforcement Enforcement { get; }

    /// <summary>The number of symbols produced so far, which is the index of the next symbol <see cref="ProduceNext"/> will yield.</summary>
    public int ProducedCount => Cells.Count;


    /// <summary>
    /// Adds <paramref name="item"/> to the net set. Its contribution is XORed into every already-produced
    /// symbol whose walk index is below <see cref="ProducedCount"/>, and it contributes to every future
    /// symbol its walk visits.
    /// </summary>
    /// <param name="item">The item bytes. Must be exactly <c>Contract.ItemWidth</c> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="item"/>'s length differs from <c>Contract.ItemWidth</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown under <see cref="ReconciliationInjectivityEnforcement.Strict"/> when <paramref name="item"/> is already in the membership set.
    /// </exception>
    public void Add(ReadOnlySpan<byte> item)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        ApplyOperation(item, isAdd: true);
    }


    /// <summary>
    /// Removes <paramref name="item"/> from the net set. Mechanically identical to
    /// <see cref="Add(ReadOnlySpan{byte})"/> because XOR is self-inverse: the contribution is XORed out of
    /// every produced symbol below <see cref="ProducedCount"/> and out of every future symbol its walk visits.
    /// </summary>
    /// <param name="item">The item bytes. Must be exactly <c>Contract.ItemWidth</c> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="item"/>'s length differs from <c>Contract.ItemWidth</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown under <see cref="ReconciliationInjectivityEnforcement.Strict"/> when <paramref name="item"/> is not in the membership set.
    /// </exception>
    public void Remove(ReadOnlySpan<byte> item)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        ApplyOperation(item, isAdd: false);
    }


    /// <summary>
    /// Produces the symbol at the current <see cref="ProducedCount"/>, then advances the count by one. The
    /// returned instance is a snapshot: later mutations do not change it, though they do change what
    /// <see cref="SymbolAt(int)"/> returns for the same index.
    /// </summary>
    /// <returns>The newly produced symbol.</returns>
    public ReconciliationSymbol ProduceNext()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        //Append the cell first (it starts all zero), then fold pending cursors straight into its spans. No
        //Append happens between obtaining the spans and snapshotting, so the spans stay valid throughout.
        int i = Cells.Append();
        long index = i;

        //Fold in every pending cursor that visits this index, then advance and re-queue it past the index.
        while(PendingCursors.TryPeek(out _, out long nextIndex) && nextIndex == index)
        {
            WalkCursor cursor = PendingCursors.Dequeue();
            FoldInto(Cells.SumAt(i), Cells.ChecksumAt(i), cursor);

            cursor.Position = ReconciliationIndexWalk.Next(cursor.Position);
            PendingCursors.Enqueue(cursor, cursor.Position.Index);
        }

        return new ReconciliationSymbol(Cells.SumAt(i), Cells.ChecksumAt(i));
    }


    /// <summary>
    /// Returns a snapshot of the current value of the produced symbol at <paramref name="index"/>, reflecting
    /// every <see cref="Add(ReadOnlySpan{byte})"/> and <see cref="Remove(ReadOnlySpan{byte})"/> applied so
    /// far. This is the incremental-update property: a cached stream prefix tracks the live set.
    /// </summary>
    /// <param name="index">The symbol index, in the range zero through <see cref="ProducedCount"/> exclusive.</param>
    /// <returns>A snapshot of the symbol at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the produced range.</exception>
    public ReconciliationSymbol SymbolAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(index < 0 || index >= ProducedCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The symbol index must be within the produced range.");
        }

        return new ReconciliationSymbol(Cells.SumAt(index), Cells.ChecksumAt(index));
    }


    /// <summary>
    /// Disposes the produced-cell store, releasing its rentals; the call is idempotent. After it, the
    /// encoder's mutators and readers throw <see cref="ObjectDisposedException"/>. The injected pool, if any,
    /// is not disposed — the encoder owns its cell store, not the pool.
    /// </summary>
    public void Dispose()
    {
        if(disposed)
        {
            return;
        }

        disposed = true;
        Cells.Dispose();
        Items.Dispose();
    }


    private void ApplyOperation(ReadOnlySpan<byte> item, bool isAdd)
    {
        if(item.Length != Contract.ItemWidth)
        {
            throw new ArgumentException($"An item must be exactly {Contract.ItemWidth} bytes.", nameof(item));
        }

        //Copy the item once into the never-moved arena; both the membership key and the walk cursor view this
        //stable slice, so an item costs one tracked copy instead of two naked arrays. The append happens before
        //EnforceMembership, which under Strict throws on a duplicate add / missing remove before recording the
        //key or enqueuing the cursor — so a rejected op leaves an orphaned arena slice (dead bytes until
        //dispose). That is acceptable: the slice is owned and returned on dispose so accountability still
        //balances, and Members cannot be probed by a bare span without first materializing the bytes, which is
        //exactly this append.
        ReadOnlyMemory<byte> itemBytes = Items.Append(item);

        EnforceMembership(itemBytes, isAdd);

        Span<byte> checksumBytes = stackalloc byte[Contract.ChecksumWidth];
        ulong checksum = ReconciliationChecksum.Compute(Contract.ChecksumKeyLow, Contract.ChecksumKeyHigh, itemBytes.Span);
        ReconciliationChecksum.Write(checksum, checksumBytes);

        //Walk the item: fold every index below ProducedCount straight into the produced cell, and seed a
        //pending cursor at the first index at or above ProducedCount for all future symbols.
        ReconciliationWalkPosition position = ReconciliationIndexWalk.Start(itemBytes.Span);
        while(position.Index < ProducedCount)
        {
            int cellIndex = (int)position.Index;
            ReconciliationXor.Fold(Cells.SumAt(cellIndex), itemBytes.Span);
            ReconciliationXor.Fold(Cells.ChecksumAt(cellIndex), checksumBytes);

            position = ReconciliationIndexWalk.Next(position);
        }

        PendingCursors.Enqueue(new WalkCursor(itemBytes, checksum, position), position.Index);
    }


    private void EnforceMembership(ReadOnlyMemory<byte> itemBytes, bool isAdd)
    {
        if(Enforcement == ReconciliationInjectivityEnforcement.None)
        {
            return;
        }

        var key = new ContentAddress(itemBytes);
        bool present = Members.Contains(key);

        if(Enforcement == ReconciliationInjectivityEnforcement.Strict)
        {
            if(isAdd && present)
            {
                throw new InvalidOperationException("An item already in the set cannot be added again under strict enforcement.");
            }

            if(!isAdd && !present)
            {
                throw new InvalidOperationException("An item not in the set cannot be removed under strict enforcement.");
            }
        }
        else
        {
            Debug.Assert(!(isAdd && present), "An item already in the set was added again.");
            Debug.Assert(isAdd || present, "An item not in the set was removed.");
        }

        if(isAdd)
        {
            Members.Add(key);
        }
        else
        {
            Members.Remove(key);
        }
    }


    private void FoldInto(Span<byte> sum, Span<byte> checksum, in WalkCursor cursor)
    {
        ReconciliationXor.Fold(sum, cursor.ItemBytes.Span);

        //Materialize the stored checksum into the contract-width form and fold it; this is byte-for-byte the
        //array the cursor used to carry, so the fold is unchanged.
        Span<byte> checksumBytes = stackalloc byte[Contract.ChecksumWidth];
        ReconciliationChecksum.Write(cursor.Checksum, checksumBytes);
        ReconciliationXor.Fold(checksum, checksumBytes);
    }


    //A non-compared internal priority-queue carrier reused by value across folds; the item bytes and the
    //checksum are fixed for the item's lifetime, only the next walk index advances.
    [SuppressMessage("Usage", "CA1815", Justification = "A non-compared internal priority-queue carrier; cursors are never compared, only enqueued and dequeued.")]
    private struct WalkCursor(ReadOnlyMemory<byte> itemBytes, ulong checksum, ReconciliationWalkPosition position)
    {
        public ReadOnlyMemory<byte> ItemBytes { get; } = itemBytes;


        public ulong Checksum { get; } = checksum;


        public ReconciliationWalkPosition Position { get; set; } = position;
    }
}
