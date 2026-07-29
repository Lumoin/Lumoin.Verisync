using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class InterleavedClusterTests
{
    private const int MaxAttempts = 64;

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task SameSeedReplaysTheIdenticalExecution()
    {
        (IReadOnlyList<string> firstTrace, string firstValue) = await RunContendedScenarioAsync(seed: 42).ConfigureAwait(false);
        (IReadOnlyList<string> secondTrace, string secondValue) = await RunContendedScenarioAsync(seed: 42).ConfigureAwait(false);

        Assert.AreSequenceEqual(firstTrace.ToList(), secondTrace.ToList());
        Assert.AreEqual(firstValue, secondValue);
    }


    [TestMethod]
    public async Task DifferentSeedsExploreDifferentDeliveryOrders()
    {
        var traces = new HashSet<string>();
        for(int seed = 1; seed <= 20; seed++)
        {
            (IReadOnlyList<string> trace, _) = await RunContendedScenarioAsync(seed).ConfigureAwait(false);
            traces.Add(string.Join("|", trace));
        }

        Assert.IsGreaterThan(1, traces.Count, "Twenty seeds produced a single delivery order; the scheduler is not exploring interleavings.");
    }


    [TestMethod]
    public async Task ConcurrentWritersLinearizeAcrossSeeds()
    {
        for(int seed = 1; seed <= 30; seed++)
        {
            InterleavedCluster<string> cluster = new(5, seed);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, Replica(1), 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Replica(2), 'C', 'D', TestContext.CancellationToken),
                RunWriterAsync(cluster, Replica(3), 'E', 'F', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence();
            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string finalValue = await ReadFinalAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];
            Assert.HasCount(6, history);
            AppendRegisterChecker.AssertLinearizable(history, finalValue);
        }
    }


    [TestMethod]
    public async Task WritersLinearizeWhenRequestsAreDuplicated()
    {
        //Retransmitted requests land on acceptors after the proposer has moved on — the histories where a
        //stale accept could overwrite newer state if the acceptor's promise did not rise with each accept.
        for(int seed = 1; seed <= 30; seed++)
        {
            InterleavedCluster<string> cluster = new(5, seed)
            {
                RequestDuplicationPercent = 25
            };

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, Replica(1), 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Replica(2), 'C', 'D', TestContext.CancellationToken),
                RunWriterAsync(cluster, Replica(3), 'E', 'F', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence();
            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string finalValue = await ReadFinalAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];
            Assert.HasCount(6, history);
            AppendRegisterChecker.AssertLinearizable(history, finalValue);
        }
    }


    [TestMethod]
    public async Task FastPathWritersLinearizeUnderContentionAndDuplication()
    {
        //Both writers race the leaderless fast path for their first append — at most one can fast-commit,
        //the other falls back to classic recovery, and a failed fast write's value may still be resurrected
        //by the rival's recovery tally. The idempotent append plus the checker verify every resolution.
        for(int seed = 1; seed <= 30; seed++)
        {
            InterleavedCluster<string> cluster = new(5, seed)
            {
                RequestDuplicationPercent = 25
            };

            Task<RegisterOperation[]>[] writers =
            [
                RunFastFirstWriterAsync(cluster, Replica(1), 'A', 'B', TestContext.CancellationToken),
                RunFastFirstWriterAsync(cluster, Replica(2), 'C', 'D', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence();
            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string finalValue = await ReadFinalAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];
            Assert.HasCount(4, history);
            AppendRegisterChecker.AssertLinearizable(history, finalValue);
        }
    }


    [TestMethod]
    public async Task PiggybackFastPathWritersLinearizeUnderContentionAndDuplication()
    {
        //Each writer chains its two appends on the leaderless fast path: the first fast write piggybacks
        //fast(2) so the second can blind-write at fast(2) without a prepare — the recurring fast round. Under
        //contention at most one writer wins the fast(1) round and reaches fast(2); the rival and every
        //failed fast write fall back to classic recovery. With requests duplicated, a stale piggybacked
        //accept can land after newer ballots, so the run only linearizes if the promise rises with every
        //accept and the equality rule keeps an un-established fast round un-writable.
        for(int seed = 1; seed <= 30; seed++)
        {
            InterleavedCluster<string> cluster = new(5, seed)
            {
                RequestDuplicationPercent = 25
            };

            Task<RegisterOperation[]>[] writers =
            [
                RunPiggybackFastWriterAsync(cluster, Replica(1), 'A', 'B', TestContext.CancellationToken),
                RunPiggybackFastWriterAsync(cluster, Replica(2), 'C', 'D', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence();
            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string finalValue = await ReadFinalAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];
            Assert.HasCount(4, history);
            AppendRegisterChecker.AssertLinearizable(history, finalValue);
        }
    }


    [TestMethod]
    public async Task WritersLinearizeWhenAMinorityIsPartitioned()
    {
        for(int seed = 1; seed <= 10; seed++)
        {
            InterleavedCluster<string> cluster = new(5, seed);
            cluster.Partition(3);
            cluster.Partition(4);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, Replica(1), 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Replica(2), 'C', 'D', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence();
            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string finalValue = await ReadFinalAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];
            Assert.HasCount(4, history);
            AppendRegisterChecker.AssertLinearizable(history, finalValue);
        }
    }


    [TestMethod]
    public async Task WritersLinearizeAcrossAMidRunPartitionAndHeal()
    {
        for(int seed = 1; seed <= 10; seed++)
        {
            InterleavedCluster<string> cluster = new(5, seed);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, Replica(1), 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Replica(2), 'C', 'D', TestContext.CancellationToken)
            ];

            //Let some messages fly, fail two acceptors mid-flight (their in-air replies are lost too),
            //run partitioned for a while, then heal and drain.
            for(int i = 0; i < 5 && cluster.PendingCount > 0; i++)
            {
                cluster.Step();
            }

            cluster.Partition(0);
            cluster.Partition(1);
            for(int i = 0; i < 20 && cluster.PendingCount > 0; i++)
            {
                cluster.Step();
            }

            cluster.Heal(0);
            cluster.Heal(1);
            cluster.RunToQuiescence();

            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string finalValue = await ReadFinalAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];
            Assert.HasCount(4, history);
            AppendRegisterChecker.AssertLinearizable(history, finalValue);
        }
    }


    [TestMethod]
    public async Task WritersCannotCommitWithoutAClassicQuorum()
    {
        InterleavedCluster<string> cluster = new(5, seed: 7);
        cluster.Partition(2);
        cluster.Partition(3);
        cluster.Partition(4);

        Task<RegisterOperation[]> writer = RunWriterAsync(cluster, Replica(1), 'A', 'B', TestContext.CancellationToken);
        cluster.RunToQuiescence();

        //Two reachable acceptors fall short of the classic quorum of three: every attempt fails and the
        //writer gives up — liveness is lost, but nothing was chosen.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer).ConfigureAwait(false);
    }


    [TestMethod]
    public void CheckerRejectsALostUpdate()
    {
        //'B' observed and wrote a chain without 'A', and the final value never contains 'A'.
        List<RegisterOperation> history =
        [
            new RegisterOperation('A', 1, 4, "", "A"),
            new RegisterOperation('B', 5, 8, "", "B")
        ];

        Assert.ThrowsExactly<AssertFailedException>(() => AppendRegisterChecker.AssertLinearizable(history, "B"));
    }


    [TestMethod]
    public void CheckerRejectsARealTimeOrderViolation()
    {
        //'A' completed before 'B' was invoked, yet the final value witnesses 'B' taking effect first.
        List<RegisterOperation> history =
        [
            new RegisterOperation('A', 1, 4, "", "A"),
            new RegisterOperation('B', 5, 8, "A", "AB")
        ];

        Assert.ThrowsExactly<AssertFailedException>(() => AppendRegisterChecker.AssertLinearizable(history, "BA"));
    }


    private static async Task<(IReadOnlyList<string> Trace, string FinalValue)> RunContendedScenarioAsync(int seed)
    {
        InterleavedCluster<string> cluster = new(5, seed);

        Task<RegisterOperation[]>[] writers =
        [
            RunWriterAsync(cluster, Replica(1), 'A', 'B', CancellationToken.None),
            RunWriterAsync(cluster, Replica(2), 'C', 'D', CancellationToken.None)
        ];

        cluster.RunToQuiescence();
        await Task.WhenAll(writers).ConfigureAwait(false);
        string finalValue = await ReadFinalAsync(cluster, CancellationToken.None).ConfigureAwait(false);

        return (cluster.DeliveryTrace, finalValue);
    }


    private static async Task<RegisterOperation[]> RunWriterAsync(InterleavedCluster<string> cluster, ReplicaId writer, char firstLabel, char secondLabel, CancellationToken cancellationToken)
    {
        FastProposer<string> proposer = cluster.CreateProposer();
        int round = 0;

        RegisterOperation first = await AppendAsync(cluster, proposer, writer, firstLabel, NextRound, cancellationToken).ConfigureAwait(false);
        RegisterOperation second = await AppendAsync(cluster, proposer, writer, secondLabel, NextRound, cancellationToken).ConfigureAwait(false);

        return [first, second];

        int NextRound() => ++round;
    }


    private static async Task<RegisterOperation[]> RunFastFirstWriterAsync(InterleavedCluster<string> cluster, ReplicaId writer, char firstLabel, char secondLabel, CancellationToken cancellationToken)
    {
        FastProposer<string> proposer = cluster.CreateProposer();
        int round = 0;

        //The first append races the leaderless fast path; when the fast round is contended away, the
        //append completes through classic recovery like any other write.
        long invoked = cluster.Tick();
        (_, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), firstLabel.ToString(), cancellationToken).ConfigureAwait(false);
        RegisterOperation first = committed
            ? new RegisterOperation(firstLabel, invoked, cluster.Tick(), "", firstLabel.ToString())
            : await AppendAsync(cluster, proposer, writer, firstLabel, NextRound, cancellationToken, invoked).ConfigureAwait(false);
        RegisterOperation second = await AppendAsync(cluster, proposer, writer, secondLabel, NextRound, cancellationToken).ConfigureAwait(false);

        return [first, second];

        int NextRound() => ++round;
    }


    private static async Task<RegisterOperation[]> RunPiggybackFastWriterAsync(InterleavedCluster<string> cluster, ReplicaId writer, char firstLabel, char secondLabel, CancellationToken cancellationToken)
    {
        FastProposer<string> proposer = cluster.CreateProposer();
        int round = 0;

        //The first append races the leaderless fast path and piggybacks fast(2): a successful accept raises
        //each acceptor's promise to fast(2), establishing the next fast round. At most one writer wins the
        //fast(1) round, so only that writer's own bare label is the whole register here.
        long firstInvoked = cluster.Tick();
        (_, bool firstCommitted) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), firstLabel.ToString(), cancellationToken, FastBallot.Fast(2)).ConfigureAwait(false);
        if(!firstCommitted)
        {
            //The fast round was contended away; both appends complete through classic recovery like any other write.
            RegisterOperation recoveredFirst = await AppendAsync(cluster, proposer, writer, firstLabel, NextRound, cancellationToken, firstInvoked).ConfigureAwait(false);
            RegisterOperation recoveredSecond = await AppendAsync(cluster, proposer, writer, secondLabel, NextRound, cancellationToken).ConfigureAwait(false);

            return [recoveredFirst, recoveredSecond];
        }

        RegisterOperation first = new(firstLabel, firstInvoked, cluster.Tick(), "", firstLabel.ToString());

        //The second append blind-writes at the established fast(2) round. The winner built the chain itself,
        //so its observed prefix is exactly the first label and the value written is the two-label chain. When
        //the recurring fast round loses its quorum, it falls back to recovery, which reads the live value.
        long secondInvoked = cluster.Tick();
        string chain = string.Concat(firstLabel, secondLabel);
        (_, bool secondCommitted) = await proposer.TryFastWriteAsync(FastBallot.Fast(2), chain, cancellationToken).ConfigureAwait(false);
        RegisterOperation second = secondCommitted
            ? new RegisterOperation(secondLabel, secondInvoked, cluster.Tick(), firstLabel.ToString(), chain)
            : await AppendAsync(cluster, proposer, writer, secondLabel, NextRound, cancellationToken, secondInvoked).ConfigureAwait(false);

        return [first, second];

        int NextRound() => ++round;
    }


    private static async Task<RegisterOperation> AppendAsync(InterleavedCluster<string> cluster, FastProposer<string> proposer, ReplicaId writer, char label, Func<int> nextRound, CancellationToken cancellationToken, long? invokedAt = null)
    {
        long invoked = invokedAt ?? cluster.Tick();
        for(int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string? observed = null;
            ChangeOutcome<string> outcome = await proposer.RecoverAsync(
                FastBallot.Classic(nextRound(), writer),
                current =>
                {
                    observed = current;

                    //Idempotent append: a retry whose earlier attempt was already chosen must not apply twice.
                    return current is null
                        ? label.ToString()
                        : current.Contains(label, StringComparison.Ordinal) ? current : current + label;
                },
                cancellationToken).ConfigureAwait(false);

            if(outcome.IsChosen)
            {
                return new RegisterOperation(label, invoked, cluster.Tick(), observed ?? "", outcome.Value!);
            }
        }

        throw new InvalidOperationException($"Operation '{label}' did not commit within {MaxAttempts} attempts.");
    }


    private static async Task<string> ReadFinalAsync(InterleavedCluster<string> cluster, CancellationToken cancellationToken)
    {
        //An identity update at a round above every writer's reach: a linearizable read via recovery.
        FastProposer<string> proposer = cluster.CreateProposer();
        Task<ChangeOutcome<string>> read = proposer.RecoverAsync(FastBallot.Classic(100_000, Replica(9)), current => current ?? "", cancellationToken);
        cluster.RunToQuiescence();

        ChangeOutcome<string> outcome = await read.ConfigureAwait(false);
        Assert.IsTrue(outcome.IsChosen);

        return outcome.Value!;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
