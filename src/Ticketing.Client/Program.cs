using Bogus;
using Grpc.Core;
using Grpc.Net.Client;
using Polly;
using Polly.Retry;
using Serilog;
using System.CommandLine;
using System.Diagnostics;
using Ticketing.Shared.Grpc;

// ============================================================================
// Ticketing Client Simulator
// Simule des validateurs effectuant des validations en parallèle
// ============================================================================

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

Log.Information("Ticketing Client Simulator starting...");

// ============================================================================
// Command line parsing
// ============================================================================
var rootCommand = new RootCommand("Ticketing Client Simulator - Validator load tester");

var serverOption = new Option<string>(
    "--server",
    getDefaultValue: () => "http://localhost:5000",
    description: "gRPC server address");

var usersOption = new Option<int>(
    "--users",
    getDefaultValue: () => 100,
    description: "Number of concurrent users to simulate");

var durationOption = new Option<int>(
    "--duration",
    getDefaultValue: () => 60,
    description: "Test duration in seconds");

var requestsPerSecondOption = new Option<int>(
    "--rps",
    getDefaultValue: () => 100,
    description: "Target requests per second");

var validatorIdOption = new Option<string>(
    "--validator-id",
    getDefaultValue: () => "zone-default",
    description: "Validator group ID");

rootCommand.AddOption(serverOption);
rootCommand.AddOption(usersOption);
rootCommand.AddOption(durationOption);
rootCommand.AddOption(requestsPerSecondOption);
rootCommand.AddOption(validatorIdOption);

rootCommand.SetHandler(async (string server, int users, int duration, int rps, string validatorId) =>
{
    await RunLoadTestAsync(server, users, duration, rps, validatorId);
}, serverOption, usersOption, durationOption, requestsPerSecondOption, validatorIdOption);

return await rootCommand.InvokeAsync(args);

// ============================================================================
// Load test implementation
// ============================================================================
static async Task RunLoadTestAsync(string serverAddress, int userCount, int durationSeconds, int targetRps, string validatorId)
{
    Log.Information(
        "Starting load test: Server={Server}, Users={Users}, Duration={Duration}s, TargetRPS={TargetRPS}, ValidatorId={ValidatorId}",
        serverAddress, userCount, durationSeconds, targetRps, validatorId);

    // Configuration gRPC channel avec pooling et keepalive
    var channelOptions = new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 10
        },
        MaxReceiveMessageSize = 10 * 1024 * 1024, // 10 MB
        MaxSendMessageSize = 10 * 1024 * 1024
    };

    var channel = GrpcChannel.ForAddress(serverAddress, channelOptions);
    var client = new TicketingService.TicketingServiceClient(channel);

    // Test de connexion
    try
    {
        var healthResponse = await client.HealthCheckAsync(new HealthCheckRequest
        {
            ServiceName = "ticketing-api"
        });

        Log.Information(
            "Server health check: Status={Status}, Version={Version}, Uptime={Uptime}s",
            healthResponse.Status,
            healthResponse.Version,
            healthResponse.UptimeSeconds);
    }
    catch (RpcException ex)
    {
        Log.Error(ex, "Failed to connect to server: {Server}", serverAddress);
        return;
    }

    // Génération des données de test (users)
    var faker = new Faker();
    var userIds = Enumerable.Range(1, userCount)
        .Select(i => $"user-{i:D8}")
        .ToList();

    Log.Information("Generated {Count} user IDs", userIds.Count);

    // Pré-chargement des comptes avec du solde
    await PreloadAccountsAsync(client, userIds);

    // Statistiques
    var stats = new LoadTestStats();
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));

    // Démarrage du test de charge
    var sw = Stopwatch.StartNew();
    var tasks = new List<Task>();

    // Calcul du délai inter-requêtes pour atteindre le target RPS
    var delayMs = (int)(1000.0 / targetRps * userCount);

    for (int i = 0; i < userCount; i++)
    {
        var userId = userIds[i];
        tasks.Add(SimulateValidatorAsync(client, userId, validatorId, stats, delayMs, cts.Token));

        // Ramp-up progressif pour éviter le spike initial
        if (i % 10 == 0 && i > 0)
        {
            await Task.Delay(100);
        }
    }

    // Reporting périodique
    var reportingTask = ReportProgressAsync(stats, durationSeconds, cts.Token);

    // Attente de la fin du test
    await Task.WhenAll(tasks);
    cts.Cancel();

    await reportingTask;

    sw.Stop();

    // Rapport final
    PrintFinalReport(stats, sw.Elapsed);

    await channel.ShutdownAsync();
}

