using System.Diagnostics;
using System.Diagnostics.Metrics;

internal sealed class WorkerService(IClusterClient client, ILogger<WorkerService> logger, ILocalSiloDetails localSiloDetails) : BackgroundService
{
    public const string InstrumentName = "WorkerService";
    private static readonly Meter Meter = new(InstrumentName);
    private static readonly Counter<int> Success = Meter.CreateCounter<int>(nameof(Success), unit: "{requests}");
    private static readonly Counter<int> Failure = Meter.CreateCounter<int>(nameof(Failure), unit: "{requests}");
    private static readonly Histogram<long> SuccessDurationMs = Meter.CreateHistogram<long>(nameof(SuccessDurationMs), unit: "{ms}");
    private static readonly Histogram<long> FailureDurationMs = Meter.CreateHistogram<long>(nameof(FailureDurationMs), unit: "{ms}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int NumGrains = 1000;
        var host = localSiloDetails.DnsHostName;

        try
        {
            logger.LogInformation("Starting worker on host {Host}", host);
            var grains = new List<IPingGrain>(NumGrains);
            for (var i = 0; i < NumGrains; i++)
            {
                grains.Add(client.GetGrain<IPingGrain>($"ping-{host}-{i}"));
            }

            var grainNum = 0;
            Stopwatch stopwatch = new();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    stopwatch.Restart();
                    await grains[grainNum].Ping();
                    grainNum = (grainNum + 1) % grains.Count;
                    Success.Add(1);
                    SuccessDurationMs.Record(stopwatch.ElapsedMilliseconds);
                }
                catch (Exception exception)
                {
                    Failure.Add(1);
                    FailureDurationMs.Record(stopwatch.ElapsedMilliseconds);
                    logger.LogError(exception, "Error in worker.");
                }
                finally
                {
                    await Task.Delay(15, stoppingToken);
                }
            }
        }
        finally
        {
            logger.LogInformation("Stopping worker on host {Host}", host);
        }
    }
}
