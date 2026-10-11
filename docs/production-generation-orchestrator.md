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
