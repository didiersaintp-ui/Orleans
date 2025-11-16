using Orleans;
using Ticketing.Shared.Grpc;

namespace Ticketing.Grains.Interfaces;

/// <summary>
/// Grain représentant un compte utilisateur account-based ticketing.
/// Clé: userId (string)
/// État: solde, tickets, nonce pour idempotence
/// </summary>
public interface IAccountGrain : IGrainWithStringKey
{
    /// <summary>
    /// Valide un ticket et déduit le montant du solde.
    /// Gère l'idempotence via validationId (nonce).
    /// </summary>
    /// <param name="request">Requête de validation</param>
    /// <returns>Réponse avec statut et nouveau solde</returns>
    ValueTask<ValidateTicketResponse> ValidateTicketAsync(ValidateTicketRequest request);

    /// <summary>
    /// Récupère le solde et l'état du compte.
    /// </summary>
    ValueTask<GetAccountBalanceResponse> GetBalanceAsync();

    /// <summary>
    /// Recharge le compte (ajout de crédit).
    /// Idempotent via transactionId.
    /// </summary>
    /// <param name="request">Requête de rechargement</param>
    /// <returns>Réponse avec nouveau solde</returns>
    ValueTask<RechargeAccountResponse> RechargeAsync(RechargeAccountRequest request);

    /// <summary>
    /// Suspend le compte (fraude détectée, etc.).
    /// </summary>
    ValueTask SuspendAccountAsync(string reason);

    /// <summary>
    /// Réactive un compte suspendu.
    /// </summary>
    ValueTask ReactivateAccountAsync();

    /// <summary>
    /// Retourne des statistiques du compte (pour observabilité).
    /// </summary>
    ValueTask<AccountStats> GetStatsAsync();
}

/// <summary>
/// Statistiques d'un compte (pour monitoring).
/// </summary>
[GenerateSerializer]
public record AccountStats
{
    [Id(0)] public string UserId { get; init; } = string.Empty;
    [Id(1)] public double Balance { get; init; }
    [Id(2)] public int TotalValidations { get; init; }
    [Id(3)] public long LastValidationTimestampMs { get; init; }
    [Id(4)] public AccountStatus Status { get; init; }
    [Id(5)] public DateTime CreatedAt { get; init; }
    [Id(6)] public DateTime LastAccessedAt { get; init; }
    [Id(7)] public int DeduplicatedCount { get; init; } // Nombre de validations dupliquées bloquées
}
