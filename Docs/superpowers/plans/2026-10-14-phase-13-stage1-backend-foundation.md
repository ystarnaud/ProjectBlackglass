# Phase 13 Stage 1: backend foundation, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-14. Status: **structure approved by the owner as a planning direction; revised for the owner's Stage 1 rulings 1–6 (2026-10-14).** Nothing in it has been implemented or installed. E1 is limited to the SSH alias and read-only inspection until the owner approves the installation.

**Goal:** a .NET 10 backend skeleton that can migrate, back up and restore a PostgreSQL 17 database and serve `GET /health`. Everything is tested against a real PostgreSQL on `bg-dev-services01` and stays loopback-only.

**Architecture:**
- **Projects.** `Accounts.Persistence` holds the migrator: embedded, checksummed, forward-only SQL files. `Accounts.Api` is a minimal ASP.NET Core host with two commands, `serve` and `migrate`. `Accounts.Integration.Tests` holds xUnit tests: pure ones run anywhere, and database ones run against a dedicated test cluster on `bg-dev-services01`, reached through an SSH tunnel.
- **Deployment.** Deploy, backup and restore are shell scripts around Docker Compose on the VM.
- **Scope.** There are no gameplay, account, energy, insurance or settlement tables or endpoints.

**Tech stack:**
- .NET 10 (SDK 10.0.x), ASP.NET Core minimal APIs;
- Npgsql and Dapper (the latest stable versions, resolved by `dotnet add package` and recorded);
- xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.10.1 and xunit.runner.visualstudio 4.0.0, the versions the repo already uses in `Tools/BlackglassAssetStudio`;
- PostgreSQL 17 (`postgres:17` image), Docker Engine ≥ 28 with the Compose plugin, bash.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`, revision 2.1: §2.3 layout, §4.4 secrets, §4.5 exposure, §15.4 migrations, §18.3 invariants, B19. The stage-level plan is `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`, Stage 1. Decision 048.

## Global Constraints

- **Ports.** Every listener is loopback only.
  - The API is published as `"127.0.0.1:5080:8080"`.
  - PostgreSQL in the accounts stack has **no `ports:`** and sits on an `internal: true` network.
  - The test cluster's only port is `"127.0.0.1:55432:5432"`.
  - Nothing binds `0.0.0.0` or `[::]`.
- **Docker.** Docker Engine ≥ 28. `/etc/docker/daemon.json` sets `"ip": "127.0.0.1"` and `default-network-opts.bridge."com.docker.network.bridge.host_binding_ipv4": "127.0.0.1"` before Docker first starts.
- **VM changes.** No `ufw`, nftables, sshd or resolver change in Stage 1. SSH must keep working after every VM step.
- **Packages.** Only Npgsql, Dapper and the xUnit test packages. No EF Core, no JWT or Identity library, no test-host package such as `Microsoft.AspNetCore.Mvc.Testing`.
- **Migrations.**
  - Files are `Backend/db/migrations/NNNN_lower_snake_name.sql`, contiguous from `0000`.
  - The SHA-256 is taken over UTF-8 text with any BOM removed and CRLF changed to LF.
  - Each file runs in its own transaction with `lock_timeout` set (default 5 s), and is forward-only.
  - A changed or renamed applied file stops the migrator.
- **Roles.**
  - `accounts_migrator` owns the database and runs DDL.
  - `accounts_app` gets DML only, through default privileges, and only `SELECT` on `schema_migrations`.
  - Neither role is a superuser, and neither can create roles or databases.
- **Startup.** `migrate` is a separate command, never run when the API starts. The API refuses to start, with exit code 3, when the schema is behind, has drifted, or is **ahead** with versions it doesn't know (ruling 4). Running an older API against a newer schema needs an explicit, tested compatibility declaration, which is a separate future design; Stage 1 has none.
- **Deploy order.** Backup (`pg_dump -Fc`), then `migrate`, then start the API.
- **Secrets.**
  - No secret in Git, logs, error text, `ToString()` or HTTP bodies. Passwords come from files named by `*_FILE` variables, never from inline environment values.
  - **One restrictive file per consumer (ruling 5).** Each file is mode 0400 and owned by the uid of the one container user that reads it. They sit in root-owned 0700 directories: `<stack>/postgres/`, `<stack>/migrate/` and `<stack>/api/`. No file is shared between services, and none is made world-readable. See Task 5.
  - The live stack's secrets are under `/opt/blackglass/accounts/`, as the owner approved. The test cluster has its own credentials, secrets directory (`/opt/blackglass/backend/testdb-secrets/`) and data volume (ruling 2).
  - **VM commands run through `sudo`, including `sudo docker`.** Nobody is added to the `docker` group.
  - VM paths are `/opt/blackglass/...`. IP addresses and key file names never appear in the repo; scripts use the `bg-dev-services01-admin` SSH alias (`admin` with `sudo`). Root SSH login is disabled on the VM.
- **Repository.**
  - Backend line endings: `Backend/**/*.sh`, `*.sql`, `*.yaml` and `Dockerfile` are LF in the working tree.
  - Paths this stage may touch: `Backend/**`, `.gitignore`, `.gitattributes`, the stage-level plan and this plan. Task 13 also touches the spec's §2.3, §4.4 and §4.5 rows, whose exact text is given there.
  - The owner's `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` and `.claude/` are never staged.
- **Evidence and commits.**
  - Test counts, transcripts and the diff stat go to `.claude/scratchpad/phase13/stage-01/`, which is not committed. A report says "N passed, 0 failed", never just "tests pass".
  - Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Provisional values.** `lock_timeout` 5 s, the migrator's lock wait of 60 s and the health timeout of 2 s are configuration, not rules.

## Review Focus

1. **Line endings.** A migration checked out with CRLF on Windows (`core.autocrlf=true`) and with LF in the Linux image must produce the same checksum. Otherwise the deployed migrator refuses a database migrated from the PC. Pinned in Task 2 (CRLF and BOM tests) and Task 6 (a CRLF copy of an applied file is not drift), and backed by `.gitattributes` (Task 1).
2. **The test fixture pointed at the wrong cluster.** The destructive fixture creates and drops databases, so a tunnel left pointing at a real cluster would be dangerous. It must refuse unless `cluster_name = 'blackglass-test'`. Pinned in Task 5.
3. **Two migrators at once,** for example a re-run deploy alongside an operator. Each script must apply exactly once. A second migrator waits, then does nothing; a stuck one gives a clear "busy" error, never a primary-key crash. Pinned in Task 6.
4. **A backup taken while writes are happening.** The recorded row counts must describe exactly what the dump contains, so the drill doesn't fail falsely or pass falsely. Pinned in Task 11: the snapshot is held open while a writer runs, and the recorded count must be below the live count.
5. **A secret leaking through an error path,** such as a wrong password, an unreachable database or a 503. Pinned in Task 4 (`ToString`, the data source's connection string) and Task 9 (command output and health bodies).

The plan also covers the first-boot race, where the official image's temporary init server is "ready" on its socket before the real server listens on TCP. The Compose health check uses `pg_isready -h 127.0.0.1` (Task 10), and the smoke test (Task 12) always starts on a fresh volume.

---

## Part A: environment preparation (E1), before Task 5

**Why this step exists.** The stage-level plan put Docker installation in Stage 17, but Stage 1's integration tests need PostgreSQL 17 in Docker. E1 installs only Docker Engine ≥ 28 and the Compose plugin on `bg-dev-services01`, with the loopback daemon defaults, so Stage 1 can test against a real database.
- **Done early in E1, with the owner's approval:** key-only SSH, no root SSH login and the LLMNR drop-in.
- **Stays in Stage 17:** `ufw`, the live stack, the backup timer and the off-host IPv4 and IPv6 TCP and UDP scans of the live stack.
- **Stage 17 still re-verifies** the Docker version, `daemon.json`, and the sshd and resolved drop-ins.

**Known facts (from the owner):** Debian 13, 4 vCPUs, about 8 GiB of memory, an 80 GB disk, SSH verified, public networking configured, Docker not yet established.

**Planning assumptions, confirmed by E1.1:**
- **Debian's own Docker is too old.** Debian 13's `docker.io` is older than 28, so Docker's official apt repository (`download.docker.com/linux/debian`) is the source. E1.1 shows the candidate version.
- **The resolver may differ from Ubuntu.** Debian doesn't always run `systemd-resolved`, so the spec's Ubuntu-based LLMNR row might not apply. E1.1 found that it does run, with LLMNR public on 5355; E1 disabled LLMNR with a `resolved.conf.d` drop-in (record below).
- **Docker changes the host's routing rules.** Installing it enables IP forwarding, sets the `FORWARD` policy to DROP, and adds NAT chains. It adds nothing to `INPUT` and doesn't change `ufw`, nftables or sshd configuration, so SSH is unaffected. E1.4 checks that.

Tasks 1 to 4 need no database. Task 5 needs E1, which is done (2026-10-14).

### E1 record (done 2026-10-14, owner-approved; evidence in `.claude/scratchpad/phase13/stage-01/env/`)

**Access (E1.0).**
- The Blackglass public key was appended to `/home/admin/.ssh/authorized_keys`. The owner's existing key was kept, and the original was backed up as `authorized_keys.bak-2026-10-14`.
- A PC alias, `bg-dev-services01-admin`, was added (`User admin`, the dedicated Blackglass key, `IdentitiesOnly yes`).
- **All VM work uses this alias and `sudo -n`, including `sudo docker`.** Nobody is in the `docker` group; the package created the group empty.
- The owner verified the hypervisor console login as `admin` and the host-key fingerprint ED25519 `SHA256:CCiFnZA0/WQDUAi6RbkM8Etemf42NTS+DoOcsY+Fpis`.
- The owner's old root alias `bg-dev-services01` no longer logs in, because root SSH login is disabled (see below).

**Inventory (E1.1) and review (E1.2).** None of the stop-and-ask conditions applied:
- no Debian Docker, podman, containerd or runc installed;
- no overlap with Docker's address pools;
- no firewall ruleset at all: iptables-nft with every policy ACCEPT, and no nftables, ufw or firewalld;
- the clock is NTP-synchronized.

**Install (E1.3).** Installed from Docker's Debian 13 repository (`trixie`, `stable`), with every version pinned and checked as available before install:

| Package | Version |
|---|---|
| `docker-ce`, `docker-ce-cli` | `5:29.9.0-1~debian.13~trixie` |
| `containerd.io` | `2.4.2-2~debian.13~trixie` |
| `docker-buildx-plugin` | `0.38.0-1~debian.13~trixie` |
| `docker-compose-plugin` | `5.6.0-1~debian.13~trixie` |

- The signing key's fingerprint `9DC858229FC7DD38854AE2D88D81803C0EBFCD88` was checked before the key was trusted.
- `daemon.json` was written before Docker's first start:
  ```json
  { "ip": "127.0.0.1",
    "default-network-opts": { "bridge": { "com.docker.network.bridge.host_binding_ipv4": "127.0.0.1" } } }
  ```
- `dockerd --validate` passed. The daemon runs storage overlayfs, the systemd cgroup driver on cgroup v2, and AppArmor, seccomp and cgroupns.
- **Script pitfall.** Under `set -o pipefail`, `apt-cache madison … | grep -q` fails even when it matches, because `grep -q` exits early and `apt-cache` gets SIGPIPE. Capture the output first, then grep.
- **Recommended packages that came in:**
  - `nftables`: its service is **disabled**, and must stay disabled until the firewall review, because its default `/etc/nftables.conf` starts with `flush ruleset`;
  - `docker-ce-rootless-extras`: installed, nothing uses it;
  - `git`, `perl`, `pigz`, `patch`.

**Verification (E1.4).** The probes published ports **without** a host address, so they tested the daemon defaults. Busybox httpd served TCP and busybox `nc -l -u` served UDP.
- **Default bridge and Compose network:** probes ran on the default bridge (`18080/tcp`, `18082/udp`) and on a Compose-created network (`18081/tcp`, `18083/udp`).
  - The Compose network inherited `host_binding_ipv4=127.0.0.1` with no `driver_opts`.
  - `docker port`, `ss` and the DNAT rules (`-d 127.0.0.1/32`) showed only `127.0.0.1`. Nothing bound to `0.0.0.0` or `[::]`.
- **From the VM itself:** loopback answered. `[::1]` and the VM's own public IPv4 and IPv6 addresses did not.
- **Off-host:** the PC tested IPv4 (it has no IPv6 route); `bg-dev-mission01` tested IPv4 and IPv6.
  - Port 22 was open everywhere; 18080 and 18081 were closed.
  - UDP datagrams to 18082 and 18083 never reached the listeners. A loopback control datagram, sent afterwards, did.
- **Docker's host changes:**
  - `net.ipv4.ip_forward` went 0 to 1; IPv6 forwarding stays 0, since there are no IPv6 Docker networks.
  - The IPv4 `FORWARD` policy became DROP. Docker added `DOCKER-*` chains and MASQUERADE for `172.17.0.0/16`.
  - There are no `INPUT` rules.
  - Routes gained only the link-down `docker0` routes (`172.17.0.0/16`, `fe80::/64`); the IPv6 default route is unchanged.
- **Cleanup:** probes, networks and the image were removed. TCP and UDP listeners after cleanup were identical to before the install. Fresh `admin` SSH sessions worked throughout.

**SSH hardening (owner-approved, separate changes, each with a backup and a 10-minute automatic rollback timer cancelled only after verification).** Drop-in `/etc/ssh/sshd_config.d/10-blackglass-hardening.conf` sorts before `50-cloud-init.conf`, and sshd keeps the first value it reads. The provider's `/etc/cloud/cloud.cfg.d/99-synteq-pwauth.cfg` (`ssh_pwauth: true`) is left alone; the drop-in wins over it either way.
1. **Key-only:** `PasswordAuthentication no`, `KbdInteractiveAuthentication no`, `PermitEmptyPasswords no`. Key logins for admin and root were then verified, and password attempts got `Permission denied (publickey)`.
2. **No root login:** `PermitRootLogin no`, applied after admin key login and sudo were re-verified. Fresh admin login and sudo were then verified, and root got `Permission denied (publickey)`.

**LLMNR (owner-approved, separate change).**
- Drop-in `/etc/systemd/resolved.conf.d/10-blackglass-no-llmnr.conf` sets `LLMNR=no` and `MulticastDNS=no`. No networkd file set per-link LLMNR.
- After a `systemd-resolved` restart, 5355 listens nowhere (TCP and UDP, IPv4 and IPv6), and off-host TCP checks from `bg-dev-mission01` show it closed.
- DNS still resolves over IPv4 and IPv6.

**Final public listeners:** only sshd on `0.0.0.0:22` and `[::]:22`. The resolver stubs are `127.0.0.53` and `127.0.0.54`. **`ufw` was not touched.**

**Reboot validation (2026-10-14, owner-requested, console available).** After a controlled reboot, all nine checks passed:
- **Logins:** the fresh `admin` key login and `sudo -n` work. Root and password logins are refused.
- **Services:** docker and containerd are active, with no warnings or failed units. Compose `up` and `down` work.
- **Loopback default:** `daemon.json` is unchanged and still in effect on the default bridge, on new networks and on Compose-published TCP and UDP ports.
- **Firewall:** Docker's `filter` and `nat` chains came back identical, and `ip_forward` is 1. `nftables.service` stays disabled; `ufw` is not installed.
- **Network:** addresses, both default routes, DNS, and HTTPS over IPv4 and IPv6 all work.
- **LLMNR:** off, and 5355 is closed.
- **Public listeners:** only sshd on 22. The published test ports were closed off-host over IPv4 and IPv6, and UDP datagrams sent to them never arrived.
- **Cleanup:** complete.

The only differences from before the reboot were expected:
- the empty `raw` table is absent until a port is published (Docker recreated its `raw` DROP rules during the Compose check);
- `docker0` has no IPv6 link-local address until a container attaches to it.

Evidence: `e1-reboot-summary.txt`.

**Maintenance follow-ups (not done in E1):**
- 61 Debian upgrades are pending; no bulk upgrade was run.
- **Docker update policy is an open owner decision.** `unattended-upgrades` covers Debian origins only, so Docker stays at the E1 pins until upgraded deliberately.

**Backups on the VM** are in `/root/e1-snapshots/` (0700): network snapshots before the install, after it and at the end, plus the sshd and resolved backups.

**Rollback**, if the owner wants it (as `admin` with `sudo`):
```
# Docker
apt-get purge docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin docker-ce-rootless-extras
rm -rf /var/lib/docker /var/lib/containerd /etc/docker /etc/apt/sources.list.d/docker.sources /etc/apt/keyrings/docker.asc
# (a reboot then clears Docker's iptables chains and the forwarding setting)
# SSH hardening: re-enable root login first, keep a session open
rm /etc/ssh/sshd_config.d/10-blackglass-hardening.conf && sshd -t && systemctl reload ssh
# LLMNR
rm /etc/systemd/resolved.conf.d/10-blackglass-no-llmnr.conf && systemctl restart systemd-resolved
```

---

## Part B: implementation tasks

### File map

```
.gitignore                                   + Backend exceptions (csproj/sln are globally ignored) and Backend bin/obj
.gitattributes                               + LF for Backend scripts, SQL, YAML, Dockerfile
Backend/
  Backend.sln
  Directory.Build.props                      net10.0, nullable, warnings as errors
  Directory.Packages.props                   central package versions
  global.json                                SDK 10.0.100, rollForward latestFeature (the sdk:10.0 image may be an older feature band than the PC)
  Dockerfile, .dockerignore                  the API image (also runs `migrate`)
  README.md                                  how to run tests, the tunnel, and the VM scripts
  db/migrations/0000_foundation.sql          database and schema privileges for the two roles
  db/invariants/0000_schema_migrations_contiguous.sql
  src/Accounts.Persistence/
    Accounts.Persistence.csproj              embeds db/migrations/*.sql
    DbSettings.cs                            env + password file -> NpgsqlDataSource; never prints the password
    Migrations/MigrationScript.cs            one parsed file
    Migrations/MigrationCatalog.cs           parse, normalize, checksum, ordering rules, embedded loading
    Migrations/SchemaComparison.cs           pure: catalog vs applied rows -> Current/Behind/Ahead/Drift
    Migrations/SchemaMigrationsTable.cs      DDL and read of schema_migrations
    Migrations/Migrator.cs                   advisory lock, per-file transactions, target version
    Migrations/SchemaGuard.cs                the API's startup check
    Migrations/MigrationExceptions.cs
  src/Accounts.Api/
    Accounts.Api.csproj
    Program.cs                               -> Cli.RunAsync
    Cli.cs, ExitCodes.cs, ProcessEnvironment.cs
    ApiHost.cs                               URL resolution (loopback default)
    ServeCommand.cs, MigrateCommand.cs, HealthEndpoint.cs
  tests/Accounts.Integration.Tests/
    Accounts.Integration.Tests.csproj
    Unit/...                                 no database (catalog, comparison, settings, URLs, compose files)
    Database/...                             real PostgreSQL ([Trait("Requires","Postgres")])
    Support/TestCluster.cs, TestDatabase.cs, DatabaseFixture.cs, Catalogs.cs, InvariantRunner.cs, ComposeText.cs
  deploy/services/
    compose.yaml                             the accounts stack (postgres, migrate [profile tools], api)
    compose.test.yaml                        the integration-test cluster (127.0.0.1:55432)
    initdb/00-roles.sh                       first-start role and database creation
    make-secrets.sh                          password files, once
    sql/row-counts.sql                       exact counts + schema_migrations digest
    backup.sh, restore-drill.sh, deploy.sh, check-exposure.sh
    tests/drill-test.sh, tests/smoke-test.sh (VM)
  tools/
    vm-sync.sh                               git archive HEAD:Backend -> /opt/blackglass/backend/src on the VM
    testdb.sh                                up | tunnel | down | destroy for the test cluster
    offhost-probe.sh                         from the PC: listed ports must be closed, 22 open
```

Run commands from `Backend/` unless a step says otherwise. Use these two test commands:
- `UNIT` = `dotnet test Backend.sln --filter "Requires!=Postgres"`, which runs anywhere;
- `ALL` = `dotnet test Backend.sln`, which needs `bash tools/testdb.sh tunnel` running in another terminal.

---

### Task 1: Solution scaffold and repository plumbing

**Files:**
- Modify: `.gitignore`, `.gitattributes`
- Create:
  - `Backend/Backend.sln`, `Backend/Directory.Build.props`, `Backend/Directory.Packages.props`, `Backend/global.json`;
  - `Backend/src/Accounts.Persistence/Accounts.Persistence.csproj`;
  - `Backend/src/Accounts.Api/Accounts.Api.csproj`, `Backend/src/Accounts.Api/Program.cs`, `Backend/src/Accounts.Api/ApiHost.cs`;
  - `Backend/tests/Accounts.Integration.Tests/Accounts.Integration.Tests.csproj`;
  - `Backend/tests/Accounts.Integration.Tests/Unit/ApiHostTests.cs`.

**Interfaces:**
- Produces: `ApiHost.DefaultUrl` (`"http://127.0.0.1:5080"`) and `ApiHost.ResolveUrls(IReadOnlyDictionary<string, string?> env) : string?`.

- [ ] **Step 1: Repository plumbing.** Append to `.gitignore`:

```gitignore

# Phase 13 backend (.NET solution under Backend/; the global ignores above hide csproj/sln files)
!/Backend/Backend.sln
!/Backend/src/**/*.csproj
!/Backend/tests/**/*.csproj
/Backend/**/bin/
/Backend/**/obj/
/Backend/**/secrets/
```

Append to `.gitattributes`:

```gitattributes

