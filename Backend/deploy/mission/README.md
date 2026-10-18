# Mission host (bg-dev-mission01): the agent, the slot templates, the credential broker and the tunnel

Phase 13 Stage 10, with the security corrections of 2026-10-18 (C1, I3, network listeners). Everything here is **written, not
installed**. Nothing changes on `bg-dev-mission01` or in the services VM's SSH configuration until the owner authorizes the
infrastructure checkpoint (plan `Docs/superpowers/plans/2026-10-17-phase-13-stage10-mission-agent.md`, "Security corrections").
It was validated end to end in a disposable Ubuntu 24.04 systemd container (systemd 255.4-1ubuntu8.18, the mission VM's build).

## What runs, as whom

| Identity | Runs | May write | May read |
|---|---|---|---|
| `blackglass-agent` (static, no shell) | `blackglass-agent.service`: the agent | `/var/lib/blackglass/agent` (claims, 0700), `/var/lib/blackglass/relay` (delivery records, 0700) | the outboxes through its unit's read-only bind of `/var/lib/private/blackglass-outbox` at `/var/lib/blackglass/outbox`; the exit records (group read); the broker's socket (group) |
| `blackglass-mNN` (systemd dynamic user, one per slot 00..63) | `blackglass-mission-NN@<attempt>.service`: one mission process per attempt; one live unit per slot | its own `$STATE_DIRECTORY` (`/var/lib/private/blackglass-outbox/<attempt>`) and `$LOGS_DIRECTORY` only | its credential (`$CREDENTIALS_DIRECTORY/process`); nothing of another attempt (namespace, uid, `ProtectProc=invisible`) |
| root, no capabilities | `blackglass-credential@.service`, one per connection to `/run/blackglass/credential.sock` | `/run/blackglass/credentials` only | what root owns (it passes owner checks), except what its unit makes inaccessible (`/etc/shadow`, `/etc/gshadow`, `/etc/ssh`, `/etc/sudoers*`, the Blackglass state); it reads only `/etc/passwd` and the request on its socket |
| `blackglass-tunnel` (static, no shell) | `blackglass-tunnel.service`: `ssh -N` to the services VM | nothing persistent | `/etc/blackglass/tunnel` (its key, 0600) |
| root | the slot templates' fixed `ExecStartPre=+slot-guard NN %i` and `ExecStopPost=+record-exit %i`, and the operator's `emergency-stop` | `/var/lib/blackglass/exits` | |

- **Slots (C1):** a mission's slot is its port minus the host's port base. Each slot's template binds only that port (where systemd can enforce it, see Listeners).
  - `render-slot-units.sh <portBase> <slots>` writes the templates from `blackglass-mission-slot.service.in`. `install.sh` installs them; there must be no drop-ins.
  - `portBase` and `localCapacity` in `agent.json` must equal the backend's registration (`port_base`; a `capacity` no larger than the slots).
  - The agent starts a slot's unit only when that slot has no live unit. `slot-guard` refuses a second live unit in the slot, the same attempt live in another slot, and a start while any process still runs as the slot's uid (one that survived a final SIGKILL). It runs after systemd has set the unit up (dynamic user, credential, state directory), so a refused attempt is burned, never started.
  - The agent persists each claim's slot and derives its unit from that, never again from `agent.json`. Changing `portBase` or `localCapacity` never redirects a live mission.
  - An attempt's unit runs at most once (`AssertPathExists=!` on its outbox and exit record).
- **Credentials (I3):** the agent hands `prepare <attempt> <generation>\n<pc1 credential>\n` to the root broker's socket (`root:blackglass-agent 0660`; the broker also checks the peer's uid). The broker writes `/run/blackglass/credentials/<attempt>/process` (root 0400 in root 0700), the units' only `LoadCredential` source.
  - The broker never takes a path, never reads a file for the agent, refuses links, other file types, hard links, wrong owners and modes, and lets generations only move forward.
  - The agent starts nothing without its `ok`, and discards the copy after the start.
