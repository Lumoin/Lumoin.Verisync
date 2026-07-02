using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Pins the convergence surface <see cref="AntiEntropySession{TElement}.IsConverged"/> against the terminal
/// state it disambiguates: <see cref="AntiEntropySessionState.Completed"/> alone conflates a genuinely
/// converged decode with a decode wound down while still incomplete (State, DecodedItems, and the run's
/// <see cref="Task.IsCompletedSuccessfully"/> are byte-for-byte identical across the two — the conflation
/// assertions retained below prove the surface is necessary), and <c>IsConverged</c> is the one public
/// signal that separates them: <see langword="true"/> only through the reconciliation path, never through
/// a wind-down.
/// </summary>
[TestClass]
internal sealed class AntiEntropyTerminalStateConflationProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int TriggerCap = 200;

    private static ReconciliationContract Contract { get; } = ReconciliationContract.ContentHashDefault;

    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// Case A (equal sets, honest convergence) and Case B (a real non-empty difference wound down before a
    /// single symbol is absorbed) both leave the initiator at exactly <see cref="AntiEntropySessionState.Completed"/>
    /// with an empty <see cref="AntiEntropySession{TElement}.DecodedItems"/> and a successfully-completed run —
    /// yet in Case A the sets are equal and in Case B the entire difference {gamma, delta, epsilon} was
    /// dropped by the wind-down. <see cref="AntiEntropySession{TElement}.IsConverged"/> is the one public
    /// signal that separates them: <see langword="true"/> for the reconciled Case A (on both roles),
    /// <see langword="false"/> for the wound-down Case B.
    /// </summary>
    [TestMethod]
    public async Task IsConvergedSeparatesConvergedEqualSetsFromWoundDownRealDifference()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Case A: equal sets. An honest exchange converges with an empty symmetric difference.
        ReadOnlyMemory<byte>[] equalItems = Hashes("alpha", "beta");
        using AntiEntropySession<string> convergedInitiator = new(AntiEntropyRole.Initiator, Contract, equalItems, BaseMemoryPool.Shared);
        using AntiEntropySession<string> convergedResponder = new(AntiEntropyRole.Responder, Contract, equalItems, BaseMemoryPool.Shared);

        ResolveReconciliationDifferenceDelegate<string> resolveEmpty = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        ServeReconciliationFetchDelegate<string> serveNothing = _ => [];

        Task convergedInitiatorRun = convergedInitiator.RunAsync(Forward(convergedResponder), resolveEmpty, null, null, cancellationToken: cancellationToken);
        Task convergedResponderRun = convergedResponder.RunAsync(Forward(convergedInitiator), null, serveNothing, null, cancellationToken: cancellationToken);

        await PaceUntilCompletedAsync(convergedInitiator, convergedResponder, cancellationToken).ConfigureAwait(false);
        convergedResponder.Complete();
        await Task.WhenAll(convergedInitiatorRun, convergedResponderRun).ConfigureAwait(false);

        AntiEntropySessionState convergedState = convergedInitiator.State;
        int convergedDecodedCount = convergedInitiator.DecodedItems.Count;
        bool convergedRanToSuccess = convergedInitiatorRun.IsCompletedSuccessfully;

        //Case B: a genuine non-empty difference. The initiator lacks {gamma, delta, epsilon}; the host winds the
        //initiator down (the doc-sanctioned Complete()) before a single symbol is absorbed. No symbols are fed,
        //so the wind-down is deterministic: AbsorbedCount stays zero, nothing decodes.
        ReadOnlyMemory<byte>[] initiatorItems = Hashes("alpha", "beta");
        ReadOnlyMemory<byte>[] responderItems = Hashes("alpha", "beta", "gamma", "delta", "epsilon");
        Assert.AreEqual(3, responderItems.Length - initiatorItems.Length, "The constructed difference must be the three real items.");

        using AntiEntropySession<string> woundInitiator = new(AntiEntropyRole.Initiator, Contract, initiatorItems, BaseMemoryPool.Shared);

        //Drive the initiator directly: it emits its own offer into the void, we hand it the matching peer offer to
        //move it to Reconciling, then wind it down with no symbols at all.
        Task woundInitiatorRun = woundInitiator.RunAsync(Discard, resolveEmpty, null, null, cancellationToken: cancellationToken);
        await woundInitiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(Contract)), cancellationToken).ConfigureAwait(false);
        await WaitForStateAsync(woundInitiator, AntiEntropySessionState.Reconciling, cancellationToken).ConfigureAwait(false);

        woundInitiator.Complete();
        await woundInitiatorRun.ConfigureAwait(false);

        AntiEntropySessionState woundState = woundInitiator.State;
        int woundDecodedCount = woundInitiator.DecodedItems.Count;
        bool woundRanToSuccess = woundInitiatorRun.IsCompletedSuccessfully;

        //Every legacy surface is IDENTICAL across the two cases — the conflation IsConverged exists to break.
        Assert.AreEqual(AntiEntropySessionState.Completed, convergedState, "Case A must reach Completed.");
        Assert.AreEqual(AntiEntropySessionState.Completed, woundState, "Case B must also reach Completed.");
        Assert.AreEqual(convergedState, woundState, "The terminal State conflates the two outcomes.");
        Assert.AreEqual(0, convergedDecodedCount, "Case A decodes nothing (equal sets).");
        Assert.AreEqual(0, woundDecodedCount, "Case B decodes nothing (wound down before any symbol).");
        Assert.AreEqual(convergedDecodedCount, woundDecodedCount, "DecodedItems.Count conflates the two outcomes.");
        Assert.IsTrue(convergedRanToSuccess, "Case A run completed successfully.");
        Assert.IsTrue(woundRanToSuccess, "Case B run completed successfully — no fault, no negative signal.");
        Assert.AreEqual(convergedRanToSuccess, woundRanToSuccess, "The run outcome conflates the two outcomes.");

        //IsConverged is the surface that separates them: reconciliation-path only, never wind-down.
        Assert.IsTrue(convergedInitiator.IsConverged, "Case A: the initiator's decode completed and resolved, so it converged.");
        Assert.IsTrue(convergedResponder.IsConverged, "Case A: the responder received the done signal attesting the complete decode, so it converged.");
        Assert.IsFalse(woundInitiator.IsConverged, "Case B: a wound-down session with an unrecovered real difference must NOT report convergence.");
    }


    /// <summary>
    /// A large real difference paced with too few symbols and then wound down: the initiator reaches
    /// <see cref="AntiEntropySessionState.Completed"/> with strictly fewer decoded items than the true
    /// difference and its run completes successfully — and <see cref="AntiEntropySession{TElement}.IsConverged"/>
    /// stays <see langword="false"/>, the negative signal the wind-down previously lacked. The count bound is
    /// information-theoretic (a handful of absorbed cells cannot recover eighteen distinct items), so the
    /// assertion holds under every interleaving.
    /// </summary>
    [TestMethod]
    public async Task WoundDownAfterInsufficientSymbolsReportsNotConverged()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        string[] shared = ["alpha", "beta"];
        string[] responderOnly = ["g0", "g1", "g2", "g3", "g4", "g5", "g6", "g7", "g8", "g9", "g10", "g11", "g12", "g13", "g14", "g15", "g16", "g17"];
        int trueDifference = responderOnly.Length;

        ReadOnlyMemory<byte>[] initiatorItems = Hashes(shared);
        ReadOnlyMemory<byte>[] responderItems = Hashes([.. shared, .. responderOnly]);

        //A batch size of one lets us cap the absorbed cells well below the difference size.
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, Contract, initiatorItems, 1, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, Contract, responderItems, 1, BaseMemoryPool.Shared);

        ResolveReconciliationDifferenceDelegate<string> resolveEmpty = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        ServeReconciliationFetchDelegate<string> serveNothing = _ => [];

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolveEmpty, null, null, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serveNothing, null, cancellationToken: cancellationToken);

        //Feed only a few symbols — far fewer than the eighteen-item difference needs — then wind the session down.
        const int insufficientTriggers = 4;
        for(int i = 0; i < insufficientTriggers; i++)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        }

        initiator.Complete();
        responder.Complete();
        await initiatorRun.ConfigureAwait(false);
        await SwallowAsync(responderRun).ConfigureAwait(false);

        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State, "The wound-down initiator reaches Completed.");
        Assert.IsTrue(initiatorRun.IsCompletedSuccessfully, "The wound-down run completes successfully — no fault surfaces.");
        Assert.IsLessThan(trueDifference, initiator.DecodedItems.Count, "Fewer than the true difference decoded, yet the session reports Completed.");
        Assert.IsFalse(initiator.IsConverged, "An incomplete decode wound down mid-stream must NOT report convergence.");
    }


    private static async Task PaceUntilCompletedAsync(AntiEntropySession<string> initiator, AntiEntropySession<string> responder, CancellationToken cancellationToken)
    {
        int triggers = 0;
        while(initiator.State != AntiEntropySessionState.Completed)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            triggers++;
            Assert.IsLessThan(TriggerCap, triggers, "The initiator never completed within the trigger cap.");
        }
    }


    private static async Task WaitForStateAsync(AntiEntropySession<string> session, AntiEntropySessionState state, CancellationToken cancellationToken)
    {
        int waits = 0;
        while(session.State != state)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            waits++;
            Assert.IsLessThan(TriggerCap, waits, $"The session never reached {state} within the wait cap.");
        }
    }


    private static SendReconciliationEnvelopeDelegate<string> Forward(AntiEntropySession<string> peer)
    {
        return (envelope, cancellationToken) =>
        {
            try
            {
                return peer.SubmitAsync(envelope, cancellationToken);
            }
            catch(System.Threading.Channels.ChannelClosedException)
            {
                return ValueTask.CompletedTask;
            }
        };
    }


    private static ValueTask Discard(ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }


    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch(InvalidOperationException)
        {
        }
        catch(OperationCanceledException)
        {
        }
    }


    private static ReadOnlyMemory<byte>[] Hashes(params string[] elements)
    {
        ReadOnlyMemory<byte>[] items = new ReadOnlyMemory<byte>[elements.Length];
        for(int i = 0; i < elements.Length; i++)
        {
            items[i] = SHA256.HashData(Encoding.UTF8.GetBytes(elements[i]));
        }

        return items;
    }
}
