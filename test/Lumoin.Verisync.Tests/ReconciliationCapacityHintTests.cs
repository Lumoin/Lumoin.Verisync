using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>Verifies capacity hint validation and immutable value semantics.</summary>
[TestClass]
internal sealed class ReconciliationCapacityHintTests
{
    /// <summary>A negative item hint is rejected with the item parameter's name.</summary>
    [TestMethod]
    public void NegativeItemsThrowsNamingItems()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ReconciliationCapacityHint(items: -1, cells: 0));

        Assert.AreEqual("items", thrown.ParamName);
    }


    /// <summary>A negative cell hint is rejected with the cell parameter's name.</summary>
    [TestMethod]
    public void NegativeCellsThrowsNamingCells()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ReconciliationCapacityHint(items: 0, cells: -1));

        Assert.AreEqual("cells", thrown.ParamName);
    }


    /// <summary>The empty hint lets both stores grow from their minimum capacities.</summary>
    [TestMethod]
    public void NoneHasZeroItemsAndCells()
    {
        Assert.AreEqual(0, ReconciliationCapacityHint.None.Items);
        Assert.AreEqual(0, ReconciliationCapacityHint.None.Cells);
        Assert.AreEqual(default, ReconciliationCapacityHint.None);
    }


    /// <summary>Value equality and hashing account for both independent capacity hints.</summary>
    [TestMethod]
    public void ValueEqualityIncludesBothHints()
    {
        ReconciliationCapacityHint hint = new(items: 16, cells: 4);
        ReconciliationCapacityHint equal = new(items: 16, cells: 4);

        Assert.AreEqual(hint, equal);
        Assert.AreEqual(hint.GetHashCode(), equal.GetHashCode());
        Assert.AreNotEqual(hint, new ReconciliationCapacityHint(items: 17, cells: 4));
        Assert.AreNotEqual(hint, new ReconciliationCapacityHint(items: 16, cells: 5));
    }


    /// <summary>A with expression preserves the validated, get-only values and the empty hint.</summary>
    [TestMethod]
    public void WithPreservesBothValidatedValues()
    {
        ReconciliationCapacityHint hint = new(items: 16, cells: 4);
        ReconciliationCapacityHint copy = hint with { };

        Assert.AreEqual(hint, copy);
        Assert.AreEqual(16, copy.Items);
        Assert.AreEqual(4, copy.Cells);
        Assert.AreEqual(ReconciliationCapacityHint.None, ReconciliationCapacityHint.None with { });
    }
}
