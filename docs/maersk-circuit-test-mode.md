# Temporary Maersk circuit test mode

The reset error `maersk_circuit_reset_verification_pending` means that the
original browser session has not authenticated since the restriction or that
another active profile is blocked/expired. The internal circuit may be
temporarily disabled by operator action, but that does not fix provider login.

## How to use

Run **Actions → Maersk circuit - controlled test toggle** on `master`
(production / current prod1 Worker) or `develop` (staging).

- For **disable**, first suspend Maersk schedules, ensure no execution is
  actively running and limit tests to authorized/manual operations. Enter
  `DISABLE_MAERSK_CIRCUIT_SCHEDULES_PAUSED` as the confirmation.
- For **restore**, enter `RESTORE_MAERSK_CIRCUIT`.

The workflow recreates only the currently running Agent API and Worker
services, preserving the existing images and Docker volumes. It refuses to
operate if Docker cannot verify the existing Compose manifests or the known
active Worker. It does not edit PostgreSQL, authentication records, cookies,
browser profiles or `/opt/dhole/.env`.

It creates a private temporary env copy with
`MaerskCircuit__Enabled=false` for manual tests (or `true` to restore),
recreates the targeted containers and verifies the effective setting in
every container. If an operation fails after mutation, it attempts to
recreate affected services with their original host configuration.

When disabled, the Maersk Operations API returns circuit state `Disabled`
and does not enforce the circuit's scheduling stop. **Maersk authentication,
CAPTCHA and provider restrictions still apply.** Do not flood the provider
with queued retries or run automated searches under a denied session.

**This override is temporary**: a subsequent deployment can restore the
host-provided configuration. On any missing/removed Compose file the
workflow fails closed before changing a container; inspect the Docker
Compose paths actually deployed on the host.
