using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A thread-safe memory pool that returns memory of exactly the requested size, unlike
/// <see cref="ArrayPool{T}.Shared"/> which may return larger buffers. Exact sizing is essential for
/// byte-identity types such as <see cref="ReplicaId"/>, whose equality is over their full byte content.
/// </summary>
/// <typeparam name="T">The element type, typically <see cref="byte"/>.</typeparam>
/// <remarks>
/// <para>
/// The pool keeps a separate collection of slabs per requested size; each slab is a contiguous buffer
/// divided into fixed-size segments, tracked with a bit array to prevent double returns. Slab capacity
/// follows a <see cref="SlabCapacityStrategy"/>. Rentals and returns emit the library's reserved pool
/// metrics on <see cref="VerisyncMetrics"/> and an optional rental span on <see cref="VerisyncActivitySource"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("VerisyncMemoryPool<{typeof(T).Name,nq}>: Slabs={totalSlabs}, Active={activeRentals}, Allocated={totalMemoryAllocated} bytes")]
public sealed class VerisyncMemoryPool<T>: MemoryPool<T>
{
    private Dictionary<int, List<Slab>> Slabs { get; } = new();
    private Lock LockObject { get; } = new();
    private SlabCapacityStrategy CapacityStrategy { get; }
    private bool disposed;
    private int totalSlabs;
    private long totalMemoryAllocated;
    private int activeRentals;
    private int totalSegments;

    private static Lazy<VerisyncMemoryPool<T>> SharedInstance { get; } = new(() => new VerisyncMemoryPool<T>());


    /// <summary>The default number of segments per slab when no strategy is supplied.</summary>
    public const int DefaultInitialSlabCapacity = 4;


    /// <summary>
    /// Initializes a new pool.
    /// </summary>
    /// <param name="capacityStrategy">The slab capacity strategy, or <see langword="null"/> for <see cref="DefaultCapacityStrategy"/>.</param>
    /// <param name="tracingEnabled">Whether to emit a rental span per rent; disable on hot paths.</param>
    public VerisyncMemoryPool(SlabCapacityStrategy? capacityStrategy = null, bool tracingEnabled = true)
    {
        CapacityStrategy = capacityStrategy ?? DefaultCapacityStrategy;
        TracingEnabled = tracingEnabled;
    }


    /// <summary>A shared, lazily created pool using the default strategy.</summary>
    public static new VerisyncMemoryPool<T> Shared => SharedInstance.Value;

    /// <inheritdoc/>
    public override int MaxBufferSize => int.MaxValue;

    /// <summary>Whether rental spans are emitted.</summary>
    public bool TracingEnabled { get; }


    /// <summary>
    /// The default capacity strategy: more segments for small buffers, fewer for large ones.
    /// </summary>
    /// <param name="segmentSize">The size of each segment in elements.</param>
    /// <returns>The number of segments to allocate.</returns>
    public static int DefaultCapacityStrategy(int segmentSize) => segmentSize switch
    {
        <= 64 => 32,
        <= 256 => 16,
        <= 4096 => 8,
        _ => 4
    };


    /// <summary>
    /// Rents memory of exactly <paramref name="bufferSize"/> elements.
    /// </summary>
    /// <param name="bufferSize">The exact number of elements required.</param>
    /// <returns>An owner over exactly <paramref name="bufferSize"/> elements. Dispose it to return the memory.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the pool has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="bufferSize"/> is less than or equal to zero.</exception>
    [SuppressMessage("Naming", "CA1725:Parameter names should match base declaration", Justification = "This pool returns a buffer of exactly the requested size, so the parameter is named for that contract.")]
    public override IMemoryOwner<T> Rent(int bufferSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bufferSize, 0);

        Activity? activity = TracingEnabled
            ? VerisyncActivitySource.Instance.StartActivity(VerisyncTelemetry.ActivityNamePoolRental)
            : null;
        activity?.SetTag(VerisyncTelemetry.TagBufferSize, bufferSize);

        IMemoryOwner<T> result;
        using(LockObject.EnterScope())
        {
            if(!Slabs.TryGetValue(bufferSize, out List<Slab>? slabList))
            {
                slabList = [];
                Slabs.Add(bufferSize, slabList);
            }

            Slab? available = null;
            ArraySegment<T> rented = default;
            foreach(Slab slab in slabList)
            {
                if(slab.TryRent(out rented))
                {
                    available = slab;
                    break;
                }
            }

            if(available is null)
            {
                int capacity = CapacityStrategy(bufferSize);
                available = new Slab(bufferSize, capacity);
                slabList.Add(available);

                Interlocked.Increment(ref totalSlabs);
                Interlocked.Add(ref totalMemoryAllocated, (long)bufferSize * capacity);
                Interlocked.Add(ref totalSegments, capacity);
                VerisyncMetrics.MemoryAllocatedBytes.Record((long)bufferSize * capacity);

                bool rentSuccess = available.TryRent(out rented);
                Debug.Assert(rentSuccess, "A new slab should always have an available segment.");
            }

            Interlocked.Increment(ref activeRentals);
            result = new ExactSizeMemoryOwner(rented, available, this, activity);
        }

        VerisyncMetrics.MemoryRented.Add(1);
        VerisyncMetrics.MemoryActiveRentals.Add(1);

