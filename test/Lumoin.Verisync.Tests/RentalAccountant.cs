using Lumoin.Base;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A <see cref="MeterListener"/> over the pooled-memory rental ledger, shared by every reconciliation
/// memory-accountability test. It subscribes to <see cref="BaseMemoryPoolMetrics.MeterName"/> on construction
/// and accumulates the pool's rent and return counters by instrument name. The recording callback can fire on
/// the recording thread, so each total moves under <see cref="Interlocked"/> even though the test bodies are
/// single-threaded; the pooled objects are disposed inside the accountant's using-scope before the totals are
/// read, so every emitted measurement has flushed. Disposing stops the listener.
/// </summary>
internal sealed class RentalAccountant: IDisposable
{
    private readonly MeterListener listener;
    private long rented;
    private long returned;


    public RentalAccountant()
    {
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if(instrument.Meter.Name == BaseMemoryPoolMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(OnMeasurement);
        listener.Start();
    }


    public long Rented => Interlocked.Read(ref rented);

    public long Returned => Interlocked.Read(ref returned);

    /// <summary>
    /// The pool emits a +1 rent counter and a +1 return counter per operation; the active-rentals it also
    /// exposes is a pull-gauge the pool force-zeroes on its own disposal, so it cannot witness a leak after the
    /// scope.
    /// </summary>
    /// <remarks>
    /// The emitted rent and return counters can: their difference is the net still-outstanding rentals, which
    /// must be zero once every owner and the pool are disposed.
    /// </remarks>
    public long NetActive => Rented - Returned;


    public void Dispose()
    {
        listener.Dispose();
    }


    private void OnMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if(instrument.Name == BaseMemoryPoolMetrics.BaseMemoryPoolRentOperationsTotal)
        {
            Interlocked.Add(ref rented, measurement);
        }
        else if(instrument.Name == BaseMemoryPoolMetrics.BaseMemoryPoolReturnOperationsTotal)
        {
            Interlocked.Add(ref returned, measurement);
        }
    }
}
