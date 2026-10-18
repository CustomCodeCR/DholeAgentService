# Production generation orchestration

## Purpose
An operator-controlled service-generation switch for planned maintenance, recurring schedules and verified infrastructure failures. This design is independent of provider-authentication challenges.

The configurable event sources are `manual`, `scheduled` and `technical_failure`. The orchestrator must never infer provider authorization from the readiness of a Docker container.

## Release invariants
- No temporary Git branches are required.
- The destination generation must already exist, be healthy, and have independently verified, authorized application access.
- Never delete profiles, cookies, credentials, persistent volumes, database rows or encryption keys.
- Stop dispatch before transferring workers. Drain current jobs, hold an exclusive lock and reject any duplicate active Worker.
- Preserve circuit-breaker restrictions in the shared database. A switch must not reset or bypass them.
- Validate target health before changing the active generation; roll back the service routing on technical failure.
- Default mode is a read-only dry run, and activation requires an explicit operator confirmation.

## Configurable triggering model
```json
{
  "enabled": false,
  "events": {
    "manual": true,
    "scheduled": false,
    "technical_failure": false
  },
  "cooldown_seconds": 1800,
  "require_drain": true,
  "require_destination_readiness": true
}
```

Operators can schedule an ordinary technical maintenance event through cron, systemd timers or GitHub Actions. Error classification for CAPTCHA, MFA, HTTP 401/403 and rate limits remains owned by the provider circuit breaker; these restrictions are not cleared by the generation orchestrator.

## Deployment
A complete cutover requires registered production deployments, matching Agent image revision, unique browser storage, correct PostgreSQL/Redis configuration and a separately verified healthy standby. Do not activate an unprovisioned generation.

## Implemented components and operation

- `config/production-generation-orchestrator.json`: operator-editable event policy and destination registry.
- `scripts/production-generation-orchestrator.py`: validates a trigger, rejects disabled/unregistered events, verifies a technical Docker failure when selected, enforces an exclusive lock, a fresh destination-readiness record, a cooldown and the pending-request limit.
- `.github/workflows/production-generation-orchestrator.yml`: GitHub Actions entry point for manual requests; contains an editable daily UTC cron for scheduled requests. Scheduled requests are disabled by default.
- `tests/test_production_generation_orchestrator.py`: policy and persistence regression coverage.

All event submission is disabled until an operator sets `enabled` to `true`. Only manual events are initially enabled as a source. The event publisher writes `pending/<uuid>.json` within its protected host state directory. It does not start or stop containers and it does not create sessions.

Plan without modifying runtime:

```bash
python3 scripts/production-generation-orchestrator.py \
  --config config/production-generation-orchestrator.json \
  --event manual --target prod1 --mode plan
```

To submit an event after provisioning and verifying the destination, the host must have a fresh, trusted `ready/prod1.json` marker under its protected state directory with `generation`, `ready: true`, and `verified_at_utc`. This marker records infrastructure readiness only; it is not proof of a successful provider login. The separate executor must independently verify authorization, queue leases and the persisted provider circuit before any switch.

```bash
python3 scripts/production-generation-orchestrator.py \
  --config config/production-generation-orchestrator.json \
  --event manual --target prod1 --mode submit \
  --confirmation AUTHORIZE_GENERATION_EVENT
```

## Pre-provisioning, controlled handoff and GitHub Actions

- `.github/workflows/provision-agent-prod1.yml`: a protected **manual** workflow with `check` and `apply` modes. The apply mode uses the `DholeSystem/master` standby manifest, starts only its Hermes and API and **creates but does not start** its Worker. Production's current containers stay running. Images/keys/network/disk and HTTP health must pass.
- `scripts/production-generation-executor.py`: processes a specific pending event only after checking a separately authorized, recent destination attestation; a stopped pre-provisioned Worker; matching production image IDs, PostgreSQL/Redis, encryption keys and circuit settings; and the live shared database for open Maersk circuit restrictions or Running executions.
- `.github/workflows/production-generation-orchestrator.yml`: push triggers **validation jobs only** on both branches. A manual `workflow_dispatch` provides `plan`, `submit` and `execute`. A scheduled invocation submits a request only if explicitly enabled in the configuration (no automatic executable cutover by default).
- On an approved handoff, stop the original idle Worker, re-check database restrictions, start the pre-existing standby Worker and require a queue pump ready signal; if the standby fails to start, stop it and restart the original Worker. Preserve every existing browser volume and all business data.

