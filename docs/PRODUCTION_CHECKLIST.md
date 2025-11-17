# Production Readiness Checklist - Orleans Ticketing Backend

Use this checklist before deploying to production to ensure all critical components are configured correctly.

---

## 📋 Pre-Deployment Checklist

### Infrastructure

- [ ] Kubernetes cluster is provisioned and accessible
- [ ] Cluster has sufficient resources (CPU, memory, storage)
- [ ] Multiple availability zones configured (for HA)
- [ ] Network policies are supported (CNI plugin compatibility)
- [ ] Ingress controller is installed and configured
- [ ] Metrics server is running (`kubectl top nodes` works)
- [ ] DNS resolution is working correctly

### Networking

- [ ] LoadBalancer service can provision external IPs
- [ ] Firewall rules allow traffic on required ports:
  - 5000 (gRPC API)
  - 5001 (Metrics)
  - 11111 (Orleans silo-to-silo)
  - 30000 (Orleans gateway)
  - 6379 (Redis)
- [ ] TLS certificates are generated and valid
- [ ] mTLS is configured (if required)
- [ ] DNS records point to load balancer IP

### Storage

- [ ] Redis is deployed (cluster mode for HA)
- [ ] Redis password is set and stored in Kubernetes Secrets
- [ ] Redis persistence is enabled (AOF + RDB)
- [ ] Backup strategy for Redis is configured
- [ ] Storage class supports dynamic provisioning (if using PVCs)

### Security

- [ ] Kubernetes Secrets are created:
  - `ticketing-secrets` (Redis password, TLS certs)
- [ ] RBAC roles and bindings are applied
- [ ] ServiceAccounts are created for silos and API
- [ ] Network policies are applied and tested
- [ ] Container images are scanned for vulnerabilities (Trivy/Snyk)
- [ ] Images are pulled from private registry (if required)
- [ ] ImagePullSecrets are configured
- [ ] Pod security policies / Pod security standards are enforced
- [ ] No secrets in environment variables (use mounted volumes)

---

## 🔧 Configuration Checklist

### ConfigMap (`deploy/k8s/configmap.yaml`)

- [ ] `ORLEANS_CLUSTER_ID` is set to production value
- [ ] `ORLEANS_SERVICE_ID` is set to production value
- [ ] `ASPNETCORE_ENVIRONMENT` is set to `Production`
- [ ] `REDIS_CONNECTION` points to production Redis
- [ ] `JAEGER_ENDPOINT` points to production Jaeger (if enabled)

### Secrets (`deploy/k8s/secret.yaml`)

- [ ] `REDIS_PASSWORD` is strong and rotated
- [ ] TLS certificates are valid and not expired
- [ ] Secrets are encrypted at rest (etcd encryption enabled)

### Resource Limits

**Silos** (`deploy/k8s/silo-statefulset.yaml`):
- [ ] CPU requests: ≥ 1000m (1 vCPU)
- [ ] CPU limits: ≥ 2000m (2 vCPU)
- [ ] Memory requests: ≥ 2Gi
- [ ] Memory limits: ≥ 4Gi
- [ ] Replicas: ≥ 3 (for HA)

**API** (`deploy/k8s/api-deployment.yaml`):
- [ ] CPU requests: ≥ 500m
- [ ] CPU limits: ≥ 2000m
- [ ] Memory requests: ≥ 1Gi
- [ ] Memory limits: ≥ 2Gi
- [ ] Replicas: ≥ 2 (for HA)

### Autoscaling

**HPA** (`deploy/k8s/hpa.yaml`):
- [ ] Silos: `minReplicas` ≥ 3, `maxReplicas` configured for peak load
- [ ] API: `minReplicas` ≥ 2, `maxReplicas` configured for peak load
- [ ] Target CPU utilization: 70-80% (not too aggressive)
- [ ] Scale-down stabilization window: ≥ 300s (5 minutes)

### Pod Disruption Budgets

**PDB** (`deploy/k8s/pdb.yaml`):
- [ ] Silos: `minAvailable` ≥ 2
- [ ] API: `minAvailable` ≥ 1

### Health Checks

**Liveness Probes**:
- [ ] Silos: `initialDelaySeconds` ≥ 60s, `failureThreshold` = 3
- [ ] API: `initialDelaySeconds` ≥ 30s, `failureThreshold` = 3

**Readiness Probes**:
- [ ] Silos: Checks Redis connectivity
- [ ] API: Checks Orleans client connectivity
- [ ] Probes are tested and return correct status

