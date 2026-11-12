using System.Diagnostics;

namespace RdpProxy;

// Opt-in aggregates only: no destinations, credentials, payloads or per-frame logging.
internal sealed class TransferMetrics
{
    public bool Enabled { get; } = Environment.GetEnvironmentVariable("RDPPROXY_TIMING") == "1";
    private readonly DurationHistogram? queue;
    private readonly DurationHistogram? write;
    private readonly DurationHistogram? credit;

    public TransferMetrics()
    {
        if (!Enabled) return;
        queue = new DurationHistogram();
        write = new DurationHistogram();
        credit = new DurationHistogram();
    }

    public long Timestamp() => Enabled ? Stopwatch.GetTimestamp() : 0;
    public void RecordQueueDelay(long started) { if (started != 0) queue?.Record(started); }
    public void RecordWriteDuration(long started) { if (started != 0) write?.Record(started); }
    public void RecordCreditWait(long started) { if (started != 0) credit?.Record(started); }

    public object? Snapshot() => !Enabled ? null : new
    {
        QueueToWrite = queue!.Snapshot(),
        NativeWrite = write!.Snapshot(),
        CreditWait = credit!.Snapshot()
    };

    private sealed class DurationHistogram
    {
        // Bucket upper bounds: 1, 2, 4, ... microseconds; final bucket is unbounded.
        private readonly long[] buckets = new long[48];
        private long count;
        private long totalTicks;
        private long maxTicks;

        public void Record(long started)
        {
            long ticks = Math.Max(0, Stopwatch.GetTimestamp() - started);
            double microseconds = ticks * (1_000_000.0 / Stopwatch.Frequency);
            int bucket = 0;
            double upper = 1;
            while (bucket < buckets.Length - 1 && microseconds > upper) { bucket++; upper *= 2; }
            Interlocked.Increment(ref buckets[bucket]);
            Interlocked.Add(ref totalTicks, ticks);
            long previous = Interlocked.Read(ref maxTicks);
            while (ticks > previous)
            {
                long observed = Interlocked.CompareExchange(ref maxTicks, ticks, previous);
                if (observed == previous) break;
                previous = observed;
            }
            Interlocked.Increment(ref count);
        }

        public object Snapshot()
        {
            long samples = Interlocked.Read(ref count);
            double scale = 1000.0 / Stopwatch.Frequency;
            double maximum = Interlocked.Read(ref maxTicks) * scale;
            var histogram = new long[buckets.Length];
            long histogramSamples = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                histogram[i] = Interlocked.Read(ref buckets[i]);
                histogramSamples += histogram[i];
            }
            return new
            {
                Count = samples,
                MeanMs = samples == 0 ? 0 : Interlocked.Read(ref totalTicks) * scale / samples,
                MaxMs = maximum,
                P50UpperMs = Percentile(histogram, histogramSamples, .50),
                P95UpperMs = Percentile(histogram, histogramSamples, .95),
                P99UpperMs = Percentile(histogram, histogramSamples, .99)
            };
        }

        private static double Percentile(long[] histogram, long samples, double percentile)
        {
            if (samples == 0) return 0;
            long rank = (long)Math.Ceiling(samples * percentile);
            long accumulated = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                accumulated += histogram[i];
                if (accumulated >= rank) return Math.Pow(2, i) / 1000;
            }
            return 0;
        }
    }
}
