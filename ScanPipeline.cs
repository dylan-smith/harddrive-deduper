using System.Threading.Channels;

namespace HarddriveDeduper;

/// <summary>
/// Runs the scan as a producer/consumer pipeline: many threads enumerate and hash files into a
/// bounded channel, while a single consumer drains the channel into SQL Server. Returns a process
/// exit code (0 = success, 130 = canceled, 1 = fatal error).
/// </summary>
public sealed class ScanPipeline
{
    private readonly Options _options;
    private readonly FileScanner _scanner;
    private readonly DatabaseWriter _writer;
    private readonly ConsoleReporter _reporter;

    public ScanPipeline(Options options, FileScanner scanner, DatabaseWriter writer, ConsoleReporter reporter)
    {
        _options = options;
        _scanner = scanner;
        _writer = writer;
        _reporter = reporter;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        // Log the run as started; it stays "Running" with no completion time until we stamp it below.
        await _writer.BeginScanAsync(roots, ct);

        // Give producers enough headroom to keep hashing while the consumer is mid-flush.
        const int batchHeadroomFactor = 2;
        var channel = Channel.CreateBounded<FileRecord>(new BoundedChannelOptions(_options.BatchSize * batchHeadroomFactor)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Single consumer: write each record to SQL as it arrives.
        Task consumer = Task.Run(async () =>
        {
            await foreach (FileRecord rec in channel.Reader.ReadAllAsync(ct))
                await _writer.AddAsync(rec, ct);
        });

        await using IAsyncDisposable progress = _reporter.StartProgress(_scanner, _writer);

        try
        {
            // Hash on N threads when hashing is on; otherwise a single producer is enough.
            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.ComputeHash ? _options.Parallelism : 1,
                CancellationToken = ct,
            };

            IEnumerable<FileRecord> allFiles = roots.SelectMany(_scanner.EnumerateFiles);

            await Parallel.ForEachAsync(allFiles, parallelOpts, async (record, token) =>
            {
                _scanner.ComputeHash(record);
                await channel.Writer.WriteAsync(record, token);
            });

            channel.Writer.Complete();
            await consumer;
            await _writer.FlushAsync(CancellationToken.None);
            await _writer.WriteSkipsAsync(_scanner.Skips.ToArray(), CancellationToken.None);
            await _writer.CompleteScanAsync("Completed", null, CancellationToken.None);
            return 0;
        }
        catch (OperationCanceledException)
        {
            // Drain and flush whatever was already buffered before exiting.
            channel.Writer.TryComplete();
            try { await consumer; } catch { /* ignore */ }
            try { await _writer.FlushAsync(CancellationToken.None); } catch { /* ignore */ }
            try { await _writer.WriteSkipsAsync(_scanner.Skips.ToArray(), CancellationToken.None); } catch { /* ignore */ }
            // Record the partial run as canceled; its rows must not be treated as a complete inventory.
            try { await _writer.CompleteScanAsync("Canceled", null, CancellationToken.None); } catch { /* ignore */ }
            return 130;
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            _reporter.ReportFatalError(ex.Message);
            try { await _writer.CompleteScanAsync("Failed", ex.Message, CancellationToken.None); } catch { /* ignore */ }
            return 1;
        }
    }
}
