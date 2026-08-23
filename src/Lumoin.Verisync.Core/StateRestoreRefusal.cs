namespace Lumoin.Verisync.Core;

/// <summary>
/// Which rule a restore refused durable state on.
/// </summary>
/// <remarks>
/// <para>
/// A restore reads one snapshot and refuses every state its protocol cannot hold. Which rule fired is
/// operationally different information, because the answers lead to different acts: a snapshot whose parts
/// disagree is a torn write and the store is restored from elsewhere, a snapshot naming another chain is an
/// operator's filing mistake and the host is re-provisioned, and a snapshot whose shape no transition
/// produces is a store that was written by something other than this library. Recovering that from exception
/// prose makes a consumer pin sentences, which a reword breaks while the behaviour stands.
/// </para>
/// <para>
/// The members are prefixed by the type whose rule they are, because the recorder and the host both have step
/// rules and both have membership rules, and a flat set would need a comment to say which was which.
/// </para>
/// <para>
/// One rule is one member however many entry points raise it. The chain check is made by
/// <see cref="QuePaxaVersionedNode{TValue}"/>'s constructor against the record it is handed and by its restore
/// against the record it decoded, and both report <see cref="HostForeignChain"/>; a caller acting on the
/// refusal cares which rule fired and not which door it came through.
/// </para>
/// <para>
/// The zero value claims nothing, as <see cref="QuePaxaWriteStatus.Undecided"/> does, so a default-valued
/// refusal cannot be read as a rule that fired. No restore raises it.
/// </para>
/// </remarks>
public enum StateRestoreRefusal
{
    /// <summary>No rule is named. Nothing raises this, and a refusal carrying it has not said which rule fired.</summary>
    Unspecified = 0,

    /// <summary>The recorder's step stands below round one phase zero, which is a step it records nothing at.</summary>
    RecorderStepBelowFloor = 1,

    /// <summary>The recorder stands above step zero carrying no first proposal.</summary>
    RecorderFirstProposalMissing = 2,

    /// <summary>The recorder's first proposal at round one phase zero holds the reserved priority for a lane other than the configured leader's.</summary>
    RecorderForeignClaimInFirstProposal = 3,

    /// <summary>The recorder's current aggregate at round one phase zero holds the reserved priority for a lane other than the configured leader's.</summary>
    RecorderForeignClaimInAggregate = 4,

    /// <summary>The recorder carries a first proposal with no current aggregate beside it.</summary>
    RecorderAggregateMissing = 5,

    /// <summary>The recorder's current aggregate orders below its first proposal at the same step.</summary>
    RecorderAggregateBelowFirstProposal = 6,

    /// <summary>The recorder stands at round one phase zero carrying a prior aggregate, which only a non-clearing advance leaves behind.</summary>
    RecorderPriorAggregateAtFloor = 7,

    /// <summary>The recorder's prior aggregate one step above round one phase zero holds the reserved priority for a lane other than the configured leader's.</summary>
    RecorderForeignClaimInPriorAggregate = 8,

    /// <summary>The host's stored configured leader is not the one its own record derives.</summary>
    HostLeaderMismatch = 9,

    /// <summary>The host's recorder serves a version other than the one after its stored record's.</summary>
    HostRecorderVersionMismatch = 10,

    /// <summary>The host's stored active membership is not the one its own record implies.</summary>
    HostConfigurationMismatch = 11,

    /// <summary>The host's record names a chain other than the genesis membership it was given.</summary>
    HostForeignChain = 12,

    /// <summary>The host's recorder stands at step zero carrying a proposal, which is a proposal that was never recorded.</summary>
    HostUnwrittenRecorderCarriesProposal = 13,

    /// <summary>The host restoring is not the host that wrote the state it was handed.</summary>
    HostIdentityMismatch = 14,

    /// <summary>The acceptor's promise stands below the initial fast ballot it is pre-promised to.</summary>
    AcceptorPromiseBelowInitialBallot = 15,

    /// <summary>The acceptor's accepted ballot is neither the zero ballot nor at or above the initial fast ballot.</summary>
    AcceptorAcceptedBallotBelowInitialBallot = 16,

    /// <summary>The acceptor's promise trails its accepted ballot, which accepting raises it to.</summary>
    AcceptorPromiseTrailsAcceptedBallot = 17,

    /// <summary>The acceptor carries an accepted value under the zero accepted ballot, which are written together.</summary>
    AcceptorValueWithoutAcceptedBallot = 18,

    /// <summary>The Raft node's stored vote is neither empty nor one replica identity wide.</summary>
    RaftVoteMalformed = 19,

    /// <summary>The Raft node's stored vote names a replica the cluster does not contain.</summary>
    RaftVoteOutsideMembership = 20,

    /// <summary>The Raft node's stored log carries a term below the one before it.</summary>
    RaftLogTermsDecrease = 21,

    /// <summary>The Raft node's stored last log term stands above its stored current term.</summary>
    RaftLastLogTermAboveCurrentTerm = 22
}
