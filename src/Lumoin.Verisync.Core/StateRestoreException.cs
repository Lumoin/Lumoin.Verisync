using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Thrown when a restore refuses durable state, naming the rule it refused on.
/// </summary>
/// <remarks>
/// <para>
/// It derives from <see cref="ArgumentException"/> because the claim is unchanged: the state handed in is one
/// no host of that protocol can hold, which is a statement about the argument. That keeps
/// <see cref="ArgumentException.ParamName"/>, which names which argument was refused and is a different axis
/// from <see cref="Refusal"/>, which names which rule refused it — the chain check is raised against a record
/// by one entry point and against a decoded snapshot by another, and the rule is the same both times.
/// </para>
/// <para>
/// The message stays exact and stays worth reading, because an operator reads it. What
/// <see cref="Refusal"/> adds is that a consumer no longer has to: a regression row or a recovery path
/// switches on the rule, and rewording the sentence beside it breaks nothing.
/// </para>
/// </remarks>
public sealed class StateRestoreException: ArgumentException
{
    /// <summary>Initializes an exception naming no rule.</summary>
    public StateRestoreException()
    {
    }


    /// <summary>Initializes an exception naming no rule, with <paramref name="message"/>.</summary>
    /// <param name="message">The message that describes the refusal.</param>
    public StateRestoreException(string? message): base(message)
    {
    }


    /// <summary>Initializes an exception naming no rule, with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    /// <param name="message">The message that describes the refusal.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public StateRestoreException(string? message, Exception? innerException): base(message, innerException)
    {
    }


    /// <summary>Initializes an exception refusing <paramref name="paramName"/> on <paramref name="refusal"/>.</summary>
    /// <param name="refusal">The rule the restore refused on.</param>
    /// <param name="message">The message that describes the refusal.</param>
    /// <param name="paramName">The parameter that carried the refused state.</param>
    public StateRestoreException(StateRestoreRefusal refusal, string? message, string? paramName): base(message, paramName)
    {
        Refusal = refusal;
    }


    /// <summary>The rule the restore refused on.</summary>
    public StateRestoreRefusal Refusal { get; }
}
