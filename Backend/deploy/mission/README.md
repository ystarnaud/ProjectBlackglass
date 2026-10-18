# Mission host (bg-dev-mission01): the agent, the mission template and the tunnel

Phase 13 Stage 10. Everything here is **written, not installed**. Nothing changes on `bg-dev-mission01` or in the services VM's SSH
configuration until the owner authorizes the infrastructure checkpoint (plan `Docs/superpowers/plans/2026-10-17-phase-13-stage10-mission-agent.md`,
revision 2, "Infrastructure checkpoint").

## What runs, as whom

| Identity | Runs | May write | May read |
|---|---|---|---|
| `blackglass-agent` (no shell) | `blackglass-agent.service`: the agent | `/var/lib/blackglass/agent` (claims, 0700), `/var/lib/blackglass/relay` (delivery records, 0700), `/run/blackglass/launch` (credential handoff, 0700) | the outbox and exit records (group `blackglass-agent`, read-only) |
| `blackglass-mission` (no shell) | `blackglass-mission@<uuid>.service`: one mission process per attempt | its own `/var/lib/blackglass/outbox/<uuid>` and `/var/log/blackglass/<uuid>` only | its credential via `LoadCredential`; other attempts' directories are invisible (mount namespace) |
| `blackglass-tunnel` (no shell) | `blackglass-tunnel.service`: `ssh -N` to the services VM | nothing persistent | `/etc/blackglass/tunnel` (its key, 0600) |
| root | only the template's fixed `ExecStartPre=+` (directory creation) and `ExecStopPost=+` (`record-exit`), and the operator's `emergency-stop` | `/var/lib/blackglass/exits` | |

- **Polkit** (`60-blackglass-mission.rules`): `blackglass-agent` may `start` and `stop` exactly `blackglass-mission@<canonical lowercase uuid>.service`. Every other systemd request from it is refused, including enable, mask, restart, reload, other units and malformed names.
- **Ownership:** every executable and unit file is root-owned and not writable by the agent or a mission process:
  - `/opt/blackglass/agent/releases/<rev>` (0755), `/etc/systemd/system/*.service` (0644), `/etc/polkit-1/rules.d/60-blackglass-mission.rules` (0644);
  - `/etc/blackglass/agent/agent.json` is `root:blackglass-agent 0640`;
  - the host secret `/etc/blackglass/agent/host-secret` is `blackglass-agent 0600`.

## The agent release (A10-D6)

- **Runtime identifier:** `linux-x64`. The release is self-contained and single-file, so no machine-wide .NET runtime is installed.
- **Build:** `deploy/mission/publish-agent.sh <out>`, run from `Backend/` with the .NET 10 SDK on a clean tree. It writes `agent-<rev>/` with:
  - `blackglass-agent` and the native libraries it ships with;
  - `record-exit`;
  - `RELEASE`, holding `revision=`, `runtime=linux-x64` and the SHA-256 of every file. That file is the **release hash** record.
- **Install:** `install.sh` verifies every hash against `RELEASE` before copying the release to `/opt/blackglass/agent/releases/<rev>` (root-owned). It then switches `/opt/blackglass/agent/current` atomically (`ln -sfn` and `mv -T`).
- **Rollback:**
  1. `ln -sfn /opt/blackglass/agent/releases/<previous-rev> /opt/blackglass/agent/current.new && mv -Tf /opt/blackglass/agent/current.new /opt/blackglass/agent/current`.
  2. `systemctl restart blackglass-agent`.

  The agent resumes from its state directory; running units are untouched.
- **Full rollback:** `uninstall.sh --yes` removes the units, the polkit rule and the stub link. It never deletes the outbox, exit records, delivery records, agent state or logs. To retire the tunnel completely:
  - on the services VM, remove `/etc/ssh/authorized_keys.d/bg-api-tunnel` and `/etc/ssh/sshd_config.d/60-bg-api-tunnel.conf`, run `systemctl reload ssh`, and lock or remove the `bg-api-tunnel` account;
  - on the mission VM, remove `/etc/blackglass/tunnel/id_ed25519*`.

