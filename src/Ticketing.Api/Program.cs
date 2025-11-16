using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orleans;
using Orleans.Configuration;
using Polly;
using Serilog;
using Serilog.Formatting.Compact;
using System.Net;
using Ticketing.Api.Services;

// ============================================================================
// Configuration de Serilog (structured logging JSON)
// ============================================================================
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithProperty("Application", "Ticketing.Api")
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

Log.Information("Starting Ticketing gRPC API...");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ========================================================================
    // Performance tuning - ThreadPool
    // ========================================================================
    ThreadPool.SetMinThreads(200, 200);
    Log.Information("ThreadPool configured: MinWorkerThreads=200, MinCompletionPortThreads=200");

    // ========================================================================
    // Kestrel configuration - HTTP/2, performance tuning
    // ========================================================================
    builder.WebHost.ConfigureKestrel((context, options) =>
    {
        // HTTP/2 endpoint (gRPC)
        options.Listen(IPAddress.Any, 5000, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            // TLS configuration (production)
            // listenOptions.UseHttps("path/to/cert.pfx", "password");
        });

        // HTTP/1.1 endpoint (metrics, health checks)
        options.Listen(IPAddress.Any, 5001, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http1;
        });

        // Performance limits
        options.Limits.MaxConcurrentConnections = 10000;
        options.Limits.MaxConcurrentUpgradedConnections = 10000;
        options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

        // HTTP/2 specific limits
        options.Limits.Http2.MaxStreamsPerConnection = 100;
        options.Limits.Http2.HeaderTableSize = 4096;
        options.Limits.Http2.MaxFrameSize = 16384;
        options.Limits.Http2.MaxRequestHeaderFieldSize = 8192;
        options.Limits.Http2.InitialConnectionWindowSize = 128 * 1024; // 128 KB
        options.Limits.Http2.InitialStreamWindowSize = 96 * 1024; // 96 KB
        options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(60);
        options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(30);

        Log.Information("Kestrel configured: HTTP/2 on :5000, HTTP/1.1 on :5001");
    });

    // ========================================================================
    // Serilog integration
    // ========================================================================
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "Ticketing.Api")
        .WriteTo.Console(new CompactJsonFormatter()));

    // ========================================================================
    // Orleans Client Configuration
    // ========================================================================
    var isKubernetes = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));

    builder.Host.UseOrleansClient((context, clientBuilder) =>
    {
        Log.Information("Configuring Orleans Client: IsKubernetes={IsKubernetes}", isKubernetes);

        clientBuilder.Configure<ClusterOptions>(options =>
        {
            options.ClusterId = context.Configuration["Orleans:ClusterId"] ?? "ticketing-cluster";
            options.ServiceId = context.Configuration["Orleans:ServiceId"] ?? "ticketing-service";
        });

        if (isKubernetes)
        {
            // Kubernetes clustering
            clientBuilder.UseKubernetesHosting();
            Log.Information("Using Kubernetes clustering for Orleans client");
        }
        else
        {
            // Development: Localhost clustering
            clientBuilder.UseLocalhostClustering(gatewayPort: 30000);
            Log.Information("Using localhost clustering for Orleans client: GatewayPort=30000");
        }

        // Connection retry avec Polly
        clientBuilder.Configure<GatewayOptions>(options =>
        {
            options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(30);
        });

        Log.Information("Orleans Client configured successfully");
    });

    // ========================================================================
    // gRPC Services
    // ========================================================================
    builder.Services.AddGrpc(options =>
    {
        options.MaxReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
        options.MaxSendMessageSize = 10 * 1024 * 1024; // 10 MB
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();

        // Compression
        options.ResponseCompressionAlgorithm = "gzip";
        options.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Optimal;

        // Interceptors pour logging, metrics, etc. (à ajouter si besoin)
    });

    builder.Services.AddGrpcReflection(); // Pour debugging avec grpcurl

    // Services applicatifs
    builder.Services.AddSingleton<IMetricsService, MetricsService>();

    // ========================================================================
    // OpenTelemetry - Metrics & Tracing
    // ========================================================================
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService("ticketing-api")
            .AddAttributes(new Dictionary<string, object>
            {
                ["environment"] = builder.Environment.EnvironmentName,
                ["host.name"] = Environment.MachineName,
                ["deployment.type"] = "grpc-api"
            }))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("Ticketing.Api")
            .AddPrometheusExporter())
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddGrpcClientInstrumentation()
            .AddSource("Ticketing.Api")
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
        .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

    var app = builder.Build();

    // ========================================================================
    // Middleware pipeline
    // ========================================================================

    // Prometheus metrics endpoint (HTTP/1.1 on port 5001)
    app.MapPrometheusScrapingEndpoint();

    // gRPC services
    app.MapGrpcService<TicketingGrpcService>();

    // gRPC reflection (dev only)
    if (app.Environment.IsDevelopment())
    {
        app.MapGrpcReflectionService();
    }

    // Health checks
    app.MapHealthChecks("/health/live");
    app.MapHealthChecks("/health/ready");

    // Default endpoint
    app.MapGet("/", () => new
    {
        Service = "Ticketing gRPC API",
        Version = "1.0.0",
        Environment = app.Environment.EnvironmentName,
        Endpoints = new[]
        {
            "gRPC: :5000 (HTTP/2)",
            "Metrics: :5001/metrics",
            "Health: :5001/health/live, :5001/health/ready"
        },
        Timestamp = DateTime.UtcNow
    });

    Log.Information("Ticketing gRPC API started successfully");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
