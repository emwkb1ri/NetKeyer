# NetKeyer Rendezvous Services

This package provides the network services used by NetKeyer remote mode.

It includes two containers:

- `netkeyer-rendezvous`: control-plane service (HTTP + WebSocket) for host registration, host discovery, connection coordination, and relay fallback signaling.
- `netkeyer-relay`: data-plane TCP relay used when direct or mapped-direct connectivity is unavailable.

The rendezvous health endpoint is available at `/health` on port `49920`.

## Prerequisites

- Python 3.11+
- Docker
- Docker Compose

## Network and Router Preparation

Map router ports to the system running these containers:

- TCP `49920` -> rendezvous service host
- TCP `49921` -> relay service host
- TCP `49922` reserved for a future release feature

Manual static forwarding is the recommended deployment mode.

## Installation and Deployment

### 1. Unzip the distribution bundle

Extract the release zip to:

- Linux/macOS: `$HOME/rendezvous_services`
- Windows PowerShell: `$HOME\rendezvous_services`

Examples:

Linux/macOS:

```bash
mkdir -p "$HOME/rendezvous_services"
unzip -o netkeyer-rendezvous-services-<version>.zip -d "$HOME/rendezvous_services"
```

Windows PowerShell:

```powershell
New-Item -ItemType Directory -Force -Path "$HOME\rendezvous_services" | Out-Null
Expand-Archive -LiteralPath .\netkeyer-rendezvous-services-<version>.zip -DestinationPath "$HOME\rendezvous_services" -Force
```

Overwrite behavior notes:

- `Expand-Archive` only overwrites when `-Force` is used.
- `unzip` should use `-o` for non-interactive overwrite behavior.

### Upgrade Deployment (replace existing files)

For upgrades, always force overwrite during extraction before restarting containers.

Linux/macOS:

```bash
unzip -o netkeyer-rendezvous-services-<new-version>.zip -d "$HOME/rendezvous_services"
```

Windows PowerShell:

```powershell
Expand-Archive -LiteralPath .\netkeyer-rendezvous-services-<new-version>.zip -DestinationPath "$HOME\rendezvous_services" -Force
```

### 2. Change to the deployment directory

Linux/macOS:

```bash
cd "$HOME/rendezvous_services"
```

Windows PowerShell:

```powershell
Set-Location "$HOME\rendezvous_services"
```

### 3. Start services

```bash
docker compose -f docker-compose.yml up -d --build --force-recreate
```

### Clean Upgrade Procedure

Run these commands from the `rendezvous_services` deployment directory.

Linux/macOS:

```bash
cd "$HOME/rendezvous_services"
docker compose -f docker-compose.yml down
docker compose -f docker-compose.yml build --no-cache
docker compose -f docker-compose.yml up -d --force-recreate
docker compose -f docker-compose.yml logs rendezvous --tail=50
```

Windows PowerShell:

```powershell
Set-Location "$HOME\rendezvous_services"
docker compose -f docker-compose.yml down
docker compose -f docker-compose.yml build --no-cache
docker compose -f docker-compose.yml up -d --force-recreate
docker compose -f docker-compose.yml logs rendezvous --tail=50
```

After the upgrade, verify:

- `http://127.0.0.1:49920/health` includes `version.services_version`.

### 4. Verify deployment

Open this URL in a browser:

- `http://127.0.0.1:49920/health`

Expected result includes:

- `status: "ok"`
- `version.services_version`

If `version` is missing in `/health`, an old container image is likely still running. Re-run step 3 exactly with `--build --force-recreate`.

Optional startup log check:

```bash
docker compose -f docker-compose.yml logs rendezvous --tail=50
```

You should see a startup line with services version, protocol, tag, commit, and build timestamp.

## Included Files

The deployment artifact includes only the baseline rendezvous + relay stack for initial users.

- `docker-compose.yml`
- `server/`
- `relay/`
- version/build metadata files

The optional nginx overlay is intentionally not included in this release package.

## Release Checklist

Use this checklist when creating a new `rendezvous_services` release artifact.

1. Update service version in `pyproject.toml`:
	- `project.version = "<new-version>"`
2. From repository root, generate the stamped artifact:
	- Windows PowerShell:
	  - `./build-rendezvous-release.ps1`
	- Linux/macOS:
	  - `./build-rendezvous-release.sh`
3. Confirm artifact was created:
	- `Releases/netkeyer-rendezvous-services-<new-version>.zip`
4. (Optional but recommended) Inspect artifact metadata:
	- Extract and verify `RELEASE_METADATA.json` fields (`services_version`, `protocol_version`, `build_tag`, `commit`, `built_at_utc`).
5. Validate clean deployment from the new artifact:
	- Extract with overwrite into `$HOME/rendezvous_services`.
	- Run clean upgrade commands from that directory:
	  - `docker compose -f docker-compose.yml down`
	  - `docker compose -f docker-compose.yml build --no-cache`
	  - `docker compose -f docker-compose.yml up -d --force-recreate`
6. Verify runtime identity and health:
	- `http://127.0.0.1:49920/health` includes `version.services_version` matching the release.
	- `docker compose -f docker-compose.yml logs rendezvous --tail=50` shows startup version/protocol/tag/commit/build timestamp.