## Host secret rotation (A10-D8)

1. On the services VM: `admin host rotate-secret --host-id <id>` (and `host enable` after a disable). This prints the secret once.
2. On the mission VM, as root, replace the file atomically, never putting the secret on a command line or in shell history:
   - `install -o blackglass-agent -g blackglass-agent -m 0600 /dev/null /etc/blackglass/agent/host-secret.new`;
   - `read -rs secret && printf '%s
' "$secret" > /etc/blackglass/agent/host-secret.new && unset secret` (paste at the prompt);
   - `mv -f /etc/blackglass/agent/host-secret.new /etc/blackglass/agent/host-secret`.
3. Run `systemctl reload blackglass-agent` (SIGHUP).

The agent also re-reads the file after a 401. If that secret is unchanged and still refused, the agent:
- stops claims and launches;
- raises one `host_secret_rejected` alert;
- retries only every `authRetrySeconds` (default 60);
- keeps supervising units and preserving results.

A disabled host's old secret never works again; reloading it restores nothing.

## Emergency stop (A10-D5)

`sudo /opt/blackglass/agent/current/blackglass-agent emergency-stop --reason "<why>" --dry-run` lists the active mission units. `--yes` instead of `--dry-run` stops them.
- It needs no backend and no host secret.
- It is audited in `/var/log/blackglass/emergency-stop.log` and the journal.
- It is idempotent.
- It touches only `blackglass-mission@` units: never the outbox, relay or claim state, inventory, settlement, energy or insurance.
- It needs no configuration file.
- **Caveat:** if the agent is running and authenticated, it reports each stopped unit's exit with its outbox scan, and Stage 9's exit-evidence rules apply. A complete scan with a participant absent can then void that participant. To freeze the state for an investigation, stop the agent first: `systemctl stop blackglass-agent`.

## Exit records

`record-exit`, run as root from the template's `ExecStopPost=+`, writes `/var/lib/blackglass/exits/<uuid>.exit` (`code=`, `status=`, `result=`) from systemd's `$EXIT_CODE` and `$EXIT_STATUS`. systemd forgets a cleanly exited unit's status once it collects the unit. Without a record, the agent reports the exit with no status (never void evidence) rather than inventing one.

## The tunnel (A10-D3)

- **Path:** mission VM `127.0.0.1:5081` → SSH as `bg-api-tunnel` → services VM `127.0.0.1:5080`.
- **Mission VM side:**
  - the key pair `/etc/blackglass/tunnel/id_ed25519` is generated on the VM and never leaves it;
  - `ssh_config` pins the host key in `/etc/blackglass/tunnel/known_hosts` with `StrictHostKeyChecking yes` and no global known-hosts file;
  - there is no fallback that accepts an unknown key.
- **Services VM side:**
  - the account `bg-api-tunnel` (shell `/usr/sbin/nologin`);
  - `services-vm-sshd.conf` (a `Match User` block): local forwarding to `127.0.0.1:5080` only; no TTY, command, agent, X11, stream-local or remote forwarding;
  - `services-vm-authorized_keys`: `restrict,port-forwarding,permitopen=…,command="/bin/false"`.
- **Failures:** `Restart=always` with bounded backoff (5 s rising to at most 120 s). A tunnel failure stops no mission and loses no result.

## The stub mission and the VM checks (A10-D7)

- `install.sh --stub` installs `stub/run-mission` as `/opt/blackglass/mission/releases/stub/run-mission`. It points `/opt/blackglass/mission/current` at it only when that link is absent or already the stub.
- `/opt/blackglass/server` is never touched.
- `verify.sh --yes` runs two test attempts under the real template. It checks:
  - polkit;
  - cross-attempt isolation;
  - the agent's read-only access;
  - `LoadCredential`;
  - the exit records.

  It also records `systemd-analyze security`. The test attempts' outbox and exit records are kept as evidence.
