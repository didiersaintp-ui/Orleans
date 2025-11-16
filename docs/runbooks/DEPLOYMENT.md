# Deployment Runbook - Orleans Ticketing Backend

## Overview

This runbook provides step-by-step procedures for deploying the Orleans Ticketing Backend to various environments.

---

## Prerequisites Checklist

Before deploying, ensure:

- [ ] Kubernetes cluster is accessible (`kubectl cluster-info`)
- [ ] Docker images are built and pushed to registry
- [ ] Redis is deployed and accessible
- [ ] ConfigMaps and Secrets are created
- [ ] Service accounts and RBAC are configured
- [ ] Ingress controller is installed (if using ingress)
- [ ] Monitoring stack is deployed (Prometheus, Grafana, Jaeger)

---

## Local Deployment (Docker Desktop)

### Step 1: Enable Kubernetes in Docker Desktop

**Windows**:
1. Open Docker Desktop
2. Settings → Kubernetes → ☑ Enable Kubernetes
3. Apply & Restart
4. Verify: `kubectl get nodes`

### Step 2: Deploy Redis

```bash
kubectl create namespace ticketing

kubectl apply -f - <<EOF
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
        args: ["--save", "60", "1"]
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
EOF
```

**Verify**:
```bash
kubectl get pods -n ticketing -l app=redis
kubectl logs -n ticketing -l app=redis
```

### Step 3: Build Docker Images

```bash
# From repository root
docker build -f src/Ticketing.Silo/Dockerfile -t ticketing-silo:latest .
docker build -f src/Ticketing.Api/Dockerfile -t ticketing-api:latest .
```

**Verify**:
```bash
docker images | grep ticketing
```

### Step 4: Deploy Application

```bash
# Apply all Kubernetes manifests
kubectl apply -f deploy/k8s/namespace.yaml
kubectl apply -f deploy/k8s/configmap.yaml
kubectl apply -f deploy/k8s/secret.yaml
kubectl apply -f deploy/k8s/rbac.yaml
kubectl apply -f deploy/k8s/silo-service-headless.yaml
kubectl apply -f deploy/k8s/silo-statefulset.yaml
kubectl apply -f deploy/k8s/api-deployment.yaml
kubectl apply -f deploy/k8s/api-service.yaml
kubectl apply -f deploy/k8s/hpa.yaml
kubectl apply -f deploy/k8s/pdb.yaml
```

**Or use single command**:
```bash
kubectl apply -f deploy/k8s/
```

### Step 5: Wait for Pods to Start

```bash
# Watch pods
kubectl get pods -n ticketing -w

# Expected output (after ~2 min):
# NAME                             READY   STATUS    RESTARTS   AGE
# orleans-silo-0                   1/1     Running   0          90s
# orleans-silo-1                   1/1     Running   0          85s
# orleans-silo-2                   1/1     Running   0          80s
# ticketing-api-xxxxx-xxxxx        1/1     Running   0          90s
# ticketing-api-xxxxx-xxxxx        1/1     Running   0          90s
# redis-xxxxx-xxxxx                1/1     Running   0          5m
```

### Step 6: Verify Health

```bash
# Port-forward API
kubectl port-forward -n ticketing svc/ticketing-api 5000:5000 5001:5001 &

# Check health
curl http://localhost:5001/health/ready
# Expected: Healthy

# Check metrics
curl http://localhost:5001/metrics | grep ticketing
```

### Step 7: Run Smoke Test

```bash
# Using .NET client
cd src/Ticketing.Client
dotnet run -- --server localhost:5000 --users 10 --duration 30 --rps 10

# Or using k6
cd tests/k6
k6 run --env GRPC_SERVER=localhost:5000 --env TEST_MODE=quick load-test-progressive.js
```

---

## Production Deployment (Cloud)

### Step 1: Prepare Container Registry

```bash
# Login to registry (e.g., AWS ECR, Azure ACR, GCP GCR, DockerHub)
docker login your-registry.io

# Tag images
docker tag ticketing-silo:latest your-registry.io/ticketing-silo:v1.0.0
docker tag ticketing-api:latest your-registry.io/ticketing-api:v1.0.0

# Push images
docker push your-registry.io/ticketing-silo:v1.0.0
docker push your-registry.io/ticketing-api:v1.0.0
```

