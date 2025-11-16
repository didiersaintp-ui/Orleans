using Grpc.Core;
using Microsoft.Extensions.Logging;
using Orleans;
using System.Diagnostics;
using Ticketing.Grains.Interfaces;
using Ticketing.Shared.Grpc;

namespace Ticketing.Api.Services;

/// <summary>
/// Implémentation du service gRPC TicketingService.
/// Stateless API layer qui invoque les grains Orleans.
/// </summary>
public class TicketingGrpcService : TicketingService.TicketingServiceBase
{
    private readonly ILogger<TicketingGrpcService> _logger;
    private readonly IClusterClient _clusterClient;
    private readonly IMetricsService _metrics;
    private static readonly DateTime _startTime = DateTime.UtcNow;

    public TicketingGrpcService(
        ILogger<TicketingGrpcService> logger,
        IClusterClient clusterClient,
        IMetricsService metrics)
    {
        _logger = logger;
        _clusterClient = clusterClient;
        _metrics = metrics;
    }

    /// <summary>
    /// Validation unitaire d'un ticket (unary RPC).
    /// </summary>
    public override async Task<ValidateTicketResponse> ValidateTicket(
        ValidateTicketRequest request,
        ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "ValidateTicket called: UserId={UserId}, ValidationId={ValidationId}, CorrelationId={CorrelationId}",
            request.UserId,
            request.ValidationId,
            request.CorrelationId);

        try
        {
            // Validation des inputs
            if (string.IsNullOrEmpty(request.UserId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "UserId is required"));
            }

