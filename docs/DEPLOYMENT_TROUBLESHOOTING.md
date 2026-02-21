# Digital Ocean Deployment Troubleshooting Guide

## Current Status

❌ **Deployments are failing** - Services timing out during rollout
✅ **Azure AD code merged** to main branch (Feb 19, 2026)
⚠️ **Root cause identified** - Database initialization issues

---

## Problem Summary

### 1. **Missing PostgreSQL Databases**
- Services configured to use PostgreSQL in production
- No databases exist in Digital Ocean
- Connection strings reference non-existent databases:
  - `ConnectionStrings__PrescriptionDb`
  - `ConnectionStrings__VisionDb`
  - `ConnectionStrings__DefaultConnection` (Portal)
  - And more for other services

### 2. **Database Initialization Only Ran in Development**
- Original code only created databases when `ASPNETCORE_ENVIRONMENT=Development`
- Production deployments (`ASPNETCORE_ENVIRONMENT=Production`) skipped database setup
- Services failed health checks because databases don't exist

### 3. **No Kubernetes Health/Readiness Probes**
- Kubernetes couldn't detect when services were unhealthy
- Deployments timed out waiting for pods to become ready
- No automatic restart on failure

---

## Solutions Applied

### ✅ 1. Updated Service Startup Code
**Files Modified:**
- `src/Services/PrescriptionService/Program.cs`
- `src/Services/VisionService/Program.cs`

**Changes:**
- Database initialization now runs in **all environments** (including Production)
- Added try-catch with logging for better error visibility
- Enabled Swagger in production for debugging

### ✅ 2. Added Kubernetes Health Probes
**File Modified:**
- `infrastructure/k8s/clouddental/apps.yaml`

**Changes:**
- Added `livenessProbe` (checks if service is alive)
- Added `readinessProbe` (checks if service is ready for traffic)
- 30-second initial delay to allow database initialization
- Automatic pod restart on health check failures

### ✅ 3. Created Migration Job Template
**File Created:**
- `infrastructure/k8s/clouddental/db-migrations.yaml`

**Purpose:** Optional database migration job (for future use with EF Core migrations)

---

## Required: Set Up PostgreSQL in Digital Ocean

### Option A: Digital Ocean Managed PostgreSQL (Recommended)

1. **Create Database Cluster**
   ```bash
   doctl databases create clouddentaloffice --engine postgres --region nyc3 --size db-s-1vcpu-1gb
   ```

2. **Create Individual Databases**
   ```bash
   CLUSTER_ID="<your-cluster-id>"
   
   doctl databases db create $CLUSTER_ID clouddental_portal
   doctl databases db create $CLUSTER_ID prescription_db
   doctl databases db create $CLUSTER_ID vision_db
   doctl databases db create $CLUSTER_ID patient_db
   doctl databases db create $CLUSTER_ID scheduling_db
   doctl databases db create $CLUSTER_ID claims_db
   doctl databases db create $CLUSTER_ID eligibility_db
   doctl databases db create $CLUSTER_ID era_db
   ```

3. **Get Connection Strings**
   ```bash
   doctl databases connection $CLUSTER_ID
   ```

4. **Update Kubernetes Secrets**
   ```bash
   kubectl create secret generic cdo-app-secrets \
     --namespace=clouddental \
     --from-literal=ConnectionStrings__DefaultConnection="Host=your-db-host;Database=clouddental_portal;Username=doadmin;Password=xxx" \
     --from-literal=ConnectionStrings__PrescriptionDb="Host=your-db-host;Database=prescription_db;Username=doadmin;Password=xxx" \
     --from-literal=ConnectionStrings__VisionDb="Host=your-db-host;Database=vision_db;Username=doadmin;Password=xxx" \
     --from-literal=Jwt__Key="your-secure-jwt-key-at-least-32-characters-long" \
     --from-literal=Jwt__Issuer="https://clouddental.yourdomain.com" \
     --from-literal=Jwt__Audience="https://clouddental.yourdomain.com" \
     --dry-run=client -o yaml | kubectl apply -f -
   ```

