using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one vector of the harness's verification suite found wrong.
/// </summary>
internal sealed class VectorFailures
{
    private readonly List<string> messages = [];


    /// <summary>Every violation recorded so far, in the order they were found.</summary>
    public ImmutableArray<string> Messages => [.. messages];


    /// <summary>Whether the vector found nothing wrong.</summary>
    public bool IsClean => messages.Count == 0;


    /// <summary>Records a violation unless <paramref name="condition"/> holds.</summary>
    /// <param name="condition">What the vector expected to be true.</param>
    /// <param name="message">What it means that it is not.</param>
    public void Require(bool condition, FormattableString message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if(!condition)
        {
            messages.Add(FormattableString.Invariant(message));
        }
    }
}
