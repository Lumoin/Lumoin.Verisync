using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LogStateTests
{
    [TestMethod]
    public void ActiveStateCarriesValue()
    {
        ActiveLogState<string> state = new("document");

        Assert.AreEqual("document", state.Value);
    }


    [TestMethod]
    public void DeactivatedStateCarriesValue()
    {
        DeactivatedLogState<string> state = new("final");

        Assert.AreEqual("final", state.Value);
    }


    [TestMethod]
    public void VariantsAreDistinctTypes()
    {
        LogState<string> empty = new EmptyLogState<string>();
        LogState<string> active = new ActiveLogState<string>("v");
        LogState<string> deactivated = new DeactivatedLogState<string>("v");

        Assert.IsInstanceOfType<EmptyLogState<string>>(empty);
        Assert.IsInstanceOfType<ActiveLogState<string>>(active);
        Assert.IsInstanceOfType<DeactivatedLogState<string>>(deactivated);
    }


    [TestMethod]
    public void RecordEqualityHoldsByValue()
    {
        Assert.AreEqual(new ActiveLogState<string>("v"), new ActiveLogState<string>("v"));
        Assert.AreNotEqual(new ActiveLogState<string>("v"), new ActiveLogState<string>("w"));
    }


    [TestMethod]
    public void PatternMatchesOnVariant()
    {
        LogState<string> state = new ActiveLogState<string>("current");

        string description = state switch
        {
            EmptyLogState<string> => "empty",
            ActiveLogState<string> activeState => activeState.Value,
            DeactivatedLogState<string> => "deactivated",
            _ => "unknown"
        };

        Assert.AreEqual("current", description);
    }
}
