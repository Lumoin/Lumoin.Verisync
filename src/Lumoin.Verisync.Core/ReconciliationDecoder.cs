using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A peeling decoder that recovers the symmetric difference of two item sets from a prefix of their
/// difference stream. The caller subtracts the peer stream from the local stream symbol-wise — same index,
/// one <see cref="ReconciliationSymbol.Combine(ReconciliationSymbol)"/> per symbol — and feeds each
/// difference symbol here; the decoded items are exactly the items present in one set but not the other.
/// </summary>
/// <remarks>
/// <para>
/// This is a mutable, single-threaded class and is not safe for concurrent calls. It values soundness over
/// completeness: an incomplete decode is a legal outcome, but a wrong decode never happens short of a
/// checksum collision. A degree-two cell can masquerade as a decoded item with probability bounded by the
/// checksum width — that is why the width is contractual and why a secret key exists across trust domains, so
/// a poisoned reconciliation is detected and aborted rather than silently accepted.
/// </para>
/// <para>
/// The masquerade bound is PER PURITY CHECK, and a whole decode compounds it: recovering a difference of
/// <c>d</c> items runs on the order of <c>d·ln d</c> purity checks (each item's walk revisits cells the
/// worklist re-examines), so the per-decode false-peel probability is bounded by roughly
/// <c>d·ln d · 2^(−8·ChecksumWidth)</c> — a union bound, since any single false-pure acceptance corrupts the
/// decode. At the eight-byte width the union term stays negligible for any realistic difference (a
/// million-item difference is still below 2^−39); at four bytes it grows material as differences reach the
/// tens of thousands. Size the contract's width against the expected difference, not the per-cell figure —
/// see <see cref="ReconciliationContract.ChecksumWidth"/> for the sizing rule. The running count of purity
/// checks and the bound it implies are the runtime surface of this union bound, exposed as
/// <see cref="PurityCheckCount"/> and <see cref="FalseDecodeProbabilityBound"/>.
/// </para>
/// <para>
/// Absorbing after completion is legal and changes nothing: decoded knowledge is monotone. Cross-implementation
/// the order of <see cref="DecodedItems"/> is unspecified, but the set is determined by the absorbed prefix
/// because peeling is confluent. Which side actually holds a decoded item is the caller's to resolve through a
/// membership probe, because the symbols deliberately omit any count field.
/// </para>
/// <para>
/// The cell invariant the decoder maintains is this: after each <see cref="Absorb(ReconciliationSymbol)"/>,
/// for every cell index <c>n</c> below <see cref="AbsorbedCount"/>, cell <c>n</c> holds the XOR of the
/// absorbed difference symbol at <c>n</c> and the contributions of every item already decoded whose walk
/// visits <c>n</c>. Equivalently, cell <c>n</c> is the XOR of the contributions of the not-yet-decoded
/// difference items whose walk visits <c>n</c>; a pure cell — non-neutral with <c>checksum == H(sum)</c> —
/// reveals the single such item. Two incremental structures keep this near-linear: a min-heap of decoded
/// cursors folds each decoded item into future cells as they arrive, and a worklist drains the cells a peel
/// may have just made pure, so neither the decoded set nor the cells are ever fully rescanned.
/// </para>
/// </remarks>
public sealed class ReconciliationDecoder: IDisposable
{
    //The dense, contiguous store of absorbed cells; cell n holds the running XOR fold for absorbed index n.
    private ReconciliationCellBuffer Cells { get; }

    //The decoded item bytes, kept stable so the DecodedKeys keys and the pending cursors can view them without
    //a copy of their own; the arena never relocates a stored item.
    private ReconciliationItemArena Items { get; }
    private List<ReadOnlyMemory<byte>> Decoded { get; } = [];
    private HashSet<ContentAddress> DecodedKeys { get; } = [];

    //Cursors for decoded item contributions not yet folded into future cells, ordered by the next walk
    //index each will visit. A cursor is seeded at the first walk index at or above AbsorbedCount, so it only
    //ever fires at cells absorbed after the item was decoded — applied exactly once per visited future index.
    private PriorityQueue<DecodedCursor, long> PendingDecoded { get; } = new();

