using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One recorder's answer to one record request: the summary that recorder returned, carrying the index of the
/// recorder that gave it.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Recorder">The index of the recorder that answered. Must not be negative.</param>
/// <param name="Summary">The summary that recorder answered with.</param>
/// <remarks>
/// <para>
/// This type instantiates the model's <c>Answers(i)</c>, the set of replies answering the request a proposer
/// sent at the step it is on, so the collection a conclusion runs over is the collection the model's majority
/// test runs over.
/// </para>
/// <para>
/// The catch-up rule breaks a tie among equal-step answers by the lowest recorder index so that a run is
/// reproducible, and the model's <c>Majority</c> counts reply records, so a conclusion must be able to see
/// that one recorder contributed at most one of them.
/// </para>
/// <para>
/// This type is distinct from <see cref="RecordReply{TValue}"/>: a reply is a wire message a recorder sends,
/// and an answer is a proposer-side record of who sent what. The recorder index does not travel on the wire,
/// because the recorder does not know it.
/// </para>
/// <para>
/// Both members are validated because a positional record synthesizes no validation at all, and because the
/// constructor set is not closed: <see cref="QuePaxaRound{TValue}.Conclude"/> is public so that a host which
/// is neither of the two drivers can assemble an answer array and call it.
/// </para>
/// </remarks>
public sealed record RecorderAnswer<TValue>(int Recorder, RecordSummary<TValue> Summary)
{
    /// <summary>
    /// The index of the recorder that answered. It is validated on construction and on a <c>with</c>
    /// expression alike, because the initializer writes the backing field directly and no accessor runs for
    /// it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the index is negative.</exception>
    public int Recorder { get; init { field = ValidateRecorder(value); } } = ValidateRecorder(Recorder);


    /// <summary>
    /// The summary that recorder answered with. It is validated on construction and on a <c>with</c>
    /// expression alike, for the same reason the index is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the summary is <see langword="null"/>.</exception>
    public RecordSummary<TValue> Summary { get; init { field = ValidateSummary(value); } } = ValidateSummary(Summary);


    private static int ValidateRecorder(int value)
    {
        //The parameter name is stated rather than inferred, because the caller sees a recorder index and not
        //the validator's own parameter, and an exception naming "value" would send a reader to the wrong
        //place.
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Recorder));

        return value;
    }


    private static RecordSummary<TValue> ValidateSummary(RecordSummary<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Summary));

        return value;
    }
}
