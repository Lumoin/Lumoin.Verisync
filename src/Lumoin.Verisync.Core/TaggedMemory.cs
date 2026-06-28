using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Base;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Abstract base for byte-bearing tagged owned memory. Concrete subclasses wrap protocol-relevant
/// byte payloads with type-level identity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ownership</strong>
/// </para>
/// <para>
/// Ownership of the supplied <see cref="IMemoryOwner{T}"/> transfers to the instance. The bytes are
/// cleared on disposal regardless of pool policy (defence in depth) before the owner is disposed.
/// </para>
/// <para>
/// <strong>Telemetry</strong>
/// </para>
/// <para>
/// Construction records <see cref="VerisyncMetrics.MemoryAllocatedBytes"/> unconditionally and starts
/// a lifetime <see cref="Activity"/> on <see cref="VerisyncActivitySource.Instance"/>. When no listener
/// is attached the activity is <see langword="null"/> and the trace path is zero-cost. Disposal records
/// <see cref="VerisyncMetrics.MemoryLifetimeMs"/> in every case — with the measured duration when an
/// activity was started, otherwise with <c>0</c> — so metric counts always match construction counts
/// regardless of trace-listener state.
/// </para>
/// <para>
/// <strong>Equality</strong>
/// </para>
/// <para>
/// Two instances are equal when their byte content is equal. Tag content does not participate in equality.
/// </para>
/// </remarks>
/// <seealso cref="Tag"/>
/// <seealso cref="VerisyncTags"/>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public abstract class TaggedMemory: IDisposable, IEquatable<TaggedMemory>
{
    private bool disposed;
    private readonly VerisyncKind kind;

    private Activity? Lifetime { get; }
    private bool HasKind { get; }

    /// <summary>
    /// The owned memory holding the tagged bytes.
    /// </summary>
    protected IMemoryOwner<byte> MemoryOwner { get; }

    /// <summary>
    /// Metadata describing the byte payload.
    /// </summary>
    public Tag Tag { get; }


    /// <summary>
    /// Initializes a new instance of <see cref="TaggedMemory"/>.
    /// </summary>
    /// <param name="memoryOwner">The memory owner holding the bytes. Ownership transfers to this instance.</param>
    /// <param name="tag">Metadata describing the byte payload.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="memoryOwner"/> or <paramref name="tag"/> is <see langword="null"/>.</exception>
    protected TaggedMemory(IMemoryOwner<byte> memoryOwner, Tag tag)
    {
        ArgumentNullException.ThrowIfNull(memoryOwner);
        ArgumentNullException.ThrowIfNull(tag);

        MemoryOwner = memoryOwner;
        Tag = tag;
        HasKind = tag.TryGet(out kind);

        int length = memoryOwner.Memory.Length;
        if(HasKind)
        {
            VerisyncMetrics.MemoryAllocatedBytes.Record(length, new KeyValuePair<string, object?>(VerisyncTelemetry.TagKind, kind));
        }
        else
        {
            VerisyncMetrics.MemoryAllocatedBytes.Record(length);
        }

        Lifetime = VerisyncActivitySource.Instance.StartActivity(VerisyncTelemetry.ActivityNameMemoryLifetime);
        if(Lifetime is not null)
        {
            Lifetime.SetTag(VerisyncTelemetry.TagBufferSize, length);
            if(HasKind)
            {
                Lifetime.SetTag(VerisyncTelemetry.TagKind, kind);
            }
        }
    }


    /// <summary>
    /// Exposes the bytes as a read-only span.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if this instance has been disposed.</exception>
    public ReadOnlySpan<byte> AsReadOnlySpan()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return MemoryOwner.Memory.Span;
    }


    /// <summary>
    /// Exposes the bytes as read-only memory.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if this instance has been disposed.</exception>
    public ReadOnlyMemory<byte> AsReadOnlyMemory()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return MemoryOwner.Memory;
    }


    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }


    /// <summary>
    /// Clears the bytes, disposes the memory owner, and records the lifetime metric.
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true"/> if called from <see cref="Dispose()"/>; <see langword="false"/> if called from a finalizer.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if(disposed)
        {
            return;
        }

        if(disposing)
        {
            //Clear regardless of pool policy: an unpooled or non-clearing owner would otherwise leave bytes behind.
            MemoryOwner.Memory.Span.Clear();
            MemoryOwner.Dispose();

            double durationMs = 0d;
            if(Lifetime is not null)
            {
                Lifetime.Stop();
                durationMs = Lifetime.Duration.TotalMilliseconds;
                Lifetime.SetTag(VerisyncTelemetry.ActivityLifetimeMs, durationMs);
                Lifetime.Dispose();
            }

            if(HasKind)
            {
                VerisyncMetrics.MemoryLifetimeMs.Record(durationMs, new KeyValuePair<string, object?>(VerisyncTelemetry.TagKind, kind));
            }
            else
            {
                VerisyncMetrics.MemoryLifetimeMs.Record(durationMs);
            }
        }

        disposed = true;
    }


    /// <inheritdoc/>
    /// <remarks>
    /// Equality is over live byte content and therefore fails closed once disposed: comparing a disposed
    /// instance throws <see cref="ObjectDisposedException"/> rather than reading recycled bytes or silently
    /// succeeding. Equality of disposed sensitive memory is a use-after-clear bug in the caller, so it is
    /// surfaced rather than masked — the same disposal guard as <see cref="AsReadOnlySpan"/> applies.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown if this instance or <paramref name="other"/> has been disposed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool Equals([NotNullWhen(true)] TaggedMemory? other)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(other is null)
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(other.disposed, other);

        return MemoryOwner.Memory.Span.SequenceEqual(other.MemoryOwner.Memory.Span);
    }


    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown if this instance or <paramref name="obj"/> (when a <see cref="TaggedMemory"/>) has been disposed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is TaggedMemory other && Equals(other);


    /// <inheritdoc/>
    /// <remarks>
    /// Fails closed once disposed for the same reason as <see cref="Equals(TaggedMemory)"/>: the hash is over
    /// live byte content, so a disposed instance throws <see cref="ObjectDisposedException"/> rather than
    /// hashing recycled bytes.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown if this instance has been disposed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override int GetHashCode()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var hash = new HashCode();
        hash.AddBytes(MemoryOwner.Memory.Span);

        return hash.ToHashCode();
    }


    /// <summary>Determines whether two instances contain identical bytes.</summary>
    /// <remarks>Throws <see cref="ObjectDisposedException"/> if either non-null operand has been disposed; see <see cref="Equals(TaggedMemory)"/>.</remarks>
    /// <exception cref="ObjectDisposedException">Thrown if either non-null operand has been disposed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool operator ==(TaggedMemory? left, TaggedMemory? right) => left is null ? right is null : left.Equals(right);


    /// <summary>Determines whether two instances differ in their bytes.</summary>
    /// <remarks>Throws <see cref="ObjectDisposedException"/> if either non-null operand has been disposed; see <see cref="Equals(TaggedMemory)"/>.</remarks>
    /// <exception cref="ObjectDisposedException">Thrown if either non-null operand has been disposed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool operator !=(TaggedMemory? left, TaggedMemory? right) => !(left == right);


    private string DebuggerDisplay => disposed
        ? "TaggedMemory (disposed)"
        : $"TaggedMemory[{MemoryOwner.Memory.Length} bytes] ({Tag})";
}