---

## 📊 Observability Checklist

### Metrics (Prometheus)

- [ ] Prometheus is deployed and scraping metrics
- [ ] ServiceMonitor / PodMonitor is configured (if using Prometheus Operator)
- [ ] Metrics endpoints are accessible: `http://<pod>:5001/metrics`
- [ ] Key metrics are being collected:
  - `ticketing_validations_total`
  - `ticketing_validation_duration_seconds`
  - `orleans_grain_activations`
- [ ] Metrics retention is configured (default: 15 days)

### Tracing (Jaeger)

- [ ] Jaeger is deployed (or using managed service)
- [ ] OTLP exporter is configured in silos and API
- [ ] Sample traces are visible in Jaeger UI
- [ ] Trace retention is configured

### Logging

- [ ] Logs are structured (JSON format)
- [ ] Log aggregation is configured (ELK, Loki, CloudWatch, etc.)
- [ ] Correlation IDs are present in logs
- [ ] Log retention policy is configured
- [ ] Log levels are appropriate:
  - Production: `Information` or `Warning`
  - Not `Debug` or `Trace` (too verbose)

### Dashboards (Grafana)

- [ ] Grafana is deployed
- [ ] Prometheus data source is configured
- [ ] Dashboards are imported:
  - `deploy/grafana/dashboard-overview.json`
- [ ] Dashboards display data correctly

### Alerts (Prometheus Alertmanager)

- [ ] Alertmanager is deployed
- [ ] Alert rules are configured: `deploy/prometheus/alerts.yaml`
- [ ] Notification channels are configured:
  - Slack webhook
  - PagerDuty key
  - Email (if applicable)
- [ ] Test alerts are triggered and received
- [ ] On-call rotation is defined

---

## 🧪 Testing Checklist

### Pre-Production Testing

- [ ] Unit tests pass: `dotnet test`
- [ ] Integration tests pass (if any)
- [ ] Smoke tests pass:
  - Health check returns `Healthy`
  - Sample gRPC call succeeds
  - Metrics endpoint is accessible

### Load Testing

- [ ] Load test environment is prepared (separate cluster or namespace)
- [ ] Baseline load test is run (quick mode):
  ```bash
  k6 run --env TEST_MODE=quick tests/k6/load-test-progressive.js
  ```
- [ ] Results meet acceptance criteria:
  - Success rate > 99%
  - P99 latency < 100ms
  - Error rate < 1%
- [ ] System behaves correctly under load:
  - No memory leaks
  - No CPU saturation
  - HPA scales appropriately

### Chaos Testing (Optional but Recommended)

- [ ] Pod deletion is tested (simulate failures)
- [ ] Node failure is tested (drain node)
- [ ] Network partition is tested (network policy temporarily blocks traffic)
- [ ] Redis failover is tested (kill Redis primary)
- [ ] System recovers gracefully from all scenarios

---

## 🚀 Deployment Checklist

### Pre-Deployment

- [ ] Deployment plan is reviewed and approved
- [ ] Rollback plan is documented
- [ ] Maintenance window is scheduled (if downtime is expected)
- [ ] Stakeholders are notified
- [ ] On-call engineer is available during deployment

### Deployment Steps

- [ ] Create namespace: `kubectl create namespace ticketing-prod`
- [ ] Apply secrets: `kubectl apply -f deploy/k8s/secret.yaml -n ticketing-prod`
- [ ] Apply ConfigMap: `kubectl apply -f deploy/k8s/configmap.yaml -n ticketing-prod`
- [ ] Apply RBAC: `kubectl apply -f deploy/k8s/rbac.yaml -n ticketing-prod`
- [ ] Deploy Redis (if not using managed service)
- [ ] Deploy silos: `kubectl apply -f deploy/k8s/silo-statefulset.yaml -n ticketing-prod`
- [ ] Wait for silos to be ready: `kubectl wait --for=condition=ready pod -l app=orleans-silo -n ticketing-prod --timeout=5m`
- [ ] Deploy API: `kubectl apply -f deploy/k8s/api-deployment.yaml -n ticketing-prod`
- [ ] Wait for API to be ready: `kubectl wait --for=condition=ready pod -l app=ticketing-api -n ticketing-prod --timeout=3m`
- [ ] Apply HPA: `kubectl apply -f deploy/k8s/hpa.yaml -n ticketing-prod`
- [ ] Apply PDB: `kubectl apply -f deploy/k8s/pdb.yaml -n ticketing-prod`
- [ ] Apply NetworkPolicy: `kubectl apply -f deploy/k8s/network-policy.yaml -n ticketing-prod`

