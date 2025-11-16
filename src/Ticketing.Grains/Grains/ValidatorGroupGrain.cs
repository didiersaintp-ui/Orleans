using Microsoft.Extensions.Logging;
using Orleans;
using System.Diagnostics;
using Ticketing.Grains.Interfaces;
using Ticketing.Shared.Grpc;

namespace Ticketing.Grains.Grains;

/// <summary>
/// Grain représentant un groupe de validateurs (zone géographique).
/// Optimise le routage et permet le batching des validations.
/// </summary>
public class ValidatorGroupGrain : Grain, IValidatorGroupGrain
{
    private readonly ILogger<ValidatorGroupGrain> _logger;
    private readonly IGrainFactory _grainFactory;

    // État en mémoire (non persisté - statistiques en temps réel)
    private ValidatorGroupState _state = new();

    public ValidatorGroupGrain(
        ILogger<ValidatorGroupGrain> logger,
        IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var groupId = this.GetPrimaryKeyString();

        _state = new ValidatorGroupState
        {
            ValidatorGroupId = groupId,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };

        _logger.LogInformation(
            "ValidatorGroupGrain activated: GroupId={GroupId}",
            groupId);

        return base.OnActivateAsync(cancellationToken);
    }

    public async ValueTask<ValidateTicketResponse> ProcessValidationAsync(ValidateTicketRequest request)
    {
        var sw = Stopwatch.StartNew();
        var groupId = this.GetPrimaryKeyString();

        _logger.LogDebug(
            "Processing validation: GroupId={GroupId}, UserId={UserId}, ValidationId={ValidationId}",
            groupId,
            request.UserId,
            request.ValidationId);

        try
        {
            // Routage vers le AccountGrain correspondant
            var accountGrain = _grainFactory.GetGrain<IAccountGrain>(request.UserId);
            var response = await accountGrain.ValidateTicketAsync(request);

            // Mise à jour des statistiques
            UpdateStats(request.TicketType, response.Status, sw.Elapsed.TotalMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing validation: GroupId={GroupId}, ValidationId={ValidationId}",
                groupId,
                request.ValidationId);

            _state.FailedValidations++;

            return new ValidateTicketResponse
            {
                ValidationId = request.ValidationId,
                Status = ValidationStatus.ValidationStatusSystemError,
                ErrorMessage = $"System error: {ex.Message}",
                ErrorCode = ErrorCode.ErrorCodeInternalError,
                ProcessedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ProcessingLatencyMs = sw.Elapsed.TotalMilliseconds,
                GrainId = $"ValidatorGroupGrain-{groupId}"
            };
        }
    }

    public async ValueTask<BatchValidationResponse> ProcessBatchAsync(IReadOnlyList<ValidateTicketRequest> requests)
    {
        var sw = Stopwatch.StartNew();
        var groupId = this.GetPrimaryKeyString();

        _logger.LogInformation(
            "Processing batch: GroupId={GroupId}, BatchSize={BatchSize}",
            groupId,
            requests.Count);

        var results = new List<ValidateTicketResponse>(requests.Count);
        var latencies = new List<double>(requests.Count);
        int successCount = 0;
        int failureCount = 0;

        // Traitement parallèle des validations (avec limite de concurrence)
        // Limite à 100 validations concurrentes pour éviter surcharge
        var semaphore = new SemaphoreSlim(100);
        var tasks = requests.Select(async request =>
        {
            await semaphore.WaitAsync();
            try
            {
                var requestSw = Stopwatch.StartNew();
                var accountGrain = _grainFactory.GetGrain<IAccountGrain>(request.UserId);
                var response = await accountGrain.ValidateTicketAsync(request);
                requestSw.Stop();

                latencies.Add(requestSw.Elapsed.TotalMilliseconds);

                if (response.Status == ValidationStatus.ValidationStatusSuccess)
                {
                    Interlocked.Increment(ref successCount);
                }
                else
                {
                    Interlocked.Increment(ref failureCount);
                }

                return response;
            }
            finally
            {
                semaphore.Release();
            }
        });

        results.AddRange(await Task.WhenAll(tasks));

        sw.Stop();

        // Calcul des statistiques de latence
        var averageLatency = latencies.Any() ? latencies.Average() : 0;
        var p99Latency = latencies.Any() ? CalculatePercentile(latencies, 0.99) : 0;

        // Mise à jour des statistiques du groupe
        _state.TotalValidations += requests.Count;
        _state.SuccessfulValidations += successCount;
        _state.FailedValidations += failureCount;
        _state.LastAccessedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Batch processed: GroupId={GroupId}, Total={Total}, Success={Success}, Failed={Failed}, " +
            "AvgLatency={AvgLatency}ms, P99Latency={P99Latency}ms, TotalTime={TotalTime}ms",
            groupId,
            requests.Count,
            successCount,
            failureCount,
            averageLatency,
            p99Latency,
            sw.Elapsed.TotalMilliseconds);

