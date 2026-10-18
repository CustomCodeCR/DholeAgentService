#!/usr/bin/env bash
# Ensure a fresh, content-addressed Inspector image exists on the production
# Docker host. This only builds an image; never replaces/starts/stops any Worker
# or browser, touches volumes, changes a generation, or clears a circuit.
set -Eeuo pipefail
set +x
[[ "${GITHUB_REF:-}" == refs/heads/master ]] || {
  echo STANDBY_HUB_PROBE_REF_REFUSED >&2; exit 2;
}
[[ -f Dockerfile && -f NuGet.Config && -f src/Dhole.Agent.Workers/StandbyAccountInspector.cs ]] || {
  echo STANDBY_HUB_PROBE_SOURCE_MISSING >&2; exit 2;
}
# Include all inputs to the worker Docker image, not just the Git revision.
# New UI/API commits will not force expensive, unnecessary Inspector builds.
fingerprint="$(
  { sha256sum Dockerfile NuGet.Config
    find src docker -type f -print0 | sort -z | xargs -0 -r sha256sum
  } | sha256sum | cut -c1-12
)"
[[ "$fingerprint" =~ ^[0-9a-f]{12}$ ]] || exit 2
image="dhole/agent-workers:standby-inspector-$fingerprint"
if docker image inspect "$image" >/dev/null 2>&1; then
  echo STANDBY_HUB_PROBE_IMAGE_ALREADY_PRESENT
else
  [[ -n "${GITHUB_TOKEN:-}" && -n "${NUGET_PACKAGE_USER:-}" ]] || {
    echo STANDBY_HUB_PROBE_PACKAGE_CREDENTIAL_MISSING >&2; exit 2;
  }
  status="$(printf 'user = "%s:%s"\n' "$NUGET_PACKAGE_USER" "$GITHUB_TOKEN" |
    curl --config - --silent --show-error --max-time 20 --output /dev/null \
      --write-out '%{http_code}' \
      https://nuget.pkg.github.com/CustomCodeCR/download/customcodeframework.core/index.json)"
  [[ "$status" == 200 ]] || {
    echo "STANDBY_HUB_PROBE_PACKAGE_ACCESS_REFUSED http_status=$status" >&2
    exit 2
  }
  # Secret is transferred as a BuildKit secret; no token in the image, command
  # arguments or workflow output.
  docker build --target worker-final \
    --build-arg "GITHUB_ACTOR=$NUGET_PACKAGE_USER" \
    --secret id=github_token,env=GITHUB_TOKEN \
    -t "$image" .
  echo STANDBY_HUB_PROBE_IMAGE_BUILT
fi
docker image inspect "$image" >/dev/null
# Sanity-check the .NET runtime without touching a browser or exposing CDP.
docker run --rm --pull never --network none --read-only \
  --tmpfs /tmp:rw,nosuid,nodev,size=32m \
  --cap-drop ALL --security-opt no-new-privileges \
  --entrypoint dotnet "$image" --info >/dev/null
if [[ -n "${GITHUB_ENV:-}" ]]; then
  printf 'STANDBY_HUB_PROBE_IMAGE=%s\n' "$image" >> "$GITHUB_ENV"
fi
echo "STANDBY_HUB_PROBE_READY fingerprint=$fingerprint"