# Phase 13 backend files that run or are read on Linux keep LF in the working tree (migration checksums
# also normalize CRLF, but scripts and SQL must be byte-identical on the VM).
Backend/**/*.sh      eol=lf
Backend/**/*.sql     eol=lf
Backend/**/*.yaml    eol=lf
Backend/Dockerfile   eol=lf
```

- [ ] **Step 2: Build props and the solution.**

`Backend/Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`Backend/Directory.Packages.props`:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
  </ItemGroup>
</Project>
```

`Backend/global.json`:
```json
{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
```

`Backend/src/Accounts.Persistence/Accounts.Persistence.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Blackglass.Accounts.Persistence</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <!-- One copy of the migrations: the files in db/migrations, embedded so the image needs no file layout. -->
    <EmbeddedResource Include="..\..\db\migrations\*.sql" LogicalName="migrations.%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

`Backend/src/Accounts.Api/Accounts.Api.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <AssemblyName>Accounts.Api</AssemblyName>
    <RootNamespace>Blackglass.Accounts.Api</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Accounts.Persistence\Accounts.Persistence.csproj" />
  </ItemGroup>
</Project>
```

`Backend/tests/Accounts.Integration.Tests/Accounts.Integration.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Blackglass.Accounts.IntegrationTests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\src\Accounts.Api\Accounts.Api.csproj" />
    <ProjectReference Include="..\..\src\Accounts.Persistence\Accounts.Persistence.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

`Backend/src/Accounts.Api/Program.cs` (replaced in Task 9):
```csharp
// The command-line entry point is wired in Task 9 (Cli.RunAsync).
return 0;
```

Create the solution: `dotnet new sln -n Backend --format sln`, then `dotnet sln Backend.sln add src/Accounts.Persistence/Accounts.Persistence.csproj src/Accounts.Api/Accounts.Api.csproj tests/Accounts.Integration.Tests/Accounts.Integration.Tests.csproj`.

- [ ] **Step 3: Write the failing test.** `Backend/tests/Accounts.Integration.Tests/Unit/ApiHostTests.cs`:

```csharp
using Blackglass.Accounts.Api;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class ApiHostTests
{
    [Fact]
    public void Without_configuration_the_api_binds_loopback_only()
    {
        Assert.Equal("http://127.0.0.1:5080", ApiHost.ResolveUrls(new Dictionary<string, string?>()));
        Assert.Equal(ApiHost.DefaultUrl, ApiHost.ResolveUrls(new Dictionary<string, string?>()));
    }

    [Fact]
    public void An_explicit_url_wins()
    {
        var env = new Dictionary<string, string?> { ["ASPNETCORE_URLS"] = "http://127.0.0.1:0" };
        Assert.Equal("http://127.0.0.1:0", ApiHost.ResolveUrls(env));
    }

    [Fact]
    public void Inside_the_container_the_image_port_setting_is_left_to_aspnet()
    {
        var env = new Dictionary<string, string?> { ["ASPNETCORE_HTTP_PORTS"] = "8080" };
        Assert.Null(ApiHost.ResolveUrls(env));
    }
}
```

- [ ] **Step 4: Run it and confirm it fails.** Run `dotnet test Backend.sln --filter "FullyQualifiedName~ApiHostTests"`. Expected: a build error, because `ApiHost` doesn't exist.

- [ ] **Step 5: Implement.** `Backend/src/Accounts.Api/ApiHost.cs`:

```csharp
namespace Blackglass.Accounts.Api;

public static class ApiHost
{
    /// <summary>Loopback only (spec §4.5). In the container the image sets ASPNETCORE_HTTP_PORTS=8080 and Docker
    /// publishes that port on 127.0.0.1 only.</summary>
    public const string DefaultUrl = "http://127.0.0.1:5080";

    /// <summary>The URL to pass to UseUrls, or null to let ASP.NET Core read ASPNETCORE_HTTP_PORTS itself.</summary>
    public static string? ResolveUrls(IReadOnlyDictionary<string, string?> env)
    {
        if (env.TryGetValue("ASPNETCORE_URLS", out var urls) && !string.IsNullOrWhiteSpace(urls)) return urls;
        if (env.TryGetValue("ASPNETCORE_HTTP_PORTS", out var ports) && !string.IsNullOrWhiteSpace(ports)) return null;
        return DefaultUrl;
    }
}
```

- [ ] **Step 6: Run the tests and confirm they pass.** Run `dotnet test Backend.sln --filter "FullyQualifiedName~ApiHostTests"`. Expected: 3 passed, 0 failed.

- [ ] **Step 7: Check the ignore rules and line endings.** From the repo root:
  - `git check-ignore -v Backend/Backend.sln Backend/src/Accounts.Api/Accounts.Api.csproj` prints nothing, meaning they're not ignored;
  - `git check-ignore -v Backend/src/Accounts.Api/bin/x.dll Backend/deploy/services/secrets/x` shows both ignored;
  - `git check-attr eol -- Backend/db/migrations/x.sql Backend/deploy/services/x.sh` shows `lf`.

  Save the output to `stage-01/task-01.txt`.

- [ ] **Step 8: Commit.**

```bash
git add .gitignore .gitattributes Backend/Backend.sln Backend/Directory.Build.props Backend/Directory.Packages.props Backend/global.json Backend/src Backend/tests
git commit -m "Phase 13 Stage 1: backend solution scaffold, loopback default URL

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Migration catalog (parse, normalize, checksum)

**Files:**
- Create: `Backend/src/Accounts.Persistence/Migrations/MigrationScript.cs`, `MigrationCatalog.cs`, `MigrationExceptions.cs`
- Test: `Backend/tests/Accounts.Integration.Tests/Unit/MigrationCatalogTests.cs`

**Interfaces:**
- Produces:
  - `record MigrationScript(int Version, string Name, string Sql, byte[] Checksum)` with `string FileName`;
  - `MigrationCatalog.Parse(IEnumerable<(string FileName, byte[] Content)>) : MigrationCatalog`;
  - `MigrationCatalog.Scripts : IReadOnlyList<MigrationScript>` and `LatestVersion : int`;
  - `MigrationCatalogException`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using System.Security.Cryptography;
using System.Text;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class MigrationCatalogTests
{
    static (string, byte[]) File(string name, string text) => (name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Parses_and_orders_by_version()
    {
        var catalog = MigrationCatalog.Parse([File("0001_b.sql", "select 2;"), File("0000_a.sql", "select 1;")]);
        Assert.Equal([0, 1], catalog.Scripts.Select(s => s.Version));
        Assert.Equal("a", catalog.Scripts[0].Name);
        Assert.Equal("0000_a.sql", catalog.Scripts[0].FileName);
        Assert.Equal(1, catalog.LatestVersion);
    }

    [Fact]
    public void Checksum_is_sha256_of_the_normalized_text()
    {
        var script = MigrationCatalog.Parse([File("0000_a.sql", "select 1;\n")]).Scripts[0];
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("select 1;\n")), script.Checksum);
    }

    [Fact]
    public void Crlf_and_bom_do_not_change_the_checksum_or_the_sql()
    {
        var lf = MigrationCatalog.Parse([File("0000_a.sql", "create table t (id int);\nselect 1;\n")]).Scripts[0];
        var crlfBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("create table t (id int);\r\nselect 1;\r\n")).ToArray();
        var crlf = MigrationCatalog.Parse([("0000_a.sql", crlfBytes)]).Scripts[0];
        Assert.Equal(lf.Checksum, crlf.Checksum);
        Assert.Equal(lf.Sql, crlf.Sql);
    }

    [Theory]
    [InlineData("1_a.sql")]
    [InlineData("0000-a.sql")]
    [InlineData("0000_A.sql")]
    [InlineData("0000_a.SQL")]
    [InlineData("0000_.sql")]
    [InlineData("0000_a b.sql")]
    public void Refuses_badly_named_files(string name)
    {
        var e = Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File(name, "select 1;")]));
        Assert.Contains(name, e.Message);
    }

    [Fact]
    public void Refuses_a_gap()
    {
        var e = Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0000_a.sql", "select 1;"), File("0002_c.sql", "select 3;")]));
        Assert.Contains("0001", e.Message);
    }

    [Fact]
    public void Refuses_a_catalog_that_does_not_start_at_0000()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0001_a.sql", "select 1;")]));
    }

    [Fact]
    public void Refuses_a_duplicate_version()
    {
        var e = Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0000_a.sql", "select 1;"), File("0000_b.sql", "select 2;")]));
        Assert.Contains("0000", e.Message);
    }

    [Fact]
    public void Refuses_empty_and_whitespace_files()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0000_a.sql", " \r\n\t")]));
    }

    [Fact]
    public void Refuses_invalid_utf8()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([("0000_a.sql", new byte[] { 0x73, 0xC3, 0x28 })]));
    }

    [Fact]
    public void Refuses_an_empty_catalog()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([]));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `UNIT` filtered with `--filter "FullyQualifiedName~MigrationCatalogTests"`. Expected: a build error, because the types don't exist.

- [ ] **Step 3: Implement.**

`MigrationScript.cs`:
```csharp
namespace Blackglass.Accounts.Persistence.Migrations;

/// <summary>One migration file. <see cref="Sql"/> is normalized (no BOM, LF line endings), and <see cref="Checksum"/>
/// is the SHA-256 of its UTF-8 bytes, so a CRLF checkout on Windows and an LF copy on Linux agree.</summary>
public sealed record MigrationScript(int Version, string Name, string Sql, byte[] Checksum)
{
    public string FileName => $"{Version:D4}_{Name}.sql";
}
```

`MigrationExceptions.cs`:
```csharp
namespace Blackglass.Accounts.Persistence.Migrations;

public sealed class MigrationCatalogException(string message) : Exception(message);
```

`MigrationCatalog.cs`:
```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.Accounts.Persistence.Migrations;

/// <summary>The ordered, validated set of migration files (spec §15.4): NNNN_lower_snake_name.sql, contiguous from 0000.</summary>
public sealed class MigrationCatalog
{
    static readonly Regex FileNamePattern = new(@"^(\d{4})_([a-z0-9]+(?:_[a-z0-9]+)*)\.sql$", RegexOptions.CultureInvariant);
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public IReadOnlyList<MigrationScript> Scripts { get; }
    public int LatestVersion => Scripts[^1].Version;

    MigrationCatalog(IReadOnlyList<MigrationScript> scripts) => Scripts = scripts;

    public static MigrationCatalog Parse(IEnumerable<(string FileName, byte[] Content)> files)
    {
        var scripts = new List<MigrationScript>();
        foreach (var (fileName, content) in files)
        {
            var match = FileNamePattern.Match(fileName);
            if (!match.Success)
                throw new MigrationCatalogException($"'{fileName}' is not named NNNN_lower_snake_name.sql.");
            var sql = Normalize(fileName, content);
            if (string.IsNullOrWhiteSpace(sql))
                throw new MigrationCatalogException($"'{fileName}' is empty.");
            var version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            scripts.Add(new MigrationScript(version, match.Groups[2].Value, sql, SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        }

        if (scripts.Count == 0) throw new MigrationCatalogException("No migrations found.");
        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Version == i) continue;
            throw new MigrationCatalogException(i > 0 && scripts[i].Version == scripts[i - 1].Version
                ? $"Version {scripts[i].Version:D4} appears twice."
                : $"Expected version {i:D4} but found {scripts[i].FileName}; versions start at 0000 and have no gaps.");
        }
        return new MigrationCatalog(scripts);
    }

    static string Normalize(string fileName, byte[] content)
    {
        string text;
        try { text = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException) { throw new MigrationCatalogException($"'{fileName}' is not valid UTF-8."); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return text.Replace("\r\n", "\n");
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass.** Expected: 15 passed (9 facts and 6 theory cases), 0 failed. Record the exact count the runner prints.

- [ ] **Step 5: Commit.** Use `git add Backend/src/Accounts.Persistence/Migrations Backend/tests/Accounts.Integration.Tests/Unit/MigrationCatalogTests.cs` and the message `Phase 13 Stage 1: migration catalog with normalized SHA-256 checksums`, ending with the Co-Authored-By line.

---

### Task 3: Schema comparison (pure)

**Files:**
- Create: `Backend/src/Accounts.Persistence/Migrations/SchemaComparison.cs`
- Modify: `MigrationExceptions.cs` (add `SchemaMismatchException`)
- Test: `Backend/tests/Accounts.Integration.Tests/Unit/SchemaComparisonTests.cs`

**Interfaces:**
- Consumes: `MigrationCatalog`, `MigrationScript` (Task 2).
- Produces:
  - `record AppliedMigration(int Version, string Name, byte[] Checksum)`;
  - `enum SchemaState { Current, Behind, Ahead, Drift }`;
  - `record SchemaStatus(SchemaState State, int? DatabaseVersion, int ExpectedVersion, string Detail)`;
  - `SchemaComparison.Compare(MigrationCatalog, IReadOnlyList<AppliedMigration>) : SchemaStatus`;
  - `SchemaMismatchException(SchemaStatus)` with `Status`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using System.Text;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class SchemaComparisonTests
{
    static readonly MigrationCatalog Catalog = MigrationCatalog.Parse([
        ("0000_a.sql", Encoding.UTF8.GetBytes("select 0;")),
        ("0001_b.sql", Encoding.UTF8.GetBytes("select 1;")),
        ("0002_c.sql", Encoding.UTF8.GetBytes("select 2;")),
    ]);

    static AppliedMigration Row(int v) => new(v, Catalog.Scripts[v].Name, Catalog.Scripts[v].Checksum);

    [Fact]
    public void Empty_database_is_behind()
    {
        var s = SchemaComparison.Compare(Catalog, []);
        Assert.Equal(SchemaState.Behind, s.State);
        Assert.Null(s.DatabaseVersion);
        Assert.Equal(2, s.ExpectedVersion);
    }

    [Fact]
    public void Partial_prefix_is_behind() =>
        Assert.Equal(SchemaState.Behind, SchemaComparison.Compare(Catalog, [Row(0), Row(1)]).State);

    [Fact]
    public void Full_match_is_current()
    {
        var s = SchemaComparison.Compare(Catalog, [Row(2), Row(0), Row(1)]);
        Assert.Equal(SchemaState.Current, s.State);
        Assert.Equal(2, s.DatabaseVersion);
    }

    [Fact]
    public void Changed_checksum_is_drift_and_names_the_file()
    {
        var s = SchemaComparison.Compare(Catalog, [Row(0), Row(1) with { Checksum = new byte[32] }]);
        Assert.Equal(SchemaState.Drift, s.State);
        Assert.Contains("0001_b.sql", s.Detail);
    }

    [Fact]
    public void Renamed_file_is_drift() =>
        Assert.Equal(SchemaState.Drift, SchemaComparison.Compare(Catalog, [Row(0) with { Name = "other" }]).State);

    [Fact]
    public void Gap_in_history_is_drift() =>
        Assert.Equal(SchemaState.Drift, SchemaComparison.Compare(Catalog, [Row(0), Row(2)]).State);

    [Fact]
    public void Unknown_newer_versions_are_ahead()
    {
        var s = SchemaComparison.Compare(Catalog, [Row(0), Row(1), Row(2), new AppliedMigration(3, "d", new byte[32])]);
        Assert.Equal(SchemaState.Ahead, s.State);
        Assert.Equal(3, s.DatabaseVersion);
        Assert.Contains("0003", s.Detail);
    }

    [Fact]
    public void Drift_in_the_known_part_wins_over_ahead() =>
        Assert.Equal(SchemaState.Drift, SchemaComparison.Compare(Catalog,
            [Row(0) with { Checksum = new byte[32] }, Row(1), Row(2), new AppliedMigration(3, "d", new byte[32])]).State);
}
```