            if (string.IsNullOrEmpty(request.ValidationId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "ValidationId is required"));
            }

            // Routage via ValidatorGroupGrain (pour agrégation & stats) ou direct AccountGrain
            // Option 1: Via ValidatorGroupGrain (recommandé pour stats par zone)
            if (!string.IsNullOrEmpty(request.ValidatorId))
            {
                var validatorGroupGrain = _clusterClient.GetGrain<IValidatorGroupGrain>(request.ValidatorId);
                var response = await validatorGroupGrain.ProcessValidationAsync(request);

                _metrics.RecordValidation(response.Status, sw.Elapsed.TotalMilliseconds);

                return response;
            }

            // Option 2: Direct vers AccountGrain (bypass du groupe)
            var accountGrain = _clusterClient.GetGrain<IAccountGrain>(request.UserId);
            var directResponse = await accountGrain.ValidateTicketAsync(request);

            _metrics.RecordValidation(directResponse.Status, sw.Elapsed.TotalMilliseconds);

            return directResponse;
        }
        catch (RpcException)
        {
            _metrics.RecordError("validation");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in ValidateTicket: UserId={UserId}, ValidationId={ValidationId}",
                request.UserId,
                request.ValidationId);

            _metrics.RecordError("validation");

            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Validation en batch (client streaming → unary).
    /// </summary>
    public override async Task<BatchValidationResponse> ValidateTicketBatch(
        IAsyncStreamReader<ValidateTicketRequest> requestStream,
        ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();
        var requests = new List<ValidateTicketRequest>();

        _logger.LogInformation("ValidateTicketBatch started");

        try
        {
            // Lecture du stream (avec limite pour éviter OOM)
            const int maxBatchSize = 10000;

            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                requests.Add(request);

                if (requests.Count >= maxBatchSize)
                {
                    _logger.LogWarning(
                        "Batch size limit reached: {MaxBatchSize}. Stopping stream read.",
                        maxBatchSize);
                    break;
                }
            }

            _logger.LogInformation(
                "ValidateTicketBatch: Received {Count} requests",
                requests.Count);

            if (requests.Count == 0)
            {
                return new BatchValidationResponse
                {
                    TotalProcessed = 0,
                    SuccessCount = 0,
                    FailureCount = 0
                };
            }

            // Grouper par ValidatorGroupId pour batch processing optimisé
            var groupedRequests = requests
                .GroupBy(r => r.ValidatorId ?? "default")
                .ToList();

            var allResponses = new List<BatchValidationResponse>();

            foreach (var group in groupedRequests)
            {
                var validatorGroupGrain = _clusterClient.GetGrain<IValidatorGroupGrain>(group.Key);
                var batchResponse = await validatorGroupGrain.ProcessBatchAsync(group.ToList());
                allResponses.Add(batchResponse);
            }

            // Agrégation des résultats
            var aggregated = new BatchValidationResponse
            {
                TotalProcessed = allResponses.Sum(r => r.TotalProcessed),
                SuccessCount = allResponses.Sum(r => r.SuccessCount),
                FailureCount = allResponses.Sum(r => r.FailureCount),
                AverageLatencyMs = allResponses.Average(r => r.AverageLatencyMs),
                P99LatencyMs = allResponses.Max(r => r.P99LatencyMs)
            };

            _logger.LogInformation(
                "ValidateTicketBatch completed: Total={Total}, Success={Success}, Failed={Failed}, " +
                "AvgLatency={AvgLatency}ms, TotalTime={TotalTime}ms",
                aggregated.TotalProcessed,
                aggregated.SuccessCount,
                aggregated.FailureCount,
                aggregated.AverageLatencyMs,
                sw.Elapsed.TotalMilliseconds);

            return aggregated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ValidateTicketBatch");
            _metrics.RecordError("batch_validation");
            throw new RpcException(new Status(StatusCode.Internal, $"Batch validation failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Validation en stream bidirectionnel (temps réel).
    /// </summary>
    public override async Task ValidateTicketStream(
        IAsyncStreamReader<ValidateTicketRequest> requestStream,
        IServerStreamWriter<ValidateTicketResponse> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("ValidateTicketStream started");

        try
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                var sw = Stopwatch.StartNew();

                try
                {
                    // Validation en temps réel (unary call par requête)
                    var response = await ValidateTicket(request, context);

                    // Envoyer la réponse immédiatement
                    await responseStream.WriteAsync(response, context.CancellationToken);

                    _logger.LogDebug(
                        "Stream response sent: ValidationId={ValidationId}, Latency={Latency}ms",
                        request.ValidationId,
                        sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error processing stream request: ValidationId={ValidationId}",
                        request.ValidationId);

                    // Envoyer une réponse d'erreur
                    await responseStream.WriteAsync(new ValidateTicketResponse
                    {
                        ValidationId = request.ValidationId,
                        Status = ValidationStatus.ValidationStatusSystemError,
                        ErrorMessage = $"Error: {ex.Message}",
                        ErrorCode = ErrorCode.ErrorCodeInternalError
                    }, context.CancellationToken);
                }
            }

            _logger.LogInformation("ValidateTicketStream completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ValidateTicketStream");
            throw new RpcException(new Status(StatusCode.Internal, $"Stream error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Récupération du solde d'un compte.
    /// </summary>
    public override async Task<GetAccountBalanceResponse> GetAccountBalance(
        GetAccountBalanceRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "GetAccountBalance called: UserId={UserId}, CorrelationId={CorrelationId}",
            request.UserId,
            request.CorrelationId);

        try
        {
            if (string.IsNullOrEmpty(request.UserId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "UserId is required"));
            }

            var accountGrain = _clusterClient.GetGrain<IAccountGrain>(request.UserId);
            var response = await accountGrain.GetBalanceAsync();

            return response;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetAccountBalance: UserId={UserId}", request.UserId);
            throw new RpcException(new Status(StatusCode.Internal, $"Error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Rechargement d'un compte.
    /// </summary>
    public override async Task<RechargeAccountResponse> RechargeAccount(
        RechargeAccountRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "RechargeAccount called: UserId={UserId}, Amount={Amount}, TransactionId={TransactionId}",
            request.UserId,
            request.Amount,
            request.TransactionId);

        try
        {
            if (string.IsNullOrEmpty(request.UserId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "UserId is required"));
            }

            if (request.Amount <= 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Amount must be positive"));
            }

            if (string.IsNullOrEmpty(request.TransactionId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "TransactionId is required"));
            }

            var accountGrain = _clusterClient.GetGrain<IAccountGrain>(request.UserId);
            var response = await accountGrain.RechargeAsync(request);

            return response;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RechargeAccount: UserId={UserId}", request.UserId);
            throw new RpcException(new Status(StatusCode.Internal, $"Error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Health check endpoint.
    /// </summary>
    public override Task<HealthCheckResponse> HealthCheck(
        HealthCheckRequest request,
        ServerCallContext context)
    {
        var uptime = (long)(DateTime.UtcNow - _startTime).TotalSeconds;

        // TODO: Obtenir le nombre de grains actifs depuis Orleans
        var activeGrains = 0; // _clusterClient.GetGrainCount() si disponible

        var response = new HealthCheckResponse
        {
            Status = HealthStatus.HealthStatusHealthy,
            Version = "1.0.0",
            UptimeSeconds = uptime,
            ActiveGrains = activeGrains
        };

        response.Metrics.Add("uptime_seconds", uptime.ToString());
        response.Metrics.Add("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());

        return Task.FromResult(response);
    }
}

/// <summary>
/// Service de métriques pour instrumentation.
/// </summary>
public interface IMetricsService
{
    void RecordValidation(ValidationStatus status, double latencyMs);
    void RecordError(string operation);
}

/// <summary>
/// Implémentation basique du service de métriques.
/// En production, utiliser OpenTelemetry Metrics API.
/// </summary>
public class MetricsService : IMetricsService
{
    private readonly ILogger<MetricsService> _logger;

    public MetricsService(ILogger<MetricsService> logger)
    {
        _logger = logger;
    }

    public void RecordValidation(ValidationStatus status, double latencyMs)
    {
        // TODO: Envoyer vers OpenTelemetry metrics
        _logger.LogDebug(
            "Validation metric: Status={Status}, Latency={Latency}ms",
            status,
            latencyMs);
    }

    public void RecordError(string operation)
    {
        // TODO: Envoyer vers OpenTelemetry metrics
        _logger.LogWarning(
            "Error metric: Operation={Operation}",
            operation);
    }
}
