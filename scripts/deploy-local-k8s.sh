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
