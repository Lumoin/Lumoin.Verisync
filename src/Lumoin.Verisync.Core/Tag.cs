using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A general-purpose type-keyed metadata container for tagging data with out-of-band information.
/// </summary>
/// <param name="Data">The metadata associated with the tag.</param>
/// <remarks>
/// <para>
/// <strong>Purpose</strong>
/// </para>
/// <para>
/// The Tag provides metadata to assist in managing otherwise opaque data blocks.
/// It is not tightly bound to the data itself, but rather describes characteristics
/// such as identifiers, payload kinds, or content types. Despite the provided
/// metadata, all inputs should be validated.
/// </para>
/// <para>
/// <strong>Type-Keyed Design</strong>
/// </para>
/// <para>
/// Unlike string-keyed dictionaries, this uses <see cref="Type"/> as the key,
/// providing compile-time safety and avoiding magic strings. Values are retrieved
/// using <see cref="Get{T}"/> which infers the key from the generic type parameter.
/// </para>
/// <para>
/// <strong>Immutability</strong>
/// </para>
/// <para>
/// Tags are immutable once created. Use <see cref="Create"/> to construct new tags
/// and <see cref="With{T}"/> or <see cref="With"/> to derive new tags from existing ones.
/// All methods return a new <see cref="Tag"/> backed by a
/// <see cref="FrozenDictionary{TKey, TValue}"/> optimised for read-heavy access.
/// </para>
/// <para>
/// <strong>Usage Examples</strong>
/// </para>
/// <code>
/// //Create a tag carrying a payload kind.
/// var tag = Tag.Create((typeof(VerisyncKind), VerisyncKind.ReplicaId));
///
/// //Derive a new tag that adds or replaces a single entry.
/// Tag tagged = tag.With(VerisyncKind.OperationId);
///
/// //Retrieve a value.
/// VerisyncKind kind = tagged.Get&lt;VerisyncKind&gt;();
/// </code>
/// </remarks>
/// <seealso cref="VerisyncTags"/>
[DebuggerDisplay("{DebuggerView,nq}")]
public record Tag(IReadOnlyDictionary<Type, object> Data)
{
    /// <summary>
    /// An empty tag with no metadata.
    /// </summary>
    public static Tag Empty { get; } = new(FrozenDictionary<Type, object>.Empty);


