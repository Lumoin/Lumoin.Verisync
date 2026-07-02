using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Adversarial probe for the "completion is one-sided and unconfirmed" finding against
/// <see cref="AntiEntropySession{TElement}"/>. The finding's reproduction sketch asserts two routes to a
/// false-success on a push-only resolution: (a) the elements frame is silently lost while the send returns
/// success, and (b) "the responder's channel already completed". This probe exercises route (b) with an HONEST
/// transport that surfaces the wound-down peer by throwing <see cref="ChannelClosedException"/> at the push
/// send, rather than swallowing it the way the in-memory test harness's ForwardTo does. If the finding held,
/// the initiator would still reach <see cref="AntiEntropySessionState.Completed"/> and report success. The
/// probe asserts the opposite: the fail-closed posture documented on RunAsync propagates the throw and the run
/// faults WITHOUT ever reaching Completed, so a detectable transport failure is detected and not reported as a
/// successful reconciliation.
/// </summary>
[TestClass]
internal sealed class CompletionFailClosedProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int TriggerCap = 200;

    private static ReconciliationContract ContentHashContract { get; } = ReconciliationContract.ContentHashDefault;

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task PushSendThatThrowsForAWoundDownPeerFaultsTheInitiatorInsteadOfReportingCompleted()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The initiator holds delta in surplus; the responder lacks it. The symmetric difference is {delta}, which
        //the initiator classifies as a push, so CompleteDecodeAsync takes the push-only branch (Fetch empty).
        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1);
        OrSet<string> initiatorSet = ancestor.Add("delta", R2);
        OrSet<string> responderSet = ancestor;

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, BaseMemoryPool.Shared);

        //Push every decoded surplus item; the element payload is irrelevant because the send never lands.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
        {
            ImmutableArray<ReconciliationElementEntry<string>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<string>>();
            foreach(ReadOnlyMemory<byte> item in decoded)
            {
                push.Add(new ReconciliationElementEntry<string>(item, "delta"));
            }

            return new ReconciliationDifferenceResolution<string>([], push.ToImmutable());
        };

        ServeReconciliationFetchDelegate<string> serve = _ => [];

        //An honest transport: offer and done reach the responder, but the wound-down peer surfaces as a throw at
        //the moment the push elements frame is sent. This is the case the test harness's ForwardTo masks by
        //swallowing ChannelClosedException; here it is surfaced, as a real transport that cannot deliver would.
        SendReconciliationEnvelopeDelegate<string> initiatorSend = (envelope, token) =>
        {
            if(envelope.Elements is not null)
            {
                throw new ChannelClosedException("The responder's channel is wound down; an honest transport surfaces the undeliverable push.");
            }

            return ForwardTo(responder, envelope, token);
        };

        Task initiatorRun = initiator.RunAsync(initiatorSend, resolve, null, null, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, null, cancellationToken: cancellationToken);

        //Pace the responder's symbol stream until the initiator's run terminates (it will fault at the push send).
        int triggers = 0;
        while(!initiatorRun.IsCompleted)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            triggers++;
            Assert.IsLessThan(TriggerCap, triggers, "The initiator never terminated within the trigger cap.");
        }

        //The finding claims the initiator would report a successful reconciliation. The fail-closed posture says
        //otherwise: the run faults with the transport's exception and never transitions to Completed.
        await Assert.ThrowsExactlyAsync<ChannelClosedException>(() => initiatorRun).ConfigureAwait(false);
        Assert.IsFalse(initiatorRun.IsCompletedSuccessfully, "A faulted run must not report success.");
        Assert.AreNotEqual(AntiEntropySessionState.Completed, initiator.State, "A push that could not be delivered must not leave the initiator Completed.");

        responder.Complete();
        await SwallowAsync(responderRun).ConfigureAwait(false);
    }


    private static SendReconciliationEnvelopeDelegate<string> Forward(AntiEntropySession<string> peer)
    {
        return (envelope, cancellationToken) => ForwardTo(peer, envelope, cancellationToken);
    }


    private static ValueTask ForwardTo(AntiEntropySession<string> peer, ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        try
        {
            return peer.SubmitAsync(envelope, cancellationToken);
        }
        catch(ChannelClosedException)
        {
            return ValueTask.CompletedTask;
        }
    }


    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch(InvalidOperationException)
        {
            //ChannelClosedException derives from InvalidOperationException, so this also swallows the wound-down
            //transport throw the responder's own send may surface once its peer initiator has faulted.
        }
        catch(OperationCanceledException)
        {
        }
    }


    private static ReadOnlyMemory<byte>[] ProjectHashes(OrSet<string> set)
    {
        List<ReadOnlyMemory<byte>> items = [];
        foreach(string element in set.Elements)
        {
            items.Add(SHA256.HashData(Encoding.UTF8.GetBytes(element)));
        }

        return [.. items];
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
