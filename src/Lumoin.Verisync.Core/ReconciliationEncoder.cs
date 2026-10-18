using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

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
/// The net set is the set of items whose total operation count -- <see cref="Add(ReadOnlySpan{byte})"/> plus
/// <see cref="Remove(ReadOnlySpan{byte})"/>, both self-inverse under XOR -- is odd. Because of the
/// history-erasure law, at any moment <see cref="SymbolAt(int)"/> equals the symbol a fresh encoder over the
/// current net set would produce at that index; the incremental machinery (produced cell buffers plus a heap
/// of pending walk cursors) is an implementation detail and never observable.
/// </para>
/// <para>
/// THE STREAM HAS NO LENGTH TO DECLARE IN ADVANCE, WHICH IS THE POINT OF IT RATHER THAN A GAP. How many
/// symbols a peer must absorb tracks the size of the symmetric difference, which neither side knows before
/// the exchange; an encoder that could state a budget would not be rateless. A layer that shapes or paces
/// this traffic therefore sizes a batch rather than an operation, and it can size that exactly: a symbol is
/// <see cref="ReconciliationContract.ItemWidth"/> plus <see cref="ReconciliationContract.ChecksumWidth"/>
/// bytes of payload, and the batch's own symbol count is whatever the caller put in it. A hint claiming a
/// whole operation's size would be a fiction whichever number it carried.
/// </para>
/// <para>
/// Delay, loss and reordering of symbols cost the exchange nothing but time. The encoding is linear over
/// GF(2) and every symbol is independent of the order it arrives in, so a peer that drops symbols simply
/// absorbs more of them, and one that receives them out of order decodes the same set. That tolerance is
/// structural, not a retry loop layered on top.
/// </para>
/// </remarks>
public sealed class ReconciliationEncoder: IDisposable
{
    /// <summary>The dense, contiguous store of produced cells; symbol n is the snapshot of cell n's sum and checksum.</summary>
    private ReconciliationCellBuffer Cells { get; }

    /// <summary>The stable item bytes viewed by membership keys and addressed by pending cursors through append indices.</summary>
    private ReconciliationItemArena Items { get; }

    /// <summary>Pending item contributions ordered by their next walk index; duplicate cursors cancel under XOR.</summary>
    private PriorityQueue<WalkCursor, long> PendingCursors { get; }

    /// <summary>Membership bookkeeping, populated only under DebugAssert or Strict enforcement.</summary>
    private HashSet<ContentAddress> Members { get; } = [];

    /// <summary>Whether the rentals have been released.</summary>
    private bool disposed;


    /// <summary>
    /// Initializes an encoder over <paramref name="contract"/> with the given injectivity enforcement, renting
    /// the produced-cell store from <paramref name="pool"/> and pre-sizing the cells and item arena independently with <paramref name="hint"/>.
    /// </summary>
    /// <param name="contract">The contract pinning item width, checksum width, and checksum key.</param>
    /// <param name="enforcement">How strictly the encoder polices duplicate adds and unmatched removes.</param>
    /// <param name="pool">The pool the cell store rents from. The encoder never disposes the pool.</param>
    /// <param name="hint">The item hint sizes the item arena and pending cursors; the cell hint sizes the cell store.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> or <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="enforcement"/> is not a defined value or the cell hint rounds up to a capacity whose backing would exceed the maximum array length.</exception>
    public ReconciliationEncoder(ReconciliationContract contract, ReconciliationInjectivityEnforcement enforcement, MemoryPool<byte> pool, ReconciliationCapacityHint hint)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(pool);

        if(enforcement is not (ReconciliationInjectivityEnforcement.None or ReconciliationInjectivityEnforcement.DebugAssert or ReconciliationInjectivityEnforcement.Strict))
        {
            throw new ArgumentOutOfRangeException(nameof(enforcement), enforcement, "The enforcement mode must be a defined value.");
        }

        Contract = contract;
        Enforcement = enforcement;
        Items = new ReconciliationItemArena(contract.ItemWidth, pool, hint.Items);
        PendingCursors = new PriorityQueue<WalkCursor, long>(hint.Items);

