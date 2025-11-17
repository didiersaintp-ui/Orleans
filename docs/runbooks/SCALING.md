# Scaling Runbook - Orleans Ticketing Backend

## Overview

This runbook provides procedures for scaling the Orleans Ticketing Backend both horizontally and vertically.

---

## Horizontal Scaling (Adding/Removing Instances)

### Scaling Orleans Silos

#### Manual Scaling

**Scale Up**:
```bash
# Increase to 10 silos
kubectl scale statefulset/orleans-silo --replicas=10 -n ticketing

# Watch the scale operation
kubectl get pods -n ticketing -l app=orleans-silo -w
```

**Scale Down**:
```bash
# Decrease to 5 silos
kubectl scale statefulset/orleans-silo --replicas=5 -n ticketing

# Orleans will gracefully migrate grains
# Monitor the process:
kubectl logs -n ticketing -l app=orleans-silo --tail=100 | grep -i "deactivat"
```

**Important Notes**:
- Orleans automatically rebalances grains when silos are added/removed
- Use gradual scaling (add/remove 1-2 silos at a time) for stability
- Wait 2-3 minutes between scale operations
- Monitor cluster health during scaling: `kubectl logs orleans-silo-0 -n ticketing | grep "cluster"`

#### Automatic Scaling (HPA)

**Check Current HPA Status**:
```bash
kubectl get hpa -n ticketing

# Example output:
# NAME              REFERENCE                  TARGETS         MINPODS   MAXPODS   REPLICAS
# orleans-silo-hpa  StatefulSet/orleans-silo   45%/70%         3         50        5
```

**Adjust HPA Targets**:
```bash
# Increase max replicas
kubectl patch hpa orleans-silo-hpa -n ticketing -p '{"spec":{"maxReplicas":100}}'

# Change CPU target
kubectl patch hpa orleans-silo-hpa -n ticketing -p '{"spec":{"metrics":[{"type":"Resource","resource":{"name":"cpu","target":{"type":"Utilization","averageUtilization":60}}}]}}'
```

**Disable HPA Temporarily**:
```bash
kubectl delete hpa orleans-silo-hpa -n ticketing

# Re-enable by reapplying manifest
kubectl apply -f deploy/k8s/hpa.yaml -n ticketing
```

---

### Scaling API Pods

#### Manual Scaling

**Scale Up**:
```bash
# Increase to 10 API pods
kubectl scale deployment/ticketing-api --replicas=10 -n ticketing

# Verify
kubectl get pods -n ticketing -l app=ticketing-api
```

**Scale Down**:
```bash
# Decrease to 3 API pods
kubectl scale deployment/ticketing-api --replicas=3 -n ticketing

# Rolling termination ensures zero downtime
kubectl rollout status deployment/ticketing-api -n ticketing
```

#### Automatic Scaling (HPA)

**View HPA Status**:
```bash
kubectl describe hpa ticketing-api-hpa -n ticketing
```

**Adjust HPA**:
```bash
# Change min/max replicas
kubectl patch hpa ticketing-api-hpa -n ticketing \
  -p '{"spec":{"minReplicas":5,"maxReplicas":30}}'
```

---

## Vertical Scaling (Adjusting Resources)

### Increase CPU/Memory for Silos

**Edit StatefulSet**:
```bash
kubectl edit statefulset orleans-silo -n ticketing
```

**Update Resource Limits**:
```yaml
resources:
  requests:
    cpu: 2000m        # Was: 1000m
    memory: 4Gi       # Was: 2Gi
  limits:
    cpu: 4000m        # Was: 2000m
    memory: 8Gi       # Was: 4Gi
```

**Trigger Rolling Update**:
```bash
# Pods will restart one by one with new resources
kubectl rollout status statefulset/orleans-silo -n ticketing

# Monitor resource usage
kubectl top pods -n ticketing -l app=orleans-silo
```

### Increase CPU/Memory for API

