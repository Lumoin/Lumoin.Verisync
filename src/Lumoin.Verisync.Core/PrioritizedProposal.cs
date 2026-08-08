namespace Lumoin.Verisync.Core;

/// <summary>
/// A prioritized proposal: an ordering key paired with the value the proposer wants decided. It is the
/// paper's proposal triple of priority, proposer and value.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Key">The proposal's ordering key, which carries its priority and its owning proposer lane.</param>
/// <param name="Value">The proposed value.</param>
/// <remarks>
/// <para>
/// This is a reference type so that a <see langword="null"/> proposal is the absent one, the paper's
/// <c>nil</c>, without a sentinel value inside the type.
/// </para>
/// <para>
/// <typeparamref name="TValue"/> MUST HAVE VALUE EQUALITY, and this is a protocol requirement rather than a
/// nicety. The fast path and the phase-two decision test compare whole proposals, and the synthesized record
/// equality routes the value through <c>EqualityComparer&lt;TValue&gt;.Default</c>. A value type that falls
/// back to reference equality breaks the phase-two test outright, because the proposal never equals the
/// aggregate and the proposer therefore never decides. The defect is invisible in an in-memory core, where
/// the same object travels from proposer to recorder and back, and appears the moment a proposal has crossed
/// a codec and the recorder's copy is a different instance.
/// </para>
/// <para>
/// Nothing here rents from the memory pool, because a proposal allocates no byte buffers and carries
/// <typeparamref name="TValue"/> by reference. A host whose value type holds pooled memory owes that
/// memory's lifetime to its own discipline.
/// </para>
/// </remarks>
public sealed record PrioritizedProposal<TValue>(ProposalKey Key, TValue Value)
{
    /// <summary>Returns the same value under a re-prioritized key.</summary>
    /// <param name="priority">The new priority.</param>
    /// <returns>The proposal under the new priority, with its owner and value unchanged.</returns>
    /// <remarks>
    /// <para>
    /// This is the only mutation the protocol paths perform on a proposal, and it is not incidental. The
    /// proposer's phase zero redraws the priority and leaves the owner attached, so a proposal carried
    /// forward from another proposer keeps that proposer's identity. The map records that Lemma C.10's second
    /// case depends on exactly that, and that an implementation restamping the owner has to redo the
    /// argument; the checked concrete configurations ran one round, where the distinction is unreachable,
    /// which is why it is easy to get wrong.
    /// </para>
    /// <para>
    /// This is a DISCIPLINE and not a shape guarantee. The type has a public constructor because a codec
    /// layer needs one, so an owner can always be restamped by construction or through a <c>with</c>
    /// expression. What the type does is make re-prioritizing the obvious move and say here why restamping an
    /// owner is a protocol defect.
    /// </para>
    /// </remarks>
    public PrioritizedProposal<TValue> WithPriority(ProposalPriority priority) => new(Key.WithPriority(priority), Value);
}
