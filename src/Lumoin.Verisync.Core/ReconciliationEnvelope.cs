using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The wire envelope that carries exactly one reconciliation message between two peers over a dedicated
/// point-to-point channel. The seven payload slots cover the whole session shape: an offer that pins the
/// contract, the coded-symbol batches the responder streams, the done signal that closes the stream, the
/// fetch that requests decoded items, the elements that resolve them, the causal context a remove-aware
/// session exchanges, and the drop that propagates a remove.
/// </summary>
/// <typeparam name="TElement">The application element type carried by the elements payload.</typeparam>
/// <param name="Offer">The carried <see cref="ReconciliationOffer"/>, or <see langword="null"/>.</param>
/// <param name="Symbols">The carried <see cref="ReconciliationSymbolBatch"/>, or <see langword="null"/>.</param>
/// <param name="Done">The carried <see cref="ReconciliationDone"/>, or <see langword="null"/>.</param>
/// <param name="Fetch">The carried <see cref="ReconciliationFetch"/>, or <see langword="null"/>.</param>
/// <param name="Elements">The carried <see cref="ReconciliationElements{TElement}"/>, or <see langword="null"/>.</param>
/// <param name="Context">The carried <see cref="ReconciliationContext"/>, or <see langword="null"/>.</param>
/// <param name="Drop">The carried <see cref="ReconciliationDrop"/>, or <see langword="null"/>.</param>
/// <remarks>
/// Exactly one of the seven payloads is non-null; an envelope carrying none or more than one is not a valid
/// message. The seven static factories — <see cref="ForOffer"/>, <see cref="ForSymbols"/>,
/// <see cref="ForDone"/>, <see cref="ForFetch"/>, <see cref="ForElements"/>, <see cref="ForContext"/>, and
/// <see cref="ForDrop"/> — are the only documented construction path, and both the codec and the session
/// runner fail closed on an envelope that violates the invariant: the codec at its boundary
/// (<see cref="ArgumentException"/> in process, <c>JsonException</c> on the wire), and the runner through
/// <see cref="EnsureSinglePayload"/> as it validates every inbound envelope. The context and drop are the
/// remove-aware additions; an add-only session constructs neither, so the add-only path is unchanged. The
/// primary constructor stays public so the record's value semantics and <c>with</c> expressions work, but
/// constructing a malformed envelope through it is a caller error caught downstream. The session runs
/// point-to-point over a dedicated channel, so the envelope carries no sender identity.
/// </remarks>
public sealed record ReconciliationEnvelope<TElement>(
    ReconciliationOffer? Offer,
    ReconciliationSymbolBatch? Symbols,
    ReconciliationDone? Done,
    ReconciliationFetch? Fetch,
    ReconciliationElements<TElement>? Elements,
    ReconciliationContext? Context,
    ReconciliationDrop? Drop)
{
    /// <summary>Builds an envelope carrying a <see cref="ReconciliationOffer"/>.</summary>
    /// <param name="offer">The offer to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="offer"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="offer"/> is <see langword="null"/>.</exception>
    public static ReconciliationEnvelope<TElement> ForOffer(ReconciliationOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        return new ReconciliationEnvelope<TElement>(offer, null, null, null, null, null, null);
    }


    /// <summary>Builds an envelope carrying a <see cref="ReconciliationSymbolBatch"/>.</summary>
    /// <param name="symbols">The symbol batch to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="symbols"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="symbols"/> is <see langword="null"/>.</exception>
    public static ReconciliationEnvelope<TElement> ForSymbols(ReconciliationSymbolBatch symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        return new ReconciliationEnvelope<TElement>(null, symbols, null, null, null, null, null);
    }


    /// <summary>Builds an envelope carrying a <see cref="ReconciliationDone"/>.</summary>
    /// <param name="done">The done signal to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="done"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="done"/> is <see langword="null"/>.</exception>
    public static ReconciliationEnvelope<TElement> ForDone(ReconciliationDone done)
    {
        ArgumentNullException.ThrowIfNull(done);

        return new ReconciliationEnvelope<TElement>(null, null, done, null, null, null, null);
    }


    /// <summary>Builds an envelope carrying a <see cref="ReconciliationFetch"/>.</summary>
    /// <param name="fetch">The fetch to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="fetch"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="fetch"/> is <see langword="null"/>.</exception>
    public static ReconciliationEnvelope<TElement> ForFetch(ReconciliationFetch fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        return new ReconciliationEnvelope<TElement>(null, null, null, fetch, null, null, null);
    }


    /// <summary>Builds an envelope carrying a <see cref="ReconciliationElements{TElement}"/>.</summary>
    /// <param name="elements">The element resolutions to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="elements"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="elements"/> is <see langword="null"/>.</exception>
    public static ReconciliationEnvelope<TElement> ForElements(ReconciliationElements<TElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        return new ReconciliationEnvelope<TElement>(null, null, null, null, elements, null, null);
    }


    /// <summary>Builds an envelope carrying a <see cref="ReconciliationContext"/>.</summary>
    /// <param name="context">The causal context to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="context"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="context"/> is <see langword="null"/>.</exception>
    public static ReconciliationEnvelope<TElement> ForContext(ReconciliationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ReconciliationEnvelope<TElement>(null, null, null, null, null, context, null);
    }


    /// <summary>Builds an envelope carrying a <see cref="ReconciliationDrop"/>.</summary>
    /// <param name="drop">The drop to carry.</param>
    /// <returns>An envelope whose only payload is <paramref name="drop"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="drop"/> is <see langword="null"/>.</exception>
    public static ReconciliationEnvelope<TElement> ForDrop(ReconciliationDrop drop)
    {
        ArgumentNullException.ThrowIfNull(drop);

        return new ReconciliationEnvelope<TElement>(null, null, null, null, null, null, drop);
    }


    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless exactly one payload is non-null. The session runner calls
    /// this before enqueueing an inbound envelope, so a malformed message fails closed rather than being
    /// silently dropped or matching no dispatch arm.
    /// </summary>
    /// <param name="paramName">The parameter name to attribute the exception to.</param>
    /// <exception cref="ArgumentException">Thrown if zero, or more than one, payload is non-null.</exception>
    internal void EnsureSinglePayload(string paramName)
    {
        int payloadCount = 0;
        if(Offer is not null)
        {
            payloadCount++;
        }

        if(Symbols is not null)
        {
            payloadCount++;
        }

        if(Done is not null)
        {
            payloadCount++;
        }

        if(Fetch is not null)
        {
            payloadCount++;
        }

        if(Elements is not null)
        {
            payloadCount++;
        }

        if(Context is not null)
        {
            payloadCount++;
        }

        if(Drop is not null)
        {
            payloadCount++;
        }

        if(payloadCount != 1)
        {
            throw new ArgumentException($"A reconciliation envelope must carry exactly one payload, but it carries {payloadCount}.", paramName);
        }
    }
}