```bash
kubectl edit deployment ticketing-api -n ticketing

# Update resources, then:
kubectl rollout status deployment/ticketing-api -n ticketing
```

---

## Scaling Based on Load Tests

### Running a Baseline Test

```bash
# Run k6 test
k6 run --env GRPC_SERVER=your-api:5000 --env TEST_MODE=medium tests/k6/load-test-progressive.js

# Monitor during test
watch -n 5 'kubectl top pods -n ticketing'
```

### Interpreting Results

**Indicators for Scaling Up**:
- ✗ P99 latency > 100ms → Add more silos/API pods
- ✗ CPU > 80% sustained → Increase resource limits or add replicas
- ✗ Memory > 85% → Increase memory limits
- ✗ Error rate > 1% → Investigate (may need more resources)

**Indicators for Scaling Down**:
- ✓ CPU < 30% sustained → Reduce replicas
- ✓ P99 latency < 20ms → Over-provisioned

### Sizing Formula (Rule of Thumb)

```
Silos Needed = (Target RPS / RPS per Silo) × 1.2 (safety margin)

Example:
- Target: 30k RPS
- Measured: 2000 RPS per silo (4 vCPU, 8 GB)
- Silos needed: (30000 / 2000) × 1.2 = 18 silos
```

---

## Scaling Redis

### Horizontal Scaling (Cluster Mode)

**Transition to Redis Cluster**:

1. Deploy Redis Cluster (6 nodes: 3 masters + 3 replicas):
   ```bash
   # Using Helm
   helm install redis bitnami/redis-cluster \
     --set cluster.nodes=6 \
     --set cluster.replicas=1 \
     --set persistence.size=50Gi \
     -n ticketing
   ```

2. Update ConfigMap with new connection string:
   ```yaml
   REDIS_CONNECTION: "redis-cluster.ticketing.svc.cluster.local:6379"
   ```

3. Restart silos to use new Redis cluster:
   ```bash
   kubectl rollout restart statefulset/orleans-silo -n ticketing
   ```

### Vertical Scaling (More Memory)

**Using Managed Redis** (recommended for production):
- AWS ElastiCache: Increase instance type (e.g., cache.r6g.large → cache.r6g.xlarge)
- Azure Cache for Redis: Scale up tier
- GCP Memorystore: Increase memory allocation

---

## Scaling for Specific Scenarios

### Scenario 1: Black Friday / Peak Event

**Before Event** (1 week prior):
```bash
# Pre-scale to handle 5x normal traffic
kubectl scale statefulset/orleans-silo --replicas=25 -n ticketing
kubectl scale deployment/ticketing-api --replicas=15 -n ticketing

# Warm up caches
./scripts/warmup-cache.sh  # (create this script to preload accounts)
```

**During Event**:
```bash
# Monitor closely
watch -n 10 'kubectl top pods -n ticketing'

# If needed, scale more
kubectl scale statefulset/orleans-silo --replicas=40 -n ticketing
```

**After Event**:
```bash
# Gradually scale down (wait 30 min between steps)
kubectl scale statefulset/orleans-silo --replicas=20 -n ticketing
# Wait 30 min
kubectl scale statefulset/orleans-silo --replicas=10 -n ticketing
# Wait 30 min
kubectl scale statefulset/orleans-silo --replicas=5 -n ticketing
```

### Scenario 2: Regional Expansion

**Deploy Multi-Region**:
```bash
# Region 1 (US East)
kubectl config use-context us-east-cluster
kubectl apply -f deploy/k8s/ -n ticketing-us

# Region 2 (EU West)
kubectl config use-context eu-west-cluster
kubectl apply -f deploy/k8s/ -n ticketing-eu

# Use global load balancer (AWS Route53, Azure Traffic Manager, GCP Load Balancer)
```

### Scenario 3: Gradual Traffic Migration

**Blue/Green Deployment for Scaling**:

