using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LogEntryClassificationTests
{
    [TestMethod]
    public void BuiltInClassificationsAreDistinct()
    {
        Assert.AreNotEqual(LogEntryClassification.Genesis, LogEntryClassification.Update);
        Assert.AreNotEqual(LogEntryClassification.Update, LogEntryClassification.Deactivate);
        Assert.AreNotEqual(LogEntryClassification.Deactivate, LogEntryClassification.Heartbeat);
    }


    [TestMethod]
    public void EqualityIsByValue()
    {
        Assert.AreEqual(LogEntryClassification.Update, new LogEntryClassification("update"));
        Assert.IsTrue(LogEntryClassification.Update == new LogEntryClassification("update"));
    }


    [TestMethod]
    public void CustomClassificationIsSupported()
    {
        LogEntryClassification custom = new("rekey");

        Assert.AreEqual(new LogEntryClassification("rekey"), custom);
        Assert.AreNotEqual(LogEntryClassification.Update, custom);
    }


    [TestMethod]
    public void ConstructorRejectsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new LogEntryClassification(null!));
    }


    [TestMethod]
    public void ToStringReturnsValue()
    {
        Assert.AreEqual("genesis", LogEntryClassification.Genesis.ToString());
    }


    [TestMethod]
    public void DefaultClassificationToStringIsEmpty()
    {
        LogEntryClassification uninitialized = default;

        Assert.AreEqual(string.Empty, uninitialized.ToString());
    }
}
