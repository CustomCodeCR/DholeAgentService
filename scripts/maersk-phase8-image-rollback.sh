#!/usr/bin/env bash
# Safeguard deployed images without modifying PostgreSQL, jobs or browser profiles.
# Runs on the self-hosted Agent runner within a single workflow run.
set -euo pipefail

mode="${1:?Usage: maersk-phase8-image-rollback.sh <capture|mark-deploy|restore>}"
environment="${2:?Environment required: staging|production}"
die() { echo "Phase 8 image rollback: $*" >&2; exit 1; }
case "$environment" in
  staging)
    image_suffix="staging"
    project="dhole-staging"
    ;;
  production)
    image_suffix="latest"
    project="dhole"
    ;;
  *) die "Unknown environment" ;;
esac

[[ "${GITHUB_RUN_ID:-}" =~ ^[0-9]+$ ]] || die "GITHUB_RUN_ID must be a numeric workflow run identifier"
[[ -n "${RUNNER_TEMP:-}" && -d "$RUNNER_TEMP" ]] || die "RUNNER_TEMP must exist"
[[ -r "${DHOLE_ENV_FILE:-}" ]] || die "DHOLE_ENV_FILE is missing"
[[ -r "${COMPOSE_FILE:-}" ]] || die "Compose configuration not found"
command -v docker >/dev/null || die "Docker is required"
base="$RUNNER_TEMP/maersk-phase8-${GITHUB_RUN_ID}-$environment"
manifest="$base.manifest"
started="$base.deploy-started"
compose=(docker compose --env-file "$DHOLE_ENV_FILE" -f "$COMPOSE_FILE")
if [[ "$environment" == staging ]]; then
  [[ -r "${COMPOSE_OVERRIDE:-}" ]] || die "Staging Compose override is missing"
  compose+=(-f "$COMPOSE_OVERRIDE")
fi
compose+=(-p "$project")
suffix="phase8-prev-$environment-$GITHUB_RUN_ID"

case "$mode" in
  capture)
    [[ ! -e "$manifest" && ! -e "$started" ]] || die "Snapshot was already taken for this run"
    images=()
    for service in dhole-agent-api dhole-agent-workers; do
      image="dhole/${service#dhole-}"
      # Exact images in Compose are agent-api and agent-workers, not namespaced service IDs.
      container_id="$("${compose[@]}" ps -q "$service" | head -n 1)"
      if [[ -n "$container_id" ]]; then
        image_id="$(docker inspect --format='{{.Image}}' "$container_id")"
      else
        image_id="$(docker image inspect --format='{{.Id}}' "$image:$image_suffix")" \
          || die "Unable to identify previous image for $service"
      fi
      [[ -n "$image_id" ]] || die "Missing previous image ID for $service"
      rollback_tag="$image:$suffix"
      docker image tag "$image_id" "$rollback_tag"
      images+=("$rollback_tag")
    done
    # Only mark capture successful after BOTH images have protected tags.
    printf 'environment=%s\napi_tag=%s\nworker_tag=%s\n' \
      "$environment" "${images[0]}" "${images[1]}" > "$manifest.tmp"
    mv "$manifest.tmp" "$manifest"
    echo "PHASE8_IMAGES_CAPTURED: previous API and worker images protected for $environment"
    ;;
  mark-deploy)
    [[ -s "$manifest" ]] || die "No verified image snapshot before deploy"
    : > "$started"
    ;;
  restore)
    if [[ ! -f "$started" ]]; then
      echo "PHASE8_ROLLBACK_SKIPPED: deployment was never started"
      exit 0
    fi
    [[ -s "$manifest" ]] || die "Deployment started but previous image manifest is missing"
    expected_api="dhole/agent-api:$suffix"
    expected_worker="dhole/agent-workers:$suffix"
    grep -Fxq "environment=$environment" "$manifest" || die "Snapshot environment mismatch"
    grep -Fxq "api_tag=$expected_api" "$manifest" || die "API backup tag mismatch"
    grep -Fxq "worker_tag=$expected_worker" "$manifest" || die "Worker backup tag mismatch"
    docker image inspect "$expected_api" >/dev/null || die "Previous API image unavailable"
    docker image inspect "$expected_worker" >/dev/null || die "Previous worker image unavailable"
    docker image tag "$expected_api" "dhole/agent-api:$image_suffix"
    docker image tag "$expected_worker" "dhole/agent-workers:$image_suffix"
    # Preserve named volumes and ALL execution records. No schema downgrade.
    "${compose[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api dhole-agent-workers
    echo "PHASE8_ROLLBACK_APPLIED: $environment previous tagged images restored; verify health/queue"
    ;;
  *) die "Unknown mode: $mode" ;;
esac
