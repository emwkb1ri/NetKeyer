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
unzip netkeyer-rendezvous-services-<version>.zip -d "$HOME/rendezvous_services"
```

Windows PowerShell:

```powershell
New-Item -ItemType Directory -Force -Path "$HOME\rendezvous_services" | Out-Null
Expand-Archive -LiteralPath .\netkeyer-rendezvous-services-<version>.zip -DestinationPath "$HOME\rendezvous_services" -Force
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
docker compose -f docker-compose.yml up -d
```

### 4. Verify deployment

Open this URL in a browser:

- `http://127.0.0.1:49920/health`

Expected result includes:

- `status: "ok"`

## Included Files

The deployment artifact includes only the baseline rendezvous + relay stack for initial users.

- `docker-compose.yml`
- `server/`
- `relay/`
- version/build metadata files

The optional nginx overlay is intentionally not included in this release package.
