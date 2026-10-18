using System;
using System.Buffers;
using System.Threading;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Produces the same coded symbols as <see cref="ReconciliationEncoder"/> over one pinned snapshot without
/// retaining its items: each produced range is folded in one pass over the snapshot's enumeration, replaying every
/// item's index walk from zero to the range's end, so retained state is the produced cells alone. The first symbol
/// therefore costs a full pass, and every later range costs another; the shape suits rare sessions over large
/// sets, where the incremental encoder's per-item cursor heap would dominate memory.
/// </summary>
/// <remarks>
/// <para>
/// Each pass costs two hashes per item, for the walk seed and checksum, and about <c>2 ln(end)</c> walk steps,
/// of which those at or above the range's start fold into cells. There are no Add, Remove, or ProduceNext
/// operations because producing even one symbol costs a full pass. Duplicate detection is the source's
/// obligation; repeated items cancel silently under XOR. A range of any width costs one pass, so a caller
/// amortizes the pass by producing its symbols in ranges; AntiEntropySession produces one range per batch, so
/// its batch size is the range width.
/// </para>
/// <para>This mutable class is single-threaded and is not safe for concurrent calls.</para>
/// </remarks>
public sealed class ReconciliationSnapshotEncoder: IDisposable
{
    /// <summary>The dense store of produced sums and checksums.</summary>
    private ReconciliationCellBuffer Cells { get; }

    /// <summary>The restartable enumeration of the pinned snapshot.</summary>
    private EnumerateReconciliationItemsDelegate Enumerate { get; }

    /// <summary>The visitor instance shared by every pass.</summary>
    private VisitReconciliationItemDelegate Visit { get; }

    /// <summary>The inclusive start of the range being folded.</summary>
    private int start;

    /// <summary>The exclusive end of the range being folded.</summary>
    private int end;

    /// <summary>Whether a pass is currently running.</summary>
    private bool inPass;

    /// <summary>Whether a failed pass prevents further production and symbol reads.</summary>
    private bool faulted;

    /// <summary>Whether the cell rentals have been released.</summary>
    private bool disposed;


    /// <summary>Initializes an encoder over a pinned snapshot without enumerating it.</summary>
    /// <param name="contract">The contract pinning item width, checksum width, and checksum key.</param>
    /// <param name="enumerate">The restartable source of every item in the pinned snapshot.</param>
    /// <param name="pool">The pool the cell store rents from. The encoder never disposes the pool.</param>
    /// <param name="cellCapacityHint">A nonnegative lower bound on the cell capacity to reserve.</param>
    /// <exception cref="ArgumentNullException">Thrown if contract, enumerate, or pool is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the hint is negative or its rounded backing exceeds the maximum array length.</exception>
    public ReconciliationSnapshotEncoder(ReconciliationContract contract, EnumerateReconciliationItemsDelegate enumerate, MemoryPool<byte> pool, int cellCapacityHint)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(enumerate);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(cellCapacityHint);

        Contract = contract;
        Enumerate = enumerate;
        Visit = VisitItem;
        Cells = new ReconciliationCellBuffer(contract.ItemWidth, contract.ChecksumWidth, pool, cellCapacityHint);
    }


    /// <summary>The contract this encoder produces against.</summary>
    public ReconciliationContract Contract { get; }

    /// <summary>The number of appended symbols, including any range whose pass faulted.</summary>
    public int ProducedCount => Cells.Count;


    /// <summary>Produces the next <paramref name="count"/> symbols in one pass over the snapshot.</summary>
    /// <param name="count">The positive number of symbols to append.</param>
    /// <param name="cancellationToken">The token checked before enumeration and observed by the source at least once per item.</param>
    /// <exception cref="ObjectDisposedException">Thrown after disposal.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if count is less than one.</exception>
    /// <exception cref="InvalidOperationException">Thrown if a pass faulted or is already running.</exception>
    /// <exception cref="OverflowException">Thrown if the resulting symbol count exceeds the integer range.</exception>
    /// <exception cref="ArgumentException">Thrown if an enumerated item's width differs from the contract.</exception>
    /// <exception cref="OperationCanceledException">Thrown on cancellation, which faults the encoder.</exception>
    /// <remarks>Any exception during the pass faults the encoder and propagates to the caller.</remarks>
    public void Produce(int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        if(faulted)
        {
            throw new InvalidOperationException("A snapshot encoder whose pass faulted cannot produce.");
        }

        if(inPass)
        {
            throw new InvalidOperationException("A snapshot encoder cannot produce while a pass is running.");
        }

        int rangeStart = ProducedCount;
        int rangeEnd = checked(rangeStart + count);
        try
        {
            for(int i = 0; i < count; i++)
            {
                Cells.Append();
            }

            start = rangeStart;
            end = rangeEnd;
            inPass = true;
            cancellationToken.ThrowIfCancellationRequested();
            Enumerate(Visit, cancellationToken);
        }
        catch
        {
            faulted = true;

            throw;
        }
        finally
        {
            inPass = false;
        }
    }


    /// <summary>Returns a snapshot of the produced symbol at the supplied index.</summary>
    /// <param name="index">The symbol index, in the range zero through ProducedCount exclusive.</param>
    /// <returns>A snapshot of the symbol at the index.</returns>
    /// <exception cref="ObjectDisposedException">Thrown after disposal.</exception>
    /// <exception cref="InvalidOperationException">Thrown if a pass faulted or is running.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the index is outside the produced range.</exception>
    public ReconciliationSymbol SymbolAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(faulted)
        {
            throw new InvalidOperationException("A snapshot encoder whose pass faulted cannot read its symbols.");
        }

        if(inPass)
        {
            throw new InvalidOperationException("A snapshot encoder cannot read its symbols while a pass is running.");
        }

        if(index < 0 || index >= ProducedCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The symbol index must be within the produced range.");
        }

        return new ReconciliationSymbol(Cells.SumAt(index), Cells.ChecksumAt(index));
    }


    /// <summary>Releases every cell rental; the call is idempotent and never disposes the injected pool.</summary>
    public void Dispose()
    {
        if(disposed)
        {
            return;
        }

        disposed = true;
        Cells.Dispose();
    }


    /// <summary>Folds one borrowed item and its checksum into the cells visited within the current range.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no pass is running.</exception>
    private void VisitItem(ReadOnlySpan<byte> item)
    {
        if(!inPass)
        {
            throw new InvalidOperationException("A snapshot encoder folds items only while its own pass is running.");
        }

        if(item.Length != Contract.ItemWidth)
        {
            throw new ArgumentException($"An enumerated item must be exactly {Contract.ItemWidth} bytes.");
        }

        Span<byte> checksumBytes = stackalloc byte[Contract.ChecksumWidth];
        ulong checksum = ReconciliationChecksum.Compute(Contract.ChecksumKeyLow, Contract.ChecksumKeyHigh, item);
        ReconciliationChecksum.Write(checksum, checksumBytes);
        ReconciliationWalkPosition position = ReconciliationIndexWalk.Start(item);
        while(position.Index < end)
        {
            if(position.Index >= start)
            {
                ReconciliationXor.Fold(Cells.SumAt((int)position.Index), item);
                ReconciliationXor.Fold(Cells.ChecksumAt((int)position.Index), checksumBytes);
            }

            position = ReconciliationIndexWalk.Next(position);
        }
    }
}
