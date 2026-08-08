using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Drives the concrete QuePaxa protocol from the proposer side over a set of recorder endpoints. It depends
/// only on <see cref="RecorderEndpointDelegate{TValue}"/>, so the same proposer runs over in-process calls,
/// in-memory channels, or sockets.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// The protocol rules live in <see cref="QuePaxaRound{TValue}"/>:
/// <see cref="QuePaxaRound{TValue}.NextSend"/> produces what each send carries and
/// <see cref="QuePaxaRound{TValue}.Conclude"/> decides what a majority of answers means. What belongs here is
/// asynchrony: which endpoints are outstanding, which have answered, and which are worth asking again.
/// </para>
/// <para>
/// It acts on the first quorum and does not wait for the remaining endpoints, which is quorum latency rather
/// than total latency. The model's proposer acts on any majority subset of the replies it holds, so acting on
/// the first majority to arrive is one of the choices it admits.
/// </para>
/// <para>
/// A request may be re-delivered and must then be identical. Each step draws its per-recorder proposals once
/// and retains the resulting requests for the lifetime of that step, so a recorder that faulted is asked
/// again with the very same step, proposal and priority. A second identical record is the identity on the
/// recorder, while a re-draw would put two distinct proposal keys at one recorder and step, which is the one
/// state the model does not admit. The retention is a discipline of the step's send path rather than a
/// guarantee this type's shape can give.
/// </para>
/// <para>
/// One recorder contributes at most one answer per step, by construction. Within one step at most one call
/// per recorder is outstanding, and a recorder that has answered is never called again during that step, so a
/// second answer from one recorder is unreachable rather than discarded. The model's majority test counts
/// reply records rather than recorders, so this is what keeps the quorum arithmetic honest;
/// <see cref="QuePaxaRound{TValue}.Conclude"/> re-checks it for a host that assembles an answer array itself.
/// </para>
/// <para>
/// Calls for consecutive steps do overlap, and a transport must be built for it. Once a quorum answers, the
/// endpoints still outstanding are abandoned rather than cancelled, and the next step calls every recorder
/// again, so one recorder can hold an abandoned call from the previous step and a live one from this step at
/// the same moment. A reply carries the recorder's own step rather than the step of the request it answers, so
/// nothing above the transport can tell the two apart; the transport therefore owes per-call correlation and
/// must never route a reply by a single per-recorder slot. A reply that arrives from below the step being
/// gathered is discarded here, which is the model's own filter over the replies a proposer may act on.
/// </para>
/// </remarks>
public sealed class QuePaxaProposer<TValue>
{
    //Guards single entry into ProposeAsync. The one-shot contract is a protocol rule rather than a
    //convenience, and Interlocked is what makes a concurrent second call lose as reliably as a sequential
    //one.
    private int proposed;


    /// <summary>
    /// Initializes a proposer on <paramref name="lane"/> over <paramref name="recorders"/>.
    /// </summary>
    /// <param name="recorders">The recorder endpoints, in index order.</param>
    /// <param name="lane">The lane this proposer proposes on.</param>
    /// <param name="drawPriority">The source of the phase-zero priority draw.</param>
    /// <param name="attemptsPerRecorder">
    /// How many times one step may send to one recorder before abandoning it for that step. Must be at least
    /// one.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="recorders"/> or <paramref name="drawPriority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="recorders"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="attemptsPerRecorder"/> is less than one.</exception>
    /// <remarks>
    /// <paramref name="attemptsPerRecorder"/> has no default, because the right value is a property of the
    /// transport. The budget is spent per step rather than per round: a recorder abandoned at one step is
    /// asked again at the next one with a full budget, because a transport fault at one step says nothing
    /// about the next.
    /// </remarks>
    public QuePaxaProposer(
        IReadOnlyList<RecorderEndpointDelegate<TValue>> recorders,
        ProposerLane lane,
        ProposalPrioritySourceDelegate drawPriority,
        int attemptsPerRecorder)
    {
        ArgumentNullException.ThrowIfNull(recorders);
        if(recorders.Count == 0)
        {
            throw new ArgumentException("At least one recorder is required.", nameof(recorders));
        }

        ArgumentNullException.ThrowIfNull(drawPriority);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsPerRecorder, 1);

