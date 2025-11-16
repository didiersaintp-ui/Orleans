using Orleans;
using Ticketing.Shared.Grpc;

namespace Ticketing.Grains.Interfaces;

/// <summary>
/// Grain représentant un groupe de validateurs (zone géographique).
/// Clé: validatorGroupId (ex: "zone-paris-nord")
/// Rôle: agrégation des validations, routage optimisé, statistiques
/// </summary>
public interface IValidatorGroupGrain : IGrainWithStringKey
{
    /// <summary>
    /// Traite une validation en routant vers le bon AccountGrain.
    /// Permet le batching et l'optimisation des appels réseau.
    /// </summary>
    ValueTask<ValidateTicketResponse> ProcessValidationAsync(ValidateTicketRequest request);

    /// <summary>
    /// Traite un batch de validations de manière optimisée.
    /// </summary>
    ValueTask<BatchValidationResponse> ProcessBatchAsync(IReadOnlyList<ValidateTicketRequest> requests);

    /// <summary>
    /// Retourne les statistiques du groupe de validateurs.
    /// </summary>
    ValueTask<ValidatorGroupStats> GetStatsAsync();

    /// <summary>
    /// Réinitialise les statistiques (pour tests ou maintenance).
    /// </summary>
    ValueTask ResetStatsAsync();
}

/// <summary>
/// Statistiques d'un groupe de validateurs.
/// </summary>
[GenerateSerializer]
public record ValidatorGroupStats
{
    [Id(0)] public string ValidatorGroupId { get; init; } = string.Empty;
    [Id(1)] public long TotalValidations { get; init; }
    [Id(2)] public long SuccessfulValidations { get; init; }
    [Id(3)] public long FailedValidations { get; init; }
    [Id(4)] public double AverageLatencyMs { get; init; }
    [Id(5)] public long LastValidationTimestampMs { get; init; }
    [Id(6)] public DateTime CreatedAt { get; init; }
    [Id(7)] public DateTime LastAccessedAt { get; init; }
    [Id(8)] public Dictionary<string, long> ValidationsByTicketType { get; init; } = new();
}
