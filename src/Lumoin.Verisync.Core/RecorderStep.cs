using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The threshold logical clock a recorder and a proposer share: a step is four times the round plus the
/// phase, so one round of the concrete protocol spans four steps.
/// </summary>
/// <param name="Value">The step value. Must lie between zero and the value of <see cref="MaxValue"/>.</param>
/// <remarks>
/// <para>
/// The step is bounded, and the bound is not a tuning parameter. A step arrives from the network once the
/// core is wrapped in a transport, so an unbounded clock is an attacker-controlled field, and a slot that
/// survives <see cref="MaxRound"/> complete rounds is a deployment that should checkpoint or reconfigure
/// rather than keep counting. The type is backed by an <see cref="int"/> because the cap makes every
/// arithmetic operation in it unable to overflow, and the wire field is then a bounded 32-bit integer
/// validated on the way in.
/// </para>
/// <para>
/// The <see langword="default"/> value is <see cref="Zero"/>, the step of a register that was never written.
/// No request ever carries it, so it is also the value a decision step field takes when there was no
/// decision.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct RecorderStep(int Value): IComparable<RecorderStep>
{
    /// <summary>The highest round a step may name.</summary>
    public const int MaxRound = 256;

    private const int MaxStepValue = (4 * MaxRound) + 3;


    /// <summary>
    /// The step value. It is validated on construction and on a <c>with</c> expression alike.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value lies outside zero and the value of <see cref="MaxValue"/>.</exception>
    public int Value { get; init { field = Validate(value); } } = Validate(Value);


    /// <summary>The step of a register that was never written. It is round zero, and no request ever carries it.</summary>
    public static RecorderStep Zero { get; } = new(0);

    /// <summary>
    /// The protocol's first step, which is round one phase zero. It is the only step at which the reserved
    /// priority means anything.
    /// </summary>
    public static RecorderStep RoundOnePhaseZero { get; } = new(4);

    /// <summary>The highest representable step, which is <see cref="MaxRound"/> phase three.</summary>
    public static RecorderStep MaxValue { get; } = new(MaxStepValue);


    /// <summary>The round this step belongs to.</summary>
    public int Round => Value / 4;

    /// <summary>The phase within <see cref="Round"/>, from zero to three.</summary>
    public int Phase => Value % 4;

    /// <summary>Whether this is the last representable step, so that no successor exists.</summary>
    public bool IsExhausted => Value == MaxValue.Value;


    /// <summary>Builds the step for <paramref name="round"/> and <paramref name="phase"/>.</summary>
    /// <param name="round">The round. Must lie between zero and <see cref="MaxRound"/>.</param>
    /// <param name="phase">The phase. Must lie between zero and three.</param>
    /// <returns>The step.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the round or the phase is out of range.</exception>
    public static RecorderStep FromRoundAndPhase(int round, int phase)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(round);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(round, MaxRound);
        ArgumentOutOfRangeException.ThrowIfNegative(phase);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(phase, 3);

        return new RecorderStep((4 * round) + phase);
    }


    /// <summary>Returns the step after this one.</summary>
    /// <returns>The successor step.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this step <see cref="IsExhausted"/>.</exception>
    /// <remarks>
    /// A caller checks <see cref="IsExhausted"/> first and reports an exhausted step budget as its own
    /// outcome; the throw is the fail-closed backstop for a caller that did not.
    /// </remarks>
    public RecorderStep Next()
    {
        if(IsExhausted)
        {
            throw new InvalidOperationException("The step budget is spent; the last representable step has no successor.");
        }

        return new RecorderStep(Value + 1);
    }


    /// <summary>Whether this step is exactly one above <paramref name="previous"/>.</summary>
    /// <param name="previous">The step to test against.</param>
    /// <returns><see langword="true"/> when this step is the immediate successor of <paramref name="previous"/>.</returns>
    public bool IsNextAfter(RecorderStep previous) => Value == previous.Value + 1;


    /// <summary>Compares this step with <paramref name="other"/> by value.</summary>
    /// <param name="other">The step to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public int CompareTo(RecorderStep other) => Value.CompareTo(other.Value);


    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(RecorderStep left, RecorderStep right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(RecorderStep left, RecorderStep right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(RecorderStep left, RecorderStep right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(RecorderStep left, RecorderStep right) => left.CompareTo(right) >= 0;


    private static int Validate(int value)
    {
        //The parameter name is stated rather than inferred, because the caller sees a step value and not the
        //validator's own parameter, and an exception naming "value" would send a reader to the wrong place.
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Value));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxStepValue, nameof(Value));

        return value;
    }


    private string DebuggerDisplay => $"RecorderStep: {Value} (round {Round}, phase {Phase})";
}
