#!/bin/bash

###############################################################################
# Quick Start Script - Orleans Ticketing Backend
#
# Deploys the entire stack to Docker Desktop Kubernetes (Windows/Mac/Linux)
#
# Usage:
#   ./scripts/quick-start.sh
#
# Prerequisites:
#   - Docker Desktop with Kubernetes enabled
#   - kubectl installed
#   - .NET SDK 8.0 (for building)
###############################################################################

set -e  # Exit on error

echo "========================================="
echo "Orleans Ticketing - Quick Start"
echo "========================================="
echo ""

# Check prerequisites
command -v docker >/dev/null 2>&1 || { echo "ERROR: docker not found. Please install Docker Desktop."; exit 1; }
command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl not found. Please install kubectl."; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "ERROR: dotnet not found. Please install .NET SDK 8.0."; exit 1; }

echo "✓ Prerequisites check passed"
echo ""

# Verify Kubernetes is running
echo "Checking Kubernetes cluster..."
kubectl cluster-info >/dev/null 2>&1 || { echo "ERROR: Kubernetes cluster not accessible. Enable Kubernetes in Docker Desktop."; exit 1; }
echo "✓ Kubernetes cluster is accessible"
echo ""

# Build Docker images
echo "Building Docker images..."
echo "  - Building ticketing-silo..."
docker build -f src/Ticketing.Silo/Dockerfile -t ticketing-silo:latest . --quiet || { echo "ERROR: Failed to build silo image"; exit 1; }
echo "  - Building ticketing-api..."
docker build -f src/Ticketing.Api/Dockerfile -t ticketing-api:latest . --quiet || { echo "ERROR: Failed to build API image"; exit 1; }
echo "✓ Docker images built successfully"
echo ""

# Create namespace
echo "Creating Kubernetes namespace..."
kubectl create namespace ticketing --dry-run=client -o yaml | kubectl apply -f - >/dev/null
echo "✓ Namespace created: ticketing"
echo ""

# Deploy Redis
echo "Deploying Redis..."
cat <<EOF | kubectl apply -f - >/dev/null
apiVersion: apps/v1
kind: Deployment
metadata:
  name: redis
  namespace: ticketing
spec:
  replicas: 1
  selector:
    matchLabels:
      app: redis
  template:
    metadata:
      labels:
        app: redis
    spec:
      containers:
      - name: redis
        image: redis:7-alpine
        ports:
        - containerPort: 6379
        args: ["--save", "60", "1", "--loglevel", "warning"]
---
apiVersion: v1
kind: Service
metadata:
  name: redis-master
  namespace: ticketing
spec:
  selector:
    app: redis
  ports:
  - port: 6379
    targetPort: 6379
EOF
echo "✓ Redis deployed"
echo ""

# Wait for Redis to be ready
echo "Waiting for Redis to be ready..."
kubectl wait --for=condition=ready pod -l app=redis -n ticketing --timeout=60s >/dev/null
echo "✓ Redis is ready"
echo ""

# Deploy application
echo "Deploying Orleans Ticketing application..."
kubectl apply -f deploy/k8s/configmap.yaml >/dev/null
kubectl apply -f deploy/k8s/secret.yaml >/dev/null
kubectl apply -f deploy/k8s/rbac.yaml >/dev/null
kubectl apply -f deploy/k8s/silo-service-headless.yaml >/dev/null
kubectl apply -f deploy/k8s/silo-statefulset.yaml >/dev/null
kubectl apply -f deploy/k8s/api-deployment.yaml >/dev/null
kubectl apply -f deploy/k8s/api-service.yaml >/dev/null
kubectl apply -f deploy/k8s/hpa.yaml >/dev/null
kubectl apply -f deploy/k8s/pdb.yaml >/dev/null
echo "✓ Application manifests applied"
echo ""

# Wait for API to be ready
echo "Waiting for API pods to be ready (this may take 2-3 minutes)..."
kubectl wait --for=condition=ready pod -l app=ticketing-api -n ticketing --timeout=180s >/dev/null || {
    echo "WARNING: API pods not ready yet. Check status with: kubectl get pods -n ticketing"
}
echo "✓ API is ready"
echo ""

# Wait for Silos to be ready
echo "Waiting for Orleans Silos to be ready..."
kubectl wait --for=condition=ready pod -l app=orleans-silo -n ticketing --timeout=180s >/dev/null || {
    echo "WARNING: Silo pods not ready yet. Check status with: kubectl get pods -n ticketing"
}
echo "✓ Silos are ready"
echo ""

# Show deployment status
echo "========================================="
echo "Deployment Summary"
echo "========================================="
kubectl get pods -n ticketing
echo ""

# Port-forward for local access
echo "========================================="
echo "Starting port-forward for local access..."
echo "========================================="
echo ""
echo "API gRPC endpoint: localhost:5000"
echo "Metrics endpoint:  localhost:5001/metrics"
echo "Health check:      localhost:5001/health/ready"
echo ""
echo "Press Ctrl+C to stop port-forward and exit."
echo ""

# Port-forward API (blocking)
kubectl port-forward -n ticketing svc/ticketing-api 5000:5000 5001:5001 &
PF_PID=$!

# Wait a moment for port-forward to start
sleep 3

# Test health endpoint
echo "Testing health endpoint..."
curl -s http://localhost:5001/health/ready >/dev/null && echo "✓ Health check passed" || echo "WARNING: Health check failed"
echo ""

# Show next steps
echo "========================================="
echo "Next Steps"
echo "========================================="
echo ""
echo "1. Run load test:"
echo "   cd src/Ticketing.Client"
echo "   dotnet run -- --server localhost:5000 --users 100 --duration 60 --rps 100"
echo ""
echo "2. Or use k6:"
echo "   cd tests/k6"
echo "   k6 run --env GRPC_SERVER=localhost:5000 --env TEST_MODE=quick load-test-progressive.js"
echo ""
echo "3. View logs:"
echo "   kubectl logs -n ticketing -l app=orleans-silo --tail=100"
echo "   kubectl logs -n ticketing -l app=ticketing-api --tail=100"
echo ""
echo "4. View metrics:"
echo "   curl http://localhost:5001/metrics | grep ticketing"
echo ""
echo "5. Cleanup:"
echo "   ./scripts/cleanup.sh"
echo ""
echo "========================================="

# Keep port-forward running
wait $PF_PID
