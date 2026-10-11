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

**Not yet implemented:** production standby provisioning, destination authentication validation, consumption/execution of queued requests, Docker routing changes, live rollback and end-to-end deployment. Those steps require separate validation and must not be inferred from event-publisher success.