        Recorders = recorders;
        Lane = lane;
        DrawPriority = drawPriority;
        AttemptsPerRecorder = attemptsPerRecorder;
    }


    /// <summary>The number of recorder endpoints.</summary>
    public int RecorderCount => Recorders.Count;

    /// <summary>The quorum size, which is a strict majority.</summary>
    /// <remarks>
    /// A strict majority is what the proofs need: Lemma B.4 asks for sets exceeding half the replicas, and
    /// Lemmas C.5 and C.6 discharge the two threshold-broadcast properties from majority quorums. A deployment
    /// sized above the minimum may use larger quorums and stays safe; it is never required to.
    /// </remarks>
    public int Quorum => (Recorders.Count / 2) + 1;

    /// <summary>The lane this proposer proposes on.</summary>
    public ProposerLane Lane { get; }

    /// <summary>How many times one step may send to one recorder before abandoning it for that step.</summary>
    public int AttemptsPerRecorder { get; }


    private IReadOnlyList<RecorderEndpointDelegate<TValue>> Recorders { get; }

    private ProposalPrioritySourceDelegate DrawPriority { get; }


    /// <summary>
    /// Drives a proposal of <paramref name="value"/> against the recorders until it decides or the round can
    /// no longer make progress.
    /// </summary>
    /// <param name="believedLeader">The lane this proposer believes leads round one, or <see langword="null"/> for none.</param>
    /// <param name="value">The value to propose.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of the attempt.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if this proposer has already proposed, or if the priority source returns a priority that is not
    /// ordinary, or if a recorder above step zero answered with no first proposal.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled.</exception>
    /// <remarks>
    /// <para>
    /// This proposer is one-shot and this call latches: a second call, concurrent or sequential, throws.
    /// Re-entering round one phase zero on the same lane redraws the priority, so the recorders reached by
    /// both attempts would hold two distinct proposal keys at one step from one proposer, which is the state
    /// the model's one-send-per-step flag forbids. The recovery is a fresh <see cref="ProposerLane"/> on a
    /// fresh proposer: an attempt under a new lane is a different proposer identity, which the model already
    /// handles as a second proposer, and it is also what <see cref="ProposalKey"/>'s uniqueness contract asks
    /// for.
    /// </para>
    /// <para>
    /// The loop terminates because advancing raises the step by exactly one, catching up raises it further,
    /// and the last representable step bounds the loop. A missed quorum is reachable here, because endpoints
    /// fault and attempts are bounded, and it terminates the loop because a missed quorum carries no successor
    /// round.
    /// </para>
    /// <para>
    /// An undecided outcome is not evidence that this proposer's value was not chosen. Every recorder a step
    /// reached still recorded, so the proposal may be carried by another proposer and decided later.
    /// </para>
    /// <para>
    /// Cancellation is classified rather than rethrown blindly. An endpoint that ends cancelled or throws
    /// <see cref="OperationCanceledException"/> is treated as a transport fault and retried while
    /// <paramref name="cancellationToken"/> is unsignalled, so an endpoint imposing its own deadline through
    /// a linked token does not kill a proposal the caller never cancelled; the cancellation propagates only
    /// once the caller's own token is signalled. Because the proposer waits on
    /// <see cref="Task.WhenAny(Task[])"/>, which takes no token, it cannot interrupt its own wait: an
    /// endpoint that ignores the token blocks a cancelled proposal indefinitely, which is the contract stated
    /// on <see cref="RecorderEndpointDelegate{TValue}"/>.
    /// </para>
    /// </remarks>
    public Task<QuePaxaOutcome<TValue>> ProposeAsync(ProposerLane? believedLeader, TValue value, CancellationToken cancellationToken)
    {
        //The latch is taken before the first await so that the refusal is synchronous, which is what makes a
        //second call fail at the call site rather than at whatever point the caller happens to await it.
        if(Interlocked.Exchange(ref proposed, 1) != 0)
        {
            throw new InvalidOperationException("A QuePaxa proposer proposes once; a retry needs a fresh proposer on a fresh lane, because re-proposing on this lane would redraw at round one phase zero and put two distinct proposal keys at one recorder and step.");
        }

        return ProposeCoreAsync(believedLeader, value, cancellationToken);
    }


    private async Task<QuePaxaOutcome<TValue>> ProposeCoreAsync(ProposerLane? believedLeader, TValue value, CancellationToken cancellationToken)
    {
        QuePaxaRound<TValue> round = QuePaxaRound<TValue>.Begin(Lane, believedLeader, value);
        int steps = 0;
        while(true)
        {
            QuePaxaStepOutcome<TValue> outcome = await StepAsync(round, cancellationToken).ConfigureAwait(false);
            steps++;

            if(outcome.Kind == QuePaxaStepKind.Decided)
            {
                return new QuePaxaOutcome<TValue>(true, outcome.DecidedValue, outcome.DecidedBy, outcome.DecidedAt, steps);
            }

            //A successor round exists exactly for the advanced and caught-up kinds, so its absence is the
            //loop's terminal condition and needs no second test against the kind.
            if(outcome.Next is null)
            {
                return new QuePaxaOutcome<TValue>(false, default, null, RecorderStep.Zero, steps);
            }

            round = outcome.Next;
        }
    }


    private async Task<QuePaxaStepOutcome<TValue>> StepAsync(QuePaxaRound<TValue> round, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int recorderCount = Recorders.Count;

        //The per-recorder proposals are drawn once and the requests are retained for the lifetime of this
        //step. THIS ARRAY IS THE RE-SEND DISCIPLINE: a re-delivery re-sends the object built here, so no path
        //below can reach the priority source and produce a second distinct key at one recorder and step.
        var requests = new RecordRequest<TValue>[recorderCount];
        for(int index = 0; index < recorderCount; index++)
        {
            requests[index] = new RecordRequest<TValue>(round.Step, round.NextSend(DrawPriority));
        }

        //The attempt budget is per step, so a recorder abandoned at one step starts the next one with a full
        //budget.
        var attemptsSpent = new int[recorderCount];
        var answered = new bool[recorderCount];
        var outstanding = new Task<RecordReply<TValue>>?[recorderCount];
        ImmutableArray<RecorderAnswer<TValue>>.Builder answers = ImmutableArray.CreateBuilder<RecorderAnswer<TValue>>(recorderCount);
        var inFlight = new List<Task<RecordReply<TValue>>>(recorderCount);
        try
        {
            for(int index = 0; index < recorderCount; index++)
            {
                attemptsSpent[index] = 1;
                outstanding[index] = SendAsync(index, requests[index], cancellationToken);
            }

            //The terminal condition is stated over the recorders rather than over the in-flight set: the step
            //ends when a quorum has answered, or when a quorum can no longer be assembled from the recorders
            //that are still worth waiting for. THE SECOND HALF IS ARITHMETIC AND NOT MERELY LIVENESS
            //BOOKKEEPING. Waiting on endpoints that cannot carry the count to a quorum is waiting for an answer
            //the step already has: a proposal against five recorders of which three refused immediately would
            //otherwise sit on the two remaining connections for their full transport timeout, and against an
            //endpoint that never completes at all it would never return.
            while(answers.Count < Quorum && CanStillReachQuorum(answers.Count, Quorum, outstanding, answered, attemptsSpent, AttemptsPerRecorder))
            {
                inFlight.Clear();
                foreach(Task<RecordReply<TValue>>? pending in outstanding)
                {
                    if(pending is not null)
                    {
                        inFlight.Add(pending);
                    }
                }

                if(inFlight.Count == 0)
                {
                    //The drain below re-sends every eligible recorder before this test runs again, so an
                    //empty in-flight set means nothing is reachable. Leaving is the fail-closed reading and
                    //stops a caller-visible spin if that ever stops being true.
                    break;
                }

                await Task.WhenAny(inFlight).ConfigureAwait(false);

                //EVERY completed endpoint is drained in this pass, and a recorder whose attempt ended without
                //an answer is re-sent here, the moment its own attempt completed. Eligibility is PER
                //RECORDER: gating a re-send on the other endpoints being idle would leave one faulting
                //recorder unretried while a live majority is outstanding, and one endpoint that never
                //completes would then hang the proposal with a quorum available.
                for(int index = 0; index < recorderCount; index++)
                {
                    Task<RecordReply<TValue>>? task = outstanding[index];
                    if(task is null || !task.IsCompleted)
                    {
                        continue;
                    }

                    outstanding[index] = null;

                    RecordReply<TValue> ? reply = TakeReply(task, cancellationToken);

                    //A REPLY FROM BELOW THIS STEP IS DISCARDED RATHER THAN COUNTED, which is the model's
                    //Answers filter: a proposer reads only the replies answering the step it is on. A recorder
                    //advances to the requested step before answering, so this can only be a reply the transport
                    //correlated to the wrong call, and counting it would put a stale aggregate into the
                    //conclusion and could take a phase-two decision on a majority that never gathered here.
                    if(reply is not null && reply.Step >= round.Step)
                    {
                        //A recorder that answered is finished for this step and is never called again during
                        //it, which is what makes one recorder contribute at most one answer by construction.
                        answered[index] = true;
                        answers.Add(new RecorderAnswer<TValue>(index, new RecordSummary<TValue>(reply.Step, reply.First, reply.PriorAggregate)));

                        continue;
                    }

                    //A recorder is not worth asking again once the step has its quorum: the answer could not
                    //be read, and the send would be issued only to be abandoned.
                    if(answers.Count < Quorum && attemptsSpent[index] < AttemptsPerRecorder)
                    {
                        attemptsSpent[index]++;
                        outstanding[index] = SendAsync(index, requests[index], cancellationToken);
                    }
                }
            }
        }
        finally
        {
            AbandonQuietly(outstanding);
        }

        //CANCELLATION IS DECIDED BY THE TOKEN AND NOT BY WHAT A TRANSPORT THREW. The per-task arms above catch
        //the cancellation that arrives as a cancelled or OperationCanceledException-faulted endpoint, but a
        //transport is equally entitled to answer a signalled token by aborting its connection, and that
        //arrives as an IOException like any other fault. Without this test such a proposal would return a
        //missed quorum, which is a protocol outcome a caller may act on by starting a fresh attempt, where a
        //cancellation is one it must unwind.
        cancellationToken.ThrowIfCancellationRequested();

        return round.Conclude(answers.ToImmutable(), recorderCount);
    }


    private Task<RecordReply<TValue>> SendAsync(int recorder, RecordRequest<TValue> request, CancellationToken cancellationToken)
    {
        try
        {
            //THE ENDPOINT'S VALUETASK IS MATERIALIZED HERE, AT THE CALL SITE, EXACTLY ONCE. A ValueTask may
            //legally be consumed only once, Task.WhenAny does not accept one at all, and a step reads each
            //outcome several times: to classify it, to take its result, and to observe its fault. An
            //IValueTaskSource-backed transport read twice returns another operation's result or throws, and
            //an in-memory endpoint cannot reproduce that.
            return Recorders[recorder](request, cancellationToken).AsTask();
        }
        catch(Exception exception)
        {
            //An endpoint that throws before it returns a task is the same transport fault as one whose task
            //faults, and routing both through a faulted task keeps the classification in one place.
            return Task.FromException<RecordReply<TValue>>(exception);
        }
    }


    private static RecordReply<TValue>? TakeReply(Task<RecordReply<TValue>> task, CancellationToken cancellationToken)
    {
        //A COMPLETED TASK FALLS INTO THREE CASES AND NOT TWO. A task that ended cancelled has IsFaulted false
        //and Exception null, so a faulted-or-result split would read Result on it, surface an
        //AggregateException and abandon a recorder that a retry could still reach.
        if(task.IsCanceled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return null;
        }

        if(task.IsFaulted)
        {
            //Reading the exception also observes it. An endpoint that imposes its own deadline through a
            //linked token must not kill a proposal the caller never cancelled, so a cancellation is rethrown
            //only when the caller's own token is signalled.
            AggregateException? failure = task.Exception;
            if(failure?.InnerException is OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return null;
        }

        //A null reply counts as a fault rather than as an answer. The endpoint delegate is typed non-nullable,
        //but a nullable-oblivious host can return null and a null summary must never reach the conclusion.
        return task.Result;
    }


    private static bool CanStillReachQuorum(int answerCount, int quorum, Task<RecordReply<TValue>>?[] outstanding, bool[] answered, int[] attemptsSpent, int attemptsPerRecorder)
    {
        //A recorder can still contribute when it has not answered and either its attempt is in flight or it
        //has an attempt left. Counting them rather than stopping at the first one is what turns this from a
        //test of whether anything is pending into a test of whether the quorum is still arithmetically
        //reachable.
        int reachable = 0;
        for(int index = 0; index < outstanding.Length; index++)
        {
            if(answered[index])
            {
                continue;
            }

            if(outstanding[index] is not null || attemptsSpent[index] < attemptsPerRecorder)
            {
                reachable++;
            }
        }

        return answerCount + reachable >= quorum;
    }


    private static void AbandonQuietly(Task<RecordReply<TValue>>?[] outstanding)
    {
        foreach(Task<RecordReply<TValue>>? task in outstanding)
        {
            if(task is null)
            {
                continue;
            }

            //Once a quorum is in, the remaining endpoints are never awaited. Observing their faults is
            //hygiene rather than crash prevention: an unobserved task exception has not terminated the
            //process since .NET 4.5, so what this buys is a quiet TaskScheduler.UnobservedTaskException
            //stream and a host whose own handler is not polluted by endpoints this step stopped waiting for.
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