        return new BatchValidationResponse
        {
            TotalProcessed = requests.Count,
            SuccessCount = successCount,
            FailureCount = failureCount,
            AverageLatencyMs = averageLatency,
            P99LatencyMs = p99Latency
            // results intentionnellement vide pour économiser bande passante
            // (peut être activé pour debugging)
        };
    }

    public ValueTask<ValidatorGroupStats> GetStatsAsync()
    {
        var groupId = this.GetPrimaryKeyString();

        var stats = new ValidatorGroupStats
        {
            ValidatorGroupId = groupId,
            TotalValidations = _state.TotalValidations,
            SuccessfulValidations = _state.SuccessfulValidations,
            FailedValidations = _state.FailedValidations,
            AverageLatencyMs = _state.TotalLatencyMs / Math.Max(_state.TotalValidations, 1),
            LastValidationTimestampMs = _state.LastValidationTimestampMs,
            CreatedAt = _state.CreatedAt,
            LastAccessedAt = _state.LastAccessedAt,
            ValidationsByTicketType = new Dictionary<string, long>(_state.ValidationsByTicketType)
        };

        return ValueTask.FromResult(stats);
    }

    public ValueTask ResetStatsAsync()
    {
        var groupId = this.GetPrimaryKeyString();

        _logger.LogInformation(
            "Resetting stats: GroupId={GroupId}",
            groupId);

        _state.TotalValidations = 0;
        _state.SuccessfulValidations = 0;
        _state.FailedValidations = 0;
        _state.TotalLatencyMs = 0;
        _state.LastValidationTimestampMs = 0;
        _state.ValidationsByTicketType.Clear();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Met à jour les statistiques du groupe après une validation.
    /// </summary>
    private void UpdateStats(TicketType ticketType, ValidationStatus status, double latencyMs)
    {
        _state.TotalValidations++;
        _state.TotalLatencyMs += latencyMs;
        _state.LastValidationTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _state.LastAccessedAt = DateTime.UtcNow;

        if (status == ValidationStatus.ValidationStatusSuccess)
        {
            _state.SuccessfulValidations++;
        }
        else
        {
            _state.FailedValidations++;
        }

        // Statistiques par type de ticket
        var ticketTypeName = ticketType.ToString();
        if (!_state.ValidationsByTicketType.ContainsKey(ticketTypeName))
        {
            _state.ValidationsByTicketType[ticketTypeName] = 0;
        }
        _state.ValidationsByTicketType[ticketTypeName]++;
    }

    /// <summary>
    /// Calcule le percentile d'une liste de latences.
    /// </summary>
    private static double CalculatePercentile(List<double> latencies, double percentile)
    {
        if (!latencies.Any()) return 0;

        var sorted = latencies.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        index = Math.Max(0, Math.Min(index, sorted.Count - 1));

        return sorted[index];
    }
}

/// <summary>
/// État en mémoire du ValidatorGroupGrain (non persisté).
/// </summary>
internal class ValidatorGroupState
{
    public string ValidatorGroupId { get; set; } = string.Empty;
    public long TotalValidations { get; set; }
    public long SuccessfulValidations { get; set; }
    public long FailedValidations { get; set; }
    public double TotalLatencyMs { get; set; }
    public long LastValidationTimestampMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public Dictionary<string, long> ValidationsByTicketType { get; set; } = new();
}
