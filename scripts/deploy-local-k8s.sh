#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NAMESPACE="${NAMESPACE:-clouddental}"
KIND_CLUSTER_NAME="${KIND_CLUSTER_NAME:-docker-desktop}"
SKIP_BUILD="${SKIP_BUILD:-false}"

services=(
  "portal:Portal"
  "api-gateway:ApiGateway"
  "patient-service:PatientService"
  "scheduling-service:SchedulingService"
  "claims-service:ClaimsService"
  "eligibility-service:EligibilityService"
  "era-service:EraService"
  "auth-service:AuthService"
  "prescription-service:PrescriptionService"
  "vision-service:VisionService"
)

kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -

if ! kubectl get secret cdo-app-secrets -n "$NAMESPACE" >/dev/null 2>&1; then
  postgres_password="$(openssl rand -hex 24)"
  jwt_key="$(openssl rand -base64 48 | tr -d '\n')"

  kubectl create secret generic cdo-app-secrets \
    --namespace "$NAMESPACE" \
    --from-literal="Postgres__Password=$postgres_password" \
    --from-literal="ConnectionStrings__DefaultConnection=Host=postgres;Database=cdo_portal;Username=cdo;Password=$postgres_password" \
    --from-literal="ConnectionStrings__PatientDb=Host=postgres;Database=cdo_patients;Username=cdo;Password=$postgres_password" \
    --from-literal="ConnectionStrings__SchedulingDb=Host=postgres;Database=cdo_scheduling;Username=cdo;Password=$postgres_password" \
    --from-literal="ConnectionStrings__ClaimsDb=Host=postgres;Database=cdo_claims;Username=cdo;Password=$postgres_password" \
    --from-literal="ConnectionStrings__PrescriptionDb=Host=postgres;Database=cdo_prescriptions;Username=cdo;Password=$postgres_password" \
    --from-literal="ConnectionStrings__VisionDb=Host=postgres;Database=cdo_vision;Username=cdo;Password=$postgres_password" \
    --from-literal="Jwt__Key=$jwt_key" \
    --from-literal="Jwt__Issuer=CloudDentalOffice" \
    --from-literal="Jwt__Audience=CloudDentalOffice"
elif [[ -z "$(kubectl get secret cdo-app-secrets -n "$NAMESPACE" -o jsonpath='{.data.Postgres__Password}')" ]]; then
  # Preserve the password used by an existing local database when upgrading an older overlay.
  existing_connection="$(kubectl get secret cdo-app-secrets -n "$NAMESPACE" \
    -o jsonpath='{.data.ConnectionStrings__DefaultConnection}' | base64 --decode)"
  postgres_password="${existing_connection##*Password=}"
  postgres_password="${postgres_password%%;*}"
  kubectl patch secret cdo-app-secrets -n "$NAMESPACE" --type merge \
    -p "{\"stringData\":{\"Postgres__Password\":\"$postgres_password\"}}"
fi

if [[ "$SKIP_BUILD" != "true" ]]; then
  for entry in "${services[@]}"; do
    image="${entry%%:*}"
    dockerfile="${entry##*:}"
    docker build \
      -t "clouddentaloffice/${image}:local" \
      -f "$REPO_ROOT/infrastructure/docker/${dockerfile}.Dockerfile" \
      "$REPO_ROOT"
  done
fi

if command -v kind >/dev/null 2>&1 && kind get clusters | grep -qx "$KIND_CLUSTER_NAME"; then
  for entry in "${services[@]}"; do
    image="${entry%%:*}"
    kind load docker-image "clouddentaloffice/${image}:local" --name "$KIND_CLUSTER_NAME"
  done
fi

kubectl apply -k "$REPO_ROOT/infrastructure/k8s/local"
kubectl rollout status deployment/postgres -n "$NAMESPACE" --timeout=120s
kubectl rollout status deployment/portal -n "$NAMESPACE" --timeout=240s

printf '\nCloudDentalOffice is deployed in namespace %s.\n' "$NAMESPACE"
printf 'Open it with: kubectl port-forward -n %s service/portal 5000:5000\n' "$NAMESPACE"