        return result;
    }


    /// <summary>
    /// Releases every slab that has no active rentals, reclaiming its memory.
    /// </summary>
    /// <returns>The number of slabs reclaimed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the pool has been disposed.</exception>
    public int TrimExcess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        int reclaimed = 0;
        using(LockObject.EnterScope())
        {
            foreach(List<Slab> slabList in Slabs.Values)
            {
                for(int i = slabList.Count - 1; i >= 0; i--)
                {
                    Slab slab = slabList[i];
                    if(slab.IsFull)
                    {
                        int segmentCount = slab.SegmentCount;
                        int segmentSize = slab.SegmentSize;
                        slab.Dispose();
                        slabList.RemoveAt(i);

                        Interlocked.Decrement(ref totalSlabs);
                        Interlocked.Add(ref totalMemoryAllocated, -((long)segmentSize * segmentCount));
                        Interlocked.Add(ref totalSegments, -segmentCount);
                        reclaimed++;
                    }
                }
            }
        }

        return reclaimed;
    }


    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if(disposed)
        {
            return;
        }

        if(disposing)
        {
            using(LockObject.EnterScope())
            {
                foreach(List<Slab> slabList in Slabs.Values)
                {
                    foreach(Slab slab in slabList)
                    {
                        slab.Dispose();
                    }
                }

                Slabs.Clear();
                totalSlabs = 0;
                totalMemoryAllocated = 0;
                activeRentals = 0;
                totalSegments = 0;
            }
        }

        disposed = true;
    }


    private void Return(ArraySegment<T> segment, Slab slab, long rentTimestamp)
    {
        ArgumentNullException.ThrowIfNull(slab);

        using(LockObject.EnterScope())
        {
            slab.Return(segment);
            Interlocked.Decrement(ref activeRentals);
        }

        VerisyncMetrics.MemoryReturned.Add(1);
        VerisyncMetrics.MemoryActiveRentals.Add(-1);
        VerisyncMetrics.MemoryRentalDurationMs.Record(Stopwatch.GetElapsedTime(rentTimestamp).TotalMilliseconds);
    }


    private sealed class Slab: IDisposable
    {
        private bool disposed;

        private T[] Buffer { get; }
        private Stack<int> AvailableSegments { get; }
        private BitArray RentedSegments { get; }

        public Slab(int segmentSize, int segmentCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentSize, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentCount, 0);

            SegmentSize = segmentSize;
            SegmentCount = segmentCount;
            Buffer = new T[segmentSize * segmentCount];
            RentedSegments = new BitArray(segmentCount, false);
            AvailableSegments = new Stack<int>(segmentCount);
            for(int i = 0; i < segmentCount; i++)
            {
                AvailableSegments.Push(i);
            }
        }

        public int SegmentSize { get; }

        public int SegmentCount { get; }

        public bool IsFull => AvailableSegments.Count == SegmentCount;

        public bool TryRent(out ArraySegment<T> segment)
        {
            if(disposed)
            {
                segment = default;

                return false;
            }

            if(AvailableSegments.TryPop(out int segmentIndex))
            {
                RentedSegments[segmentIndex] = true;
                segment = new ArraySegment<T>(Buffer, segmentIndex * SegmentSize, SegmentSize);

                return true;
            }

            segment = default;

            return false;
        }

        public void Return(ArraySegment<T> segment)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if(segment.Array != Buffer)
            {
                throw new ArgumentException("The segment does not belong to this slab.", nameof(segment));
            }

            if(segment.Count != SegmentSize)
            {
                throw new ArgumentException("The segment size does not match the slab segment size.", nameof(segment));
            }

            int segmentIndex = segment.Offset / SegmentSize;
            if(segment.Offset % SegmentSize != 0 || segmentIndex >= SegmentCount)
            {
                throw new ArgumentException("The segment offset is invalid for this slab.", nameof(segment));
            }

            if(!RentedSegments[segmentIndex])
            {
                throw new InvalidOperationException("The segment was not rented or has already been returned.");
            }

            RentedSegments[segmentIndex] = false;
            AvailableSegments.Push(segmentIndex);
        }

        public void Dispose()
        {
            if(!disposed)
            {
                AvailableSegments.Clear();
                disposed = true;
            }
        }
    }


    private sealed class ExactSizeMemoryOwner: IMemoryOwner<T>
    {
        private readonly Slab slab;
        private bool disposed;

        private ArraySegment<T> Segment { get; }
        private VerisyncMemoryPool<T> Pool { get; }
        private Activity? Lifecycle { get; }
        private long RentTimestamp { get; }

        public ExactSizeMemoryOwner(ArraySegment<T> segment, Slab slab, VerisyncMemoryPool<T> pool, Activity? lifecycle)
        {
            if(segment.Array is null || segment.Count == 0)
            {
                throw new InvalidOperationException("Failed to rent a valid memory segment.");
            }

            ArgumentNullException.ThrowIfNull(slab);
            ArgumentNullException.ThrowIfNull(pool);

            Segment = segment;
            this.slab = slab;
            Pool = pool;
            Lifecycle = lifecycle;
            RentTimestamp = Stopwatch.GetTimestamp();
        }

        public Memory<T> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);

                return Segment;
            }
        }

        public void Dispose()
        {
            if(disposed)
            {
                return;
            }

            try
            {
                Pool.Return(Segment, slab, RentTimestamp);
                Lifecycle?.SetStatus(ActivityStatusCode.Ok);
            }
            catch(ObjectDisposedException ex)
            {
                //The pool or slab was disposed before this rental was returned (e.g. during shutdown).
                Lifecycle?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
            finally
            {
                Lifecycle?.Dispose();
                disposed = true;
            }
        }
    }
}