        //The cell store is rented last so that no later step can fail.
        Cells = new ReconciliationCellBuffer(contract.ItemWidth, contract.ChecksumWidth, pool, hint.Cells);
    }


    /// <summary>The contract this encoder produces against.</summary>
    public ReconciliationContract Contract { get; }

    /// <summary>The injectivity enforcement mode in effect.</summary>
    public ReconciliationInjectivityEnforcement Enforcement { get; }

    /// <summary>The number of symbols produced so far, which is the index of the next symbol <see cref="ProduceNext"/> will yield.</summary>
    public int ProducedCount => Cells.Count;


    /// <summary>The number of pending walk cursors; zero after disposal.</summary>
    internal int PendingCursorCount => PendingCursors.Count;


    /// <summary>The capacity of the pending cursor backing; zero after disposal.</summary>
    internal int PendingCursorCapacity => PendingCursors.EnsureCapacity(0);


    /// <summary>The number of membership keys; zero after disposal.</summary>
    internal int MemberCount => Members.Count;


    /// <summary>The capacity of the membership backing; trimmed on disposal.</summary>
    internal int MemberCapacity => Members.EnsureCapacity(0);


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
            PendingCursors.TryDequeue(out WalkCursor cursor, out long cursorIndex);
            var position = new ReconciliationWalkPosition(cursorIndex, cursor.State);
            FoldInto(Cells.SumAt(i), Cells.ChecksumAt(i), Items.ItemAt(cursor.ItemIndex).Span, cursor.Checksum);

            position = ReconciliationIndexWalk.Next(position);
            cursor.State = position.State;
            PendingCursors.Enqueue(cursor, position.Index);
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
    /// Releases the cell and item rentals, pending cursors, and membership storage; the call is idempotent.
    /// After it, the encoder's mutators and readers throw <see cref="ObjectDisposedException"/>.
    /// The injected pool is not disposed.
    /// If a component's release throws, every remaining component is still released and the first failure propagates afterwards.
    /// </summary>
    public void Dispose()
    {
        if(disposed)
        {
            return;
        }

        disposed = true;
        Exception? failure = null;

        try
        {
            Cells.Dispose();
        }
        catch(Exception exception)
        {
            failure = exception;
        }

        try
        {
            Items.Dispose();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            PendingCursors.Clear();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            PendingCursors.TrimExcess();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            Members.Clear();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            Members.TrimExcess();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }

        if(failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }


    /// <summary>Copies an item and folds its contribution into produced cells and pending walk cursors.</summary>
    private void ApplyOperation(ReadOnlySpan<byte> item, bool isAdd)
    {
        if(item.Length != Contract.ItemWidth)
        {
            throw new ArgumentException($"An item must be exactly {Contract.ItemWidth} bytes.", nameof(item));
        }

        //Copy the item once into the never-moved arena; the membership key views this stable slice and the walk
        //cursor names it by its append index. The append precedes EnforceMembership, which under Strict throws
        //on a duplicate add or a missing remove before the key is recorded or the cursor enqueued, so a rejected
        //operation leaves an orphaned arena slice until dispose. The slice stays owned and is returned on
        //dispose, so accountability still balances, and Members cannot be probed by a bare span without first
        //materializing the bytes.
        int itemIndex = Items.Count;
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

        PendingCursors.Enqueue(new WalkCursor(itemIndex, checksum, position.State), position.Index);
    }


    /// <summary>Checks and updates membership according to the configured enforcement mode.</summary>
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


    /// <summary>Folds the item bytes and their contract-width checksum into the supplied cell spans.</summary>
    private void FoldInto(Span<byte> sum, Span<byte> checksum, ReadOnlySpan<byte> item, ulong itemChecksum)
    {
        ReconciliationXor.Fold(sum, item);

        //Materialize the stored checksum into the contract-width form before folding it.
        Span<byte> checksumBytes = stackalloc byte[Contract.ChecksumWidth];
        ReconciliationChecksum.Write(itemChecksum, checksumBytes);
        ReconciliationXor.Fold(checksum, checksumBytes);
    }


    /// <summary>
    /// Names an item by its arena index and carries its checksum and the walk's generator state; the next
    /// walk index lives only in the queue's priority. Cursors are reused by value and never compared.
    /// </summary>
    /// <param name="itemIndex">The item's append index in the arena.</param>
    /// <param name="checksum">The item's fixed checksum.</param>
    /// <param name="state">The generator state at the queued walk index.</param>
    [SuppressMessage("Usage", "CA1815", Justification = "A non-compared internal priority-queue carrier; cursors are never compared, only enqueued and dequeued.")]
    private struct WalkCursor(int itemIndex, ulong checksum, ulong state)
    {
        /// <summary>The item's append index in the arena.</summary>
        public int ItemIndex { get; } = itemIndex;


        /// <summary>The item's fixed checksum.</summary>
        public ulong Checksum { get; } = checksum;


        /// <summary>The generator state at the walk index held in the queue's priority.</summary>
        public ulong State { get; set; } = state;
    }
}