### Post-Deployment Verification

- [ ] All pods are running: `kubectl get pods -n ticketing-prod`
- [ ] Health checks pass:
  ```bash
  kubectl port-forward -n ticketing-prod svc/ticketing-api 5001:5001
  curl http://localhost:5001/health/ready
  ```
- [ ] gRPC API is accessible:
  ```bash
  grpcurl -plaintext localhost:5000 ticketing.TicketingService/HealthCheck
  ```
- [ ] Metrics are being scraped by Prometheus
- [ ] Logs are flowing to log aggregation system
- [ ] Traces are visible in Jaeger
- [ ] Run smoke test:
  ```bash
  cd src/Ticketing.Client
  dotnet run -- --server <api-external-ip>:5000 --users 10 --duration 30
  ```
- [ ] Monitor for 30 minutes post-deployment:
  - No errors in logs
  - No pod restarts
  - Latency is acceptable
  - CPU/memory usage is normal

---

## 📚 Documentation Checklist

### Required Documentation

- [ ] README.md is up-to-date
- [ ] ARCHITECTURE.md is complete
- [ ] DEPLOYMENT.md runbook is available
- [ ] SCALING.md runbook is available
- [ ] Contact information is correct (on-call, Slack channels)
- [ ] Disaster recovery procedures are documented

### Operational Runbooks

- [ ] Deployment procedures
- [ ] Scaling procedures (up and down)
- [ ] Rollback procedures
- [ ] Incident response procedures
- [ ] Backup and restore procedures

---

## 🛡 Disaster Recovery Checklist

### Backup

- [ ] Redis snapshots are automated (daily RDB + AOF)
- [ ] Backups are stored in durable storage (S3, Azure Blob, GCS)
- [ ] Backup retention policy is defined (e.g., 30 days)
- [ ] Backups are tested (restore to dev environment monthly)

### High Availability

- [ ] Multiple silos are running (≥ 3)
- [ ] Multiple API pods are running (≥ 2)
- [ ] Pods are distributed across availability zones
- [ ] Anti-affinity rules are configured

### Monitoring & Alerting

- [ ] Critical alerts are configured:
  - High error rate
  - High latency (P99 > 100ms)
  - Pods down
  - Redis down
- [ ] Alerts are routed to on-call engineer (PagerDuty, OpsGenie)
- [ ] Runbooks are linked in alert annotations

---

## 🔄 Post-Deployment

### Within 24 Hours

- [ ] Monitor dashboards for anomalies
- [ ] Review error logs
- [ ] Check HPA behavior (scaling up/down appropriately)
- [ ] Verify backup jobs ran successfully

### Within 1 Week

- [ ] Run full load test (medium mode): 100k users
- [ ] Review performance metrics:
  - Compare to baseline
  - Identify any regressions
- [ ] Collect feedback from users (if applicable)
- [ ] Document lessons learned
- [ ] Update runbooks based on deployment experience

### Continuous

- [ ] Monitor alerts and respond to incidents
- [ ] Review Grafana dashboards weekly
- [ ] Rotate secrets quarterly
- [ ] Update TLS certificates before expiration
- [ ] Conduct chaos engineering exercises monthly

---

## ✅ Sign-Off

### Approvals Required

- [ ] **Engineering Lead**: Code review complete, tests pass
- [ ] **DevOps/SRE**: Infrastructure ready, monitoring configured
- [ ] **Security Team**: Security review complete, vulnerabilities addressed
- [ ] **Product Owner**: Acceptance criteria met, ready for production

### Final Check

**I certify that**:
- All items in this checklist have been reviewed and completed
- The system is ready for production deployment
- Rollback procedures are documented and understood
- On-call support is available during and after deployment

**Signed**: ___________________________
**Date**: ___________________________
**Role**: ___________________________

---

## 📞 Emergency Contacts

- **On-Call Engineer**: oncall@ticketing.example.com
- **PagerDuty**: https://ticketing.pagerduty.com
- **Slack Channel**: #ticketing-incidents
- **Escalation Manager**: manager@ticketing.example.com

---

**Remember**: It's better to delay deployment than to rush and cause an outage. Take your time and verify each item thoroughly.

🎯 **Good luck with your production deployment!**