    /// <summary>
    /// Creates a new tag from the specified key-value pairs.
    /// </summary>
    /// <param name="items">The metadata items as type-value tuples.</param>
    /// <returns>A new tag containing the specified metadata, or <see cref="Empty"/> when no items are supplied.</returns>
    /// <remarks>
    /// This factory method creates an immutable <see cref="FrozenDictionary{TKey, TValue}"/>
    /// internally, optimised for read-heavy access patterns typical of tag lookups.
    /// </remarks>
    /// <example>
    /// <code>
    /// var tag = Tag.Create((typeof(VerisyncKind), VerisyncKind.ReplicaId));
    /// </code>
    /// </example>
    public static Tag Create(params ReadOnlySpan<(Type Key, object Value)> items)
    {
        if(items.IsEmpty)
        {
            return Empty;
        }

        var dict = new Dictionary<Type, object>(items.Length);
        foreach((Type key, object value) in items)
        {
            dict[key] = value;
        }

        return new Tag(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns a new tag that contains all entries from this tag plus the provided
    /// items, with the provided items taking precedence on key conflicts.
    /// </summary>
    /// <param name="items">The metadata items to add or replace.</param>
    /// <returns>A new <see cref="Tag"/> with the merged entries, or this tag when no items are supplied.</returns>
    /// <remarks>
    /// Existing entries whose keys are not present in <paramref name="items"/> are
    /// preserved unchanged. This tag is not modified.
    /// </remarks>
    public Tag With(params ReadOnlySpan<(Type Key, object Value)> items)
    {
        if(items.IsEmpty)
        {
            return this;
        }

        var dict = new Dictionary<Type, object>(Data.Count + items.Length);
        foreach(KeyValuePair<Type, object> existing in Data)
        {
            dict[existing.Key] = existing.Value;
        }

        foreach((Type key, object value) in items)
        {
            dict[key] = value;
        }

        return new Tag(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns a new tag that contains all entries from this tag plus one additional
    /// entry, inferred from the type of <paramref name="value"/>.
    /// </summary>
    /// <typeparam name="T">The type used as the dictionary key.</typeparam>
    /// <param name="value">The value to add or replace.</param>
    /// <returns>A new <see cref="Tag"/> with the entry added or replaced.</returns>
    /// <remarks>
    /// This is the preferred overload for single-entry updates since it avoids
    /// spelling out the <see cref="Type"/> explicitly.
    /// </remarks>
    public Tag With<T>(T value) where T : notnull
    {
        var dict = new Dictionary<Type, object>(Data.Count + 1);
        foreach(KeyValuePair<Type, object> existing in Data)
        {
            dict[existing.Key] = existing.Value;
        }

        dict[typeof(T)] = value;

        return new Tag(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns a new tag that contains all entries from this tag except the entry
    /// keyed by <typeparamref name="T"/>. If no such entry exists the original tag
    /// is returned unchanged; if the removed entry was the last one, <see cref="Empty"/>
    /// is returned.
    /// </summary>
    /// <typeparam name="T">The type key of the entry to remove.</typeparam>
    /// <returns>
    /// A new <see cref="Tag"/> without the entry for <typeparamref name="T"/>, this
    /// tag if no such entry was present, or <see cref="Empty"/> if the last entry was removed.
    /// </returns>
    public Tag Without<T>()
    {
        if(!Data.ContainsKey(typeof(T)))
        {
            return this;
        }

        var dict = new Dictionary<Type, object>(Data.Count - 1);
        foreach(KeyValuePair<Type, object> existing in Data)
        {
            if(existing.Key != typeof(T))
            {
                dict[existing.Key] = existing.Value;
            }
        }

        return dict.Count == 0
            ? Empty
            : new Tag(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Retrieves a value from the tag, inferring the key from the generic type.
    /// </summary>
    /// <typeparam name="T">The type of the value to retrieve, also used as the key.</typeparam>
    /// <returns>The value associated with the type key, cast to the specified type.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the key is not found in the tag.</exception>
    /// <exception cref="InvalidCastException">Thrown if the value cannot be cast to the specified type.</exception>
    public T Get<T>()
    {
        Type key = typeof(T);
        if(!Data.TryGetValue(key, out object? value))
        {
            throw new KeyNotFoundException($"Key '{key}' was not found in the tag's Data.");
        }

        if(value is not T typedValue)
        {
            throw new InvalidCastException($"Value for key '{key}' is not of type '{typeof(T)}'.");
        }

        return typedValue;
    }


    /// <summary>
    /// Attempts to retrieve a value from the tag.
    /// </summary>
    /// <typeparam name="T">The type of the value to retrieve, also used as the key.</typeparam>
    /// <param name="value">When successful, contains the retrieved value.</param>
    /// <returns><see langword="true"/> if the value was found and cast successfully; otherwise <see langword="false"/>.</returns>
    public bool TryGet<T>([MaybeNullWhen(false)] out T value)
    {
        Type key = typeof(T);
        if(Data.TryGetValue(key, out object? obj) && obj is T typedValue)
        {
            value = typedValue;

            return true;
        }

        value = default;

        return false;
    }


    /// <summary>
    /// Gets the value associated with the specified key in the tag data.
    /// </summary>
    /// <param name="key">The key of the value to get.</param>
    /// <returns>The value associated with the specified key.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the key is not found.</exception>
    [SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers", Justification = "Type-keyed lookup is the entire point of this container.")]
    public object this[Type key] => Data[key];


    /// <inheritdoc />
    public override string ToString() => TagString;


    private string DebuggerView
    {
        get
        {
            try
            {
                return TagString;
            }
            catch
            {
                return "Tag: (error)";
            }
        }
    }


    private string TagString => Data.Count == 0
        ? "Tag: (empty)"
        : $"Tag: [{string.Join(", ", Data.Select(kvp => $"{kvp.Key.Name}={kvp.Value}"))}]";
}
