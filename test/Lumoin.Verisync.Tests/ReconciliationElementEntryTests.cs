using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Equality and hash-code coverage for <see cref="ReconciliationElementEntry{TElement}"/>: the reference-identity
/// fast path in <c>Equals</c>, the null-other rejection, and that <c>GetHashCode</c> incorporates both the item
/// bytes and the element value rather than either alone.
/// </summary>
[TestClass]
internal sealed class ReconciliationElementEntryTests
{
    /// <summary>Pins that an entry equals the same instance via the reference-identity fast path.</summary>
    [TestMethod]
    public void EqualsIsReflexiveForTheSameInstance()
    {
        byte[] itemBytes = [9, 8, 7];
        ReconciliationElementEntry<int> entry = new(itemBytes, 5);

        Assert.IsTrue(entry.Equals(entry));
    }


    /// <summary>Pins that GetHashCode incorporates the element: two entries sharing an item but differing in element must not collide.</summary>
    [TestMethod]
    public void HashCodeReflectsTheElement()
    {
        byte[] item = [1, 2, 3, 4];
        ReconciliationElementEntry<int> first = new(item, 1);
        ReconciliationElementEntry<int> second = new(item, 2);

        Assert.AreNotEqual(first.GetHashCode(), second.GetHashCode());
    }


    /// <summary>Pins that GetHashCode incorporates the item bytes: two entries sharing an element but differing in item must not collide.</summary>
    [TestMethod]
    public void HashCodeReflectsTheItemBytes()
    {
        byte[] firstItem = [1, 2, 3, 4];
        byte[] secondItem = [9, 9, 9, 9];
        ReconciliationElementEntry<int> first = new(firstItem, 7);
        ReconciliationElementEntry<int> second = new(secondItem, 7);

        Assert.AreNotEqual(first.GetHashCode(), second.GetHashCode());
    }


    /// <summary>
    /// Returns a null-typed reference for the entry type, kept behind a static helper so the null literal used in
    /// <see cref="EqualsRejectsNullOther"/> is not a compile-time-known operand (CA1508).
    /// </summary>
    private static ReconciliationElementEntry<int>? NullEntry() => null;


    /// <summary>Pins that <see cref="ReconciliationElementEntry{TElement}.Equals(ReconciliationElementEntry{TElement}?)"/> reports a null other as unequal.</summary>
    [TestMethod]
    public void EqualsRejectsNullOther()
    {
        byte[] itemBytes = [1, 2, 3];
        ReconciliationElementEntry<int> entry = new(itemBytes, 42);

        Assert.IsFalse(entry.Equals(NullEntry()));
    }
}
