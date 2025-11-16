using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using System.Diagnostics;
using Ticketing.Grains.Interfaces;
using Ticketing.Shared.Grpc;

namespace Ticketing.Grains.Grains;

/// <summary>
/// Implémentation du grain AccountGrain avec persistence Redis.
/// Gère l'état du compte, l'idempotence, et les validations de tickets.
/// </summary>
public class AccountGrain : Grain, IAccountGrain
{
    private readonly ILogger<AccountGrain> _logger;
    private readonly IPersistentState<AccountState> _state;

    // Configuration des coûts de tickets (à externaliser en config réelle)
    private static readonly Dictionary<TicketType, double> TicketPrices = new()
    {
        { TicketType.TicketTypeSingleJourney, 2.50 },
        { TicketType.TicketTypeDayPass, 10.00 },
        { TicketType.TicketTypeWeekPass, 40.00 },
        { TicketType.TicketTypeMonthPass, 120.00 },
        { TicketType.TicketTypeAnnualPass, 1200.00 }
    };

    // Fenêtre de déduplication: 5 minutes
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(5);

    public AccountGrain(
        ILogger<AccountGrain> logger,
        [PersistentState("account", "RedisStore")] IPersistentState<AccountState> state)
    {
        _logger = logger;
        _state = state;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var userId = this.GetPrimaryKeyString();

        // Initialisation si nouveau compte
        if (!_state.RecordExists)
        {
            _state.State = new AccountState
            {
                UserId = userId,
                Balance = 0.0,
                Status = AccountStatus.AccountStatusActive,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                TotalValidations = 0,
                DeduplicatedCount = 0,
                RecentValidations = new Dictionary<string, long>()
            };

            _logger.LogInformation(
                "AccountGrain activated for new user {UserId} at {Timestamp}",
                userId,
                DateTime.UtcNow);
        }
        else
        {
            _logger.LogInformation(
                "AccountGrain activated for existing user {UserId}, Balance={Balance}, Status={Status}",
                userId,
                _state.State.Balance,
                _state.State.Status);
        }

        // Mise à jour du dernier accès
        _state.State.LastAccessedAt = DateTime.UtcNow;

        return base.OnActivateAsync(cancellationToken);
    }

