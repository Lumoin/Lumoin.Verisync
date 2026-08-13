using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// A placement of replicas: the region each replica sits in and the one-way delay, in microseconds, between
/// every ordered pair of them.
/// </summary>
/// <remarks>
/// <para>
/// The matrix is symmetric and its diagonal is the intra-region delay rather than zero, because a pair of
/// replicas in one region is a real link and a deployment that puts two replicas in one place still pays for
/// the hop between them. Delays are microseconds throughout, so a rack-scale placement and a global one are
/// the same kind of object.
/// </para>
/// <para>
/// The provenance travels with the matrix and is printed beside every number derived from it, because a
/// modelled figure and a measured median must never be read as the same kind of evidence.
/// </para>
/// </remarks>
internal sealed class Topology
{
    private readonly long[][] oneWay;


    /// <summary>
    /// Initializes a placement.
    /// </summary>
    /// <param name="name">The tier name the grid keys on.</param>
    /// <param name="provenance">Where the figures come from, stated as measurement or as modelling choice.</param>
    /// <param name="siteRegions">The region each replica sits in, in replica index order.</param>
    /// <param name="oneWayMicroseconds">The one-way delay matrix in microseconds.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/>, <paramref name="provenance"/> or <paramref name="oneWayMicroseconds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the matrix is not square over the regions, is not symmetric, or carries a negative delay.</exception>
    public Topology(string name, string provenance, ImmutableArray<string> siteRegions, long[][] oneWayMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(oneWayMicroseconds);
        if(siteRegions.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A placement needs at least one replica.", nameof(siteRegions));
        }

        if(oneWayMicroseconds.Length != siteRegions.Length)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The delay matrix has {oneWayMicroseconds.Length} rows against {siteRegions.Length} replicas."), nameof(oneWayMicroseconds));
        }

        for(int from = 0; from < oneWayMicroseconds.Length; from++)
        {
            if(oneWayMicroseconds[from].Length != siteRegions.Length)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Row {from} of the delay matrix has {oneWayMicroseconds[from].Length} entries against {siteRegions.Length} replicas."), nameof(oneWayMicroseconds));
            }

            for(int to = 0; to < oneWayMicroseconds.Length; to++)
            {
                if(oneWayMicroseconds[from][to] < 0)
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The delay from {from} to {to} is negative."), nameof(oneWayMicroseconds));
                }

                if(oneWayMicroseconds[from][to] != oneWayMicroseconds[to][from])
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The delay matrix is asymmetric at ({from},{to}): {oneWayMicroseconds[from][to]}us against {oneWayMicroseconds[to][from]}us."), nameof(oneWayMicroseconds));
                }
            }
        }

        Name = name;
        Provenance = provenance;
        SiteRegions = siteRegions;
        this.oneWay = oneWayMicroseconds;
    }


    /// <summary>The tier name the grid keys on.</summary>
    public string Name { get; }

    /// <summary>Where the figures come from.</summary>
    public string Provenance { get; }

    /// <summary>The region each replica sits in, in replica index order.</summary>
    public ImmutableArray<string> SiteRegions { get; }

    /// <summary>The number of replicas this placement holds.</summary>
    public int SiteCount => SiteRegions.Length;


    /// <summary>The one-way delay, in microseconds, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="from">The sending replica index.</param>
    /// <param name="to">The receiving replica index.</param>
    /// <returns>The delay in microseconds.</returns>
    public long OneWay(int from, int to) => oneWay[from][to];


    /// <summary>
    /// The one-way delays from <paramref name="site"/> to every replica, in ascending order, which is what
    /// every quorum radius is read off.
    /// </summary>
    /// <param name="site">The replica index the radius is measured from.</param>
    /// <returns>The sorted delays in microseconds.</returns>
    public ImmutableArray<long> SortedRadii(int site) => [.. oneWay[site].Order()];
}
