# Maersk — manual original Chromium profile verification with noVNC

## Operator workflow

1. Leave the Maersk schedules paused. Connect through the existing SSH
   tunnel, for example
   `ssh -N -L 6080:127.0.0.1:6082 dhole-server`.
2. In DholeWeb → Agents → Web sessions, click **Open original profile in
   noVNC** for the existing Maersk credential. Requires the
   `agent.browser-profiles.authenticate` scope.
3. Open `http://localhost:6080/vnc.html` on the operator's machine.
   The queued **MAERSK_SESSION_VERIFY** task uses the active prod1 Worker,
   opening the existing persistent Chromium directory under the same exclusive
   file lock used by automated rate searches.
4. Complete the **authorized** Maersk login in that browser. Manually visit
   `https://www.maersk.com/book/`; account controls must be visible before
   the task will record `Authenticated` and return success.
5. A provider edge-denial / CAPTCHA / authorization restriction is **not
   bypassed**. If access is denied, do not retry repeatedly; request provider
   clearance. The profile stays Blocked unless the official authenticated
   account control can actually be confirmed.

The interactive window stays open for a maximum of seven minutes, or
until verified, cancelled or the Worker shuts down. It never submits stored
credentials, automatically rotates the profile or changes the persistent
circuit. A normal scheduled rate task cannot bypass the Blocked state.

The explicit operator endpoint `POST /api/agents/maersk-circuit/{providerId}/verify-session`
accepts only a valid active Maersk credential and existing profile path.
It deduplicates Queued/Running verification tasks and works with a
persisted operator-open circuit as well as a temporarily Disabled circuit.

## Safety conditions

- No direct public noVNC or VNC listener. Prod1 hosts noVNC on loopback port
  6082 and requires the operator's SSH tunnel.
- Never use a manually launched second Chromium process with the persistent
  `/data/browser-profiles/MAERSK/<credentialId>` directory: exclusive lock
  ownership belongs to the worker process.
- Do not delete `.locks`, profile files, cookies, databases or browser volumes.
- A successful manual check does not automatically close an operator-open
  circuit nor requeue existing WaitingForAuthentication rate executions.
  Reset and resume require their separate audited, authorized gates.
- This change does not alter current password credentials or session storage,
  and does not automatically replay failed bookings/searches.