    public async ValueTask<ValidateTicketResponse> ValidateTicketAsync(ValidateTicketRequest request)
    {
        var sw = Stopwatch.StartNew();
        var userId = this.GetPrimaryKeyString();

        _logger.LogInformation(
            "Validating ticket: UserId={UserId}, ValidationId={ValidationId}, TicketType={TicketType}, CorrelationId={CorrelationId}",
            userId,
            request.ValidationId,
            request.TicketType,
            request.CorrelationId);

        try
        {
            // 1. Vérification de l'idempotence (déduplication)
            if (IsDuplicateValidation(request.ValidationId, request.TimestampMs))
            {
                _state.State.DeduplicatedCount++;
                await _state.WriteStateAsync(); // Persister le compteur de déduplication

                _logger.LogWarning(
                    "Duplicate validation detected: UserId={UserId}, ValidationId={ValidationId}",
                    userId,
                    request.ValidationId);

                return new ValidateTicketResponse
                {
                    ValidationId = request.ValidationId,
                    Status = ValidationStatus.ValidationStatusDuplicateValidation,
                    NewBalance = _state.State.Balance,
                    RemainingTickets = 0,
                    ErrorMessage = "Duplicate validation (idempotence check)",
                    ErrorCode = ErrorCode.ErrorCodeInvalidRequest,
                    ProcessedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ProcessingLatencyMs = sw.Elapsed.TotalMilliseconds,
                    GrainId = $"AccountGrain-{userId}"
                };
            }

            // 2. Vérification du statut du compte
            if (_state.State.Status == AccountStatus.AccountStatusSuspended)
            {
                _logger.LogWarning(
                    "Account suspended: UserId={UserId}, ValidationId={ValidationId}",
                    userId,
                    request.ValidationId);

                return new ValidateTicketResponse
                {
                    ValidationId = request.ValidationId,
                    Status = ValidationStatus.ValidationStatusAccountSuspended,
                    NewBalance = _state.State.Balance,
                    RemainingTickets = 0,
                    ErrorMessage = "Account is suspended",
                    ErrorCode = ErrorCode.ErrorCodeInvalidRequest,
                    ProcessedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ProcessingLatencyMs = sw.Elapsed.TotalMilliseconds,
                    GrainId = $"AccountGrain-{userId}"
                };
            }

            // 3. Détermination du coût du ticket
            if (!TicketPrices.TryGetValue(request.TicketType, out var ticketCost))
            {
                _logger.LogError(
                    "Invalid ticket type: UserId={UserId}, TicketType={TicketType}",
                    userId,
                    request.TicketType);

                return new ValidateTicketResponse
                {
                    ValidationId = request.ValidationId,
                    Status = ValidationStatus.ValidationStatusInvalidTicket,
                    NewBalance = _state.State.Balance,
                    RemainingTickets = 0,
                    ErrorMessage = $"Invalid ticket type: {request.TicketType}",
                    ErrorCode = ErrorCode.ErrorCodeInvalidRequest,
                    ProcessedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ProcessingLatencyMs = sw.Elapsed.TotalMilliseconds,
                    GrainId = $"AccountGrain-{userId}"
                };
            }

            // 4. Vérification du solde
            if (_state.State.Balance < ticketCost)
            {
                _logger.LogWarning(
                    "Insufficient balance: UserId={UserId}, Balance={Balance}, Required={Required}",
                    userId,
                    _state.State.Balance,
                    ticketCost);

                return new ValidateTicketResponse
                {
                    ValidationId = request.ValidationId,
                    Status = ValidationStatus.ValidationStatusInsufficientBalance,
                    NewBalance = _state.State.Balance,
                    RemainingTickets = 0,
                    ErrorMessage = $"Insufficient balance: {_state.State.Balance:F2} < {ticketCost:F2}",
                    ErrorCode = ErrorCode.ErrorCodeInsufficientFunds,
                    ProcessedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ProcessingLatencyMs = sw.Elapsed.TotalMilliseconds,
                    GrainId = $"AccountGrain-{userId}"
                };
            }

            // 5. Déduction du solde
            _state.State.Balance -= ticketCost;
            _state.State.TotalValidations++;
            _state.State.LastValidationTimestampMs = request.TimestampMs;
            _state.State.LastAccessedAt = DateTime.UtcNow;

            // Enregistrer la validation pour idempotence
            _state.State.RecentValidations[request.ValidationId] = request.TimestampMs;

            // Nettoyage des anciennes validations (déduplication window)
            CleanupOldValidations(request.TimestampMs);

            // 6. Persistance (optimistic concurrency via ETag si supporté par le provider)
            await _state.WriteStateAsync();

            sw.Stop();

            _logger.LogInformation(
                "Ticket validated successfully: UserId={UserId}, ValidationId={ValidationId}, " +
                "NewBalance={NewBalance}, Latency={LatencyMs}ms",
                userId,
                request.ValidationId,
                _state.State.Balance,
                sw.Elapsed.TotalMilliseconds);

            return new ValidateTicketResponse
            {
                ValidationId = request.ValidationId,
                Status = ValidationStatus.ValidationStatusSuccess,
                NewBalance = _state.State.Balance,
                RemainingTickets = (int)(_state.State.Balance / ticketCost),
                ErrorMessage = string.Empty,
                ErrorCode = ErrorCode.ErrorCodeNone,
                ProcessedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ProcessingLatencyMs = sw.Elapsed.TotalMilliseconds,
                GrainId = $"AccountGrain-{userId}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error validating ticket: UserId={UserId}, ValidationId={ValidationId}",
                userId,
                request.ValidationId);

            return new ValidateTicketResponse
            {
                ValidationId = request.ValidationId,
                Status = ValidationStatus.ValidationStatusSystemError,
                NewBalance = _state.State.Balance,
                RemainingTickets = 0,
                ErrorMessage = $"System error: {ex.Message}",
                ErrorCode = ErrorCode.ErrorCodeInternalError,
                ProcessedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ProcessingLatencyMs = sw.Elapsed.TotalMilliseconds,
                GrainId = $"AccountGrain-{userId}"
            };
        }
    }

    public ValueTask<GetAccountBalanceResponse> GetBalanceAsync()
    {
        var userId = this.GetPrimaryKeyString();

        var response = new GetAccountBalanceResponse
        {
            UserId = userId,
            Balance = _state.State.Balance,
            TotalTickets = 0, // À calculer selon les passes valides
            AccountStatus = _state.State.Status,
            LastValidationTimestampMs = _state.State.LastValidationTimestampMs,
            ValidationCount = _state.State.TotalValidations
        };

        _logger.LogDebug(
            "Balance retrieved: UserId={UserId}, Balance={Balance}",
            userId,
            _state.State.Balance);

        return ValueTask.FromResult(response);
    }

