using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using Ticketing.Grains.Grains;
using Ticketing.Grains.Interfaces;
using Ticketing.Shared.Grpc;
using Xunit;

namespace Ticketing.Tests;

/// <summary>
/// Unit tests for AccountGrain using Orleans TestingHost.
/// </summary>
public class AccountGrainTests : IClassFixture<ClusterFixture>
{
    private readonly TestCluster _cluster;

    public AccountGrainTests(ClusterFixture fixture)
    {
        _cluster = fixture.Cluster;
    }

    [Fact]
    public async Task ValidateTicket_WithSufficientBalance_ShouldSucceed()
    {
        // Arrange
        var userId = $"user-test-{Guid.NewGuid()}";
        var grain = _cluster.GrainFactory.GetGrain<IAccountGrain>(userId);

        // Recharge account
        var rechargeRequest = new RechargeAccountRequest
        {
            UserId = userId,
            Amount = 100.0,
            TransactionId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString()
        };

        var rechargeResponse = await grain.RechargeAsync(rechargeRequest);
        rechargeResponse.Success.Should().BeTrue();
        rechargeResponse.NewBalance.Should().Be(100.0);

        // Act
        var validationRequest = new ValidateTicketRequest
        {
            ValidationId = Guid.NewGuid().ToString(),
            UserId = userId,
            ValidatorId = "zone-test",
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TicketType = TicketType.TicketTypeSingleJourney,
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response = await grain.ValidateTicketAsync(validationRequest);

        // Assert
        response.Should().NotBeNull();
        response.Status.Should().Be(ValidationStatus.ValidationStatusSuccess);
        response.NewBalance.Should().BeLessThan(100.0); // Balance decreased
        response.ErrorCode.Should().Be(ErrorCode.ErrorCodeNone);
    }

    [Fact]
    public async Task ValidateTicket_WithInsufficientBalance_ShouldFail()
    {
        // Arrange
        var userId = $"user-test-{Guid.NewGuid()}";
        var grain = _cluster.GrainFactory.GetGrain<IAccountGrain>(userId);

        // Account starts with 0 balance

        // Act
        var validationRequest = new ValidateTicketRequest
        {
            ValidationId = Guid.NewGuid().ToString(),
            UserId = userId,
            ValidatorId = "zone-test",
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TicketType = TicketType.TicketTypeSingleJourney,
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response = await grain.ValidateTicketAsync(validationRequest);

        // Assert
        response.Should().NotBeNull();
        response.Status.Should().Be(ValidationStatus.ValidationStatusInsufficientBalance);
        response.ErrorCode.Should().Be(ErrorCode.ErrorCodeInsufficientFunds);
        response.NewBalance.Should().Be(0.0);
    }

    [Fact]
    public async Task ValidateTicket_DuplicateValidationId_ShouldReturnDuplicateStatus()
    {
        // Arrange
        var userId = $"user-test-{Guid.NewGuid()}";
        var grain = _cluster.GrainFactory.GetGrain<IAccountGrain>(userId);

        // Recharge account
        await grain.RechargeAsync(new RechargeAccountRequest
        {
            UserId = userId,
            Amount = 100.0,
            TransactionId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString()
        });

        var validationId = Guid.NewGuid().ToString();

        // Act - First validation
        var request1 = new ValidateTicketRequest
        {
            ValidationId = validationId, // Same ID
            UserId = userId,
            ValidatorId = "zone-test",
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TicketType = TicketType.TicketTypeSingleJourney,
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response1 = await grain.ValidateTicketAsync(request1);

        // Act - Second validation (duplicate)
        var request2 = new ValidateTicketRequest
        {
            ValidationId = validationId, // Same ID!
            UserId = userId,
            ValidatorId = "zone-test",
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TicketType = TicketType.TicketTypeSingleJourney,
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response2 = await grain.ValidateTicketAsync(request2);

        // Assert
        response1.Status.Should().Be(ValidationStatus.ValidationStatusSuccess);
        response2.Status.Should().Be(ValidationStatus.ValidationStatusDuplicateValidation);
        response2.ErrorCode.Should().Be(ErrorCode.ErrorCodeInvalidRequest);
    }

    [Fact]
    public async Task RechargeAccount_WithValidAmount_ShouldIncreaseBalance()
    {
        // Arrange
        var userId = $"user-test-{Guid.NewGuid()}";
        var grain = _cluster.GrainFactory.GetGrain<IAccountGrain>(userId);

        // Act
        var request = new RechargeAccountRequest
        {
            UserId = userId,
            Amount = 50.0,
            TransactionId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response = await grain.RechargeAsync(request);

        // Assert
        response.Success.Should().BeTrue();
        response.NewBalance.Should().Be(50.0);

        // Verify balance
        var balanceResponse = await grain.GetBalanceAsync();
        balanceResponse.Balance.Should().Be(50.0);
    }

    [Fact]
    public async Task RechargeAccount_DuplicateTransactionId_ShouldBeIdempotent()
    {
        // Arrange
        var userId = $"user-test-{Guid.NewGuid()}";
        var grain = _cluster.GrainFactory.GetGrain<IAccountGrain>(userId);
        var transactionId = Guid.NewGuid().ToString();

        // Act - First recharge
        var request1 = new RechargeAccountRequest
        {
            UserId = userId,
            Amount = 50.0,
            TransactionId = transactionId,
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response1 = await grain.RechargeAsync(request1);

        // Act - Second recharge (duplicate transaction ID)
        var request2 = new RechargeAccountRequest
        {
            UserId = userId,
            Amount = 50.0,
            TransactionId = transactionId, // Same ID!
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response2 = await grain.RechargeAsync(request2);

        // Assert - Balance should only increase once
        response1.Success.Should().BeTrue();
        response1.NewBalance.Should().Be(50.0);

        response2.Success.Should().BeTrue();
        response2.NewBalance.Should().Be(50.0); // Not 100!

        var balanceResponse = await grain.GetBalanceAsync();
        balanceResponse.Balance.Should().Be(50.0);
    }

    [Fact]
    public async Task SuspendAccount_ShouldPreventValidations()
    {
        // Arrange
        var userId = $"user-test-{Guid.NewGuid()}";
        var grain = _cluster.GrainFactory.GetGrain<IAccountGrain>(userId);

        // Recharge account
        await grain.RechargeAsync(new RechargeAccountRequest
        {
            UserId = userId,
            Amount = 100.0,
            TransactionId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString()
        });

        // Act - Suspend account
        await grain.SuspendAccountAsync("Fraudulent activity detected");

        // Try to validate
        var validationRequest = new ValidateTicketRequest
        {
            ValidationId = Guid.NewGuid().ToString(),
            UserId = userId,
            ValidatorId = "zone-test",
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TicketType = TicketType.TicketTypeSingleJourney,
            CorrelationId = Guid.NewGuid().ToString()
        };

        var response = await grain.ValidateTicketAsync(validationRequest);

        // Assert
        response.Status.Should().Be(ValidationStatus.ValidationStatusAccountSuspended);
        response.ErrorCode.Should().Be(ErrorCode.ErrorCodeInvalidRequest);
    }

    [Fact]
    public async Task GetStats_ShouldReturnAccountStatistics()
    {
        // Arrange
        var userId = $"user-test-{Guid.NewGuid()}";
        var grain = _cluster.GrainFactory.GetGrain<IAccountGrain>(userId);

        // Recharge
        await grain.RechargeAsync(new RechargeAccountRequest
        {
            UserId = userId,
            Amount = 100.0,
            TransactionId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString()
        });

        // Validate ticket
        await grain.ValidateTicketAsync(new ValidateTicketRequest
        {
            ValidationId = Guid.NewGuid().ToString(),
            UserId = userId,
            ValidatorId = "zone-test",
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TicketType = TicketType.TicketTypeSingleJourney,
            CorrelationId = Guid.NewGuid().ToString()
        });

        // Act
        var stats = await grain.GetStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.UserId.Should().Be(userId);
        stats.Balance.Should().BeLessThan(100.0);
        stats.TotalValidations.Should().Be(1);
        stats.Status.Should().Be(AccountStatus.AccountStatusActive);
    }
}

/// <summary>
/// Cluster fixture for Orleans TestingHost (shared across tests).
/// </summary>
public class ClusterFixture : IDisposable
{
    public TestCluster Cluster { get; private set; }

    public ClusterFixture()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();

        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public void Dispose()
    {
        Cluster?.StopAllSilos();
    }
}

/// <summary>
/// Test silo configurator (in-memory storage for tests).
/// </summary>
public class TestSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("RedisStore"); // Use in-memory storage for tests
    }
}
