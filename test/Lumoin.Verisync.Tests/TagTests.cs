using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class TagTests
{
    [TestMethod]
    public void EmptyHasNoEntries()
    {
        Assert.HasCount(0, Tag.Empty.Data);
    }


    [TestMethod]
    public void EmptyIsSingleton()
    {
        Assert.AreSame(Tag.Empty, Tag.Empty);
    }


    [TestMethod]
    public void CreateWithNoItemsReturnsEmpty()
    {
        Assert.AreSame(Tag.Empty, Tag.Create());
    }


    [TestMethod]
    public void CreateStoresItems()
    {
        Tag tag = Tag.Create((typeof(VerisyncKind), VerisyncKind.ReplicaId), (typeof(string), "payload"));

        Assert.HasCount(2, tag.Data);
        Assert.AreEqual(VerisyncKind.ReplicaId, tag.Get<VerisyncKind>());
        Assert.AreEqual("payload", tag.Get<string>());
    }


    [TestMethod]
    public void CreateLastWinsOnDuplicateKey()
    {
        Tag tag = Tag.Create((typeof(string), "first"), (typeof(string), "second"));

        Assert.AreEqual("second", tag.Get<string>());
    }


    [TestMethod]
    public void WithSingleValueInfersKey()
    {
        Tag tag = Tag.Empty.With("value");

        Assert.AreEqual("value", tag.Get<string>());
    }


    [TestMethod]
    public void WithSingleValueAddsWithoutMutating()
    {
        Tag original = Tag.Empty;
        Tag derived = original.With("value");

        Assert.HasCount(0, original.Data);
        Assert.HasCount(1, derived.Data);
    }


    [TestMethod]
    public void WithSingleValueReplacesExisting()
    {
        Tag tag = Tag.Empty.With("first").With("second");

        Assert.HasCount(1, tag.Data);
        Assert.AreEqual("second", tag.Get<string>());
    }


    [TestMethod]
    public void WithMultipleItemsAddsAll()
    {
        Tag tag = Tag.Empty.With((typeof(string), "text"), (typeof(VerisyncKind), VerisyncKind.OperationId));

        Assert.HasCount(2, tag.Data);
        Assert.AreEqual("text", tag.Get<string>());
        Assert.AreEqual(VerisyncKind.OperationId, tag.Get<VerisyncKind>());
    }


    [TestMethod]
    public void WithMultipleItemsEmptyReturnsSameInstance()
    {
        Tag tag = Tag.Empty.With("value");

        Assert.AreSame(tag, tag.With());
    }


    [TestMethod]
    public void WithoutAbsentKeyReturnsSameInstance()
    {
        Tag tag = Tag.Empty.With("value");

        Assert.AreSame(tag, tag.Without<int>());
    }


    [TestMethod]
    public void WithoutPresentKeyRemoves()
    {
        Tag tag = Tag.Create((typeof(string), "text"), (typeof(VerisyncKind), VerisyncKind.ReplicaId));
        Tag stripped = tag.Without<string>();

        Assert.HasCount(1, stripped.Data);
        Assert.IsFalse(stripped.TryGet<string>(out _));
        Assert.AreEqual(VerisyncKind.ReplicaId, stripped.Get<VerisyncKind>());
    }


    [TestMethod]
    public void WithoutLastEntryReturnsEmptySingleton()
    {
        Tag tag = Tag.Empty.With("value");

        Assert.AreSame(Tag.Empty, tag.Without<string>());
    }


    [TestMethod]
    public void GetMissingKeyThrows()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(() => Tag.Empty.Get<string>());
    }


    [TestMethod]
    public void TryGetReturnsFalseForMissingKey()
    {
        Assert.IsFalse(Tag.Empty.TryGet<string>(out _));
    }


    [TestMethod]
    public void TryGetReturnsTrueForPresentKey()
    {
        Tag tag = Tag.Empty.With("value");

        Assert.IsTrue(tag.TryGet<string>(out string? value));
        Assert.AreEqual("value", value);
    }


    [TestMethod]
    public void IndexerReturnsValue()
    {
        Tag tag = Tag.Empty.With("value");

        Assert.AreEqual("value", tag[typeof(string)]);
    }


    [TestMethod]
    public void IndexerThrowsForMissingKey()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(() => _ = Tag.Empty[typeof(string)]);
    }


    [TestMethod]
    public void OriginalTagIsNotMutatedByWith()
    {
        Tag original = Tag.Create((typeof(string), "original"));
        _ = original.With((typeof(VerisyncKind), VerisyncKind.ReplicaId));

        Assert.HasCount(1, original.Data);
        Assert.IsFalse(original.TryGet<VerisyncKind>(out _));
    }


    [TestMethod]
    public void OriginalTagIsNotMutatedByWithout()
    {
        Tag original = Tag.Create((typeof(string), "original"));
        _ = original.Without<string>();

        Assert.HasCount(1, original.Data);
        Assert.AreEqual("original", original.Get<string>());
    }
}