- **Polkit** (`60-blackglass-mission.rules`): `blackglass-agent` may `start` and `stop` exactly `blackglass-mission-NN@<canonical lowercase uuid>.service` (NN 00..63). Every other systemd request from it is refused, including enable, mask, restart, kill, set-property, transient units, other units (the broker's too) and malformed or retired names.
- **Ownership:** every executable and unit file is root-owned and not writable by the agent or a mission:
  - `/opt/blackglass/agent/releases/<rev>` (0755);
  - `/etc/systemd/system/*.service`, `*.socket` (0644);
  - `/etc/polkit-1/rules.d/60-blackglass-mission.rules` (0644).

  `/etc/blackglass/agent/agent.json` is `root:blackglass-agent 0640`; the host secret `/etc/blackglass/agent/host-secret` is `blackglass-agent 0600`.

## Listeners

- **Missions:**
  - `SystemCallFilter=~listen` (seccomp, enforced on every build): no stream listener, so a mission can never impersonate the tunnel's loopback port.
  - `SocketBindAllow=udp:<the slot's port>` and `SocketBindDeny=any`: the slot's own UDP port only, **enforced only where systemd has `BPF_FRAMEWORK`**. Ubuntu 24.04's doesn't, and `verify.sh` says which.
  - **An enforced restriction is a blocking Stage 12 acceptance requirement** (spec §18.2 B24; the plan lists the options). The rule: a mission binds no address or port except its slot's UDP game port.
  - **Known gap until then:** a compromised mission could bind TCP 127.0.0.1:5081 during a tunnel restart (a host-wide outage of heartbeats, claims, relays and stop directives until `emergency-stop`; no secret leaks), or take another slot's idle game port.
- **The agent:** it sends nothing unless the tunnel port's listener belongs to `tunnelUser`.
- **Agent and broker:** they never listen; `DOTNET_EnableDiagnostics=0` keeps the .NET diagnostic socket closed.

## The release (A10-D6)

- **Runtime identifier:** `linux-x64`, self-contained and single-file, so no machine-wide .NET runtime.
- **Build:** run `deploy/mission/publish-agent.sh <out>` from `Backend/` with the .NET 10 SDK on a clean LF checkout. It writes `agent-<rev>/` with:
  - `blackglass-agent` and `blackglass-credential-broker` with their native libraries;
  - `record-exit` and `slot-guard`;
  - `RELEASE`: `revision=`, `runtime=linux-x64` and the SHA-256 of every file, the **release hash** record.

  It refuses uncommitted changes and CRLF scripts.
- **Install:** `install.sh` verifies every hash against `RELEASE` and requires exactly that file set. It refuses an existing revision, a drop-in, the retired `blackglass-mission@.service`, a static `blackglass-mNN`, CRLF scripts and an `agent.json` whose `portBase`/`localCapacity` differ from `--port-base`/`--slots`. It copies the release to `/opt/blackglass/agent/releases/<rev>` (root-owned), switches `/opt/blackglass/agent/current` atomically, renders the slot templates and starts the broker's socket.
- **Rollback:**
  1. `ln -sfn /opt/blackglass/agent/releases/<previous-rev> /opt/blackglass/agent/current.new && mv -Tf /opt/blackglass/agent/current.new /opt/blackglass/agent/current`.
  2. `systemctl restart blackglass-agent`.

  The agent resumes from its state directory; running units are untouched.
- **Full rollback:** `uninstall.sh --yes` removes the units (slot templates, broker, agent, tunnel), the polkit rule and the stub link, and empties root's tmpfs credential copies.
  - It never deletes the outboxes (`/var/lib/private/blackglass-outbox`), exit records, delivery records, agent state or logs.
  - To retire the tunnel completely: on the services VM, remove `/etc/ssh/authorized_keys.d/bg-api-tunnel` and `/etc/ssh/sshd_config.d/60-bg-api-tunnel.conf`, run `systemctl reload ssh`, and lock or remove `bg-api-tunnel`; on the mission VM, remove `/etc/blackglass/tunnel/id_ed25519*`.

## Host secret rotation (A10-D8)

1. On the services VM: `admin host rotate-secret --host-id <id>` (and `host enable` after a disable). This prints the secret once.
2. On the mission VM, as root, replace the file atomically, never putting the secret on a command line or in shell history:
   - `install -o blackglass-agent -g blackglass-agent -m 0600 /dev/null /etc/blackglass/agent/host-secret.new`;
   - `read -rs secret && printf '%s\n' "$secret" > /etc/blackglass/agent/host-secret.new && unset secret` (paste at the prompt);
   - `mv -f /etc/blackglass/agent/host-secret.new /etc/blackglass/agent/host-secret`.
3. Run `systemctl reload blackglass-agent` (SIGHUP).

The agent also re-reads the file after a 401. If that secret is unchanged and still refused, the agent:
- stops claims and launches;
- raises one `host_secret_rejected` alert;
- retries only every `authRetrySeconds` (default 60);
- keeps supervising units and preserving results.

A disabled host's old secret never works again.

## Emergency stop (A10-D5)

`sudo /opt/blackglass/agent/current/blackglass-agent emergency-stop --reason "<why>" --dry-run` lists the active mission units. `--yes` instead of `--dry-run` stops them.
- It needs no backend, no host secret and no configuration file.
- It is audited in `/var/log/blackglass/emergency-stop.log` and the journal.
- It is idempotent.
- It touches only `blackglass-mission-NN@` units.
- **Caveat:** if the agent is running and authenticated, it reports each stopped unit's exit with its outbox scan, and Stage 9's exit-evidence rules apply. To freeze the state for an investigation, stop the agent first.

## Exit records

`record-exit`, run as root from the templates' `ExecStopPost=+`, writes `/var/lib/blackglass/exits/<uuid>.exit` (`code=`, `status=`, `result=`) from systemd's `$EXIT_CODE` and `$EXIT_STATUS`. systemd forgets a cleanly exited unit's status once it collects the unit. Without a record, the agent reports the exit with no status (never void evidence).

## The tunnel (A10-D3)

- **Path:** mission VM `127.0.0.1:5081` → SSH as `bg-api-tunnel` → services VM `127.0.0.1:5080`.
- **Mission VM side:**
  - the key pair `/etc/blackglass/tunnel/id_ed25519` is generated on the VM and never leaves it;
  - `ssh_config` pins the host key in `/etc/blackglass/tunnel/known_hosts` with `StrictHostKeyChecking yes` and no global known-hosts file;
  - there is no fallback that accepts an unknown key.
- **Services VM side:**
  - the account `bg-api-tunnel` (`nologin`);
  - `services-vm-sshd.conf`: local forwarding to `127.0.0.1:5080` only; no TTY, command, agent, X11, stream-local or remote forwarding;
  - `services-vm-authorized_keys`: `restrict,port-forwarding,permitopen=…,command="/bin/false"`.
- **Failures:** `Restart=always` with bounded backoff (5 s to 120 s). A tunnel failure stops no mission and loses no result.

## The stub mission and the VM checks (A10-D7)

- `install.sh --stub` installs `stub/run-mission` as `/opt/blackglass/mission/releases/stub/run-mission`. It points `/opt/blackglass/mission/current` at it only when that link is absent or already the stub; `/opt/blackglass/server` is never touched.
- `verify.sh --yes` runs test attempts under the real slot templates and the real broker. It checks:
  - polkit;
  - distinct uids;
  - isolation in both directions (outbox, environment, memory, descriptors, `/proc/<pid>/root`, signals, credentials, logs);
  - the listeners;
  - the agent's read-only access;
  - the broker's refusals, generations and concurrency;
  - the slot guard and the single start;
  - uid reuse;
  - the delivered credential's exact bytes;
  - the exit records.

  It writes a state file; after a reboot, `verify.sh --yes --after-restart <file>` checks every result is still there, unchanged and readable. It refuses a host whose `/` isn't a shared mount.
