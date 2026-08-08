using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A <see cref="RecordReply{TValue}"/> answering a <see cref="VersionedRecordRequest{TValue}"/>, carrying the
/// version of the instance that produced it.
/// </summary>
/// <typeparam name="TValue">The consensus value type, which a versioned register instantiates at <see cref="VersionedValue{TValue}"/>.</typeparam>
/// <param name="Version">The register version of the instance that answered. Must not be <see cref="RegisterVersion.Unwritten"/>.</param>
/// <param name="Reply">The reply itself, unchanged. Must not be <see langword="null"/>.</param>
/// <remarks>
/// <para>
/// The version is here so that a mis-route is detectable, which is the one correlation failure a caller can
/// catch. A caller holds the version it asked about, so comparing it against this one is a single test; a
/// reply from another instance that passed unchecked would be counted toward this instance's quorum, and a
/// decision taken on an answer set whose members are a minority of the instance is exactly the majority
/// intersection the agreement argument rests on.
/// </para>
/// <para>
/// It does not make the pair self-correlating. Two calls to one recorder for consecutive steps of one
/// instance overlap by design, and a reply carries the recorder's own step rather than the step of the
/// request it answers, so a transport still owes per-call correlation. The version removes the cross-instance
/// case only.
/// </para>
/// </remarks>
public sealed record VersionedRecordReply<TValue>(RegisterVersion Version, RecordReply<TValue> Reply)
{
    /// <summary>
    /// The register version of the instance that answered. It is validated on construction and on a
    /// <c>with</c> expression alike, because the initializer writes the backing field directly and no accessor
    /// runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the version is <see cref="RegisterVersion.Unwritten"/>.</exception>
    public RegisterVersion Version { get; init { field = ValidateVersion(value); } } = ValidateVersion(Version);


    /// <summary>
    /// The reply itself. It is validated on construction and on a <c>with</c> expression alike, for the same
    /// reason the version is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the reply is <see langword="null"/>.</exception>
    public RecordReply<TValue> Reply { get; init { field = ValidateReply(value); } } = ValidateReply(Reply);


    private static RegisterVersion ValidateVersion(RegisterVersion value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value.Value, RegisterVersion.Unwritten.Value, nameof(Version));

        return value;
    }


    private static RecordReply<TValue> ValidateReply(RecordReply<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Reply));

        return value;
    }
}