### Option B: Use SQLite (Quick Testing Only)

If you just want to test deployment without PostgreSQL:

1. **Change Database Provider in K8s**
   Edit `infrastructure/k8s/clouddental/apps.yaml`:
   ```yaml
   - name: DatabaseProvider
     value: SQLite  # Change from PostgreSQL to SQLite
   ```

2. **Remove connection string references** (SQLite uses local files)

3. **Note:** SQLite is **NOT recommended for production** - use only for testing

---

## Deployment Steps

### 1. Check Current Secrets
```bash
# Connect to your cluster
doctl kubernetes cluster kubeconfig save bf91c5dd-22ae-4778-9e06-5cb2227cd1b4

# Check if secrets exist
kubectl -n clouddental get secret cdo-app-secrets

# View secret keys (without values)
kubectl -n clouddental get secret cdo-app-secrets -o jsonpath='{.data}' | jq 'keys'
```

### 2. Create/Update Secrets
```bash
# If secret doesn't exist, create it (see Option A step 4 above)
# If it exists, you can update specific keys:
kubectl -n clouddental patch secret cdo-app-secrets \
  --type merge \
  -p '{"stringData":{"ConnectionStrings__PrescriptionDb":"Host=..."}}'
```

### 3. Commit and Push Changes
```bash
cd /Users/karkusdog/git/cdo/clouddentaloffice

git add .
git commit -m "fix: Add database initialization for production and health probes"
git push origin main
```

### 4. Monitor Deployment
```bash
# Watch deployment progress
kubectl -n clouddental get pods -w

# Check pod logs if issues occur
kubectl -n clouddental logs -f deployment/prescription-service
kubectl -n clouddental logs -f deployment/vision-service

# Check service endpoints
kubectl -n clouddental get svc
kubectl -n clouddental get ingress
```

### 5. Verify Health Checks
```bash
# Port forward to check health endpoint
kubectl -n clouddental port-forward deployment/prescription-service 5107:5107

# In another terminal:
curl http://localhost:5107/health
```

---

## Expected Timeline

1. **Database Setup**: 15-30 minutes (if using DO Managed PostgreSQL)
2. **Secret Configuration**: 5-10 minutes
3. **Code Deployment**: Automatic via GitHub Actions (5-15 minutes)
4. **Service Startup**: 30-60 seconds per service

---

## Verification Checklist

- [ ] PostgreSQL database cluster created in Digital Ocean
- [ ] Individual databases created for each service
- [ ] Kubernetes secrets updated with correct connection strings
- [ ] Code changes committed and pushed to main branch
- [ ] GitHub Actions workflow completed successfully
- [ ] All pods in `Running` state
- [ ] Health endpoints returning 200 OK
- [ ] Portal accessible via Ingress URL
- [ ] Azure AD authentication working

---

## Common Issues

### Issue: "Failed to initialize database"
**Solution:** Check connection string format and database server accessibility

### Issue: Pods in CrashLoopBackOff
**Solution:** Check pod logs: `kubectl -n clouddental logs <pod-name>`

### Issue: Health checks failing after 30 seconds
**Solution:** Increase `initialDelaySeconds` in health probe configuration

### Issue: Connection refused to database
**Solution:** Ensure database allows connections from Kubernetes cluster IPs

---

## Next Steps for Production

1. **Switch to EF Core Migrations** instead of `EnsureCreatedAsync()`
2. **Add Azure AD configuration** to Kubernetes secrets
3. **Configure production Ingress** with TLS certificates
4. **Set up monitoring** (Azure Application Insights or similar)
5. **Configure backup strategy** for PostgreSQL databases
6. **Review and harden security settings**

---

## Support Commands

```bash
# Get all resources in namespace
kubectl -n clouddental get all

# Describe pod for detailed events
kubectl -n clouddental describe pod <pod-name>

# Get recent deployments
gh run list --workflow=deploy-doks.yml --limit 10

# View workflow logs
gh run view <run-id> --log

# Force new deployment
kubectl -n clouddental rollout restart deployment/prescription-service
```
