using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The telemetry surface an application configures against and scrapes.
/// </summary>
/// <remarks>
/// <para>
/// EVERY NAME IS PINNED BY A LITERAL AGAINST WHAT IS ACTUALLY PUBLISHED, never against the constant that
/// declares it. A row comparing a constant with itself passes whatever the constant is changed to, so it
/// would let a rename through while every dashboard, alert and <c>AddMeter</c> call built on the old name
/// stopped resolving. These strings leave the assembly and are as much a published contract as any method
/// signature.
/// </para>
/// <para>
/// Reading the runtime value rather than the constant also catches the failure a constant comparison cannot
/// see at all: a name that is correct in <see cref="VerisyncTelemetry"/> and never reaches an instrument,
/// because the instrument was created on another meter or was never created.
/// </para>
/// <para>
/// The instrument kinds and units are pinned one step further along. A counter that became a histogram, or a
/// byte unit that became a millisecond one, keeps its name and silently changes what every query over it
/// means.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class VerisyncTelemetryContractTests
{
    private const int AttemptsPerRecorder = 2;

    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(61);
    private static ReplicaId Second { get; } = Replica(62);
    private static ReplicaId Third { get; } = Replica(63);

    private static ImmutableArray<ReplicaId> Order { get; } = [First, Second, Third];


    /// <summary>
    /// The two names an application subscribes with, read from the objects it subscribes to.
    /// </summary>
    [TestMethod]
    public void TheMeterAndTheActivitySourceCarryTheirPublishedNames()
    {
        Assert.AreEqual("Lumoin.Verisync", VerisyncActivitySource.Instance.Name);
        Assert.AreEqual("Lumoin.Verisync", Published().Values.First().Meter.Name);

        //Metrics and spans are subscribed to separately and are documented to share one name, so a change to
        //one alone would send half an application's configuration somewhere that emits nothing.
        Assert.AreEqual(VerisyncActivitySource.Instance.Name, Published().Values.First().Meter.Name);
    }


    /// <summary>
    /// Every instrument is published on the library's meter under the name, kind and unit a query is written
    /// against.
    /// </summary>
    [TestMethod]
    public void EveryInstrumentIsPublishedUnderItsNameKindAndUnit()
    {
        Dictionary<string, Instrument> published = Published();

        Assert.Contains("verisync.memory.allocated_bytes", published.Keys);
        Assert.Contains("verisync.memory.lifetime_ms", published.Keys);
        Assert.Contains("verisync.consensus.writes", published.Keys);
        Assert.Contains("verisync.consensus.write.attempts", published.Keys);
        Assert.Contains("verisync.consensus.membership.size", published.Keys);
        Assert.Contains("verisync.consensus.membership.quorum", published.Keys);
        Assert.Contains("verisync.consensus.probes", published.Keys);

        Assert.IsInstanceOfType<Histogram<long>>(published["verisync.memory.allocated_bytes"]);
        Assert.IsInstanceOfType<Histogram<double>>(published["verisync.memory.lifetime_ms"]);
        Assert.IsInstanceOfType<Counter<long>>(published["verisync.consensus.writes"]);
        Assert.IsInstanceOfType<Histogram<int>>(published["verisync.consensus.write.attempts"]);
        Assert.IsInstanceOfType<Gauge<int>>(published["verisync.consensus.membership.size"]);
        Assert.IsInstanceOfType<Gauge<int>>(published["verisync.consensus.membership.quorum"]);
        Assert.IsInstanceOfType<Counter<long>>(published["verisync.consensus.probes"]);

        Assert.AreEqual("By", published["verisync.memory.allocated_bytes"].Unit);
        Assert.AreEqual("ms", published["verisync.memory.lifetime_ms"].Unit);
        Assert.AreEqual("{write}", published["verisync.consensus.writes"].Unit);
        Assert.AreEqual("{attempt}", published["verisync.consensus.write.attempts"].Unit);
        Assert.AreEqual("{member}", published["verisync.consensus.membership.size"].Unit);
        Assert.AreEqual("{member}", published["verisync.consensus.membership.quorum"].Unit);
        Assert.AreEqual("{probe}", published["verisync.consensus.probes"].Unit);
    }


    /// <summary>
    /// The dimensions a consensus measurement actually carries, and the values an application groups by.
    /// </summary>
    /// <remarks>
    /// A dimension's values are as much a contract as its name, because a query filtering on one of them stops
    /// matching when the string moves. The dimensions are read off measurements the library emitted rather
    /// than off the constants naming them.
    /// </remarks>
    [TestMethod]
    public async Task ConsensusMeasurementsCarryTheirPublishedDimensions()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        using Dimensions dimensions = Dimensions.Listening();

        QuePaxaVersionedRegister<string> register = Register(cluster, observeMember: (member, token) => member.Equals(Third)
            ? throw new IOException("This member's transport is down.")
            : new ValueTask<MemberVersionReport>(new MemberVersionReport(Membership.Member(member), RegisterVersion.First)));

        _ = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);
        _ = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

        HashSet<string> write = dimensions.Carried("verisync.consensus.writes");

        Assert.Contains("verisync.consensus.cluster", write);
        Assert.Contains("verisync.consensus.write.status", write);
        Assert.Contains("verisync.consensus.write.fast_path", write);

        HashSet<string> membership = dimensions.Carried("verisync.consensus.membership.size");

        Assert.Contains("verisync.consensus.cluster", membership);

        HashSet<string> probe = dimensions.Carried("verisync.consensus.probes");

        Assert.Contains("verisync.consensus.cluster", probe);
        Assert.Contains("verisync.consensus.member", probe);
        Assert.Contains("verisync.consensus.probe.outcome", probe);

        //The grouping values themselves, read off the probes that were made.
        HashSet<string> outcomes = dimensions.Values("verisync.consensus.probes", "verisync.consensus.probe.outcome");

        Assert.Contains("answered", outcomes);
        Assert.Contains("faulted", outcomes);

        HashSet<string> statuses = dimensions.Values("verisync.consensus.writes", "verisync.consensus.write.status");

        Assert.Contains("Committed", statuses);
    }


    /// <summary>
    /// The spans an application subscribes to, under the names and tags it reads them by.
    /// </summary>
    [TestMethod]
    public async Task ConsensusSpansCarryTheirPublishedNamesAndTags()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        List<Activity> stopped = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "Lumoin.Verisync",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };

        ActivitySource.AddActivityListener(listener);

        QuePaxaVersionedRegister<string> register = Register(cluster, observeMember: (member, token) =>
            new ValueTask<MemberVersionReport>(new MemberVersionReport(Membership.Member(member), RegisterVersion.First)));

        _ = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);
        _ = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

        Activity write = stopped.Single(activity => activity.OperationName == "verisync.consensus.write");

        Assert.IsNotNull(write.GetTagItem("verisync.consensus.cluster"));
        Assert.IsNotNull(write.GetTagItem("verisync.consensus.write.status"));
        Assert.IsNotNull(write.GetTagItem("verisync.consensus.write.fast_path"));
        Assert.IsNotNull(write.GetTagItem("verisync.consensus.write.attempts"));

        Activity readiness = stopped.Single(activity => activity.OperationName == "verisync.consensus.readiness");

        Assert.IsNotNull(readiness.GetTagItem("verisync.consensus.readiness.measured"));
        Assert.IsNotNull(readiness.GetTagItem("verisync.consensus.readiness.reachable"));
    }


    /// <summary>Every instrument the library has published on its own meter, by name.</summary>
    /// <returns>Those instruments.</returns>
    /// <remarks>
    /// The holder is touched before the listener starts, because instruments are created when it is first read
    /// and a listener only enumerates what already exists.
    /// </remarks>
    private static Dictionary<string, Instrument> Published()
    {
        Touch();

        Dictionary<string, Instrument> published = [];
        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, _) =>
            {
                if(instrument.Meter.Name == VerisyncTelemetry.MeterName)
                {
                    published[instrument.Name] = instrument;
                }
            }
        };

        listener.Start();

        return published;
    }


    /// <summary>Forces the instrument holder's initialization.</summary>
    private static void Touch()
    {
        _ = VerisyncMetrics.ConsensusWrites;
        _ = VerisyncMetrics.ConsensusWriteAttempts;
        _ = VerisyncMetrics.ConsensusMembershipSize;
        _ = VerisyncMetrics.ConsensusMembershipQuorum;
        _ = VerisyncMetrics.ConsensusProbes;
        _ = VerisyncMetrics.MemoryAllocatedBytes;
        _ = VerisyncMetrics.MemoryLifetimeMs;
    }


    private static QuePaxaLeaderSchedule Schedule() => new(HedgingSchedule.Create(Order, TimeSpan.Zero));


    private static QuePaxaVersionedRegister<string> Register(VersionedQuePaxaCluster<string> cluster, ObserveMemberVersionDelegate observeMember)
    {
        return new QuePaxaVersionedRegister<string>(
            cluster.Genesis,
            First,
            TimeSpan.Zero,
            cluster.Resolve,
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            TimeProvider.System,
            observeMemberVersion: observeMember);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// Collects the dimension names and values the library's measurements actually carry.
    /// </summary>
    private sealed class Dimensions: IDisposable
    {
        private Lock Gate { get; } = new();

        private Dictionary<string, HashSet<string>> NamesByInstrument { get; } = [];

        private Dictionary<string, HashSet<string>> ValuesByDimension { get; } = [];

        private MeterListener Listener { get; } = new();


        /// <summary>Starts collecting.</summary>
        /// <returns>The collector, which stops when it is disposed.</returns>
        public static Dimensions Listening()
        {
            Dimensions dimensions = new();

            dimensions.Listener.InstrumentPublished = (instrument, listener) =>
            {
                if(instrument.Meter.Name == VerisyncTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            dimensions.Listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => dimensions.Take(instrument, tags));
            dimensions.Listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) => dimensions.Take(instrument, tags));
            dimensions.Listener.Start();

            return dimensions;
        }


        /// <summary>The dimension names measurements of <paramref name="instrument"/> carried.</summary>
        /// <param name="instrument">The instrument name.</param>
        /// <returns>Those dimension names.</returns>
        public HashSet<string> Carried(string instrument)
        {
            lock(Gate)
            {
                return NamesByInstrument.TryGetValue(instrument, out HashSet<string>? carried) ? carried : [];
            }
        }


        /// <summary>The values measurements of <paramref name="instrument"/> carried under <paramref name="dimension"/>.</summary>
        /// <param name="instrument">The instrument name.</param>
        /// <param name="dimension">The dimension name.</param>
        /// <returns>Those values.</returns>
        public HashSet<string> Values(string instrument, string dimension)
        {
            lock(Gate)
            {
                return ValuesByDimension.TryGetValue($"{instrument}|{dimension}", out HashSet<string>? carried) ? carried : [];
            }
        }


        /// <inheritdoc/>
        public void Dispose() => Listener.Dispose();


        private void Take(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock(Gate)
            {
                foreach(KeyValuePair<string, object?> tag in tags)
                {
                    if(!NamesByInstrument.TryGetValue(instrument.Name, out HashSet<string>? carried))
                    {
                        carried = [];
                        NamesByInstrument[instrument.Name] = carried;
                    }

                    _ = carried.Add(tag.Key);

                    if(tag.Value is string text)
                    {
                        string key = $"{instrument.Name}|{tag.Key}";
                        if(!ValuesByDimension.TryGetValue(key, out HashSet<string>? seen))
                        {
                            seen = [];
                            ValuesByDimension[key] = seen;
                        }

                        _ = seen.Add(text);
                    }
                }
            }
        }
    }
}