### Step 2: Configure Kubernetes Context

```bash
# List contexts
kubectl config get-contexts

# Switch to production context
kubectl config use-context prod-cluster

# Verify
kubectl cluster-info
```

### Step 3: Create Namespace

```bash
kubectl create namespace ticketing-prod
```

### Step 4: Create Secrets

```bash
# Redis password
kubectl create secret generic ticketing-secrets \
  --from-literal=REDIS_PASSWORD='your-secure-password' \
  --namespace ticketing-prod

# TLS certificates (if applicable)
kubectl create secret tls ticketing-tls \
  --cert=path/to/tls.crt \
  --key=path/to/tls.key \
  --namespace ticketing-prod

# Registry credentials (if using private registry)
kubectl create secret docker-registry registry-credentials \
  --docker-server=your-registry.io \
  --docker-username=your-username \
  --docker-password=your-password \
  --namespace ticketing-prod
```

### Step 5: Update ConfigMap

```bash
# Edit deploy/k8s/configmap.yaml with production values:
# - REDIS_CONNECTION: redis-cluster.prod.svc.cluster.local:6379
# - JAEGER_ENDPOINT: http://jaeger-collector.observability.svc.cluster.local:4317

kubectl apply -f deploy/k8s/configmap.yaml -n ticketing-prod
```

### Step 6: Update Manifests with Production Values

**deploy/k8s/silo-statefulset.yaml**:
```yaml
spec:
  replicas: 20  # Scale for production
  template:
    spec:
      containers:
        - name: silo
          image: your-registry.io/ticketing-silo:v1.0.0
          resources:
            requests:
              cpu: 2000m
              memory: 4Gi
            limits:
              cpu: 4000m
              memory: 8Gi
```

**deploy/k8s/api-deployment.yaml**:
```yaml
spec:
  replicas: 10  # Scale for production
  template:
    spec:
      containers:
        - name: api
          image: your-registry.io/ticketing-api:v1.0.0
          resources:
            requests:
              cpu: 1000m
              memory: 2Gi
            limits:
              cpu: 2000m
              memory: 4Gi
```

### Step 7: Deploy

```bash
# Apply all manifests
kubectl apply -f deploy/k8s/ -n ticketing-prod

# Watch rollout
kubectl rollout status statefulset/orleans-silo -n ticketing-prod
kubectl rollout status deployment/ticketing-api -n ticketing-prod
```

### Step 8: Verify Deployment

```bash
# Check pods
kubectl get pods -n ticketing-prod

# Check services
kubectl get svc -n ticketing-prod

# Check HPA
kubectl get hpa -n ticketing-prod

# Check logs
kubectl logs -n ticketing-prod orleans-silo-0 --tail=100
kubectl logs -n ticketing-prod -l app=ticketing-api --tail=100
```

### Step 9: Run Production Smoke Tests

```bash
# Get API external IP (LoadBalancer)
API_EXTERNAL_IP=$(kubectl get svc ticketing-api -n ticketing-prod -o jsonpath='{.status.loadBalancer.ingress[0].ip}')

# Test gRPC health check (using grpcurl)
grpcurl -plaintext $API_EXTERNAL_IP:5000 ticketing.TicketingService/HealthCheck

# Run k6 smoke test
k6 run --env GRPC_SERVER=$API_EXTERNAL_IP:5000 --env TEST_MODE=quick tests/k6/load-test-progressive.js
```

---

## Rolling Update (Zero-Downtime)

### For StatefulSet (Silos)

```bash
# Update image
kubectl set image statefulset/orleans-silo \
  silo=your-registry.io/ticketing-silo:v1.0.1 \
  -n ticketing-prod

# Watch rollout
kubectl rollout status statefulset/orleans-silo -n ticketing-prod

# Verify
kubectl get pods -n ticketing-prod -l app=orleans-silo
```

### For Deployment (API)

```bash
# Update image
kubectl set image deployment/ticketing-api \
  api=your-registry.io/ticketing-api:v1.0.1 \
  -n ticketing-prod

# Watch rollout
kubectl rollout status deployment/ticketing-api -n ticketing-prod

# Verify
kubectl get pods -n ticketing-prod -l app=ticketing-api
```

---

## Rollback

### If Deployment Fails