- [ ] **Step 2: Run them and confirm they fail.** Expected: a build error.

- [ ] **Step 3: Implement.**

`SchemaComparison.cs`:
```csharp
namespace Blackglass.Accounts.Persistence.Migrations;

public sealed record AppliedMigration(int Version, string Name, byte[] Checksum);

public enum SchemaState { Current, Behind, Ahead, Drift }

public sealed record SchemaStatus(SchemaState State, int? DatabaseVersion, int ExpectedVersion, string Detail);

/// <summary>Compares the catalog with schema_migrations. The history must be exactly 0000, 0001, ... and every
/// version the catalog knows must match by name and checksum. Drift in the known part outranks "ahead".</summary>
public static class SchemaComparison
{
    public static SchemaStatus Compare(MigrationCatalog catalog, IReadOnlyList<AppliedMigration> applied)
    {
        var expected = catalog.LatestVersion;
        var rows = applied.OrderBy(a => a.Version).ToList();
        int? dbVersion = rows.Count == 0 ? null : rows[^1].Version;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Version != i)
                return new(SchemaState.Drift, dbVersion, expected, $"schema_migrations is not contiguous: found {row.Version:D4} where {i:D4} was expected.");
            if (row.Version > expected)
                return new(SchemaState.Ahead, dbVersion, expected, $"the database has {expected + 1:D4}..{dbVersion:D4}, which this build doesn't know.");
            var script = catalog.Scripts[row.Version];
            if (row.Name != script.Name || !row.Checksum.AsSpan().SequenceEqual(script.Checksum))
                return new(SchemaState.Drift, dbVersion, expected, $"{script.FileName} differs from the applied {row.Version:D4}_{row.Name}.sql (name or checksum).");
        }

        return dbVersion == expected
            ? new(SchemaState.Current, dbVersion, expected, $"at {expected:D4}.")
            : new(SchemaState.Behind, dbVersion, expected, dbVersion is null ? $"no migrations applied; expected {expected:D4}." : $"at {dbVersion:D4}; expected {expected:D4}.");
    }
}
```

Append to `MigrationExceptions.cs`:
```csharp
public sealed class SchemaMismatchException(SchemaStatus status) : Exception($"schema {status.State}: {status.Detail}")
{
    public SchemaStatus Status { get; } = status;
}
```

- [ ] **Step 4: Run the tests and confirm they pass.** Expected: 8 passed, 0 failed.

- [ ] **Step 5: Commit.** Message: `Phase 13 Stage 1: pure schema comparison (current, behind, ahead, drift)`, with the Co-Authored-By line.

---

### Task 4: Database settings (environment, password file, no secret output)

**Files:**
- Modify: `Backend/Directory.Packages.props`, via `dotnet add src/Accounts.Persistence package Npgsql`, which records the latest stable version.
- Create: `Backend/src/Accounts.Persistence/DbSettings.cs`
- Test: `Backend/tests/Accounts.Integration.Tests/Unit/DbSettingsTests.cs`

