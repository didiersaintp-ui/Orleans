#!/bin/bash

###############################################################################
# Build Script - Orleans Ticketing Backend
#
# Builds .NET projects and Docker images
#
# Usage:
#   ./scripts/build.sh [--no-docker] [--no-test]
###############################################################################

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

NO_DOCKER=false
NO_TEST=false

# Parse arguments
while [[ $# -gt 0 ]]; do
  case $1 in
    --no-docker)
      NO_DOCKER=true
      shift
      ;;
    --no-test)
      NO_TEST=true
      shift
      ;;
    *)
      echo "Unknown option: $1"
      exit 1
      ;;
  esac
done

echo "========================================="
echo "Orleans Ticketing - Build Script"
echo "========================================="
echo ""

# Check prerequisites
command -v dotnet >/dev/null 2>&1 || { echo "ERROR: dotnet not found. Please install .NET SDK 8.0."; exit 1; }

if [ "$NO_DOCKER" = false ]; then
  command -v docker >/dev/null 2>&1 || { echo "ERROR: docker not found. Please install Docker."; exit 1; }
fi

cd "$ROOT_DIR"

# Restore dependencies
echo "Restoring NuGet packages..."
dotnet restore OrleansTicketing.sln
echo "✓ NuGet packages restored"
echo ""

# Build solution
echo "Building solution (Release configuration)..."
dotnet build OrleansTicketing.sln --configuration Release --no-restore
echo "✓ Solution built successfully"
echo ""

# Run tests (if not skipped)
if [ "$NO_TEST" = false ]; then
  echo "Running tests..."
  dotnet test OrleansTicketing.sln \
    --configuration Release \
    --no-build \
    --verbosity normal \
    --logger "console;verbosity=detailed"

  echo "✓ All tests passed"
  echo ""
fi

# Build Docker images (if not skipped)
if [ "$NO_DOCKER" = false ]; then
  echo "Building Docker images..."

  echo "  - Building ticketing-silo:latest..."
  docker build \
    -f src/Ticketing.Silo/Dockerfile \
    -t ticketing-silo:latest \
    -t ticketing-silo:dev \
    . \
    || { echo "ERROR: Failed to build silo image"; exit 1; }

  echo "  - Building ticketing-api:latest..."
  docker build \
    -f src/Ticketing.Api/Dockerfile \
    -t ticketing-api:latest \
    -t ticketing-api:dev \
    . \
    || { echo "ERROR: Failed to build API image"; exit 1; }

  echo "✓ Docker images built successfully"
  echo ""

  # Show images
  echo "Built images:"
  docker images | grep -E "ticketing-(silo|api)" | grep -E "(latest|dev)"
  echo ""
fi

echo "========================================="
echo "Build completed successfully!"
echo "========================================="
echo ""
echo "Next steps:"
echo "  - Run locally: ./scripts/quick-start.sh"
echo "  - Run tests: dotnet test"
echo "  - Deploy to K8s: kubectl apply -f deploy/k8s/"
echo ""