```bash
# Rollback StatefulSet
kubectl rollout undo statefulset/orleans-silo -n ticketing-prod

# Rollback Deployment
kubectl rollout undo deployment/ticketing-api -n ticketing-prod

# Verify rollback
kubectl rollout status statefulset/orleans-silo -n ticketing-prod
kubectl rollout status deployment/ticketing-api -n ticketing-prod
```

### Check Rollout History

```bash
# View history
kubectl rollout history statefulset/orleans-silo -n ticketing-prod
kubectl rollout history deployment/ticketing-api -n ticketing-prod

# Rollback to specific revision
kubectl rollout undo statefulset/orleans-silo --to-revision=2 -n ticketing-prod
```

---

## Scaling

### Manual Scaling

```bash
# Scale silos
kubectl scale statefulset/orleans-silo --replicas=30 -n ticketing-prod

# Scale API
kubectl scale deployment/ticketing-api --replicas=15 -n ticketing-prod

# Verify
kubectl get pods -n ticketing-prod -w
```

### HPA (Horizontal Pod Autoscaler)

```bash
# Check HPA status
kubectl get hpa -n ticketing-prod

# Edit HPA targets
kubectl edit hpa orleans-silo-hpa -n ticketing-prod
kubectl edit hpa ticketing-api-hpa -n ticketing-prod
```

---

## Troubleshooting

### Pods Not Starting

```bash
# Check pod status
kubectl describe pod orleans-silo-0 -n ticketing-prod

# Common issues:
# - ImagePullBackOff: Check image name and registry credentials
# - CrashLoopBackOff: Check logs for startup errors
# - Pending: Check resource quotas and node capacity

# View logs
kubectl logs orleans-silo-0 -n ticketing-prod --tail=100

# Check events
kubectl get events -n ticketing-prod --sort-by='.lastTimestamp'
```

### Orleans Silos Not Clustering

```bash
# Check headless service
kubectl get svc orleans-headless -n ticketing-prod

# Check silo logs
kubectl logs orleans-silo-0 -n ticketing-prod | grep -i "cluster"

# Expected log:
# "Cluster membership is stable, 20 silos active"

# Check DNS resolution
kubectl exec -it orleans-silo-0 -n ticketing-prod -- nslookup orleans-headless.ticketing-prod.svc.cluster.local
```

### High Latency

```bash
# Check resource usage
kubectl top pods -n ticketing-prod

# Check HPA
kubectl get hpa -n ticketing-prod

# Check Redis latency
kubectl exec -it redis-0 -n ticketing-prod -- redis-cli --latency

# Check network policies
kubectl get networkpolicies -n ticketing-prod
```

---

## Cleanup

### Delete Application

```bash
# Delete all resources
kubectl delete -f deploy/k8s/ -n ticketing-prod

# Or delete namespace (including all resources)
kubectl delete namespace ticketing-prod
```

### Delete PVCs (if any)

```bash
kubectl delete pvc --all -n ticketing-prod
```

---

## Post-Deployment Checklist

After deployment, verify:

- [ ] All pods are running (`kubectl get pods -n ticketing-prod`)
- [ ] Health checks pass (`curl http://api/health/ready`)
- [ ] Metrics are being scraped by Prometheus
- [ ] Traces appear in Jaeger
- [ ] Logs are flowing to ELK stack
- [ ] HPA is configured and active
- [ ] PDB is in place (min available pods)
- [ ] Network policies are applied
- [ ] Smoke tests pass
- [ ] Load tests show expected performance
- [ ] Alerts are configured in Alertmanager

---

## Emergency Procedures

### Stop All Traffic

```bash
# Scale API to 0 (emergency brake)
kubectl scale deployment/ticketing-api --replicas=0 -n ticketing-prod
```

### Database Failover

```bash
# Update ConfigMap to point to backup Redis
kubectl edit configmap ticketing-config -n ticketing-prod

# Restart pods to pick up new config
kubectl rollout restart statefulset/orleans-silo -n ticketing-prod
kubectl rollout restart deployment/ticketing-api -n ticketing-prod
```

---

## Contact Information

**On-Call Engineer**: oncall@ticketing.example.com
**PagerDuty**: https://ticketing.pagerduty.com
**Slack Channel**: #ticketing-ops
