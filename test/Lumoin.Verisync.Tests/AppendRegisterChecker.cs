
namespace Lumoin.Verisync.Tests;

/// <summary>
/// Linearizability checking for histories of idempotent append operations against a consensus register.
/// Because the register value is an append log, the final value is a complete witness of the order in
/// which operations took effect — no permutation search is needed; the checks verify that witness is
/// consistent with every operation's recorded effects and real-time interval.
/// </summary>
internal static class AppendRegisterChecker
{
    /// <summary>
    /// Asserts that <paramref name="history"/> linearizes to the order witnessed by
    /// <paramref name="finalValue"/>: every effect applied exactly once, every observed and written value
    /// on the chosen chain, and the witnessed order consistent with real-time precedence.
    /// </summary>
    /// <param name="history">The completed operations.</param>
    /// <param name="finalValue">The register value read after all operations completed.</param>
    public static void AssertLinearizable(IReadOnlyList<RegisterOperation> history, string finalValue)
    {
        foreach(RegisterOperation operation in history)
        {
            int occurrences = finalValue.Count(c => c == operation.Label);
            Assert.AreEqual(1, occurrences, $"Label '{operation.Label}' appears {occurrences} time(s) in final value '{finalValue}': an effect was lost or duplicated.");

            //Every chosen state is a prefix of every later chosen state in an append register, so a
            //recovered or committed value off the final chain means the register forked.
            Assert.IsTrue(finalValue.StartsWith(operation.Observed, StringComparison.Ordinal), $"Operation '{operation.Label}' observed '{operation.Observed}', which is not on the chosen chain '{finalValue}'.");
            Assert.IsTrue(finalValue.StartsWith(operation.Written, StringComparison.Ordinal), $"Operation '{operation.Label}' wrote '{operation.Written}', which is not on the chosen chain '{finalValue}'.");
        }

        foreach(RegisterOperation first in history)
        {
            foreach(RegisterOperation second in history)
            {
                if(first.Completed < second.Invoked)
                {
                    int firstIndex = finalValue.IndexOf(first.Label, StringComparison.Ordinal);
                    int secondIndex = finalValue.IndexOf(second.Label, StringComparison.Ordinal);
                    Assert.IsLessThan(secondIndex, firstIndex, $"Operation '{first.Label}' completed at {first.Completed}, before '{second.Label}' was invoked at {second.Invoked}, yet takes effect after it in '{finalValue}': real-time order violated.");
                }
            }
        }
    }
}