### Operational sequence

1. Use the `Provision isolated Agent prod1 standby` GitHub Action, mode `check` then `apply`, with confirmation `PROVISION_AGENT_PROD1`.
2. Independently authenticate and verify authorized provider access in the pre-existing standby environment. Docker and API health do **not** establish provider login success.
3. Record a fresh operator-signed/controlled readiness attestation under the runner's private state directory in `ready/prod1.json`. The executor requires `generation: "prod1"`, `ready: true`, `provider_authorized: true`, `verified_by` and an ISO UTC `verified_at_utc` within the configured maximum age. Do not create this attestation merely from Docker health.
4. Explicitly enable the desired event source in `config/production-generation-orchestrator.json`. It starts with `enabled: false` and scheduled/technical sources off by default.
5. Use the `Production generation orchestrator` workflow's `plan` mode, then `submit` (confirmation `AUTHORIZE_GENERATION_EVENT`) or `execute` (confirmation `AUTHORIZE_VERIFIED_WORKER_HANDOFF`). `execute` creates the request and attempts a Worker handoff. It fails closed if destination or provider clearance is not verified.

### Important limitations

This is a guarded handoff of the **Worker processing owner**, not a public API gateway cutover. The primary public Agent API stays the stable ingress; the standby Agent API and Hermes are independently provisioned. New generations may be configured through the generic `deploy/docker-compose.agent-generation.yml` manifest in DholeSystem; each requires its own persisted isolated browser and Hermes storage and independently authorized session.

An open provider circuit, CAPTCHA, 401/403, rate limit or missing provider authentication blocks a switch. The workflow cannot create, discard or rotate sessions to evade authentication restrictions.

**Deployment state is distinct from repository state:** publishing these workflows does not prove a self-hosted run completed, and provisioning the standby does not mean a production handoff occurred. Inspect the matching workflow run and Agent queue/application health before declaring success.

## Arbitrarily numbered production generations (prodN)

The API uses `agent.runtime_generations` for the registered inventory. From DholeWeb **Agent → Orquestador de entornos**, register `prod2`, `prod3`, etc. Names must match `prod[1-9][0-9]*` and contain no more than 64 characters. Database registration does **not** create or start Docker containers.

For a previously unprovisioned generation, open **Provision isolated Agent production generation** in DholeAgentService GitHub Actions and select `workflow_dispatch` with `generation=prodN`, `mode=check` first. Then choose `mode=apply` and confirmation `PROVISION_INDEPENDENT_GENERATION` to provision its independent volumes, Hermes API and an intentionally **stopped** standby Worker. This only prepares infrastructure; provider authentication must be completed and verified separately.

The generic Compose manifest lives at `DholeSystem/deploy/docker-compose.agent-generation.yml` and uses an independent Compose project `dhole-agent-prodN`, its own browser-profiles volume and Hermes volume, plus an independent encryption keyring initialized one time from the existing protected database key material (not a browser profile/session). PostgreSQL, MongoDB and Redis are shared services, **not copied into each generation**. Never call `docker compose down -v`, prune volumes or reinitialize previously allocated browser profiles.

`scripts/production-generation-status-sync.sh` discovers the actual running Worker across all configured production generations. It refuses to announce an active generation when zero or multiple Workers appear active. The backend's `runtime-generations` endpoints expose inventory, operational rules, pending requests and recorded active generation, with authorization checks. A registered name is not a health claim.

A generation transition is allowed only toward an existing destination with a fresh independent authorized-access attestation, a healthy runtime, no running jobs, and a clear provider circuit. Provider authentication errors, challenges and rate limits remain subject to their own protections; the orchestrator does not rotate browser identities to bypass them.

**Current limitation:** web-created database requests are persistent and visible but **not connected to the host handoff executor**. The separate executor is run via the guarded GitHub Actions workflow for authorized, pre-provisioned standbys. End-to-end switching from the DholeWeb interface is not considered complete until a separately audited host queue bridge is validated and deployed.


## Web-to-GitHub operational orchestration (October 2026)

