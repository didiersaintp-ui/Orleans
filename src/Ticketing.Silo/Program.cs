using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orleans.Configuration;
using Serilog;
using Serilog.Formatting.Compact;
using System.Net;
using Ticketing.Grains.Grains;

// ============================================================================
// Configuration de Serilog (structured logging JSON)
// ============================================================================
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithProperty("Application", "Ticketing.Silo")
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

Log.Information("Starting Orleans Silo...");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ========================================================================
    // Serilog integration
    // ========================================================================
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "Ticketing.Silo")
        .WriteTo.Console(new CompactJsonFormatter()));

    // ========================================================================
    // Performance tuning - ThreadPool
    // ========================================================================
    // Augmenter le nombre de threads minimum pour réduire la latence
    ThreadPool.SetMinThreads(200, 200);
    Log.Information("ThreadPool configured: MinWorkerThreads=200, MinCompletionPortThreads=200");

    // ========================================================================
    // Orleans Silo Configuration
    // ========================================================================
    builder.Host.UseOrleans((context, siloBuilder) =>
    {
        var environment = context.HostingEnvironment.EnvironmentName;
        var isKubernetes = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));

        Log.Information("Configuring Orleans Silo: Environment={Environment}, IsKubernetes={IsKubernetes}",
            environment, isKubernetes);

        // Identifiers
        siloBuilder
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = context.Configuration["Orleans:ClusterId"] ?? "ticketing-cluster";
                options.ServiceId = context.Configuration["Orleans:ServiceId"] ?? "ticketing-service";
            });

        // Clustering: Kubernetes (prod) or Localhost (dev)
        if (isKubernetes)
        {
            // Kubernetes Membership via Headless Service
            siloBuilder.UseKubernetesHosting();

            Log.Information("Using Kubernetes clustering with headless service");
        }
        else
        {
            // Development: Localhost clustering
            var siloPort = 11111;
            var gatewayPort = 30000;

            siloBuilder
                .UseLocalhostClustering(siloPort, gatewayPort)
                .ConfigureEndpoints(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    advertisedIP: IPAddress.Loopback,
                    listenOnAnyHostAddress: true);

            Log.Information("Using localhost clustering: SiloPort={SiloPort}, GatewayPort={GatewayPort}",
                siloPort, gatewayPort);
        }

        // Persistence: Redis (primary) with fallback configuration
        var redisConnectionString = context.Configuration.GetConnectionString("Redis")
            ?? "localhost:6379";

        siloBuilder.AddRedisGrainStorage("RedisStore", options =>
        {
            options.ConfigurationOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnectionString);
            options.ConfigurationOptions.AbortOnConnectFail = false;
            options.ConfigurationOptions.ConnectTimeout = 5000;
            options.ConfigurationOptions.SyncTimeout = 5000;
            options.ConfigurationOptions.KeepAlive = 60;
        });

        Log.Information("Redis storage configured: ConnectionString={ConnectionString}",
            redisConnectionString);

        // Grain configuration
        siloBuilder.ConfigureApplicationParts(parts =>
        {
            parts.AddApplicationPart(typeof(AccountGrain).Assembly).WithReferences();
        });

        // Performance tuning
        siloBuilder.Configure<SiloMessagingOptions>(options =>
        {
            options.ResponseTimeout = TimeSpan.FromSeconds(30);
            options.SystemResponseTimeout = TimeSpan.FromSeconds(30);
            options.MaxMessageBodySize = 1024 * 1024 * 10; // 10 MB
        });

        siloBuilder.Configure<GrainCollectionOptions>(options =>
        {
            // Déactivation des grains inactifs après 1 heure
            options.CollectionAge = TimeSpan.FromHours(1);
            options.DeactivationTimeout = TimeSpan.FromMinutes(5);
        });

        // Grain versioning (pour rolling updates sans downtime)
        siloBuilder.Configure<GrainVersioningOptions>(options =>
        {
            options.DefaultCompatibilityStrategy = "BackwardCompatible";
            options.DefaultVersionSelectorStrategy = "LatestVersion";
        });

        Log.Information("Orleans Silo configuration completed");
    });

    // ========================================================================
    // OpenTelemetry - Metrics & Tracing
    // ========================================================================
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService("ticketing-silo")
            .AddAttributes(new Dictionary<string, object>
            {
                ["environment"] = builder.Environment.EnvironmentName,
                ["host.name"] = Environment.MachineName,
                ["deployment.type"] = "orleans-silo"
            }))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Microsoft.Orleans")
            .AddPrometheusExporter())
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("Microsoft.Orleans")
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(
                    builder.Configuration["Jaeger:Endpoint"] ?? "http://jaeger:4317");
            }));

    Log.Information("OpenTelemetry configured: Metrics (Prometheus), Tracing (Jaeger)");

    // ========================================================================
    // Health checks
    // ========================================================================
    builder.Services.AddHealthChecks()
        .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy())
        .AddRedis(
            builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379",
            name: "redis",
            tags: new[] { "ready" });

    // ========================================================================
    // ASP.NET Core services
    // ========================================================================
    builder.Services.AddControllers();

    var app = builder.Build();

    // ========================================================================
    // Middleware pipeline
    // ========================================================================

    // Prometheus metrics endpoint
    app.MapPrometheusScrapingEndpoint();

    // Health checks endpoints
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false // Liveness: always healthy if process runs
    });

    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready") // Readiness: checks Redis, Orleans
    });

    app.MapGet("/", () => new
    {
        Service = "Ticketing Orleans Silo",
        Version = "1.0.0",
        Environment = app.Environment.EnvironmentName,
        Timestamp = DateTime.UtcNow
    });

    Log.Information("Orleans Silo started successfully");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Silo failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
