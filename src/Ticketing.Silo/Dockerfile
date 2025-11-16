# ============================================================================
# Multi-stage Dockerfile for Orleans Silo (.NET 8)
# Optimized for production: minimal image size, security, performance
# ============================================================================

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Copy solution and project files (for layer caching)
COPY ["OrleansTicketing.sln", "./"]
COPY ["src/Ticketing.Silo/Ticketing.Silo.csproj", "src/Ticketing.Silo/"]
COPY ["src/Ticketing.Grains/Ticketing.Grains.csproj", "src/Ticketing.Grains/"]
COPY ["src/Ticketing.Shared/Ticketing.Shared.csproj", "src/Ticketing.Shared/"]

# Restore dependencies (cached layer)
RUN dotnet restore "src/Ticketing.Silo/Ticketing.Silo.csproj"

# Copy source code
COPY ["src/", "src/"]

# Build application
WORKDIR "/src/src/Ticketing.Silo"
RUN dotnet build "Ticketing.Silo.csproj" \
    -c Release \
    -o /app/build \
    --no-restore

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "Ticketing.Silo.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    --no-build \
    /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app

# Security: Create non-root user
RUN addgroup -g 1000 appuser && \
    adduser -u 1000 -G appuser -s /bin/sh -D appuser

# Install necessary runtime dependencies
RUN apk add --no-cache \
    ca-certificates \
    tzdata \
    && rm -rf /var/cache/apk/*

# Copy published application
COPY --from=publish --chown=appuser:appuser /app/publish .

# Switch to non-root user
USER appuser

# Expose ports
# 11111: Orleans silo-to-silo communication
# 30000: Orleans gateway (client connections)
# 5001: HTTP/1.1 (metrics, health checks)
EXPOSE 11111 30000 5001

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:5001/health/ready || exit 1

# Environment variables (overrideable)
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0 \
    Orleans__ClusterId=ticketing-cluster \
    Orleans__ServiceId=ticketing-service

ENTRYPOINT ["dotnet", "Ticketing.Silo.dll"]