The Agent API now persists **registration and provisioning** separately. `POST /api/agents/runtime-generations/register` records `agent.runtime_generation_provisions.status=Pending`. The production Worker polls PostgreSQL every 10 seconds and dispatches a GitHub Actions `workflow_dispatch` to `provision-agent-prod1.yml` with `generation=prodN` and `mode=apply`. The existing volumes are never removed or overwritten. The workflow writes `Provisioned` only if infrastructure preparation succeeds; this **does not mean provider authentication is authorized**.

The web's **Solicitar cambio** button inserts one audited request in `agent.runtime_generation_requests`. The production Worker atomically claims it and submits `production-generation-apply.yml` with the **request UUID only**. The self-hosted runner loads its protected database configuration, re-reads and checks that specific ID, verifies that the rule is still enabled and the destination was previously registered, then invokes `production-generation-executor.py`. The executor requires a fresh operator verified, authorized-access marker `ready/prodN.json`, compatible persisted encrypted database keys, separated Chromium profile volumes, one active Worker, no running business executions and non-blocking circuit restrictions. A successful transition updates both request and active generation in PostgreSQL.

Automated triggers are evaluated by the Worker every ~10 seconds, **not by a GitHub cron**:
- `scheduled`: interval from `agent.runtime_generation_rules.schedule_minutes` since the last recorded generation switch.
- `technical_failure`: threshold of recent explicitly allowlisted infrastructure error codes; excludes authentication denials.
- `data_quality`: threshold of recently partially completed Agent extractions, without counting authentication incidents.

Only infrastructure-provisioned numbered generations are eligible for automatic selection. For eligible technical failures, selection prefers the lowest authorized `prodN` strictly above the currently active generation and wraps to the lowest authorized number only when no higher generation is ready. Authorization and provider-circuit guards remain mandatory; a CAPTCHA, denial or rate limit never authorizes a handoff.

New database installations enable the `technical_failure` rule by default, with a one-incident threshold, subject to the existing dispatcher token, fresh readiness and closed-circuit gates. Existing database rules are preserved via `ON CONFLICT DO NOTHING`: production installations with this rule currently disabled must turn on **Por fallos técnicos** in **Agentes → Orquestador de entornos → Reglas automáticas**. The separate legacy JSON workflow publisher remains disabled until explicitly configured. The destination must additionally satisfy independent access authorization at handoff time. Each event is deduplicated in PostgreSQL; queued GitHub requests expire after about 4 minutes 45 seconds, and the Actions job is capped at five minutes. GitHub runner queue times and provider checks are external and cannot be guaranteed to satisfy five minutes.

CAPTCHA, HTTP 401/403, MFA and provider rate limiting are surfaced separately as access incidents and preserved in Maersk's circuit breaker. These events **do not cause an automatic browser identity/environment rotation to circumvent provider restrictions**.

### One-time GitHub credential setup

Create a fine-grained access token or scoped GitHub App credential with **Actions: read and write** permission on `CustomCodeCR/DholeAgentService`. Add it as an Actions repository secret named **`GENERATION_ORCHESTRATOR_GITHUB_TOKEN`**. The protected production deployment copies that secret into the runner's temporary production env file (never source control or workflow output) and sets `GenerationOrchestrator__Enabled=true`. Without that secret, the API reports `githubDispatcherConfigured=false` and the automatic publisher stays disabled; do not mistake saved requests for completed transitions.

This secret must be intentionally provisioned by an authorized repository administrator. GitHub's per-workflow ephemeral `github.token` cannot be reused by a long-running Docker Worker.

### Readiness and live validation

An infrastructure-provisioned environment is not automatically an authorized provider login. Before approving a handoff, verify provider access through an independently authorized interactive session and record the recent authenticated standby readiness in the host's protected `~/.local/state/dhole-production-generation/ready/prodN.json` file. The old Chromium volumes are intentionally untouched. Validate production Worker logs for `GENERATION_DISPATCHER_RUNNING`, and GitHub Actions for the matching UUID, followed by the database request's `Completed` status and a single running Worker.

If the Actions request fails, the current environment is retained or the executor initiates rollback. Check `diagnostic_code` and host health before any operator retry. A failed provisioning job does not delete previous volumes.

The five-minute bound is a **target**, not a promise: workflow scheduling, provider authorization and business job draining may block or reject a transition. Never bypass a circuit breaker to satisfy this latency objective.