    public async ValueTask<RechargeAccountResponse> RechargeAsync(RechargeAccountRequest request)
    {
        var userId = this.GetPrimaryKeyString();

        _logger.LogInformation(
            "Recharging account: UserId={UserId}, Amount={Amount}, TransactionId={TransactionId}",
            userId,
            request.Amount,
            request.TransactionId);

        try
        {
            // Vérification de l'idempotence via transactionId
            if (_state.State.RecentValidations.ContainsKey($"recharge-{request.TransactionId}"))
            {
                _logger.LogWarning(
                    "Duplicate recharge transaction: UserId={UserId}, TransactionId={TransactionId}",
                    userId,
                    request.TransactionId);

                return new RechargeAccountResponse
                {
                    Success = true,
                    NewBalance = _state.State.Balance,
                    ErrorMessage = "Duplicate transaction (idempotence)",
                    TransactionId = request.TransactionId
                };
            }

            // Validation du montant
            if (request.Amount <= 0)
            {
                return new RechargeAccountResponse
                {
                    Success = false,
                    NewBalance = _state.State.Balance,
                    ErrorMessage = "Invalid amount: must be positive",
                    TransactionId = request.TransactionId
                };
            }

            // Rechargement
            _state.State.Balance += request.Amount;
            _state.State.LastAccessedAt = DateTime.UtcNow;
            _state.State.RecentValidations[$"recharge-{request.TransactionId}"] =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await _state.WriteStateAsync();

            _logger.LogInformation(
                "Account recharged: UserId={UserId}, Amount={Amount}, NewBalance={NewBalance}",
                userId,
                request.Amount,
                _state.State.Balance);

            return new RechargeAccountResponse
            {
                Success = true,
                NewBalance = _state.State.Balance,
                ErrorMessage = string.Empty,
                TransactionId = request.TransactionId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error recharging account: UserId={UserId}, TransactionId={TransactionId}",
                userId,
                request.TransactionId);

            return new RechargeAccountResponse
            {
                Success = false,
                NewBalance = _state.State.Balance,
                ErrorMessage = $"System error: {ex.Message}",
                TransactionId = request.TransactionId
            };
        }
    }

    public async ValueTask SuspendAccountAsync(string reason)
    {
        var userId = this.GetPrimaryKeyString();

        _logger.LogWarning(
            "Suspending account: UserId={UserId}, Reason={Reason}",
            userId,
            reason);

        _state.State.Status = AccountStatus.AccountStatusSuspended;
        _state.State.LastAccessedAt = DateTime.UtcNow;

        await _state.WriteStateAsync();
    }

    public async ValueTask ReactivateAccountAsync()
    {
        var userId = this.GetPrimaryKeyString();

        _logger.LogInformation(
            "Reactivating account: UserId={UserId}",
            userId);

        _state.State.Status = AccountStatus.AccountStatusActive;
        _state.State.LastAccessedAt = DateTime.UtcNow;

        await _state.WriteStateAsync();
    }

    public ValueTask<AccountStats> GetStatsAsync()
    {
        var userId = this.GetPrimaryKeyString();

        var stats = new AccountStats
        {
            UserId = userId,
            Balance = _state.State.Balance,
            TotalValidations = _state.State.TotalValidations,
            LastValidationTimestampMs = _state.State.LastValidationTimestampMs,
            Status = _state.State.Status,
            CreatedAt = _state.State.CreatedAt,
            LastAccessedAt = _state.State.LastAccessedAt,
            DeduplicatedCount = _state.State.DeduplicatedCount
        };

        return ValueTask.FromResult(stats);
    }

    /// <summary>
    /// Vérifie si une validation est un duplicata (idempotence).
    /// </summary>
    private bool IsDuplicateValidation(string validationId, long timestampMs)
    {
        if (_state.State.RecentValidations.TryGetValue(validationId, out var existingTimestamp))
        {
            // Vérifier que la validation est dans la fenêtre de déduplication
            var ageMs = timestampMs - existingTimestamp;
            if (ageMs < DeduplicationWindow.TotalMilliseconds)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Nettoie les validations anciennes (hors fenêtre de déduplication).
    /// </summary>
    private void CleanupOldValidations(long currentTimestampMs)
    {
        var cutoffMs = currentTimestampMs - (long)DeduplicationWindow.TotalMilliseconds;
        var toRemove = _state.State.RecentValidations
            .Where(kvp => kvp.Value < cutoffMs)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _state.State.RecentValidations.Remove(key);
        }

        if (toRemove.Count > 0)
        {
            _logger.LogDebug(
                "Cleaned up {Count} old validations for UserId={UserId}",
                toRemove.Count,
                this.GetPrimaryKeyString());
        }
    }
}

/// <summary>
/// État persistant d'un compte utilisateur.
/// Stocké dans Redis via Orleans Persistence.
/// </summary>
[GenerateSerializer]
public class AccountState
{
    [Id(0)] public string UserId { get; set; } = string.Empty;
    [Id(1)] public double Balance { get; set; }
    [Id(2)] public AccountStatus Status { get; set; }
    [Id(3)] public DateTime CreatedAt { get; set; }
    [Id(4)] public DateTime LastAccessedAt { get; set; }
    [Id(5)] public int TotalValidations { get; set; }
    [Id(6)] public long LastValidationTimestampMs { get; set; }
    [Id(7)] public int DeduplicatedCount { get; set; }

    /// <summary>
    /// Map: validationId → timestamp (pour idempotence dans la fenêtre de déduplication).
    /// </summary>
    [Id(8)] public Dictionary<string, long> RecentValidations { get; set; } = new();
}