/// <summary>
/// Pré-charge les comptes avec du solde initial.
/// </summary>
static async Task PreloadAccountsAsync(TicketingService.TicketingServiceClient client, List<string> userIds)
{
    Log.Information("Preloading {Count} accounts with initial balance...", userIds.Count);

    var tasks = userIds.Select(async userId =>
    {
        try
        {
            await client.RechargeAccountAsync(new RechargeAccountRequest
            {
                UserId = userId,
                Amount = 100.0, // 100€ initial balance
                TransactionId = $"preload-{userId}-{Guid.NewGuid()}",
                CorrelationId = Guid.NewGuid().ToString()
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to preload account: {UserId}", userId);
        }
    });

    await Task.WhenAll(tasks);

    Log.Information("Account preloading completed");
}

/// <summary>
/// Simule un validateur effectuant des validations périodiques.
/// </summary>
static async Task SimulateValidatorAsync(
    TicketingService.TicketingServiceClient client,
    string userId,
    string validatorId,
    LoadTestStats stats,
    int delayMs,
    CancellationToken cancellationToken)
{
    var random = new Random();

    // Retry policy avec backoff exponentiel
    var retryPolicy = Policy
        .Handle<RpcException>(ex => ex.StatusCode == StatusCode.Unavailable || ex.StatusCode == StatusCode.ResourceExhausted)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100),
            onRetry: (exception, timeSpan, retryCount, context) =>
            {
                stats.RecordRetry();
            });

    while (!cancellationToken.IsCancellationRequested)
    {
        var validationId = Guid.NewGuid().ToString();
        var sw = Stopwatch.StartNew();

        try
        {
            var request = new ValidateTicketRequest
            {
                ValidationId = validationId,
                UserId = userId,
                ValidatorId = validatorId,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TicketType = (TicketType)(random.Next(1, 6)), // Random ticket type
                CorrelationId = Guid.NewGuid().ToString()
            };

            request.Metadata.Add("zone", "test");

            var response = await retryPolicy.ExecuteAsync(async () =>
                await client.ValidateTicketAsync(request, cancellationToken: cancellationToken));

            sw.Stop();

            if (response.Status == ValidationStatus.ValidationStatusSuccess)
            {
                stats.RecordSuccess(sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                stats.RecordFailure(sw.Elapsed.TotalMilliseconds);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // Test terminé
            break;
        }
        catch (OperationCanceledException)
        {
            // Test terminé
            break;
        }
        catch (Exception ex)
        {
            sw.Stop();
            stats.RecordError(sw.Elapsed.TotalMilliseconds);
            Log.Debug(ex, "Validation error: UserId={UserId}, ValidationId={ValidationId}", userId, validationId);
        }

        // Délai inter-requêtes (avec jitter)
        var jitter = random.Next(-delayMs / 10, delayMs / 10);
        var actualDelay = Math.Max(10, delayMs + jitter);
        await Task.Delay(actualDelay, cancellationToken);
    }
}

/// <summary>
/// Affiche le progrès périodiquement.
/// </summary>
static async Task ReportProgressAsync(LoadTestStats stats, int durationSeconds, CancellationToken cancellationToken)
{
    var interval = TimeSpan.FromSeconds(5);

    while (!cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(interval, cancellationToken);

        var snapshot = stats.GetSnapshot();

        Log.Information(
            "Progress: Total={Total}, Success={Success}, Failed={Failed}, Errors={Errors}, " +
            "RPS={RPS:F2}, AvgLatency={AvgLatency:F2}ms, P95={P95:F2}ms, P99={P99:F2}ms",
            snapshot.TotalRequests,
            snapshot.SuccessCount,
            snapshot.FailureCount,
            snapshot.ErrorCount,
            snapshot.RequestsPerSecond,
            snapshot.AverageLatencyMs,
            snapshot.P95LatencyMs,
            snapshot.P99LatencyMs);
    }
}

/// <summary>
/// Affiche le rapport final.
/// </summary>
static void PrintFinalReport(LoadTestStats stats, TimeSpan duration)
{
    var snapshot = stats.GetSnapshot();

    Log.Information("=".PadRight(80, '='));
    Log.Information("LOAD TEST COMPLETED");
    Log.Information("=".PadRight(80, '='));
    Log.Information("Duration:           {Duration:F2}s", duration.TotalSeconds);
    Log.Information("Total Requests:     {Total}", snapshot.TotalRequests);
    Log.Information("Successful:         {Success} ({SuccessRate:F2}%)",
        snapshot.SuccessCount,
        snapshot.SuccessCount * 100.0 / Math.Max(1, snapshot.TotalRequests));
    Log.Information("Failed:             {Failed}", snapshot.FailureCount);
    Log.Information("Errors:             {Errors}", snapshot.ErrorCount);
    Log.Information("Retries:            {Retries}", snapshot.RetryCount);
    Log.Information("Requests/sec:       {RPS:F2}", snapshot.RequestsPerSecond);
    Log.Information("");
    Log.Information("Latency Statistics:");
    Log.Information("  Average:          {Avg:F2} ms", snapshot.AverageLatencyMs);
    Log.Information("  Min:              {Min:F2} ms", snapshot.MinLatencyMs);
    Log.Information("  Max:              {Max:F2} ms", snapshot.MaxLatencyMs);
    Log.Information("  P50 (median):     {P50:F2} ms", snapshot.P50LatencyMs);
    Log.Information("  P95:              {P95:F2} ms", snapshot.P95LatencyMs);
    Log.Information("  P99:              {P99:F2} ms", snapshot.P99LatencyMs);
    Log.Information("  P99.9:            {P999:F2} ms", snapshot.P999LatencyMs);
    Log.Information("=".PadRight(80, '='));
}

// ============================================================================
// Load test statistics (thread-safe)
// ============================================================================
class LoadTestStats
{
    private long _successCount;
    private long _failureCount;
    private long _errorCount;
    private long _retryCount;
    private readonly List<double> _latencies = new();
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public void RecordSuccess(double latencyMs)
    {
        Interlocked.Increment(ref _successCount);
        RecordLatency(latencyMs);
    }

    public void RecordFailure(double latencyMs)
    {
        Interlocked.Increment(ref _failureCount);
        RecordLatency(latencyMs);
    }

    public void RecordError(double latencyMs)
    {
        Interlocked.Increment(ref _errorCount);
        RecordLatency(latencyMs);
    }

    public void RecordRetry()
    {
        Interlocked.Increment(ref _retryCount);
    }

    private void RecordLatency(double latencyMs)
    {
        lock (_lock)
        {
            _latencies.Add(latencyMs);
        }
    }

    public StatsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var totalRequests = _successCount + _failureCount + _errorCount;
            var elapsed = _sw.Elapsed.TotalSeconds;

            var sortedLatencies = _latencies.OrderBy(x => x).ToList();

            return new StatsSnapshot
            {
                TotalRequests = totalRequests,
                SuccessCount = _successCount,
                FailureCount = _failureCount,
                ErrorCount = _errorCount,
                RetryCount = _retryCount,
                RequestsPerSecond = totalRequests / Math.Max(1, elapsed),
                AverageLatencyMs = sortedLatencies.Any() ? sortedLatencies.Average() : 0,
                MinLatencyMs = sortedLatencies.Any() ? sortedLatencies.First() : 0,
                MaxLatencyMs = sortedLatencies.Any() ? sortedLatencies.Last() : 0,
                P50LatencyMs = Percentile(sortedLatencies, 0.50),
                P95LatencyMs = Percentile(sortedLatencies, 0.95),
                P99LatencyMs = Percentile(sortedLatencies, 0.99),
                P999LatencyMs = Percentile(sortedLatencies, 0.999)
            };
        }
    }

    private static double Percentile(List<double> sortedValues, double percentile)
    {
        if (!sortedValues.Any()) return 0;

        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));

        return sortedValues[index];
    }
}

record StatsSnapshot
{
    public long TotalRequests { get; init; }
    public long SuccessCount { get; init; }
    public long FailureCount { get; init; }
    public long ErrorCount { get; init; }
    public long RetryCount { get; init; }
    public double RequestsPerSecond { get; init; }
    public double AverageLatencyMs { get; init; }
    public double MinLatencyMs { get; init; }
    public double MaxLatencyMs { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double P99LatencyMs { get; init; }
    public double P999LatencyMs { get; init; }
}
