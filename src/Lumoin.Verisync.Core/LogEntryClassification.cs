using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// An open, extensible discriminator that identifies the kind of operation carried by a
/// <see cref="LogEntry{TOperation, TProof}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LogEntryClassification"/> is a value-typed dynamic enum — a thin wrapper over a string
/// constant that lets callers define their own classifications without modifying the infrastructure.
/// Built-in constants cover the operation kinds common across authenticated logs:
/// <see cref="Genesis"/>, <see cref="Update"/>, <see cref="Deactivate"/>, and <see cref="Heartbeat"/>.
/// </para>
/// <para>
/// This is a different axis from <see cref="VerisyncKind"/>: that enum is a closed set fixed by the
/// protocol and names the kind of bytes a <see cref="TaggedMemory"/> carries, whereas this type is
/// open and names application-defined operation kinds. The two must not be conflated.
/// </para>
/// <para>
/// A class hierarchy would require the infrastructure to change each time a log defines a new entry
/// kind. A string-backed value type lets method-specific classifications be defined in the caller's
/// namespace and compared by value without casting, reflection, or infrastructure changes.
/// </para>
/// </remarks>
public readonly struct LogEntryClassification: IEquatable<LogEntryClassification>
{
    /// <summary>
    /// The genesis entry — the first entry in a log that establishes the initial state. Exactly one
    /// genesis entry exists per log, at index zero.
    /// </summary>
    public static LogEntryClassification Genesis { get; } = new("genesis");

    /// <summary>An update entry — an entry that transitions the current state forward.</summary>
    public static LogEntryClassification Update { get; } = new("update");

    /// <summary>
    /// A deactivation entry — an entry that marks the subject of the log as permanently deactivated.
    /// No further state-mutating entries are valid after a deactivation entry.
    /// </summary>
    public static LogEntryClassification Deactivate { get; } = new("deactivate");

    /// <summary>
    /// A heartbeat entry — an entry that re-witnesses the current digest to establish liveness without
    /// mutating state. The <see cref="LogEntry{TOperation, TProof}.Operation"/> of a heartbeat entry is
    /// <see langword="null"/>.
    /// </summary>
    public static LogEntryClassification Heartbeat { get; } = new("heartbeat");


    private string Value { get; }


    /// <summary>
    /// Initializes a new <see cref="LogEntryClassification"/> with the given value.
    /// </summary>
    /// <param name="value">The string value identifying the classification.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    public LogEntryClassification(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }


    /// <inheritdoc/>
    public bool Equals(LogEntryClassification other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LogEntryClassification other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value ?? string.Empty;

    /// <summary>Determines whether two classifications are equal.</summary>
    public static bool operator ==(LogEntryClassification left, LogEntryClassification right) => left.Equals(right);

    /// <summary>Determines whether two classifications are not equal.</summary>
    public static bool operator !=(LogEntryClassification left, LogEntryClassification right) => !(left == right);
}