**Interfaces:**
- Produces:
  - `DbSettings.FromEnvironment(IReadOnlyDictionary<string, string?>) : DbSettings`;
  - the constants `HostVar`, `PortVar`, `NameVar`, `UserVar`, `PasswordFileVar`;
  - `DbSettings(string host, int port, string database, string username, string password)`;
  - `CreateDataSource(string applicationName, int maxPoolSize = 20) : NpgsqlDataSource`;
  - `ToString()`, which returns `user@host:port/db`;
  - `DbSettingsException`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Blackglass.Accounts.Persistence;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class DbSettingsTests : IDisposable
{
    const string Secret = "s3cret-Value-Never-Printed";
    readonly string passwordFile = Path.Combine(Path.GetTempPath(), $"bg-pw-{Guid.NewGuid():N}");

    public DbSettingsTests() => File.WriteAllText(passwordFile, Secret + "\n");
    public void Dispose() => File.Delete(passwordFile);

    Dictionary<string, string?> Env() => new()
    {
        [DbSettings.HostVar] = "postgres", [DbSettings.NameVar] = "accounts",
        [DbSettings.UserVar] = "accounts_app", [DbSettings.PasswordFileVar] = passwordFile,
    };

    [Fact]
    public void Reads_settings_and_trims_the_trailing_newline_of_the_password_file()
    {
        var s = DbSettings.FromEnvironment(Env());
        Assert.Equal(("postgres", 5432, "accounts", "accounts_app"), (s.Host, s.Port, s.Database, s.Username));
        Assert.Equal("accounts_app@postgres:5432/accounts", s.ToString());
    }

    [Theory]
    [InlineData(DbSettings.HostVar)]
    [InlineData(DbSettings.NameVar)]
    [InlineData(DbSettings.UserVar)]
    [InlineData(DbSettings.PasswordFileVar)]
    public void Missing_variable_is_named(string name)
    {
        var env = Env();
        env.Remove(name);
        Assert.Contains(name, Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(env)).Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("54x")]
    public void Bad_port_is_refused(string port)
    {
        var env = Env();
        env[DbSettings.PortVar] = port;
        Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(env));
    }

    [Fact]
    public void Unreadable_or_empty_password_file_is_refused()
    {
        var env = Env();
        env[DbSettings.PasswordFileVar] = passwordFile + ".missing";
        Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(env));
        File.WriteAllText(passwordFile, "\n");
        Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(Env()));
    }

    [Fact]
    public void The_password_never_appears_in_text_output()
    {
        var s = DbSettings.FromEnvironment(Env());
        Assert.DoesNotContain(Secret, s.ToString());
        using var ds = s.CreateDataSource("test");
        Assert.DoesNotContain(Secret, ds.ConnectionString);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Expected: a build error.

- [ ] **Step 3: Implement.** Run `dotnet add src/Accounts.Persistence package Npgsql` and record the version written to `Directory.Packages.props`. Then `Backend/src/Accounts.Persistence/DbSettings.cs`:

```csharp
using System.Globalization;
using Npgsql;

namespace Blackglass.Accounts.Persistence;

public sealed class DbSettingsException(string message) : Exception(message);

/// <summary>Connection settings for one database role. The password comes from a file (a Compose secret), never from
/// an environment value, and never appears in <see cref="ToString"/> or in any error message.</summary>
public sealed class DbSettings(string host, int port, string database, string username, string password)
{
    public const string HostVar = "BLACKGLASS_DB_HOST";
    public const string PortVar = "BLACKGLASS_DB_PORT";
    public const string NameVar = "BLACKGLASS_DB_NAME";
    public const string UserVar = "BLACKGLASS_DB_USER";
    public const string PasswordFileVar = "BLACKGLASS_DB_PASSWORD_FILE";

    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Database { get; } = database;
    public string Username { get; } = username;

    public static DbSettings FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        string Required(string name) =>
            env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new DbSettingsException($"{name} is not set.");

        var portText = env.TryGetValue(PortVar, out var p) && !string.IsNullOrWhiteSpace(p) ? p : "5432";
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new DbSettingsException($"{PortVar} must be a port number between 1 and 65535.");

        var passwordFile = Required(PasswordFileVar);
        string password;
        try { password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n'); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DbSettingsException($"The file named by {PasswordFileVar} can't be read ({e.GetType().Name}).");
        }
        if (password.Length == 0) throw new DbSettingsException($"The file named by {PasswordFileVar} is empty.");

        return new DbSettings(Required(HostVar), port, Required(NameVar), Required(UserVar), password);
    }

    public NpgsqlDataSource CreateDataSource(string applicationName, int maxPoolSize = 20) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = Host, Port = Port, Database = Database, Username = Username, Password = password,
            ApplicationName = applicationName, Timeout = 5, MaxPoolSize = maxPoolSize,
        });

    public override string ToString() => $"{Username}@{Host}:{Port}/{Database}";
}
```

- [ ] **Step 4: Run the tests and confirm they pass.** Expected: 10 passed (3 facts and 7 theory cases), 0 failed. If `ds.ConnectionString` contains the password, stop and report: it would mean Npgsql's `Persist Security Info` default is not what the plan assumes. Don't weaken the assertion.

- [ ] **Step 5: Commit.** Include `Backend/Directory.Packages.props`, `Backend/src/Accounts.Persistence/Accounts.Persistence.csproj`, `DbSettings.cs` and the test. Message: `Phase 13 Stage 1: database settings from env and password files, never printed`, with the Co-Authored-By line.

---

### Task 5: The test PostgreSQL cluster and the per-class test database (needs E1)

**Files:**
- Create:
  - `Backend/deploy/services/compose.test.yaml`, `Backend/deploy/services/initdb/00-roles.sh`, `Backend/deploy/services/make-secrets.sh`;
  - `Backend/tools/vm-sync.sh`, `Backend/tools/testdb.sh`;
  - `Backend/tests/Accounts.Integration.Tests/Support/TestCluster.cs`, `TestDatabase.cs`, `DatabaseFixture.cs`.
- Test: `Backend/tests/Accounts.Integration.Tests/Unit/TestClusterGuardTests.cs`, `Backend/tests/Accounts.Integration.Tests/Database/TestClusterTests.cs`

**Interfaces:**
- Consumes: `DbSettings` (Task 4).
- Produces:
  - `TestCluster.Instance`, `TestCluster.EnsureTestCluster(string clusterName)`, `TestCluster.CreateDatabaseAsync() : Task<TestDatabase>`, `TestCluster.OutsiderPassword`;
  - `TestDatabase`: `Name`, `AsSuperuser()`, `AsMigrator()`, `AsApp()`, `AsOutsider()` (each returns an `NpgsqlDataSource`, pooled and owned by the `TestDatabase`), `EnvFor(string role) : Dictionary<string,string?>` (role `"migrator"` or `"app"`), `Password(string role)`, `DisposeAsync()`;
  - `DatabaseFixture : IAsyncLifetime` exposing `Db`;
  - the trait `[Trait("Requires", "Postgres")]` on every database test class.

**Secrets layout (ruling 5).** Compose file secrets on a non-Swarm engine are bind mounts that keep the host file's owner and mode. So each consumer gets **its own file, mode 0400, owned by the uid of the one in-container user that reads it**:

| File (under the stack's secrets directory) | Owner | Read by | Compose secret |
|---|---|---|---|
| `postgres/superuser_password` | 999 (`postgres` in the image) | the image's init (`POSTGRES_PASSWORD_FILE`) | `pg_superuser_password` |
| `postgres/migrator_password` | 999 | `initdb/00-roles.sh`, at first start only | `pg_init_migrator_password` |
| `postgres/app_password` | 999 | `initdb/00-roles.sh`, at first start only | `pg_init_app_password` |
| `migrate/db_password` | 1654 (`app` in the API image) | the `migrate` service | `migrate_db_password` |
| `api/db_password` | 1654 | the `api` service | `api_db_password` |

- **Directories:** root-owned 0700.
- **Duplicated values:** the migrator and app passwords exist in two files each, one per consumer, rather than one shared file made more readable.
- **uids:** `make-secrets.sh` takes them as `BLACKGLASS_PG_UID` (default 999) and `BLACKGLASS_APP_UID` (default 1654). Step 6 and Task 10 check both against the images.

Task 13 records this in spec §4.4.

- [ ] **Step 1: Write the cluster files.**

`Backend/deploy/services/initdb/00-roles.sh`:
```sh
#!/bin/sh
# Runs once, on the first start of an empty data directory (official postgres image, as the postgres superuser over
# the local socket). Creates the two application roles and the accounts database; passwords come from Compose secrets.
set -eu
psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname postgres <<'SQL'
\set migrator_pw `cat /run/secrets/pg_init_migrator_password`
\set app_pw `cat /run/secrets/pg_init_app_password`
create role accounts_migrator login nosuperuser nocreatedb nocreaterole noinherit noreplication password :'migrator_pw';
create role accounts_app      login nosuperuser nocreatedb nocreaterole noinherit noreplication password :'app_pw';
create database accounts owner accounts_migrator;
revoke all on database accounts from public;
grant connect on database accounts to accounts_app;
SQL
```

`Backend/deploy/services/make-secrets.sh`:
```bash
#!/usr/bin/env bash
# Creates one stack's database secret files, once (existing values are never replaced). Every consumer gets its own
# file, mode 0400, owned by the uid of the one in-container user that reads it (Stage 1 ruling 5):
#   postgres/{superuser,migrator,app}_password   uid BLACKGLASS_PG_UID  (default 999, "postgres" in postgres:17)
#   migrate/db_password  (= migrator password)    uid BLACKGLASS_APP_UID (default 1654, "app" in the API image)
#   api/db_password      (= app password)         uid BLACKGLASS_APP_UID
# Directories are root-owned 0700. Run as root (sudo).   Usage: make-secrets.sh <directory>
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "make-secrets.sh must run as root (sudo)" >&2; exit 1; }
dir="${1:?usage: make-secrets.sh <directory>}"
pg_uid="${BLACKGLASS_PG_UID:-999}"
app_uid="${BLACKGLASS_APP_UID:-1654}"
umask 077
install -d -m 0700 -o root -g root "$dir" "$dir/postgres" "$dir/migrate" "$dir/api"

random_password() {
  local pw
  pw="$(head -c 300 /dev/urandom | LC_ALL=C tr -dc 'A-Za-z0-9')"
  (( ${#pw} >= 40 )) || { echo "not enough random characters; run again" >&2; return 1; }
  printf '%s' "${pw:0:40}"
}
existing_or_new() { if [[ -e "$1" ]]; then cat "$1"; else random_password; fi; }
place() {  # place <file> <uid> <value>: write once, then enforce owner and mode
  [[ -e "$1" ]] || printf '%s' "$3" > "$1"
  chown "$2:$2" "$1"
  chmod 0400 "$1"
}

superuser="$(existing_or_new "$dir/postgres/superuser_password")"
migrator="$(existing_or_new "$dir/postgres/migrator_password")"
app="$(existing_or_new "$dir/postgres/app_password")"
place "$dir/postgres/superuser_password" "$pg_uid" "$superuser"
place "$dir/postgres/migrator_password" "$pg_uid" "$migrator"
place "$dir/postgres/app_password" "$pg_uid" "$app"
place "$dir/migrate/db_password" "$app_uid" "$migrator"
place "$dir/api/db_password" "$app_uid" "$app"
echo "secrets ready in $dir"
```

`Backend/deploy/services/compose.test.yaml`:
```yaml
# Integration-test PostgreSQL for the Phase 13 backend (bg-dev-services01 only).
# Loopback only; from the PC: ssh -N -L 127.0.0.1:55432:127.0.0.1:55432 bg-dev-services01-admin
# The test fixture refuses any cluster whose cluster_name isn't blackglass-test.
# Static tests read this file line by line: two-space indentation, ports in the short quoted form only.
name: blackglass-testdb

services:
  postgres:
    image: postgres:17
    command: ["postgres", "-c", "cluster_name=blackglass-test"]
    environment:
      POSTGRES_PASSWORD_FILE: /run/secrets/pg_superuser_password
    secrets:
      - pg_superuser_password
      - pg_init_migrator_password
      - pg_init_app_password
    volumes:
      - testdata:/var/lib/postgresql/data
      - ./initdb:/docker-entrypoint-initdb.d:ro
    ports:
      - "127.0.0.1:55432:5432"
    networks:
      - edge
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -h 127.0.0.1 -U postgres -d accounts"]
      interval: 5s
      timeout: 3s
      retries: 30
    restart: unless-stopped
    logging:
      driver: local

networks:
  edge:
    driver_opts:
      com.docker.network.bridge.host_binding_ipv4: "127.0.0.1"

volumes:
  testdata:

# Separate credentials and storage from the accounts stack (ruling 2). One 0400 file per consumer (ruling 5).
secrets:
  pg_superuser_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/backend/testdb-secrets}/postgres/superuser_password
  pg_init_migrator_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/backend/testdb-secrets}/postgres/migrator_password
  pg_init_app_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/backend/testdb-secrets}/postgres/app_password
```

`Backend/tools/vm-sync.sh`:
```bash
#!/usr/bin/env bash
# Copies the committed Backend/ tree at HEAD to <vm>:/opt/blackglass/backend/src (replacing it) and records the commit,
# so every VM transcript names exactly what it ran. Uncommitted changes are not copied.
set -euo pipefail
alias="${BLACKGLASS_SERVICES_HOST:-bg-dev-services01-admin}"
repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
commit="$(git -C "$repo" rev-parse --short HEAD)"
if [[ -n "$(git -C "$repo" status --porcelain -- Backend)" ]]; then
  echo "warning: Backend/ has uncommitted changes; the VM gets HEAD ($commit) only" >&2
fi
git -C "$repo" archive --format=tar HEAD Backend | ssh "$alias" "sudo -n sh -c 'set -e
  rm -rf /opt/blackglass/backend/src.new && mkdir -p /opt/blackglass/backend/src.new
  tar -x -C /opt/blackglass/backend/src.new
  echo $commit > /opt/blackglass/backend/src.new/COMMIT
  rm -rf /opt/blackglass/backend/src && mv /opt/blackglass/backend/src.new /opt/blackglass/backend/src'"
echo "synced $commit to $alias:/opt/blackglass/backend/src"
```

`Backend/tools/testdb.sh`:
```bash
#!/usr/bin/env bash
# The integration-test PostgreSQL on bg-dev-services01 (Compose project blackglass-testdb, 127.0.0.1:55432 on the VM).
#   testdb.sh up        sync Backend/, create the secrets once, start the cluster, copy the secrets to ~/.blackglass/testdb-secrets
#   testdb.sh tunnel    forward 127.0.0.1:55432 on this PC to the VM's loopback (foreground; Ctrl+C to stop)
#   testdb.sh down      stop the cluster (keeps its data volume)
#   testdb.sh destroy   stop it and delete the volume
set -euo pipefail
alias="${BLACKGLASS_SERVICES_HOST:-bg-dev-services01-admin}"
vm=/opt/blackglass/backend
services="$vm/src/Backend/deploy/services"
local_secrets="$HOME/.blackglass/testdb-secrets"
compose="sudo -n env BLACKGLASS_SECRETS_DIR=$vm/testdb-secrets docker compose -f $services/compose.test.yaml"
case "${1:-}" in
  up)
    bash "$(dirname "$0")/vm-sync.sh"
    ssh "$alias" "set -e; sudo -n bash $services/make-secrets.sh $vm/testdb-secrets; $compose up -d --wait"
    mkdir -p "$local_secrets"   # under the Windows user profile, which only this user can read
    for name in superuser migrator app; do
      ssh "$alias" "sudo -n cat $vm/testdb-secrets/postgres/${name}_password" > "$local_secrets/pg_${name}_password"
    done
    echo "test cluster up; secrets copied to $local_secrets" ;;
  tunnel)  exec ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:55432:127.0.0.1:55432 "$alias" ;;
  down)    ssh "$alias" "$compose down" ;;
  destroy) ssh "$alias" "$compose down -v" ;;
  *) echo "usage: testdb.sh up|tunnel|down|destroy" >&2; exit 64 ;;
esac
```

- [ ] **Step 2: Make the scripts executable in Git.** Run `git add --chmod=+x Backend/deploy/services/make-secrets.sh Backend/deploy/services/initdb/00-roles.sh Backend/tools/*.sh`. The image's entrypoint only runs `*.sh` files in `initdb` that are executable; otherwise it sources them, which also works with `sh`.

- [ ] **Step 3: Write the failing tests.**

`Unit/TestClusterGuardTests.cs`:
```csharp
using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class TestClusterGuardTests
{
    [Fact]
    public void Accepts_only_the_test_cluster()
    {
        TestCluster.EnsureTestCluster("blackglass-test");
        Assert.Throws<InvalidOperationException>(() => TestCluster.EnsureTestCluster("blackglass-accounts"));
        Assert.Throws<InvalidOperationException>(() => TestCluster.EnsureTestCluster(""));
    }
}
```

`Database/TestClusterTests.cs`:
```csharp
using Dapper;
using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class TestClusterTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Cluster_is_postgresql_17()
    {
        await using var conn = await fixture.Db.AsSuperuser().OpenConnectionAsync();
        var version = await conn.ExecuteScalarAsync<int>("select current_setting('server_version_num')::int");
        Assert.InRange(version, 170000, 179999);
    }

    [Fact]
    public async Task Roles_have_no_elevated_attributes()
    {
        await using var conn = await fixture.Db.AsSuperuser().OpenConnectionAsync();
        var rows = (await conn.QueryAsync<(string Name, bool Super, bool CreateRole, bool CreateDb)>(
            "select rolname, rolsuper, rolcreaterole, rolcreatedb from pg_roles where rolname in ('accounts_migrator','accounts_app') order by rolname")).ToList();
        Assert.Equal(["accounts_app", "accounts_migrator"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.False(r.Super || r.CreateRole || r.CreateDb));
    }

    [Fact]
    public async Task Test_database_is_owned_by_the_migrator()
    {
        await using var conn = await fixture.Db.AsSuperuser().OpenConnectionAsync();
        var owner = await conn.ExecuteScalarAsync<string>(
            "select pg_get_userbyid(datdba) from pg_database where datname = current_database()");
        Assert.Equal("accounts_migrator", owner);
    }
}
```

Dapper is used here, so add it now: `dotnet add tests/Accounts.Integration.Tests package Dapper`, and `dotnet add src/Accounts.Persistence package Dapper` for Task 6. Record the version.

- [ ] **Step 4: Run them and confirm they fail.** Expected: a build error, because `TestCluster` doesn't exist.

- [ ] **Step 5: Implement the support classes.**

`Support/TestCluster.cs`:
```csharp
using System.Globalization;
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>The Blackglass integration-test cluster, reached through the SSH tunnel on 127.0.0.1 (port from
/// BLACKGLASS_TEST_PG_PORT, default 55432). Passwords are read from ~/.blackglass/testdb-secrets (or
/// BLACKGLASS_TEST_PG_SECRETS), filled by `tools/testdb.sh up`. Refuses any cluster but blackglass-test.</summary>
public sealed class TestCluster
{
    public const string ExpectedClusterName = "blackglass-test";
    const string DatabasePrefix = "bg_t_";
    static readonly Lazy<Task<TestCluster>> Shared = new(ConnectAsync);

    public static Task<TestCluster> Instance => Shared.Value;

    public int Port { get; }
    public string SuperuserPassword { get; }
    public string MigratorPassword { get; }
    public string AppPassword { get; }
    public string OutsiderPassword { get; } = Guid.NewGuid().ToString("N");

    TestCluster(int port, string su, string migrator, string app) =>
        (Port, SuperuserPassword, MigratorPassword, AppPassword) = (port, su, migrator, app);

    public static void EnsureTestCluster(string clusterName)
    {
        if (clusterName != ExpectedClusterName)
            throw new InvalidOperationException($"Refusing to run destructive tests: cluster_name is '{clusterName}', not '{ExpectedClusterName}'. Check the SSH tunnel.");
    }

    public NpgsqlDataSource DataSource(string database, string user, string password) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = Port, Database = database, Username = user, Password = password,
            MaxPoolSize = 5, Timeout = 10, ApplicationName = "blackglass-tests",
        });

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"{DatabasePrefix}{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        await using var admin = DataSource("postgres", "postgres", SuperuserPassword);
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"create database \"{name}\" owner accounts_migrator");
        return new TestDatabase(this, name);
    }

    static async Task<TestCluster> ConnectAsync()
    {
        var dir = Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_SECRETS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blackglass", "testdb-secrets");
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"No test-cluster secrets at {dir}. Run `bash Backend/tools/testdb.sh up`, then keep `bash Backend/tools/testdb.sh tunnel` open.");
        string Read(string name) => File.ReadAllText(Path.Combine(dir, name)).Trim();
        var port = int.Parse(Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_PORT") ?? "55432", CultureInfo.InvariantCulture);
        var cluster = new TestCluster(port, Read("pg_superuser_password"), Read("pg_migrator_password"), Read("pg_app_password"));

        await using var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword);
        await using var conn = await admin.OpenConnectionAsync();
        EnsureTestCluster(await conn.ExecuteScalarAsync<string>("select current_setting('cluster_name')") ?? "");

        // Leftovers from crashed runs: test databases older than two hours.
        var cutoff = DateTime.UtcNow.AddHours(-2).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        foreach (var stale in await conn.QueryAsync<string>(
                     "select datname from pg_database where datname like 'bg\\_t\\_%' and substr(datname, 6, 14) < @cutoff", new { cutoff }))
            await conn.ExecuteAsync($"drop database if exists \"{stale}\" with (force)");

        // A login role with no grants, to prove PUBLIC has no access to application databases.
        await conn.ExecuteAsync("""
            do $$ begin
              if not exists (select from pg_roles where rolname = 'bg_test_outsider') then
                create role bg_test_outsider login nosuperuser nocreatedb nocreaterole;
              end if;
            end $$;
            """);
        await conn.ExecuteAsync($"alter role bg_test_outsider password '{cluster.OutsiderPassword}'");
        return cluster;
    }
}
```

`Support/TestDatabase.cs`:
```csharp
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>One fresh database owned by accounts_migrator, dropped on dispose.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    readonly TestCluster cluster;
    readonly Dictionary<string, NpgsqlDataSource> sources = new();
    readonly List<string> tempFiles = new();

    public string Name { get; }

    internal TestDatabase(TestCluster cluster, string name) => (this.cluster, Name) = (cluster, name);

    NpgsqlDataSource Source(string user, string password) =>
        sources.TryGetValue(user, out var ds) ? ds : sources[user] = cluster.DataSource(Name, user, password);

    public NpgsqlDataSource AsSuperuser() => Source("postgres", cluster.SuperuserPassword);
    public NpgsqlDataSource AsMigrator() => Source("accounts_migrator", cluster.MigratorPassword);
    public NpgsqlDataSource AsApp() => Source("accounts_app", cluster.AppPassword);
    public NpgsqlDataSource AsOutsider() => Source("bg_test_outsider", cluster.OutsiderPassword);

    public string Password(string role) => role switch
    {
        "migrator" => cluster.MigratorPassword,
        "app" => cluster.AppPassword,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The BLACKGLASS_DB_* environment the API commands read, with the password in a temp file.</summary>
    public Dictionary<string, string?> EnvFor(string role)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bg-test-pw-{Guid.NewGuid():N}");
        File.WriteAllText(file, Password(role));
        tempFiles.Add(file);
        return new()
        {
            ["BLACKGLASS_DB_HOST"] = "127.0.0.1",
            ["BLACKGLASS_DB_PORT"] = cluster.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BLACKGLASS_DB_NAME"] = Name,
            ["BLACKGLASS_DB_USER"] = role == "migrator" ? "accounts_migrator" : "accounts_app",
            ["BLACKGLASS_DB_PASSWORD_FILE"] = file,
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ds in sources.Values) await ds.DisposeAsync();
        foreach (var f in tempFiles) File.Delete(f);
        await using var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword);
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"drop database if exists \"{Name}\" with (force)");
    }
}
```

`Support/DatabaseFixture.cs`:
```csharp
namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>A fresh database per test class (spec §18.1 layer 2).</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public TestDatabase Db { get; private set; } = null!;

    public async Task InitializeAsync() => Db = await (await TestCluster.Instance).CreateDatabaseAsync();
    public async Task DisposeAsync() => await Db.DisposeAsync();
}
```

- [ ] **Step 6: Commit, then bring up the cluster on the VM.** The VM receives only committed files, so commit before syncing. Use `git add Backend/deploy Backend/tools Backend/tests Backend/src Backend/Directory.Packages.props`, with the message `Phase 13 Stage 1: integration-test PostgreSQL cluster and per-class test databases`. Then:
  - run `bash tools/testdb.sh up`. Expected: `synced <commit>`, then `secrets ready`, then Compose shows `blackglass-testdb-postgres-1 Healthy`, then `test cluster up`;
  - on the VM, run `ss -H -ltn '( sport = :55432 )'`, which must show only `127.0.0.1:55432`;
  - on the VM, run `sudo docker run --rm --entrypoint id postgres:17 -u postgres`, which must print `999`, the `BLACKGLASS_PG_UID` default;
  - run `sudo stat -c '%U:%G %a %n' /opt/blackglass/backend/testdb-secrets/*/*`. Every file must be `0400` and owned by its consumer's uid, and every directory `root` 0700;
  - start `bash tools/testdb.sh tunnel` in a second terminal.

  Save all of this to `stage-01/task-05.txt`.

- [ ] **Step 7: Run all the tests.** Run `ALL`. Expected: every unit test so far plus 4 new tests, all passing, with 0 failed. Record the counts.

- [ ] **Step 8: Prove the guard works end to end.** Temporarily point `BLACKGLASS_TEST_PG_PORT` at a closed port: the tests must fail with a connection error, not skip. Then confirm, by reading `TestClusterGuardTests`, that a non-test `cluster_name` is refused before any `create database` runs. Record both in the evidence.

---

### Task 6: Migrator

**Files:**
- Create: `Backend/src/Accounts.Persistence/Migrations/SchemaMigrationsTable.cs`, `Migrator.cs`
- Modify: `MigrationExceptions.cs`
- Create: `Backend/tests/Accounts.Integration.Tests/Support/Catalogs.cs`
- Test: `Backend/tests/Accounts.Integration.Tests/Database/MigratorTests.cs`

**Interfaces:**
- Consumes: `MigrationCatalog`, `SchemaComparison`, `SchemaMismatchException` (Tasks 2–3), `DatabaseFixture` (Task 5).
- Produces:
  - `record MigratorOptions { TimeSpan LockTimeout = 5 s; TimeSpan LockWait = 60 s }`;
  - `record MigrationResult(IReadOnlyList<int> Applied, int SchemaVersion)`;
  - `new Migrator(NpgsqlDataSource, MigrationCatalog, MigratorOptions? = null)` with `MigrateAsync(int? targetVersion = null, CancellationToken = default)`;
  - `Migrator.AdvisoryLockKey`;
  - `SchemaMigrationsTable.ReadAsync(NpgsqlConnection, CancellationToken) : Task<IReadOnlyList<AppliedMigration>?>`, which returns null when the table doesn't exist;
  - `MigrationFailedException(string fileName, PostgresException inner)` with `FileName`;
  - `MigratorBusyException`;
  - the test helper `Catalogs.Of(params (string FileName, string Sql)[])`.

- [ ] **Step 1: Write the failing tests.**

`Support/Catalogs.cs`:
```csharp
using System.Text;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Support;

public static class Catalogs
{
    public static MigrationCatalog Of(params (string FileName, string Sql)[] files) =>
        MigrationCatalog.Parse(files.Select(f => (f.FileName, Encoding.UTF8.GetBytes(f.Sql))));
}
```

`Database/MigratorTests.cs`:
```csharp
using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class MigratorTests
{
    static readonly (string, string)[] Three =
    [
        ("0000_t0.sql", "create table t0 (id int primary key);"),
        ("0001_t1.sql", "create table t1 (id int primary key);"),
        ("0002_t1_note.sql", "alter table t1 add column note text not null default '';"),
    ];

    static async Task<List<(int Version, string Name, byte[] Checksum)>> Rows(TestDatabase db)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<(int, string, byte[])>("select version, name, checksum from schema_migrations order by version")).ToList();
    }

    static async Task<bool> TableExists(TestDatabase db, string table)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>("select to_regclass(@t) is not null", new { t = "public." + table });
    }

    [Fact]
    public async Task Applies_every_script_to_an_empty_database_in_order()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.Of(Three);
        var result = await new Migrator(db.AsMigrator(), catalog).MigrateAsync();
        Assert.Equal([0, 1, 2], result.Applied);
        Assert.Equal(2, result.SchemaVersion);
        var rows = await Rows(db);
        Assert.Equal(catalog.Scripts.Select(s => (s.Version, s.Name)), rows.Select(r => (r.Version, r.Name)));
        Assert.All(rows, r => Assert.Equal(catalog.Scripts[r.Version].Checksum, r.Checksum));
        Assert.True(await TableExists(db, "t1"));
    }

    [Fact]
    public async Task A_second_run_is_a_no_op()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(db.AsMigrator(), Catalogs.Of(Three));
        await migrator.MigrateAsync();
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var before = await conn.QueryAsync<DateTime>("select applied_at from schema_migrations order by version");
        var again = await migrator.MigrateAsync();
        Assert.Empty(again.Applied);
        Assert.Equal(2, again.SchemaVersion);
        Assert.Equal(before, await conn.QueryAsync<DateTime>("select applied_at from schema_migrations order by version"));
    }

    [Fact]
    public async Task Target_version_stops_early_and_the_rest_applies_later()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(db.AsMigrator(), Catalogs.Of(Three));
        Assert.Equal([0, 1], (await migrator.MigrateAsync(targetVersion: 1)).Applied);
        Assert.Equal([2], (await migrator.MigrateAsync()).Applied);
    }

    [Fact]
    public async Task A_failing_script_rolls_back_alone_and_is_named()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.Of(("0000_t0.sql", "create table t0 (id int);"),
                                  ("0001_broken.sql", "create table t1 (id int);\nselect * from no_such_table;"));
        var e = await Assert.ThrowsAsync<MigrationFailedException>(() => new Migrator(db.AsMigrator(), catalog).MigrateAsync());
        Assert.Equal("0001_broken.sql", e.FileName);
        Assert.Equal([0], (await Rows(db)).Select(r => r.Version));
        Assert.False(await TableExists(db, "t1"));
    }

    [Fact]
    public async Task A_changed_applied_script_is_refused_and_nothing_else_runs()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three[0], Three[1])).MigrateAsync();
        var changed = Catalogs.Of(Three[0], ("0001_t1.sql", "create table t1 (id bigint primary key);"), Three[2]);
        var e = await Assert.ThrowsAsync<SchemaMismatchException>(() => new Migrator(db.AsMigrator(), changed).MigrateAsync());
        Assert.Equal(SchemaState.Drift, e.Status.State);
        Assert.Equal([0, 1], (await Rows(db)).Select(r => r.Version));
    }

    [Fact]
    public async Task A_renamed_applied_script_is_refused()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three[0])).MigrateAsync();
        var renamed = Catalogs.Of(("0000_renamed.sql", Three[0].Item2));
        Assert.Equal(SchemaState.Drift, (await Assert.ThrowsAsync<SchemaMismatchException>(() => new Migrator(db.AsMigrator(), renamed).MigrateAsync())).Status.State);
    }

    [Fact]
    public async Task A_database_ahead_of_the_build_is_refused()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three)).MigrateAsync();
        var older = Catalogs.Of(Three[0], Three[1]);
        Assert.Equal(SchemaState.Ahead, (await Assert.ThrowsAsync<SchemaMismatchException>(() => new Migrator(db.AsMigrator(), older).MigrateAsync())).Status.State);
    }

    [Fact]
    public async Task A_crlf_copy_of_an_applied_script_is_not_drift()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(("0000_t0.sql", "create table t0 (id int);\nselect 1;\n"))).MigrateAsync();
        var crlf = Catalogs.Of(("0000_t0.sql", "create table t0 (id int);\r\nselect 1;\r\n"));
        Assert.Empty((await new Migrator(db.AsMigrator(), crlf).MigrateAsync()).Applied);
    }

    [Fact]
    public async Task Concurrent_migrators_apply_each_script_exactly_once()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.Of(Three[0], ("0001_t1.sql", "select pg_sleep(1);\ncreate table t1 (id int primary key);"), Three[2]);
        var cluster = await TestCluster.Instance;
        await using var ds1 = cluster.DataSource(db.Name, "accounts_migrator", db.Password("migrator"));
        await using var ds2 = cluster.DataSource(db.Name, "accounts_migrator", db.Password("migrator"));
        var results = await Task.WhenAll(new Migrator(ds1, catalog).MigrateAsync(), new Migrator(ds2, catalog).MigrateAsync());
        Assert.Equal([0, 1, 2], results.SelectMany(r => r.Applied).Order());
        Assert.Equal([0, 1, 2], (await Rows(db)).Select(r => r.Version));
    }

    [Fact]
    public async Task A_busy_migrator_times_out_with_a_clear_error()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await using var holder = await db.AsMigrator().OpenConnectionAsync();
        await holder.ExecuteAsync("select pg_advisory_lock(@k)", new { k = Migrator.AdvisoryLockKey });
        var options = new MigratorOptions { LockWait = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAsync<MigratorBusyException>(() => new Migrator(db.AsMigrator(), Catalogs.Of(Three), options).MigrateAsync());
        Assert.False(await TableExists(db, "schema_migrations") && (await Rows(db)).Count > 0);
    }

    [Fact]
    public async Task Lock_timeout_applies_to_each_script()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three[0])).MigrateAsync();
        await using var blocker = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("lock table t0 in access exclusive mode", transaction: tx);
        var catalog = Catalogs.Of(Three[0], ("0001_alter.sql", "alter table t0 add column x int;"));
        var options = new MigratorOptions { LockTimeout = TimeSpan.FromMilliseconds(500) };
        var e = await Assert.ThrowsAsync<MigrationFailedException>(() => new Migrator(db.AsMigrator(), catalog, options).MigrateAsync());
        Assert.Equal("55P03", ((PostgresException)e.InnerException!).SqlState);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Expected: a build error.

- [ ] **Step 3: Implement.**

`SchemaMigrationsTable.cs`:
```csharp
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Migrations;

public static class SchemaMigrationsTable
{
    public const string CreateSql = """
        create table if not exists schema_migrations (
          version    integer primary key check (version >= 0),
          name       text not null,
          checksum   bytea not null check (length(checksum) = 32),
          applied_at timestamptz not null default now(),
          applied_by text not null default current_user)
        """;

    /// <summary>The applied rows, or null when schema_migrations doesn't exist yet.</summary>
    public static async Task<IReadOnlyList<AppliedMigration>?> ReadAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select to_regclass('public.schema_migrations') is not null", cancellationToken: ct));
        if (!exists) return null;
        return (await conn.QueryAsync<AppliedMigration>(new CommandDefinition(
            "select version, name, checksum from schema_migrations order by version", cancellationToken: ct))).AsList();
    }
}
```

Append to `MigrationExceptions.cs`:
```csharp
public sealed class MigrationFailedException(string fileName, Npgsql.PostgresException inner)
    : Exception($"{fileName} failed: {inner.SqlState} {inner.MessageText}", inner)
{
    public string FileName { get; } = fileName;
}

public sealed class MigratorBusyException(TimeSpan waited)
    : Exception($"Another migrator holds the migration lock on this database; gave up after {waited.TotalSeconds:0} s.");
```

`Migrator.cs`:
```csharp
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Migrations;

public sealed record MigratorOptions
{
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record MigrationResult(IReadOnlyList<int> Applied, int SchemaVersion);

/// <summary>Applies pending migrations forward-only (spec §15.4). A session-level advisory lock serializes migrators on
/// one database; each file runs in its own transaction with lock_timeout set; drift or an unknown newer version stops
/// everything before any file runs.</summary>
public sealed class Migrator(NpgsqlDataSource dataSource, MigrationCatalog catalog, MigratorOptions? options = null)
{
    /// <summary>"BLACKGLS" as a 64-bit key for pg_advisory_lock.</summary>
    public const long AdvisoryLockKey = 0x424C_4143_4B47_4C53;

    readonly MigratorOptions options = options ?? new MigratorOptions();

    public async Task<MigrationResult> MigrateAsync(int? targetVersion = null, CancellationToken ct = default)
    {
        var target = targetVersion ?? catalog.LatestVersion;
        if (target < 0 || target > catalog.LatestVersion) throw new ArgumentOutOfRangeException(nameof(targetVersion));

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await AcquireLockAsync(conn, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(SchemaMigrationsTable.CreateSql, cancellationToken: ct));
            var applied = await SchemaMigrationsTable.ReadAsync(conn, ct) ?? [];
            var status = SchemaComparison.Compare(catalog, applied);
            if (status.State is SchemaState.Drift or SchemaState.Ahead) throw new SchemaMismatchException(status);

            var current = status.DatabaseVersion ?? -1;
            var done = new List<int>();
            foreach (var script in catalog.Scripts.Where(s => s.Version > current && s.Version <= target))
            {
                await ApplyAsync(conn, script, ct);
                done.Add(script.Version);
            }
            return new MigrationResult(done, done.Count > 0 ? done[^1] : current);
        }
        finally
        {
            // Npgsql also resets the session (releasing the lock) when the connection returns to the pool.
            if (conn.State == System.Data.ConnectionState.Open)
                await conn.ExecuteAsync("select pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    async Task AcquireLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.LockWait;
        while (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                   "select pg_try_advisory_lock(@key)", new { key = AdvisoryLockKey }, cancellationToken: ct)))
        {
            if (DateTime.UtcNow >= deadline) throw new MigratorBusyException(options.LockWait);
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    async Task ApplyAsync(NpgsqlConnection conn, MigrationScript script, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"set local lock_timeout = {(long)options.LockTimeout.TotalMilliseconds}", transaction: tx, cancellationToken: ct));
            await using (var cmd = new NpgsqlCommand(script.Sql, conn, tx) { CommandTimeout = 0 })
                await cmd.ExecuteNonQueryAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into schema_migrations (version, name, checksum) values (@Version, @Name, @Checksum)",
                new { script.Version, script.Name, script.Checksum }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch (PostgresException e)
        {
            throw new MigrationFailedException(script.FileName, e); // disposing tx rolls back
        }
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass.** Run `ALL` with the tunnel up. Expected: 11 new passing tests, with 0 failed overall. Record the counts.

- [ ] **Step 5: Commit.** Message: `Phase 13 Stage 1: forward-only checksummed migrator with advisory lock and per-file transactions`, with the Co-Authored-By line.

---

### Task 7: Foundation migration 0000 and role privileges

**Files:**
- Create: `Backend/db/migrations/0000_foundation.sql`
- Modify: `MigrationCatalog.cs`, to add the embedded loading
- Test: `Backend/tests/Accounts.Integration.Tests/Database/FoundationPrivilegesTests.cs`, `Unit/EmbeddedCatalogTests.cs`

**Interfaces:**
- Produces:
  - `MigrationCatalog.EmbeddedFiles(Assembly? = null) : IReadOnlyList<(string FileName, byte[] Content)>`;
  - `MigrationCatalog.Embedded : MigrationCatalog`, a lazily parsed static;
  - the test helper `Catalogs.RealPlus(params (string FileName, string Sql)[] extra)`.

**Numbering:** the spec's §15.1 starts the domain schema at `0001` (accounts and auth, in Stage 3). `0000_foundation` holds only privileges, so the spec's numbers don't change.

**Bootstrap order and privileges (ruling 3; also written into `Backend/README.md` in Task 13).**
1. **First start of an empty data directory. This is the only privileged step.**
   - The official image runs `initdb` as the cluster superuser `postgres`, then `initdb/00-roles.sh` as that superuser over the local socket.
   - That script creates the `accounts_migrator` and `accounts_app` login roles, both `NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION`.
   - It creates the database `accounts`, **owned by `accounts_migrator`**, revokes `PUBLIC` access and grants `CONNECT` to `accounts_app`.
   - It runs once; a non-empty data directory skips it.
2. **`migrate`, as `accounts_migrator`.** As the database owner, and a member of `pg_database_owner` (which owns schema `public` on PostgreSQL 15 and later), it:
   - creates `schema_migrations`;
   - applies `0000_foundation`: database `CONNECT`, schema `USAGE`, revoking `PUBLIC`, and default privileges for its future tables and sequences;
   - grants `SELECT` on `schema_migrations` to the app.

   No superuser is involved.
3. **Later migrations, as `accounts_migrator`.** They create tables, and `accounts_app` gets `SELECT`, `INSERT`, `UPDATE` and `DELETE` through the default privileges. Append-only tables revoke `UPDATE` and `DELETE` in their own migration.
4. **The API runs as `accounts_app`.** It owns nothing and has no `CREATE` on the schema or `TEMPORARY` on the database, so it has **no DDL**: no `CREATE`, `ALTER`, `DROP` or `TRUNCATE`. The tests below pin this.
5. **The superuser is used only** by the image's init, and by `backup.sh` and `restore-drill.sh` through `docker exec` over the container's local socket. No service connects as it.

- [ ] **Step 1: Write the failing tests.**

`Unit/EmbeddedCatalogTests.cs`:
```csharp
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class EmbeddedCatalogTests
{
    [Fact]
    public void Embedded_catalog_starts_with_the_foundation()
    {
        Assert.Equal("0000_foundation.sql", MigrationCatalog.Embedded.Scripts[0].FileName);
        Assert.Equal(MigrationCatalog.Embedded.Scripts.Count - 1, MigrationCatalog.Embedded.LatestVersion);
    }
}
```

Add to `Support/Catalogs.cs`:
```csharp
    public static MigrationCatalog RealPlus(params (string FileName, string Sql)[] extra) =>
        MigrationCatalog.Parse(MigrationCatalog.EmbeddedFiles().Concat(extra.Select(f => (f.FileName, Encoding.UTF8.GetBytes(f.Sql)))));
```

`Database/FoundationPrivilegesTests.cs`:
```csharp
using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class FoundationPrivilegesTests : IAsyncLifetime
{
    TestDatabase db = null!;

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        // The real foundation, then a table the migrator creates afterwards (as a later migration would).
        await new Migrator(db.AsMigrator(), Catalogs.RealPlus(("0001_probe.sql", "create table probe (id int primary key, label text not null);"))).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    static async Task<string> SqlStateOf(NpgsqlDataSource ds, string sql)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
        return e.SqlState;
    }

    [Fact]
    public async Task App_reads_schema_migrations()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("select count(*) from schema_migrations"));
    }

    [Theory]
    [InlineData("create table x (id int)")]
    [InlineData("insert into schema_migrations (version, name, checksum) values (99, 'x', decode(repeat('00', 32), 'hex'))")]
    [InlineData("update schema_migrations set name = 'x'")]
    [InlineData("delete from schema_migrations")]
    [InlineData("truncate probe")]
    [InlineData("drop table probe")]
    [InlineData("create temp table x (id int)")]
    public async Task App_cannot_change_structure_or_history(string sql) =>
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), sql)); // insufficient_privilege (drop: must be owner, also 42501)

    [Fact]
    public async Task App_gets_dml_on_tables_the_migrator_creates_later()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into probe (id, label) values (1, 'a')");
        await conn.ExecuteAsync("update probe set label = 'b' where id = 1");
        Assert.Equal("b", await conn.ExecuteScalarAsync<string>("select label from probe where id = 1"));
        await conn.ExecuteAsync("delete from probe where id = 1");
    }

    [Fact]
    public async Task Public_cannot_connect()
    {
        var e = await Assert.ThrowsAsync<PostgresException>(async () => { await using var c = await db.AsOutsider().OpenConnectionAsync(); });
        Assert.Equal("42501", e.SqlState);
    }

    [Fact]
    public async Task Migrator_cannot_create_roles_or_databases()
    {
        Assert.Equal("42501", await SqlStateOf(db.AsMigrator(), "create role bg_should_not_exist"));
        Assert.Equal("42501", await SqlStateOf(db.AsMigrator(), "create database bg_should_not_exist"));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Expected: a build error, because `Embedded` and `EmbeddedFiles` don't exist.

- [ ] **Step 3: Implement.**

`Backend/db/migrations/0000_foundation.sql`:
```sql
-- 0000 foundation (decision 048, spec §15.4): database and schema privileges for the two application roles.
-- The database is owned by accounts_migrator, which runs this without superuser rights.
-- Later migrations create tables as accounts_migrator; the default privileges below give accounts_app data access
-- (no TRUNCATE, no DDL). Append-only tables revoke UPDATE and DELETE in their own migration.

do $$
begin
  execute format('revoke all on database %I from public', current_database());
  execute format('grant connect on database %I to accounts_app', current_database());
end
$$;

revoke all on schema public from public;
grant usage on schema public to accounts_app;

alter default privileges for role accounts_migrator in schema public
  grant select, insert, update, delete on tables to accounts_app;
alter default privileges for role accounts_migrator in schema public
  grant usage, select on sequences to accounts_app;

-- The migrator creates schema_migrations before running 0000, so it needs an explicit grant: read-only.
grant select on schema_migrations to accounts_app;
```

Add to `MigrationCatalog.cs`, with `using System.Reflection;`:
```csharp
    const string ResourcePrefix = "migrations.";
    static readonly Lazy<MigrationCatalog> EmbeddedCatalog = new(() => Parse(EmbeddedFiles()));

    /// <summary>The migrations compiled into this assembly from Backend/db/migrations.</summary>
    public static MigrationCatalog Embedded => EmbeddedCatalog.Value;

    public static IReadOnlyList<(string FileName, byte[] Content)> EmbeddedFiles(Assembly? assembly = null)
    {
        assembly ??= typeof(MigrationCatalog).Assembly;
        var files = new List<(string, byte[])>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files.Add((resource[ResourcePrefix.Length..], buffer.ToArray()));
        }
        return files;
    }
```

- [ ] **Step 4: Run the tests and confirm they pass.** Run `ALL`. Expected: 1 unit test and 11 database tests new, with 0 failed overall.

  If `revoke all on schema public from public` fails with 42501 for the migrator, stop and report. On PostgreSQL 15 and later, `public` is owned by `pg_database_owner`, which the migrator is a member of as the database owner. A failure would mean the image's template differs, and the fix belongs in `00-roles.sh` (`alter schema public owner to accounts_migrator`), not in a weaker test.

- [ ] **Step 5: Commit.** Message: `Phase 13 Stage 1: migration 0000 (role privileges) and the embedded catalog`, with the Co-Authored-By line.

---

### Task 8: Upgrade harness and invariant queries

**Files:**
- Create: `Backend/db/invariants/0000_schema_migrations_contiguous.sql`
- Create: `Backend/tests/Accounts.Integration.Tests/Support/InvariantRunner.cs`
- Modify: `Accounts.Integration.Tests.csproj`, to copy the invariants and the fixture invariants to the output
- Create: `Backend/tests/Accounts.Integration.Tests/Fixtures/invariants/probe_label_not_empty.sql`
- Test: `Backend/tests/Accounts.Integration.Tests/Database/UpgradeTests.cs`

**Interfaces:**
- Consumes: `Migrator`, `Catalogs.RealPlus`, `TestCluster` (Tasks 5–7).
- Produces:
  - `InvariantRunner.RunAsync(NpgsqlDataSource superuser, params string[] directories) : Task<IReadOnlyList<string>>`, which returns one message per violated invariant;
  - `InvariantRunner.RealDirectory`, the `invariants` folder in the test output;
  - the convention that an invariant file is a query returning violating rows, so zero rows means it holds;
  - the per-version seed convention `tests/Accounts.Integration.Tests/Seeds/NNNN.sql`: data valid **before** migration NNNN, applied by the upgrade test when it exists.

- [ ] **Step 1: Write the invariant and fixture files.**

`Backend/db/invariants/0000_schema_migrations_contiguous.sql`:
```sql
-- Violations: the migration history must be 0000, 0001, ... with no gaps (spec §15.4).
select s.version
from schema_migrations s
where s.version > 0
  and not exists (select 1 from schema_migrations p where p.version = s.version - 1)
union all
select 0
where exists (select 1 from schema_migrations)
  and not exists (select 1 from schema_migrations where version = 0);
```

`Fixtures/invariants/probe_label_not_empty.sql`:
```sql
-- Fixture invariant for the upgrade-harness test: every probe row has a label.
select id from probe where label = '';
```

Add to the test `.csproj`:
```xml
  <ItemGroup>
    <None Include="..\..\db\invariants\*.sql" Link="invariants\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <None Include="Fixtures\**\*.sql" CopyToOutputDirectory="PreserveNewest" />
    <None Include="Seeds\*.sql" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests.**

```csharp
using Dapper;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class UpgradeTests
{
    static readonly string FixtureInvariants = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invariants");

    public static TheoryData<int> RealVersions()
    {
        var data = new TheoryData<int>();
        foreach (var s in MigrationCatalog.Embedded.Scripts) data.Add(s.Version);
        return data;
    }

    /// <summary>Spec §15.4: every migration is applied to a database at the previous version, seeded, then checked.</summary>
    [Theory]
    [MemberData(nameof(RealVersions))]
    public async Task Every_real_migration_applies_on_top_of_its_predecessor(int version)
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(db.AsMigrator(), MigrationCatalog.Embedded);
        if (version > 0) await migrator.MigrateAsync(version - 1);
        var seed = Path.Combine(AppContext.BaseDirectory, "Seeds", $"{version:D4}.sql");
        if (File.Exists(seed))
        {
            await using var conn = await db.AsMigrator().OpenConnectionAsync();
            await conn.ExecuteAsync(await File.ReadAllTextAsync(seed));
        }
        Assert.Equal([version], (await migrator.MigrateAsync(version)).Applied);
        Assert.Empty(await InvariantRunner.RunAsync(db.AsSuperuser(), InvariantRunner.RealDirectory));
    }

    [Fact]
    public async Task Seeded_rows_survive_an_expand_migration_and_invariants_hold()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.RealPlus(
            ("0001_probe.sql", "create table probe (id int primary key, label text not null);"),
            ("0002_probe_created_at.sql", "alter table probe add column created_at timestamptz not null default now();"));
        var migrator = new Migrator(db.AsMigrator(), catalog);
        await migrator.MigrateAsync(1);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into probe (id, label) select g, 'row ' || g from generate_series(1, 100) g");
        await migrator.MigrateAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            Assert.Equal(100, await conn.ExecuteScalarAsync<int>("select count(*) from probe where created_at is not null"));
        }
        Assert.Empty(await InvariantRunner.RunAsync(db.AsSuperuser(), InvariantRunner.RealDirectory, FixtureInvariants));
    }

    [Fact]
    public async Task The_runner_reports_violations()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.RealPlus(("0001_probe.sql", "create table probe (id int primary key, label text not null);"))).MigrateAsync();
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("insert into probe values (1, '')");
            await conn.ExecuteAsync("delete from schema_migrations where version = 0");
        }
        var failures = await InvariantRunner.RunAsync(db.AsSuperuser(), InvariantRunner.RealDirectory, FixtureInvariants);
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.StartsWith("0000_schema_migrations_contiguous.sql", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("probe_label_not_empty.sql", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Expected: a build error, because `InvariantRunner` doesn't exist.

- [ ] **Step 4: Implement.** `Support/InvariantRunner.cs`:
```csharp
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Runs invariant files (spec §18.3): each is a query returning violating rows; zero rows means it holds.
/// The same files are run by deploy/services/restore-drill.sh.</summary>
public static class InvariantRunner
{
    public static string RealDirectory => Path.Combine(AppContext.BaseDirectory, "invariants");

    public static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource superuser, params string[] directories)
    {
        var failures = new List<string>();
        await using var conn = await superuser.OpenConnectionAsync();
        foreach (var file in directories.SelectMany(d => Directory.GetFiles(d, "*.sql")).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var rows = (await conn.QueryAsync(await File.ReadAllTextAsync(file))).ToList();
            if (rows.Count > 0) failures.Add($"{Path.GetFileName(file)}: {rows.Count} violating row(s)");
        }
        return failures;
    }
}
```

- [ ] **Step 5: Run the tests and confirm they pass.** Run `ALL`. Expected: 3 new tests pass (the theory has one case, 0000, today), with 0 failed overall.

- [ ] **Step 6: Commit.** Message: `Phase 13 Stage 1: upgrade-test harness and invariant queries`, with the Co-Authored-By line.

---

### Task 9: API host (`serve`, `migrate`, `/health`)

**Files:**
- Create: `Backend/src/Accounts.Persistence/Migrations/SchemaGuard.cs`
- Create: `Backend/src/Accounts.Api/Cli.cs`, `ExitCodes.cs`, `ProcessEnvironment.cs`, `ServeCommand.cs`, `MigrateCommand.cs`, `HealthEndpoint.cs`
- Modify: `Backend/src/Accounts.Api/Program.cs`
- Test: `Backend/tests/Accounts.Integration.Tests/Database/ApiTests.cs`, `Unit/CliTests.cs`

**Interfaces:**
- Consumes: `DbSettings`, `Migrator`, `MigrationCatalog.Embedded`, `SchemaComparison` and `ApiHost` (Tasks 1–7).
- Produces:
  - `SchemaGuard.CheckAsync(NpgsqlDataSource, MigrationCatalog, CancellationToken) : Task<SchemaStatus>`;
  - `ExitCodes`: `Ok = 0`, `Failure = 1`, `SchemaMismatch = 2` (migrate), `SchemaNotCurrent = 3` (serve), `Usage = 64`;
  - `Cli.RunAsync(string[] args, IReadOnlyDictionary<string,string?> env, TextWriter output, TextWriter error, CancellationToken = default) : Task<int>`;
  - `ServeCommand.StartAsync(string[] args, IReadOnlyDictionary<string,string?> env, TextWriter error, CancellationToken) : Task<ServeStart>`, where `record ServeStart(int ExitCode, WebApplication? App)`;
  - `MigrateCommand.RunAsync(env, output, error, ct) : Task<int>`;
  - `HealthEndpoint.MapHealth(this WebApplication, int schemaVersion)`, whose responses are `200 {"status":"ok","schemaVersion":N}` or `503 {"status":"unavailable","schemaVersion":null}`.

**A newer schema (ruling 4):** `serve` **refuses** a database that is ahead, with versions this build doesn't know, using exit code 3, just as for behind or drifted. `migrate` refuses it too (Task 6). Letting an older API run against a newer schema needs an explicit compatibility declaration that has been established and tested. That's a separate future design, and Stage 1 has no override.

- [ ] **Step 1: Write the failing tests.**

`Unit/CliTests.cs`:
```csharp
using Blackglass.Accounts.Api;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class CliTests
{
    [Fact]
    public async Task Unknown_command_prints_usage_and_exits_64()
    {
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await Cli.RunAsync(["bogus"], new Dictionary<string, string?>(), TextWriter.Null, err));
        Assert.Contains("usage", err.ToString());
    }

    [Fact]
    public async Task Missing_database_settings_fail_with_the_variable_named()
    {
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Failure, await Cli.RunAsync(["migrate"], new Dictionary<string, string?>(), TextWriter.Null, err));
        Assert.Contains("BLACKGLASS_DB_", err.ToString());
    }
}
```

`Database/ApiTests.cs`:
```csharp
using System.Net;
using Dapper;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class ApiTests
{
    static Dictionary<string, string?> ServeEnv(TestDatabase db)
    {
        var env = db.EnvFor("app");
        env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        return env;
    }

    static async Task<TestDatabase> MigratedAsync()
    {
        var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
        return db;
    }

    [Fact]
    public async Task Health_is_ok_with_the_schema_version()
    {
        await using var db = await MigratedAsync();
        var start = await ServeCommand.StartAsync([], ServeEnv(db), TextWriter.Null, default);
        await using var app = start.App!;
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{{\"status\":\"ok\",\"schemaVersion\":{MigrationCatalog.Embedded.LatestVersion}}}", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/anything-else")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task Health_is_503_while_the_database_refuses_connections_then_recovers()
    {
        await using var db = await MigratedAsync();
        var start = await ServeCommand.StartAsync([], ServeEnv(db), TextWriter.Null, default);
        await using var app = start.App!;
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        {
            await su.ExecuteAsync($"alter database \"{db.Name}\" allow_connections false");
            await su.ExecuteAsync("select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and usename = 'accounts_app'");
        }
        var down = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        var body = await down.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"unavailable\",\"schemaVersion\":null}", body);
        Assert.DoesNotContain(db.Password("app"), body);

        await using (var admin = (await TestCluster.Instance).DataSource("postgres", "postgres", (await TestCluster.Instance).SuperuserPassword))
        await using (var conn = await admin.OpenConnectionAsync())
            await conn.ExecuteAsync($"alter database \"{db.Name}\" allow_connections true");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task Serve_refuses_a_database_that_is_behind()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync([], ServeEnv(db), err, default);
        Assert.Equal(ExitCodes.SchemaNotCurrent, start.ExitCode);
        Assert.Null(start.App);
        Assert.Contains("Refusing to start: schema Behind", err.ToString());
    }

    [Fact]
    public async Task Serve_refuses_a_drifted_database()
    {
        await using var db = await MigratedAsync();
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
            await su.ExecuteAsync("update schema_migrations set checksum = decode(repeat('00', 32), 'hex') where version = 0");
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync([], ServeEnv(db), err, default);
        Assert.Equal(ExitCodes.SchemaNotCurrent, start.ExitCode);
        Assert.Contains("Drift", err.ToString());
    }

    [Fact]
    public async Task Serve_refuses_a_database_that_is_ahead()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.RealPlus(($"{MigrationCatalog.Embedded.LatestVersion + 1:D4}_future.sql", "create table future (id int);"))).MigrateAsync();
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync([], ServeEnv(db), err, default);
        Assert.Equal(ExitCodes.SchemaNotCurrent, start.ExitCode);
        Assert.Null(start.App);
        Assert.Contains("Refusing to start: schema Ahead", err.ToString());
    }

    [Fact]
    public async Task Serve_exits_1_when_the_database_is_unreachable_and_prints_no_password()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var env = ServeEnv(db);
        env["BLACKGLASS_DB_PORT"] = "1";
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Failure, (await ServeCommand.StartAsync([], env, err, default)).ExitCode);
        Assert.DoesNotContain(db.Password("app"), err.ToString());
    }

    [Fact]
    public async Task Wrong_password_fails_without_echoing_any_password()
    {
        await using var db = await MigratedAsync();
        var env = ServeEnv(db);
        File.WriteAllText(env["BLACKGLASS_DB_PASSWORD_FILE"]!, "wrong-password-XYZ");
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Failure, (await ServeCommand.StartAsync([], env, err, default)).ExitCode);
        Assert.DoesNotContain("wrong-password-XYZ", err.ToString());
        Assert.DoesNotContain(db.Password("app"), err.ToString());
    }

    [Fact]
    public async Task Migrate_command_applies_then_reports_nothing_to_do()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var out1 = new StringWriter();
        Assert.Equal(ExitCodes.Ok, await Cli.RunAsync(["migrate"], db.EnvFor("migrator"), out1, TextWriter.Null));
        Assert.Contains("Applied 0000", out1.ToString());
        var out2 = new StringWriter();
        Assert.Equal(ExitCodes.Ok, await Cli.RunAsync(["migrate"], db.EnvFor("migrator"), out2, TextWriter.Null));
        Assert.Contains("nothing applied", out2.ToString());
        Assert.DoesNotContain(db.Password("migrator"), out1.ToString() + out2.ToString());
    }

    [Fact]
    public async Task Migrate_command_exits_2_on_drift()
    {
        await using var db = await MigratedAsync();
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
            await su.ExecuteAsync("update schema_migrations set name = 'tampered' where version = 0");
        var err = new StringWriter();
        Assert.Equal(ExitCodes.SchemaMismatch, await Cli.RunAsync(["migrate"], db.EnvFor("migrator"), TextWriter.Null, err));
        Assert.Contains("Refusing to migrate", err.ToString());
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Expected: a build error.

- [ ] **Step 3: Implement.**

`SchemaGuard.cs` (Persistence):
```csharp
using Npgsql;

namespace Blackglass.Accounts.Persistence.Migrations;

/// <summary>The API's startup check (spec §15.4): reads schema_migrations as the application role.</summary>
public static class SchemaGuard
{
    public static async Task<SchemaStatus> CheckAsync(NpgsqlDataSource dataSource, MigrationCatalog catalog, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return SchemaComparison.Compare(catalog, await SchemaMigrationsTable.ReadAsync(conn, ct) ?? []);
    }
}
```

`ExitCodes.cs`:
```csharp
namespace Blackglass.Accounts.Api;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int SchemaMismatch = 2;
    public const int SchemaNotCurrent = 3;
    public const int Usage = 64;
}
```

`ProcessEnvironment.cs`:
```csharp
using System.Collections;

namespace Blackglass.Accounts.Api;

public static class ProcessEnvironment
{
    public static IReadOnlyDictionary<string, string?> Read() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
}
```

`HealthEndpoint.cs`:
```csharp
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Api;

public static class HealthEndpoint
{
    public sealed record HealthResponse(string Status, int? SchemaVersion);

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>GET /health: 200 when the database answers within the timeout, otherwise 503. The body never carries
    /// error details.</summary>
    public static void MapHealth(this WebApplication app, int schemaVersion) =>
        app.MapGet("/health", async (NpgsqlDataSource db, CancellationToken requestAborted) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            timeout.CancelAfter(Timeout);
            try
            {
                await using var conn = await db.OpenConnectionAsync(timeout.Token);
                await conn.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: timeout.Token));
                return Results.Json(new HealthResponse("ok", schemaVersion));
            }
            catch (Exception e) when (e is NpgsqlException or OperationCanceledException or TimeoutException)
            {
                return Results.Json(new HealthResponse("unavailable", null), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
}
```

`ServeCommand.cs`:
```csharp
using Npgsql;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.Api;

public sealed record ServeStart(int ExitCode, WebApplication? App);

public static class ServeCommand
{
    public static async Task<ServeStart> StartAsync(string[] args, IReadOnlyDictionary<string, string?> env, TextWriter error, CancellationToken ct)
    {
        DbSettings settings;
        try { settings = DbSettings.FromEnvironment(env); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return new(ExitCodes.Failure, null); }

        var dataSource = settings.CreateDataSource("blackglass-api");
        var catalog = MigrationCatalog.Embedded;
        SchemaStatus status;
        try { status = await SchemaGuard.CheckAsync(dataSource, catalog, ct); }
        catch (NpgsqlException e)
        {
            await dataSource.DisposeAsync();
            error.WriteLine($"Database unavailable at startup ({settings}): {Describe(e)}");
            return new(ExitCodes.Failure, null);
        }

        // Behind, drifted and ahead all refuse (ruling 4): no implicit compatibility with a newer schema.
        if (status.State != SchemaState.Current)
        {
            await dataSource.DisposeAsync();
            error.WriteLine(status.State == SchemaState.Behind
                ? $"Refusing to start: schema Behind: {status.Detail} Run the migrate command after a backup."
                : $"Refusing to start: schema {status.State}: {status.Detail}");
            return new(ExitCodes.SchemaNotCurrent, null);
        }

        var builder = WebApplication.CreateBuilder(args);
        if (ApiHost.ResolveUrls(env) is { } urls) builder.WebHost.UseUrls(urls);
        builder.Services.AddSingleton(dataSource);
        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(dataSource.Dispose);
        app.MapHealth(catalog.LatestVersion);
        await app.StartAsync(ct);
        return new(ExitCodes.Ok, app);
    }

    internal static string Describe(NpgsqlException e) => e is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : e.Message;
}
```

`MigrateCommand.cs`:
```csharp
using System.Globalization;
using Npgsql;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.Api;

public static class MigrateCommand
{
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string?> env, TextWriter output, TextWriter error, CancellationToken ct)
    {
        DbSettings settings;
        try { settings = DbSettings.FromEnvironment(env); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }

        await using var dataSource = settings.CreateDataSource("blackglass-migrator", maxPoolSize: 2);
        try
        {
            var result = await new Migrator(dataSource, MigrationCatalog.Embedded).MigrateAsync(ct: ct);
            output.WriteLine(result.Applied.Count == 0
                ? $"Schema is current at {result.SchemaVersion:D4}; nothing applied."
                : $"Applied {string.Join(", ", result.Applied.Select(v => v.ToString("D4", CultureInfo.InvariantCulture)))}; schema is now at {result.SchemaVersion:D4}.");
            return ExitCodes.Ok;
        }
        catch (SchemaMismatchException e) { error.WriteLine($"Refusing to migrate: {e.Message}"); return ExitCodes.SchemaMismatch; }
        catch (MigrationFailedException e) { error.WriteLine(e.Message); return ExitCodes.Failure; }
        catch (MigratorBusyException e) { error.WriteLine(e.Message); return ExitCodes.Failure; }
        catch (NpgsqlException e) { error.WriteLine($"Database error ({settings}): {ServeCommand.Describe(e)}"); return ExitCodes.Failure; }
    }
}
```

`Cli.cs`:
```csharp
namespace Blackglass.Accounts.Api;

public static class Cli
{
    public const string Usage = "usage: Accounts.Api [serve | migrate]";

    public static async Task<int> RunAsync(string[] args, IReadOnlyDictionary<string, string?> env, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var command = args.Length == 0 ? "serve" : args[0];
        switch (command)
        {
            case "migrate":
                return await MigrateCommand.RunAsync(env, output, error, ct);
            case "serve":
                var start = await ServeCommand.StartAsync(args.Skip(1).ToArray(), env, error, ct);
                if (start.App is null) return start.ExitCode;
                await using (start.App) await start.App.WaitForShutdownAsync(ct);
                return ExitCodes.Ok;
            default:
                error.WriteLine(Usage);
                return ExitCodes.Usage;
        }
    }
}
```

`Program.cs`:
```csharp
using Blackglass.Accounts.Api;

return await Cli.RunAsync(args, ProcessEnvironment.Read(), Console.Out, Console.Error);
```

- [ ] **Step 4: Run the tests and confirm they pass.** Run `ALL`. Expected: 2 new unit tests and 9 new database tests, with 0 failed overall.

  If `app.Urls.Single()` still shows port `0` after `StartAsync`, read `IServerAddressesFeature` from `app.Services.GetRequiredService<IServer>().Features` instead. Change only the tests' URL lookup.

- [ ] **Step 5: Run it by hand.** With the tunnel up, use the test cluster's `accounts` database, which is fresh and unmigrated:
  - `BLACKGLASS_DB_HOST=127.0.0.1 BLACKGLASS_DB_PORT=55432 BLACKGLASS_DB_NAME=accounts BLACKGLASS_DB_USER=accounts_app BLACKGLASS_DB_PASSWORD_FILE=~/.blackglass/testdb-secrets/pg_app_password dotnet run --project src/Accounts.Api` prints the "Refusing to start: schema Behind" line, with exit code 3;
  - `migrate` with the migrator file applies `0000`;
  - `serve` again listens on `http://127.0.0.1:5080`, and `curl -s http://127.0.0.1:5080/health` returns the ok body.

  Save the transcript to `stage-01/task-09.txt`.

- [ ] **Step 6: Commit.** Message: `Phase 13 Stage 1: API host with serve and migrate commands and GET /health`, with the Co-Authored-By line.

---

### Task 10: Container image and Compose stack, with static exposure tests

**Files:**
- Create: `Backend/Dockerfile`, `Backend/.dockerignore`, `Backend/deploy/services/compose.yaml`
- Create: `Backend/tests/Accounts.Integration.Tests/Support/ComposeText.cs`
- Modify: the test `.csproj`, to copy `deploy/services/compose*.yaml` to the output
- Test: `Backend/tests/Accounts.Integration.Tests/Unit/ComposeExposureTests.cs`

**Interfaces:**
- Consumes: the `serve` and `migrate` commands and the exit codes (Task 9), `initdb/00-roles.sh` (Task 5).
- Produces:
  - the image `blackglass-accounts-api:${BLACKGLASS_API_TAG:-dev}`;
  - the Compose services `postgres`, `migrate` (profile `tools`) and `api`;
  - the networks `db` (internal) and `edge`;
  - the secrets directory `${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/accounts/secrets}`;
  - `ComposeText.Lines(string file)` and `ComposeText.Find(IReadOnlyList<string>, params string[] path) : (int Start, int End)?`.

- [ ] **Step 1: Write the failing static tests.**

Add to the test `.csproj`:
```xml
    <None Include="..\..\deploy\services\compose*.yaml" Link="compose\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
```

`Support/ComposeText.cs`:
```csharp
namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Reads our own Compose files by indentation (two spaces per level, as the files' headers require). This is a
/// static guard; the runtime check on the VM is deploy/services/check-exposure.sh.</summary>
public static class ComposeText
{
    public static IReadOnlyList<string> Lines(string file) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "compose", file));

    public static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
    public static bool IsBlankOrComment(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    /// <summary>The child lines of the block at the key path, e.g. ("services", "postgres"), or null if absent.</summary>
    public static (int Start, int End)? Find(IReadOnlyList<string> lines, params string[] path)
    {
        int start = 0, end = lines.Count;
        for (var depth = 0; depth < path.Length; depth++)
        {
            var indent = depth * 2;
            var at = -1;
            for (var i = start; i < end; i++)
                if (!IsBlankOrComment(lines[i]) && Indent(lines[i]) == indent && lines[i].TrimStart().StartsWith(path[depth] + ":", StringComparison.Ordinal)) { at = i; break; }
            if (at < 0) return null;
            var stop = at + 1;
            while (stop < end && (IsBlankOrComment(lines[stop]) || Indent(lines[stop]) > indent)) stop++;
            (start, end) = (at + 1, stop);
        }
        return (start, end);
    }

    public static IEnumerable<string> Block(IReadOnlyList<string> lines, params string[] path) =>
        Find(lines, path) is { } r ? lines.Skip(r.Start).Take(r.End - r.Start).Where(l => !IsBlankOrComment(l)) : [];

    /// <summary>The keys directly under a block (e.g. the network names under "networks").</summary>
    public static IEnumerable<string> ChildKeys(IReadOnlyList<string> lines, params string[] path) =>
        Block(lines, path).Where(l => Indent(l) == path.Length * 2).Select(l => l.Trim().TrimEnd(':').Split(':')[0]);
}
```

`Unit/ComposeExposureTests.cs`:
```csharp
using System.Text.RegularExpressions;
using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class ComposeExposureTests
{
    public static TheoryData<string> Files => new() { "compose.yaml", "compose.test.yaml" };
    static readonly Regex LoopbackPort = new("^- \"127\\.0\\.0\\.1:\\d{1,5}:\\d{1,5}\"$");

    static List<string> Ports(IReadOnlyList<string> lines, string service) =>
        ComposeText.Block(lines, "services", service, "ports").Select(l => l.Trim()).ToList();

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_published_port_is_loopback_in_the_short_quoted_form(string file)
    {
        var lines = ComposeText.Lines(file);
        foreach (var service in ComposeText.ChildKeys(lines, "services"))
        {
            Assert.DoesNotContain(ComposeText.Block(lines, "services", service), l => l.Trim().StartsWith("ports:", StringComparison.Ordinal) && l.Trim() != "ports:");
            Assert.All(Ports(lines, service), p => Assert.Matches(LoopbackPort, p));
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void No_forbidden_settings(string file)
    {
        var text = string.Join('\n', ComposeText.Lines(file).Where(l => !ComposeText.IsBlankOrComment(l)));
        foreach (var token in new[] { "network_mode", "0.0.0.0", "\"::", "[::]", "host_ip", "privileged", "POSTGRES_PASSWORD:" })
            Assert.DoesNotContain(token, text);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_network_is_internal_or_pins_loopback_publishing(string file)
    {
        var lines = ComposeText.Lines(file);
        foreach (var network in ComposeText.ChildKeys(lines, "networks"))
        {
            var block = ComposeText.Block(lines, "networks", network).Select(l => l.Trim()).ToList();
            Assert.True(block.Contains("internal: true") || block.Contains("com.docker.network.bridge.host_binding_ipv4: \"127.0.0.1\""),
                $"{file}: network '{network}' is neither internal nor pinned to 127.0.0.1");
        }
    }

    [Fact]
    public void Accounts_stack_database_has_no_ports_and_sits_on_the_internal_network()
    {
        var lines = ComposeText.Lines("compose.yaml");
        Assert.Empty(Ports(lines, "postgres"));
        Assert.Contains("internal: true", ComposeText.Block(lines, "networks", "db").Select(l => l.Trim()));
        Assert.Equal(["- db"], ComposeText.Block(lines, "services", "postgres", "networks").Select(l => l.Trim()));
    }

    [Fact]
    public void Accounts_stack_publishes_exactly_the_api_on_5080()
    {
        var lines = ComposeText.Lines("compose.yaml");
        Assert.Equal(["- \"127.0.0.1:5080:8080\""], Ports(lines, "api"));
        Assert.Empty(Ports(lines, "migrate"));
    }

    [Fact]
    public void Test_cluster_publishes_only_55432()
    {
        Assert.Equal(["- \"127.0.0.1:55432:5432\""], Ports(ComposeText.Lines("compose.test.yaml"), "postgres"));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `UNIT` with `--filter "FullyQualifiedName~ComposeExposureTests"`. Expected: they fail because `compose.yaml` is missing.

- [ ] **Step 3: Write the image and the stack.**

`Backend/.dockerignore`:
```
**/bin/
**/obj/
tests/
deploy/
tools/
**/*.user
```

`Backend/Dockerfile`:
```dockerfile
# The accounts API image; the same image runs `migrate` (Compose service "migrate", profile tools).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
COPY db/ db/
RUN dotnet publish src/Accounts.Api/Accounts.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The base image sets ASPNETCORE_HTTP_PORTS=8080 and provides the non-root "app" user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Accounts.Api.dll"]
CMD ["serve"]
```

`Backend/deploy/services/compose.yaml`:
```yaml
# Blackglass accounts stack (bg-dev-services01). Loopback only, spec §4.5: the API is published on 127.0.0.1:5080;
# PostgreSQL has no ports and sits on an internal network. Deploy with deploy.sh (backup -> migrate -> start).
# Static tests read this file line by line: two-space indentation, ports in the short quoted form only.
name: blackglass-accounts

services:
  postgres:
    image: postgres:17
    command: ["postgres", "-c", "cluster_name=blackglass-accounts"]
    environment:
      POSTGRES_PASSWORD_FILE: /run/secrets/pg_superuser_password
    secrets:
      - pg_superuser_password
      - pg_init_migrator_password
      - pg_init_app_password
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./initdb:/docker-entrypoint-initdb.d:ro
    networks:
      - db
    healthcheck:
      # TCP, not the socket: during first-start init the image runs a temporary server on the socket only.
      test: ["CMD-SHELL", "pg_isready -h 127.0.0.1 -U postgres -d accounts"]
      interval: 5s
      timeout: 3s
      retries: 30
    restart: unless-stopped
    logging:
      driver: local

  migrate:
    image: blackglass-accounts-api:${BLACKGLASS_API_TAG:-dev}
    profiles:
      - tools
    command: ["migrate"]
    environment:
      BLACKGLASS_DB_HOST: postgres
      BLACKGLASS_DB_NAME: accounts
      BLACKGLASS_DB_USER: accounts_migrator
      BLACKGLASS_DB_PASSWORD_FILE: /run/secrets/migrate_db_password
    secrets:
      - migrate_db_password
    networks:
      - db
    depends_on:
      postgres:
        condition: service_healthy

  api:
    image: blackglass-accounts-api:${BLACKGLASS_API_TAG:-dev}
    build:
      context: ../..
      dockerfile: Dockerfile
    command: ["serve"]
    environment:
      BLACKGLASS_DB_HOST: postgres
      BLACKGLASS_DB_NAME: accounts
      BLACKGLASS_DB_USER: accounts_app
      BLACKGLASS_DB_PASSWORD_FILE: /run/secrets/api_db_password
    secrets:
      - api_db_password
    ports:
      - "127.0.0.1:5080:8080"
    networks:
      - db
      - edge
    depends_on:
      postgres:
        condition: service_healthy
    read_only: true
    tmpfs:
      - /tmp
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    restart: unless-stopped
    logging:
      driver: local

networks:
  db:
    internal: true
  edge:
    driver_opts:
      com.docker.network.bridge.host_binding_ipv4: "127.0.0.1"

volumes:
  pgdata:

# One 0400 file per consumer, owned by that container's user (ruling 5); see make-secrets.sh.
secrets:
  pg_superuser_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/accounts/secrets}/postgres/superuser_password
  pg_init_migrator_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/accounts/secrets}/postgres/migrator_password
  pg_init_app_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/accounts/secrets}/postgres/app_password
  migrate_db_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/accounts/secrets}/migrate/db_password
  api_db_password:
    file: ${BLACKGLASS_SECRETS_DIR:-/opt/blackglass/accounts/secrets}/api/db_password
```

The `api` service is on two networks. A container that is only on an `internal: true` network can't publish ports, so the API also joins `edge`, which pins publishing to `127.0.0.1` itself, independently of the daemon default.

- [ ] **Step 4: Run the tests and confirm they pass.** Run `UNIT`. Expected: the new tests pass (3 theories × 2 files + 3 facts = 9), with 0 failed overall.

- [ ] **Step 5: Commit, then build on the VM.** Commit with the message `Phase 13 Stage 1: API image and loopback-only Compose stack with static exposure tests`. Then:
  - run `bash tools/vm-sync.sh`;
  - on the VM, in `/opt/blackglass/backend/src/Backend/deploy/services`, run `sudo env BLACKGLASS_SECRETS_DIR=/tmp/none docker compose config --quiet && sudo docker compose build api`;
  - run `sudo docker run --rm --entrypoint id blackglass-accounts-api:dev`. Expected: a non-root uid, 1654, the `BLACKGLASS_APP_UID` default.

  Save the output to `stage-01/task-10.txt`.

---

### Task 11: Backup and the restore drill

**Files:**
- Create: `Backend/deploy/services/sql/row-counts.sql`, `backup.sh`, `restore-drill.sh`, `tests/drill-test.sh`
- Test: `tests/drill-test.sh` runs on the VM.

**Interfaces:**
- Consumes: `compose.yaml`, `make-secrets.sh`, `db/invariants/*.sql` and the image (Tasks 5, 8, 10).
- Produces:
  - `backup.sh`: environment `BLACKGLASS_COMPOSE_PROJECT`, `BLACKGLASS_DB`, `BLACKGLASS_BACKUP_DIR` and `BLACKGLASS_BACKUP_PAUSE`. It prints the `.dump` path on its last line and writes `<base>.dump`, `<base>.counts` and `<base>.dump.sha256`, all mode 600.
  - `restore-drill.sh <file.dump>`: prints `DRILL PASS: …` with exit 0, or `DRILL FAIL: <reason>` with exit 1.

- [ ] **Step 1: Write the test first.** `Backend/deploy/services/tests/drill-test.sh`:

```bash
#!/usr/bin/env bash
# Stage 1 drill test (bg-dev-services01): a backup taken while a writer runs records counts from its own snapshot, the
# restore drill passes, and three broken inputs make it FAIL. Uses the throwaway Compose project bg-drill and removes it.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
export BLACKGLASS_COMPOSE_PROJECT=bg-drill
work="$(mktemp -d)"
export BLACKGLASS_SECRETS_DIR="$work/secrets" BLACKGLASS_BACKUP_DIR="$work/backups"
dc() { docker compose -p bg-drill -f "$here/compose.yaml" "$@"; }
psql_su() { dc exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -A -t -U postgres -d accounts "$@"; }
cleanup() { dc --profile tools down -v --remove-orphans >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
failures=0
check() { local desc="$1"; shift; if "$@"; then echo "ok   - $desc"; else echo "FAIL - $desc"; failures=$((failures + 1)); fi; }
not() { ! "$@"; }

bash "$here/make-secrets.sh" "$BLACKGLASS_SECRETS_DIR" >/dev/null
dc build api >/dev/null
dc up -d --wait postgres
dc --profile tools run --rm migrate
psql_su <<'SQL'
create table drill_probe (id bigint generated always as identity primary key, payload bytea not null, note text not null, at timestamptz not null default now());
insert into drill_probe (payload, note, at)
  select sha256(g::text::bytea), 'ligne ' || g || ' — é ü 漢', now() - g * interval '1 minute' from generate_series(1, 1000) g;
SQL

# A writer that keeps inserting while the backup holds its snapshot open for 3 s.
( for _ in $(seq 1 60); do psql_su -c "insert into drill_probe (payload, note) values ('\\x00', 'during backup')"; sleep 0.1; done ) &
writer=$!
dump="$(BLACKGLASS_BACKUP_PAUSE=3 bash "$here/backup.sh" | tail -n 1)"
wait "$writer"
counts="${dump%.dump}.counts"
recorded="$(awk '$1 == "drill_probe" { print $2 }' "$counts")"
live="$(psql_su -c 'select count(*) from drill_probe')"
check "rows were written after the snapshot (recorded $recorded < live $live)" test "$recorded" -lt "$live"
check "backup files are private (600)" test "$(stat -c %a "$dump")" = 600 -a "$(stat -c %a "$counts")" = 600
check "restore drill passes" bash "$here/restore-drill.sh" "$dump"

head -c 2000 "$dump" > "$work/backups/truncated.dump"; cp "$counts" "$work/backups/truncated.counts"
check "a truncated dump fails the drill" not bash "$here/restore-drill.sh" "$work/backups/truncated.dump"

cp "$dump" "$work/backups/tampered.dump"; sed 's/^drill_probe .*/drill_probe 1/' "$counts" > "$work/backups/tampered.counts"
check "a count mismatch fails the drill" not bash "$here/restore-drill.sh" "$work/backups/tampered.dump"

mkdir -p "$work/invariants"; cp "$here/../../db/invariants/"*.sql "$work/invariants/"
echo "select 'always fails';" > "$work/invariants/9999_always_fails.sql"
check "a violated invariant fails the drill" not env BLACKGLASS_INVARIANTS_DIR="$work/invariants" bash "$here/restore-drill.sh" "$dump"

check "no scratch database is left behind" test -z "$(psql_su -d postgres -c "select datname from pg_database where datname like 'drill\\_%'")"
echo "drill-test: $failures failure(s)"
exit $(( failures > 0 ))
```

- [ ] **Step 2: Run it and confirm it fails.** Commit the test, then `bash tools/vm-sync.sh` and `ssh bg-dev-services01-admin 'sudo bash /opt/blackglass/backend/src/Backend/deploy/services/tests/drill-test.sh'`. Expected: it fails because `backup.sh` isn't found.

- [ ] **Step 3: Implement.**

`Backend/deploy/services/sql/row-counts.sql`:
```sql
-- Exact row count of every table in the public schema, then a digest of schema_migrations. backup.sh runs it inside
-- the dump's own snapshot and restore-drill.sh runs it on the restored copy; the two outputs must be identical.
select table_name || ' ' || (xpath('/row/c/text()',
         query_to_xml(format('select count(*) as c from public.%I', table_name), false, true, '')))[1]::text
from information_schema.tables
where table_schema = 'public' and table_type = 'BASE TABLE'
order by table_name;

select 'schema_migrations ' || case
  when to_regclass('public.schema_migrations') is null then 'absent'
  else (xpath('/row/d/text()', query_to_xml(
          'select coalesce(string_agg(version || '':'' || encode(checksum, ''hex''), '','' order by version), ''empty'') as d from schema_migrations',
          false, true, '')))[1]::text
end;
```

`Backend/deploy/services/backup.sh`:
```bash
#!/usr/bin/env bash
# Backs up one database of a Blackglass Compose stack (spec §15.4, B19): pg_dump -Fc plus exact row counts taken in the
# same snapshot, so the restore drill can compare them even while the API writes. Runs on bg-dev-services01.
# Environment:
#   BLACKGLASS_COMPOSE_PROJECT  Compose project (default blackglass-accounts)
#   BLACKGLASS_DB               database (default accounts)
#   BLACKGLASS_BACKUP_DIR       host directory (default /opt/blackglass/accounts/backups)
#   BLACKGLASS_BACKUP_PAUSE     seconds to keep the snapshot open after the dump (tests only; default 0)
# Writes <db>-<UTC>.dump, .counts and .dump.sha256 (mode 600) and prints the .dump path last.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
db="${BLACKGLASS_DB:-accounts}"
dir="${BLACKGLASS_BACKUP_DIR:-/opt/blackglass/accounts/backups}"
pause="${BLACKGLASS_BACKUP_PAUSE:-0}"
[[ "$pause" =~ ^[0-9]+$ ]] || { echo "BLACKGLASS_BACKUP_PAUSE must be a whole number" >&2; exit 2; }
[[ "$db" =~ ^[a-z_][a-z0-9_]*$ ]] || { echo "invalid database name" >&2; exit 2; }
dc() { docker compose -p "$project" -f "$here/compose.yaml" "$@"; }

umask 077
mkdir -p "$dir"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
base="$dir/$db-$stamp"
remote_counts="/tmp/bg-backup-$stamp-$$.counts"

# One psql session holds a REPEATABLE READ snapshot. pg_dump, started from inside the session with \!, dumps exactly
# that snapshot to stdout (psql itself prints nothing to stdout here); the counts are then read in the same snapshot.
dc exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -U postgres -d "$db" > "$base.dump.partial" <<SQL
\pset format unaligned
\pset tuples_only on
begin transaction isolation level repeatable read read only;
select pg_export_snapshot() as snap \gset
\setenv BG_SNAP :snap
\! pg_dump -U postgres -Fc --snapshot="\$BG_SNAP" "$db"
\! sleep $pause
\o $remote_counts
$(cat "$here/sql/row-counts.sql")
\o
commit;
SQL
dc exec -T postgres sh -c "cat '$remote_counts' && rm -f '$remote_counts'" > "$base.counts"
dc exec -T postgres pg_restore --list < "$base.dump.partial" > /dev/null   # the archive is readable
mv "$base.dump.partial" "$base.dump"
(cd "$dir" && sha256sum "$(basename "$base.dump")" > "$base.dump.sha256")
echo "backup: $(wc -l < "$base.counts") count lines, $(stat -c %s "$base.dump") bytes" >&2
echo "$base.dump"
```

`Backend/deploy/services/restore-drill.sh`:
```bash
#!/usr/bin/env bash
# Restore drill (B19): restores a backup into a scratch database, compares exact row counts with the counts recorded at
# backup time, runs the invariant queries (each returns violating rows), then drops the scratch database.
#   restore-drill.sh <file.dump>          expects <file>.counts next to it
# Environment: BLACKGLASS_COMPOSE_PROJECT, BLACKGLASS_INVARIANTS_DIR (default ../../db/invariants), BLACKGLASS_DRILL_KEEP=1
# Exit 0 with "DRILL PASS: ..." or exit 1 with "DRILL FAIL: <reason>".
set -euo pipefail
shopt -s nullglob
here="$(cd "$(dirname "$0")" && pwd)"
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
dump="${1:?usage: restore-drill.sh <file.dump>}"
counts="${dump%.dump}.counts"
invariants="${BLACKGLASS_INVARIANTS_DIR:-$here/../../db/invariants}"
dc() { docker compose -p "$project" -f "$here/compose.yaml" "$@"; }
psql_su() { dc exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -A -t -U postgres "$@"; }
fail() { echo "DRILL FAIL: $*"; exit 1; }

[[ -f "$dump" && -f "$counts" ]] || fail "missing $dump or $counts"
scratch="drill_$(date -u +%Y%m%d%H%M%S)_$$"
cleanup() {
  if [[ "${BLACKGLASS_DRILL_KEEP:-0}" != 1 ]]; then
    psql_su -d postgres -c "drop database if exists \"$scratch\" with (force)" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

psql_su -d postgres -c "create database \"$scratch\" owner accounts_migrator" >/dev/null
dc exec -T postgres pg_restore -U postgres -d "$scratch" --exit-on-error --single-transaction < "$dump" \
  || fail "pg_restore failed"
restored="$(psql_su -d "$scratch" < "$here/sql/row-counts.sql")" || fail "row counts could not be read"
diff -u "$counts" <(printf '%s\n' "$restored") >&2 || fail "row counts differ from the backup"
files=("$invariants"/*.sql)
for f in "${files[@]}"; do
  out="$(psql_su -d "$scratch" < "$f")" || fail "invariant $(basename "$f") errored"
  [[ -z "$out" ]] || fail "invariant $(basename "$f") reported: $(head -c 200 <<<"$out")"
done
echo "DRILL PASS: $(wc -l < "$counts") count lines matched, ${#files[@]} invariant(s) held, scratch database $scratch"
```

Run `git add --chmod=+x Backend/deploy/services/*.sh Backend/deploy/services/tests/*.sh`.

- [ ] **Step 4: Run the drill test and confirm it passes.** Commit, then `bash tools/vm-sync.sh` and run `drill-test.sh` on the VM. Expected: 7 `ok` lines and `drill-test: 0 failure(s)`, with exit 0. Save the transcript to `stage-01/task-11.txt`, which is the B19 evidence for Stage 1.

  If `\setenv BG_SNAP :snap` doesn't interpolate, or `pg_restore --list` refuses the piped archive, stop and report. Don't drop the snapshot: counts taken outside it are what Review Focus item 4 forbids.

- [ ] **Step 5: Commit any fixes.** Message: `Phase 13 Stage 1: snapshot-consistent backup and restore drill with invariant queries`, with the Co-Authored-By line.

---

### Task 12: Deployment order, the exposure check and the smoke test

**Files:**
- Create: `Backend/deploy/services/deploy.sh`, `check-exposure.sh`, `tests/smoke-test.sh`, `Backend/tools/offhost-probe.sh`
- Test: `tests/smoke-test.sh` on the VM, and `offhost-probe.sh` from the PC.

**Interfaces:**
- Consumes: `backup.sh` (Task 11), the Compose stack and the exit codes (Tasks 9–10).
- Produces:
  - `deploy.sh`, which runs build → database up → backup → migrate → API up → health → exposure;
  - `check-exposure.sh`, which prints `EXPOSURE PASS` with exit 0 or `EXPOSURE FAIL` with exit 1;
  - `offhost-probe.sh <alias> [ports...]`, which by default probes 5080, 5432 and 55432.

- [ ] **Step 1: Write the smoke test first.** `Backend/deploy/services/tests/smoke-test.sh`:

```bash
#!/usr/bin/env bash
# Stage 1 smoke test (bg-dev-services01), on a fresh volume: the API refuses an unmigrated database, deploy.sh runs
# backup -> migrate -> start in order, health answers on loopback, a second deploy migrates nothing, a failed backup
# stops the deploy, and the exposure check passes. Throwaway project bg-smoke; removed at the end.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
export BLACKGLASS_COMPOSE_PROJECT=bg-smoke
work="$(mktemp -d)"
export BLACKGLASS_SECRETS_DIR="$work/secrets" BLACKGLASS_BACKUP_DIR="$work/backups"
dc() { docker compose -p bg-smoke -f "$here/compose.yaml" "$@"; }
cleanup() { dc --profile tools down -v --remove-orphans >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
failures=0
check() { local desc="$1"; shift; if "$@"; then echo "ok   - $desc"; else echo "FAIL - $desc"; failures=$((failures + 1)); fi; }
not() { ! "$@"; }

if ss -H -ltn '( sport = :5080 )' | grep -q .; then echo "port 5080 is already in use; stop the other stack first" >&2; exit 2; fi
bash "$here/make-secrets.sh" "$BLACKGLASS_SECRETS_DIR" >/dev/null
dc build api >/dev/null
dc up -d --wait postgres

set +e; dc run --rm --no-deps api serve > "$work/behind.log" 2>&1; code=$?; set -e
check "the API refuses an unmigrated database with exit 3" test "$code" -eq 3
check "and says why" grep -q "Refusing to start: schema Behind" "$work/behind.log"

check "deploy.sh succeeds" bash -c "bash '$here/deploy.sh' > '$work/deploy1.log' 2>&1"
check "deploy backed up before migrating" awk '/^== backup/ { b = NR } /^== migrate/ { m = NR } END { exit !(b && m && b < m) }' "$work/deploy1.log"
check "a dump exists" compgen -G "$BLACKGLASS_BACKUP_DIR/accounts-*.dump"
check "health answers on loopback at schema 0000" test "$(curl -sf http://127.0.0.1:5080/health)" = '{"status":"ok","schemaVersion":0}'
check "the exposure check passes" bash "$here/check-exposure.sh"
for ip in $(hostname -I); do
  check "5432 refused on $ip" not timeout 2 bash -c "</dev/tcp/$ip/5432"
  [[ "$ip" == 127.* ]] || check "5080 refused on $ip" not timeout 2 bash -c "</dev/tcp/$ip/5080"
done

check "a second deploy applies nothing" bash -c "bash '$here/deploy.sh' 2>&1 | grep -q 'nothing applied'"
check "a failed backup stops the deploy before migrate" bash -c "! BLACKGLASS_BACKUP_DIR=/proc/forbidden bash '$here/deploy.sh' > '$work/deploy3.log' 2>&1 && ! grep -q '^== migrate' '$work/deploy3.log'"

# Keeps the stack up so the PC can run tools/offhost-probe.sh against it (default 0: no hold).
hold="${BLACKGLASS_SMOKE_HOLD:-0}"
if (( hold > 0 )); then echo "holding the stack for $hold s for the off-host probe"; sleep "$hold"; fi

echo "smoke-test: $failures failure(s)"
exit $(( failures > 0 ))
```

- [ ] **Step 2: Run it and confirm it fails.** Commit, sync and run it on the VM. Expected: it fails because `deploy.sh` is missing.

- [ ] **Step 3: Implement.**

`Backend/deploy/services/deploy.sh`:
```bash
#!/usr/bin/env bash
# Deploys the accounts stack in the required order (spec §15.4): build, database up, backup, migrate, API up, health,
# exposure check. Stops at the first failure, so the API never starts on an unmigrated schema and no migration runs
# without a fresh backup.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
dc() { docker compose -p "$project" -f "$here/compose.yaml" "$@"; }

echo "== build";    dc build api
echo "== database"; dc up -d --wait postgres
echo "== backup";   bash "$here/backup.sh"
echo "== migrate";  dc --profile tools run --rm migrate
echo "== api";      dc up -d api
echo "== health"
body=""
for _ in $(seq 1 30); do
  if body="$(curl -sf http://127.0.0.1:5080/health)"; then break; fi
  body=""; sleep 1
done
[[ -n "$body" ]] || { echo "health check failed; see: docker compose -p $project logs api" >&2; exit 1; }
echo "$body"
echo "== exposure"; bash "$here/check-exposure.sh"
```

`Backend/deploy/services/check-exposure.sh`:
```bash
#!/usr/bin/env bash
# Local exposure check for one Compose project on bg-dev-services01 (spec §4.5). Read-only.
# FAIL if: Docker Engine < 28; a container publishes on a non-loopback address; a project network is neither internal
# nor pinned to host_binding_ipv4=127.0.0.1; anything listens on 5432; 5080 or 55432 listen off loopback.
# The off-host scans (IPv4/IPv6, TCP/UDP, from the PC and the peer VM) are Stage 17.
set -euo pipefail
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
fail=0
bad() { echo "FAIL: $*"; fail=1; }

version="$(docker version --format '{{.Server.Version}}')"
echo "docker engine $version"
(( ${version%%.*} >= 28 )) || bad "Docker Engine $version is older than 28"

echo "-- published ports"
while read -r name ports; do
  echo "$name ${ports:-<none>}"
  for mapping in ${ports//,/ }; do
    [[ "$mapping" == *'->'* ]] || continue
    [[ "$mapping" == 127.0.0.1:* ]] || bad "$name publishes $mapping"
  done
done < <(docker ps --filter "label=com.docker.compose.project=$project" --format '{{.Names}} {{.Ports}}')

echo "-- networks"
while read -r net; do
  internal="$(docker network inspect -f '{{.Internal}}' "$net")"
  binding="$(docker network inspect -f '{{index .Options "com.docker.network.bridge.host_binding_ipv4"}}' "$net")"
  echo "$net internal=$internal host_binding_ipv4=$binding"
  [[ "$internal" == true || "$binding" == 127.0.0.1 ]] || bad "network $net can publish off loopback"
done < <(docker network ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}')

echo "-- TCP listeners"
ss -H -ltn
while read -r addr; do
  case "$addr" in
    *:5432) bad "something listens on $addr (PostgreSQL must not be published)" ;;
    127.0.0.1:5080|127.0.0.1:55432) ;;
    *:5080|*:55432) bad "$addr is not loopback" ;;
  esac
done < <(ss -H -ltn | awk '{print $4}')
echo "-- UDP listeners"
ss -H -lun

if (( fail )); then echo "EXPOSURE FAIL"; exit 1; fi
echo "EXPOSURE PASS"
```

`Backend/tools/offhost-probe.sh`:
```bash
#!/usr/bin/env bash
# From the PC: the listed TCP ports on the VM's SSH address must be closed or filtered, and 22 must answer.
# IPv4/TCP only (the full IPv4/IPv6, TCP/UDP scans are Stage 17). The address comes from the SSH alias and is never printed.
#   offhost-probe.sh [alias] [ports...]     default: bg-dev-services01-admin 5080 5432 55432
set -uo pipefail
alias="${1:-bg-dev-services01-admin}"; shift || true
ports=("$@"); (( ${#ports[@]} )) || ports=(5080 5432 55432)
host="$(ssh -G "$alias" | awk '$1 == "hostname" { print $2 }')"
fail=0
for port in "${ports[@]}"; do
  if timeout 3 bash -c "</dev/tcp/$host/$port" 2>/dev/null; then echo "FAIL - $port answers from the PC"; fail=1
  else echo "ok   - $port closed or filtered from the PC"; fi
done
if timeout 3 bash -c "</dev/tcp/$host/22" 2>/dev/null; then echo "ok   - 22 answers (SSH)"; else echo "FAIL - SSH (22) does not answer"; fail=1; fi
exit $fail
```

Run `git add --chmod=+x` on the new scripts.

- [ ] **Step 4: Run them and confirm they pass.** Commit and sync. With the Task 5 test cluster still up, so that 55432 is bound:
  - from the PC, start `ssh bg-dev-services01-admin 'sudo BLACKGLASS_SMOKE_HOLD=90 bash /opt/blackglass/backend/src/Backend/deploy/services/tests/smoke-test.sh' > .claude/scratchpad/phase13/stage-01/task-12-smoke.txt 2>&1 &`;
  - once that file shows the "holding the stack" line, run `bash Backend/tools/offhost-probe.sh` from the PC;
  - expected from the smoke test: every check `ok` and `smoke-test: 0 failure(s)`;
  - expected from the probe: 5080, 5432 and 55432 closed or filtered, and 22 open.

  Save the output to `stage-01/task-12.txt`.

- [ ] **Step 5: Commit.** Message: `Phase 13 Stage 1: deploy order script, exposure check and smoke test`, with the Co-Authored-By line.

---

### Task 13: Documentation alignment and the Stage 1 evidence

**Files:**
- Create: `Backend/README.md`, `Backend/deploy/services/env-check.sh` (the E1.1 read-only inventory script, copied unchanged from `.claude/scratchpad/phase13/stage-01/env/env-check.sh` and committed for reuse in Stage 17; run it with `ssh bg-dev-services01-admin 'sudo -n bash -s' < Backend/deploy/services/env-check.sh`)
- Modify: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (Stage 1 status), `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (§2.3, §4.4, §4.5)

- [ ] **Step 1: Write `Backend/README.md`**, about 60 lines, covering:
  - the layout;
  - the two test commands (`UNIT` and `ALL`) and the tunnel;
  - `tools/testdb.sh`;
  - the migration rules: naming; never edit an applied file; no `BEGIN`/`COMMIT` inside a file; expand and contract; add `tests/.../Seeds/NNNN.sql` when a migration transforms data;
  - the invariant convention (a file returns violating rows);
  - the VM scripts and their order;
  - the bootstrap order and privilege model from Task 7, word for word;
  - the secrets layout table from Task 5;
  - that database-level privileges are not inside a `pg_dump`. A real restore into a new cluster re-runs `initdb/00-roles.sh`, and Stage 17 and Phase 14 write the full recovery runbook.

- [ ] **Step 2: Apply the spec alignment.** Change only these rows:
  - **§2.3:** add the lines `Dockerfile`, `db/invariants/`, `deploy/services/{compose.test.yaml, initdb/, sql/, tests/, make-secrets.sh, deploy.sh, check-exposure.sh, env-check.sh}`, and `tools/{vm-sync.sh, testdb.sh, offhost-probe.sh}`. Replace "`migrate`, `backup-check` commands" with "`serve` and `migrate` commands (backup verification is `restore-drill.sh`)".
  - **§4.4,** the PostgreSQL passwords row: change "Compose secret files, mode 600 | `bg-dev-services01:/opt/accounts/secrets/`" to "Compose secret files, one per consumer, mode 0400 and owned by that container's user (`postgres/`, `migrate/`, `api/` in root-owned 0700 directories) | `bg-dev-services01:/opt/blackglass/accounts/secrets/`". In the admin key row, "same" stays.
  - **§15.4:** add after the Roles bullet: "**Bootstrap order:** first-start `initdb/00-roles.sh` (superuser, once) creates the roles and the migrator-owned database; `migrate` (as the owner) creates `schema_migrations` and applies `0000_foundation` (privileges); the application role never holds DDL. An older API refuses a newer schema unless an explicit, tested compatibility declaration exists (none yet)." 
  - **§4.5** allowlist: add the row `| Test PostgreSQL (services01, development only) | 127.0.0.1:55432 (docker-proxy) | Integration tests through an SSH tunnel; Compose project blackglass-testdb |`. Change "Only Docker listener" in the API row to "Only Docker listener of the accounts stack". Add a note under the table: "`bg-dev-services01` runs Debian 13; whether `systemd-resolved` (and so LLMNR) is present is recorded by E1.1 and handled in Stage 17."
  - **§4.5** rule 1: change "Stage 17 stops if it is older" to "Installed in E1 (before Stage 1's database work) and re-verified in Stage 17; either stops if it is older".

- [ ] **Step 3: Update the stage-level plan.** In its Stage 1 section, set "**Status: done on branch `phase13-stage1`, not merged**" and point to this plan and the evidence.

- [ ] **Step 4: Collect the evidence** in `stage-01/summary.md`:
  - the exact `ALL` counts, run on the final commit;
  - `drill-test.sh` (B19) and `smoke-test.sh`, with exit codes;
  - `check-exposure.sh` and `offhost-probe.sh` output;
  - the E1 install and verify transcripts;
  - the resolved Npgsql and Dapper versions;
  - `git status --short`;
  - `git diff --stat main...HEAD`, showing only the paths in Global Constraints;
  - sha256 of the owner's two settings files, compared with `.claude/scratchpad/phase13/stage-00/owner-files-before.txt`.

- [ ] **Step 5: Commit.** Use `git add Backend/README.md Backend/deploy/services/env-check.sh Docs/superpowers`, with the message `Phase 13 Stage 1: backend README, spec and plan alignment, Stage 1 status`, ending with the Co-Authored-By line. Then **stop and report**: no merge, no push.

---

## Stage 1 acceptance (all needed before the owner is asked to merge)

| # | Criterion | Evidence |
|---|---|---|
| S1-1 | E1: Docker Engine ≥ 28 and Compose installed from Docker's repository; the `daemon.json` defaults are in effect on the default bridge and on a user-defined network; probes publish only on `127.0.0.1` and can't be reached from the PC; SSH still works (second session) | `env-check.txt`, `e1-verify.txt`, probe output |
| S1-2 | `ALL` passes against PostgreSQL 17, with exact counts; `UNIT` passes with no database | `dotnet test` summaries |
| S1-3 | Migrator: empty database, re-run no-op, target version, failing file atomic and named, drift (checksum, rename, gap) refused, ahead refused, CRLF equivalence, concurrent migrators once each, busy timeout, `lock_timeout` per file | Task 6 and 8 test names in the run |
| S1-4 | Roles: the app has DML only (no DDL, no TRUNCATE, no history writes, no temp tables); the migrator can't create roles or databases; PUBLIC can't connect | Task 7 tests |
| S1-5 | The API refuses a database that is behind, drifted or ahead (exit 3); `/health` returns 200 or 503 and recovers; no password in any output, error or body | Task 9 tests and transcript |
| S1-6 | Compose: static loopback rules pass; the image runs as non-root; the smoke test passes on a fresh volume, including the order backup → migrate → start and a failed backup stopping the deploy | Task 10 and 12 output |
| S1-7 | B19 for Stage 1: a snapshot-consistent backup under concurrent writes; the drill passes; a truncated dump, a count mismatch and a violated invariant each fail | `drill-test.sh` transcript |
| S1-8 | Exposure: `check-exposure.sh` passes; from the PC, 5080, 5432 and 55432 are closed or filtered and 22 is open | Task 12 output |
| S1-9 | Hygiene: only the allowed paths changed; the owner's two settings files are byte-identical and unstaged; no secret is in Git; every secret file on the VM is 0400 and owned by its single consumer, in root-owned 0700 directories | Task 13 summary, `stat` output |

## Owner rulings applied (2026-10-14)

1. **E1 moves the Docker preparation ahead of Stage 1's database work.** Nothing is installed until the owner has reviewed the read-only checks and approved the installation. `ufw`, the resolver, the live stack and the off-host scans stay in Stage 17.
2. **An isolated PostgreSQL 17 test instance** on `bg-dev-services01`, published only on `127.0.0.1:55432` and reached through an SSH tunnel. It has its own credentials, secrets directory and data volume, separate from the application database.
3. **`0000_foundation` bootstraps the database permissions.** The bootstrap order is documented in Task 7 and in the README, and the application role holds no DDL.
4. **A newer schema is refused by default** (exit 3). An older API may run against a newer schema only once explicit compatibility has been established and tested; that is a future design, and Stage 1 has no override.
5. **Separate restrictive secret files per role and service:** 0400, owned by the single consumer, in root-owned 0700 directories. Files are never made more readable in order to share them. The `/opt/blackglass/accounts/` directory is approved.
6. **`restore-drill.sh` replaces `backup-check`.** The spec (§2.3) and this plan are updated in Task 13.