    //Cell indices that may have become pure: the newly absorbed cell and every cell a peel just modified.
    private Queue<int> Worklist { get; } = new();

    private const int BitsPerByte = 8;

    private bool disposed;


    /// <summary>
    /// Initializes a decoder over <paramref name="contract"/>, renting the absorbed-cell store from
    /// <paramref name="pool"/> and pre-sizing it with <paramref name="cellCapacityHint"/>.
    /// </summary>
    /// <param name="contract">The contract the difference stream was produced under; both peers must share it.</param>
    /// <param name="pool">The pool the cell store rents from. The decoder never disposes the pool.</param>
    /// <param name="cellCapacityHint">A lower bound on the cells the session will touch, pre-sizing the store; must not be negative.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> or <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="cellCapacityHint"/> is negative.</exception>
    public ReconciliationDecoder(ReconciliationContract contract, MemoryPool<byte> pool, int cellCapacityHint = 0)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(pool);

        Contract = contract;
        Cells = new ReconciliationCellBuffer(contract.ItemWidth, contract.ChecksumWidth, pool, cellCapacityHint);

        //No try/catch guard is needed building the arena after the cell buffer: the arena rents nothing here (its
        //first block is lazy), so it has nothing to leak. Neither of its ctor failure modes can fire once the cell
        //buffer succeeded — stride validation cannot trigger (contract.ItemWidth is validated to one through 1024),
        //and its eager hint bound-check rejects on capacity * ItemWidth > Array.MaxLength, the same product the cell
        //buffer already checked first and more strictly (also against the checksum width), so any hint the cell
        //buffer accepted the arena accepts too. The decoded count is typically well below the cell hint, so the arena
        //over-sizes its first block here; that is fine — one block, never relocated, released on dispose, avoiding churn.
        Items = new ReconciliationItemArena(contract.ItemWidth, pool, cellCapacityHint);
    }


    /// <summary>The contract this decoder peels against.</summary>
    public ReconciliationContract Contract { get; }

    /// <summary>The number of difference symbols absorbed so far, which is the index assigned to the next absorbed symbol.</summary>
    public int AbsorbedCount => Cells.Count;

    /// <summary>
    /// Whether the difference has been fully recovered: at least one symbol absorbed and cell zero neutral.
    /// Sound because every item's walk visits index zero, so cell zero is the last to clear; an equal-set
    /// reconciliation is complete after the very first absorbed symbol with no decoded items.
    /// </summary>
    public bool IsComplete => AbsorbedCount > 0 && IsCellNeutral(0);

    /// <summary>
    /// The recovered items in peel order, each an owned copy of <c>Contract.ItemWidth</c> bytes that outlives
    /// the decoder. The decoded bytes live in the arena, so each call copies them out to a fresh owned array
    /// (an escaped value, the sanctioned naked-array exception): handing out the raw arena view would let a
    /// caller read pooled memory after the decoder and its arena are disposed and the segment recycled — a
    /// use-after-dispose hazard. The <c>O(d)</c> copy at the boundary is the deliberate cost of that contract.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> DecodedItems
    {
        get
        {
            var items = new ReadOnlyMemory<byte>[Decoded.Count];
            for(int i = 0; i < Decoded.Count; i++)
            {
                items[i] = Decoded[i].ToArray();
            }

            return items;
        }
    }

    /// <summary>
    /// The number of masquerade opportunities this decode has run: each non-neutral purity evaluation accepts a
    /// mixed cell as pure with probability <c>2^(−8·ChecksumWidth)</c>, so this count is the union-bound
    /// multiplier over the whole decode. A neutral cell carries no single readable item and so is no masquerade
    /// opportunity; it is not counted.
    /// </summary>
    public long PurityCheckCount { get; private set; }

    /// <summary>
    /// The operative per-decode false-peel probability bound — the union over every purity check actually
    /// performed, <c>PurityCheckCount · 2^(−8·ChecksumWidth)</c> — as distinct from the per-cell bound, clamped
    /// to one. At one it is vacuous and the decode's exactness claim carries no weight; a consumer acting on a
    /// decode, a repair path for one, should require this far below one before trusting the recovered
    /// difference. The bound is against random corruption, not an adversary holding the checksum key — the
    /// contract's key discipline covers that adversary.
    /// </summary>
    public double FalseDecodeProbabilityBound => Math.Min(1.0, Math.ScaleB((double)PurityCheckCount, -BitsPerByte * Contract.ChecksumWidth));


    /// <summary>
    /// Absorbs the next difference <paramref name="symbol"/>, assigning it the index <see cref="AbsorbedCount"/>,
    /// then peels to a fixpoint. Each peel reads a pure cell's sum as a decoded item and XORs that item's
    /// contribution out of every cell its walk visits, possibly exposing further pure cells.
    /// </summary>
    /// <param name="symbol">The difference symbol, whose field widths must match the contract.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="symbol"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="symbol"/>'s sum length differs from <c>Contract.ItemWidth</c> or its checksum
    /// length differs from <c>Contract.ChecksumWidth</c>.
    /// </exception>
    public void Absorb(ReconciliationSymbol symbol)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(symbol);

        if(symbol.Sum.Length != Contract.ItemWidth)
        {
            throw new ArgumentException($"A difference symbol's sum must be exactly {Contract.ItemWidth} bytes.", nameof(symbol));
        }

        if(symbol.Checksum.Length != Contract.ChecksumWidth)
        {
            throw new ArgumentException($"A difference symbol's checksum must be exactly {Contract.ChecksumWidth} bytes.", nameof(symbol));
        }

        //Append the new cell (it starts all zero) and copy the raw difference symbol into it. No Append
        //happens again until the next Absorb, so spans at this index stay valid through the drain below.
        int index = Cells.Append();
        symbol.Sum.Span.CopyTo(Cells.SumAt(index));
        symbol.Checksum.Span.CopyTo(Cells.ChecksumAt(index));

        //Maintain the cell invariant against everything already decoded: drain every pending cursor that
        //visits this new index, fold its item out of the new cell, then advance and re-queue it past the index.
        //One reused checksum buffer for the whole drain — each cursor fully rewrites it before the fold, so it
        //is byte-for-byte the array the cursor used to carry and the fold is unchanged.
        Span<byte> drainChecksum = stackalloc byte[Contract.ChecksumWidth];
        while(PendingDecoded.TryPeek(out _, out long nextIndex) && nextIndex == index)
        {
            DecodedCursor cursor = PendingDecoded.Dequeue();
            ReconciliationXor.Fold(Cells.SumAt(index), cursor.ItemBytes.Span);

            ReconciliationChecksum.Write(cursor.Checksum, drainChecksum);
            ReconciliationXor.Fold(Cells.ChecksumAt(index), drainChecksum);

            cursor.Position = ReconciliationIndexWalk.Next(cursor.Position);
            PendingDecoded.Enqueue(cursor, cursor.Position.Index);
        }

        //Seed the worklist with the cell just absorbed, then peel every cell a peel exposes.
        Worklist.Enqueue(index);
        DrainWorklist();
    }


    /// <summary>
    /// Disposes the absorbed-cell store, releasing its rentals; the call is idempotent. After it,
    /// <see cref="Absorb(ReconciliationSymbol)"/> throws <see cref="ObjectDisposedException"/>. The injected
    /// pool, if any, is not disposed — the decoder owns its cell store, not the pool.
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


    private void DrainWorklist()
    {
        //One reused checksum buffer for the whole drain — each decoded item fully rewrites it before its peel,
        //so it is byte-for-byte the per-item array the loop used to allocate and the fold is unchanged.
        Span<byte> itemChecksum = stackalloc byte[Contract.ChecksumWidth];
        while(Worklist.TryDequeue(out int i))
        {
            if(!IsCellPure(i))
            {
                //A neutral or impure cell carries no single readable item; drop it without re-pushing.
                continue;
            }

            //Copy the pure cell's sum into the never-moved arena; the dedup key and the pending cursor view
            //this stable slice. The duplicate-key drop below leaves the slice as dead arena bytes — a rare
            //corrupted or colliding-stream path, harmless because the block is still owned and returned on
            //dispose. The append precedes the Contains check because the check needs a ContentAddress, which
            //needs the materialized bytes.
            ReadOnlyMemory<byte> item = Items.Append(Cells.SumAt(i));
            var key = new ContentAddress(item);
            if(DecodedKeys.Contains(key))
            {
                //A pure cell whose sum is already decoded can only arise from a corrupted or colliding
                //stream; stalling on it is sound, un-decoding is not. It is never modified again, so dropping
                //it here keeps the worklist draining without a loop.
                continue;
            }

            Decoded.Add(item);
            DecodedKeys.Add(key);

            ulong checksum = ReconciliationChecksum.Compute(Contract.ChecksumKeyLow, Contract.ChecksumKeyHigh, item.Span);
            ReconciliationChecksum.Write(checksum, itemChecksum);

            //Peel the item out of every cell its walk visits below AbsorbedCount, including the cell it was
            //read from, which becomes neutral; each touched cell may now be pure, so push it back on.
            ReconciliationWalkPosition position = ReconciliationIndexWalk.Start(item.Span);
            while(position.Index < AbsorbedCount)
            {
                int cellIndex = (int)position.Index;
                ReconciliationXor.Fold(Cells.SumAt(cellIndex), item.Span);
                ReconciliationXor.Fold(Cells.ChecksumAt(cellIndex), itemChecksum);
                Worklist.Enqueue(cellIndex);

                position = ReconciliationIndexWalk.Next(position);
            }

            //Seed a cursor at the first walk index at or above AbsorbedCount for the item's future cells. The
            //immediate peel above handled every visited index below AbsorbedCount, so the two partitions are
            //disjoint and the item's contribution lands on each cell exactly once.
            var cursor = new DecodedCursor(item, checksum, position);
            PendingDecoded.Enqueue(cursor, position.Index);
        }
    }


    private bool IsCellNeutral(int index)
    {
        return ReconciliationXor.IsNeutral(Cells.SumAt(index)) && ReconciliationXor.IsNeutral(Cells.ChecksumAt(index));
    }


    private bool IsCellPure(int index)
    {
        //Neutrality is tested first; purity is only evaluated on non-neutral cells, which pins the behavior
        //for the all-zero item whose own cell has an all-zero sum but a non-zero checksum.
        if(IsCellNeutral(index))
        {
            return false;
        }

        //Reaching the checksum comparison is exactly one masquerade opportunity: a mixed cell can pass it with
        //probability 2^(−8·ChecksumWidth). The class is single-threaded, so a plain increment is the count.
        PurityCheckCount++;

        Span<byte> expected = stackalloc byte[Contract.ChecksumWidth];
        ulong value = ReconciliationChecksum.Compute(Contract.ChecksumKeyLow, Contract.ChecksumKeyHigh, Cells.SumAt(index));
        ReconciliationChecksum.Write(value, expected);

        return expected.SequenceEqual(Cells.ChecksumAt(index));
    }


    //A non-compared internal priority-queue carrier reused by value across folds; the item bytes and the
    //checksum are fixed for the decoded item's lifetime, only the next walk index advances. The checksum is
    //carried as a value and rewritten per fold (same as the encoder), so no second checksum region is arena-ed.
    [SuppressMessage("Usage", "CA1815", Justification = "A non-compared internal priority-queue carrier; cursors are never compared, only enqueued and dequeued.")]
    private struct DecodedCursor(ReadOnlyMemory<byte> itemBytes, ulong checksum, ReconciliationWalkPosition position)
    {
        public ReadOnlyMemory<byte> ItemBytes { get; } = itemBytes;


        public ulong Checksum { get; } = checksum;


        public ReconciliationWalkPosition Position { get; set; } = position;
    }
}
