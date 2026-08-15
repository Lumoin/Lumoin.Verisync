using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Thrown when a consensus operation is refused, naming the rule it was refused on.
/// </summary>
/// <remarks>
/// <para>
/// It derives from <see cref="InvalidOperationException"/> because the claim is unchanged: the operation is
/// one this object cannot perform in the state it is in. What <see cref="Refusal"/> adds is that a caller
/// acting on the refusal no longer reads the sentence to find out which rule fired.
/// </para>
/// <para>
/// It is the operational counterpart of <see cref="StateRestoreException"/>, which names the rules a restore
/// refuses durable state on.
/// </para>
/// </remarks>
public sealed class ConsensusRefusedException: InvalidOperationException
{
    /// <summary>Initializes an exception naming no rule.</summary>
    public ConsensusRefusedException()
    {
    }


    /// <summary>Initializes an exception naming no rule, with <paramref name="message"/>.</summary>
    /// <param name="message">The message that describes the refusal.</param>
    public ConsensusRefusedException(string? message): base(message)
    {
    }


    /// <summary>Initializes an exception naming no rule, with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    /// <param name="message">The message that describes the refusal.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ConsensusRefusedException(string? message, Exception? innerException): base(message, innerException)
    {
    }


    /// <summary>Initializes an exception refused on <paramref name="refusal"/>.</summary>
    /// <param name="refusal">The rule the operation was refused on.</param>
    /// <param name="message">The message that describes the refusal.</param>
    public ConsensusRefusedException(ConsensusRefusal refusal, string? message): base(message)
    {
        Refusal = refusal;
    }


    /// <summary>The rule the operation was refused on.</summary>
    public ConsensusRefusal Refusal { get; }
}