1. Deploy "green" environment with more resources:
   ```bash
   kubectl apply -f deploy/k8s/ -n ticketing-green
   kubectl scale statefulset/orleans-silo --replicas=20 -n ticketing-green
   ```

2. Gradually shift traffic (using ingress weights):
   ```yaml
   # 90% blue, 10% green
   # Then 50/50
   # Then 10% blue, 90% green
   # Finally 100% green
   ```

3. Decommission blue when stable:
   ```bash
   kubectl delete namespace ticketing-blue
   kubectl rename namespace ticketing-green ticketing
   ```

---

## Monitoring During Scaling Operations

### Key Metrics to Watch

```bash
# CPU/Memory usage
kubectl top pods -n ticketing

# Request rate
curl http://localhost:5001/metrics | grep ticketing_validations_total

# Latency
curl http://localhost:5001/metrics | grep ticketing_validation_duration

# Cluster health
kubectl logs orleans-silo-0 -n ticketing | grep "Cluster membership is stable"
```

### Grafana Dashboard Queries

**Throughput**:
```promql
rate(ticketing_validations_total[5m])
```

**Latency P99**:
```promql
histogram_quantile(0.99, rate(ticketing_validation_duration_seconds_bucket[5m]))
```

**Silo Count**:
```promql
count(up{job="orleans-silo"} == 1)
```

---

## Rollback Procedures

### If Scaling Causes Issues

**Immediate Rollback**:
```bash
# Revert to previous replica count
kubectl scale statefulset/orleans-silo --replicas=<previous-count> -n ticketing

# Check recent changes
kubectl rollout history statefulset/orleans-silo -n ticketing

# Rollback to previous revision
kubectl rollout undo statefulset/orleans-silo -n ticketing
```

---

## Capacity Planning

### Estimating Capacity Needs

**Formula**:
```
1. Measure baseline:
   - Users: 10k
   - RPS: 1k
   - CPU: 40% (5 silos)

2. Calculate for target:
   - Target users: 1M
   - Expected RPS: 1k × (1M / 10k) = 100k RPS
   - Silos needed: 5 × (100k / 1k) / 0.4 = ~125 silos

3. Add safety margin: 125 × 1.3 = 163 silos
```

### Benchmarking Checklist

- [ ] Run load test with current config
- [ ] Measure RPS per silo
- [ ] Measure latency percentiles
- [ ] Calculate cost per RPS
- [ ] Determine optimal silo size (cost vs performance)
- [ ] Document findings

---

## Cost Optimization

### Autoscaling Tuning

**Aggressive (cost-optimized)**:
```yaml
targetCPUUtilizationPercentage: 80
scaleDown:
  stabilizationWindowSeconds: 60
```

**Conservative (performance-optimized)**:
```yaml
targetCPUUtilizationPercentage: 50
scaleDown:
  stabilizationWindowSeconds: 600  # 10 minutes
```

### Spot Instances / Preemptible VMs

**Use for non-critical silos** (not minimum required):
```bash
# Label nodes as "spot"
kubectl label nodes <node-name> node-type=spot

# Deploy extra silos on spot nodes
kubectl apply -f deploy/k8s/silo-statefulset-spot.yaml
```

---

## Troubleshooting Scaling Issues

### Silos Not Joining Cluster After Scale-Up

**Check**:
```bash
# Verify headless service
kubectl get svc orleans-headless -n ticketing

# Check DNS resolution
kubectl exec orleans-silo-0 -n ticketing -- nslookup orleans-headless

# View silo logs
kubectl logs orleans-silo-<new-number> -n ticketing | grep -i "membership"
```

**Fix**:
- Ensure RBAC permissions are correct
- Check network policies allow silo-to-silo communication
- Verify Redis is accessible from new silos

### HPA Not Scaling

**Check Metrics Server**:
```bash
kubectl top nodes
kubectl top pods -n ticketing

# If no data:
kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/latest/download/components.yaml
```

---

## Contact

**Escalation**: devops-oncall@ticketing.example.com
**Slack**: #ticketing-scaling
