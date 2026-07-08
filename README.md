# NetKeyer+Remote - FlexRadio CW Keyer

A cross-platform GUI application for CW (Morse code) keying with FlexRadio devices, supporting both serial port and MIDI input devices.

## Features

- **Cross-Platform**: Runs on Linux, Windows, and macOS using Avalonia UI
- **Radio Discovery**: Automatic discovery of FlexRadio devices on the network
  - Local network discovery
  - SmartLink remote connection support
  - Sidetone-only practice mode (no radio required)
- **Multiple Input Device Types**:
  - Serial port (HaliKey v1)
  - MIDI devices (HaliKey MIDI, CTR2, and other MIDI controllers)
  - Configurable MIDI note mappings for paddles, straight key, and PTT
- **CW Controls**:
  - Speed adjustment (5-60 WPM)
  - Sidetone volume control (0-100)
  - Pitch control (300-1000 Hz)
  - Iambic Mode A/B selection
  - Straight Key mode
  - Paddle swap option
- **Local Sidetone Generation**:
  - Low latency audio using platform-optimized backends
  - PortAudio for cross-platform compatibility
  - WASAPI for Windows
- **PTT Support**:
  - Supports PTT keying for non-CW modes
- **Remote Keying Transport (Phase 2)**:
  - Remote Client mode sends local paddle state over TCP
  - Remote Host mode accepts up to 5 TCP clients
  - Active-client ownership lock to prevent simultaneous multi-client keying contention
  - Configurable client ownership hold time from 0.5 to 30.0 seconds (default 1.0 second)
  - Stale-frame drop policy to reject delayed paddle frames before keying
  - Host and client telemetry summaries for last lag, avg lag, max lag (60s), accepted frames (60s), and stale drops
  - Telemetry lag values are normalized per client to remove static clock-skew bias while preserving observed network delay variation
  - Default TCP port is `49920`
  - Client keeps local sidetone active, host mutes local sidetone
- **Rendezvous + Relay Signaling and Fallback (Phase 3/4)**:
  - Optional rendezvous-assisted host discovery and connection setup for remote client mode
  - Automatic three-stage negotiation: direct -> mapped-direct (UPnP/NAT-PMP) -> relay fallback
  - Relay transport handshake support using `SESSION <session_id> <role>` (`HOST` / `CLIENT`)
  - Always-on connection outcome logging with transport labels: `direct`, `mapped-direct`, `relay`
  - Dedicated service ports aligned with remote transport defaults:
    - Remote keying transport: `49920`
    - Relay service: `49921`
    - Optional nginx relay TCP stream proxy: `49922`
    - Rendezvous HTTP/WebSocket service: `49923`

## Requirements

- .NET 8.0 Runtime
- FlexRadio device on the network (or use sidetone-only mode for practice)
- Input device:
  - Serial port device (e.g., HaliKey v1/v2), OR
  - MIDI controller (e.g., HaliKey MIDI, CTR2)
- SmartLink: you must be using a binary build from GitHub releases (or the builtin updater)
  to connect to SmartLink or see it in the UI. This is because FlexRadio requires us to keep
  the SmartLink client ID secret. Anyone wanting to develop a fork will have to negotiate a
  developer contract with FlexRadio if they want to use SmartLink. This is the best compromise
  we can manage for an open-source app.

## Building

### Requirements

- .NET 8.0 SDK
- To build the native MIDI shim (required for MIDI input):

  | Platform   | Tools required |
  |------------|----------------|
  | Linux      | `cmake`, `gcc`/`g++`, `libasound2-dev` (ALSA headers) |
  | Windows    | `cmake`, Visual Studio 2022 (includes MSVC, nmake, rc) |
  | macOS      | `cmake`, Xcode Command Line Tools (`xcode-select --install`) |

  CMake downloads libremidi automatically on first build (requires internet access).

### 1. Build the native MIDI shim

**Linux / macOS:**

```bash
cd native
./build.sh
```

**Windows (PowerShell):**

```powershell
cd native
.\build.ps1
```

The built binary is placed in the correct directory for your platform automatically
(e.g. `native/linux-x64/`, `native/osx-arm64/`, `native/windows-x64/`).

> **Not working on the native component?** You can skip the build above by copying
> the pre-built shim out of the [latest release](https://github.com/NetKeyer/NetKeyer/releases/latest)
> into the appropriate directory instead:
>
> | Platform    | File to copy                     | Destination              |
> |-------------|----------------------------------|--------------------------|
> | Linux x64   | `libnetkeyer_midi_shim.so`       | `native/linux-x64/`      |
> | Linux arm64 | `libnetkeyer_midi_shim.so`       | `native/linux-arm64/`    |
> | Windows     | `netkeyer_midi_shim.dll`         | `native/windows-x64/`    |
> | macOS x64   | `libnetkeyer_midi_shim.dylib`    | `native/osx-x64/`        |
> | macOS arm64 | `libnetkeyer_midi_shim.dylib`    | `native/osx-arm64/`      |

### 2. Build the application

```bash
dotnet build
```

## Running

```bash
dotnet run
```

## Usage

### Setup Page

1. **SmartLink (Optional)**: Click "Enable SmartLink" to connect to remote radios via FlexRadio SmartLink
2. **Select Radio**:
   - Click "Refresh" to discover FlexRadio devices
   - Select a radio and GUI client station from the dropdown, OR
   - Select "No radio (sidetone only)" for practice mode
3. **Select Input Device Type**: Choose between:
   - Serial Port (HaliKey v1) - uses CTS (left) and DSR (right) pins
   - MIDI (HaliKey MIDI, CTR2) - uses configurable MIDI note mappings
4. **Choose Input Device**:
   - For Serial: Select the serial port connected to your keyer/paddle
   - For MIDI: Select the MIDI device, then optionally click "Configure MIDI Notes..." to customize mappings
5. **Connect**: Click "Connect" to begin operating

### Operating Page

1. **Monitor Paddle Status**: Visual indicators show left/right paddle state in real-time
2. **Adjust CW Settings**:
   - Speed (WPM): Controls dit/dah timing
   - Sidetone: Volume of local audio feedback
   - Pitch: Frequency of sidetone tone
3. **Select Keyer Mode**:
   - Iambic: Automatic dit/dah generation with Mode A or Mode B
   - Straight Key: Direct on/off control
4. **Swap Paddles**: Reverse left/right paddle assignment if needed
5. **Disconnect**: Return to setup page to change settings

### Remote Mode

Use the **Remote Connection Mode** section on the setup page to select one of these modes:

- **Standalone**: Existing behavior (local input keys local radio connection)
- **Remote Client**:
  - Opens local input device and keeps local sidetone active
  - Sends paddle/straight/PTT state with timing ticks to a remote host via TCP
- **Remote Host**:
  - Connects to a local/SmartLink radio and listens for remote paddle events
  - Mutes local sidetone while host mode is active
  - Accepts up to 5 simultaneous TCP client connections
  - Includes active-client ownership lock with configurable hold time
  - Drops stale remote frames before they reach keying

Rendezvous setup inputs:
- **Redezvous Server**: enter only host name or IP (for example, `netkeyer.ddns.net`).
- **Port**: default `49923`.
- The app generates the control URL in code as `http://<server>:<port>`.

Remote host setup options include:

- **Max Clients**: limit concurrent remote clients (1 to 5)
- **Client Hold Time**: ownership hold duration after last accepted key input (0.5s to 30.0s in 0.5s increments)

Operating-page telemetry:

- **Host Status** and **Client Status** blocks include: 

- Host and Client connection list and status
- compact two-line telemetry display.
- Telemetry fields:
  - Line 1: last lag, avg lag, max lag (last 60 seconds)
  - Line 2: accepted frames in last 60 seconds, stale drops
- 60-second window metrics age out during idle periods (for example accepted 60s returns to 0 if no frames are received in the last 60 seconds).
- Telemetry text is rendered with high-contrast styling for readability in operating view.

Defaults:

- Port: `49920`
- Client target host: `127.0.0.1`
- Host bind address: `0.0.0.0`

## Rendezvous and Relay Services

NetKeyer now includes deployment artifacts for standalone rendezvous control-plane and relay data-plane services under [rendezvous_services](rendezvous_services).

The rendezvous server and relay server are Python applications requiring Python 3.11.
These Python apps are intended to run in Docker containers therfore Docker is required
to be installed on the system hosting these apps.  The repository contains the necessary Docker files for deployment.  It may be be necessary to open ports
49920-49923 on your router to allow the rendezvous server to be accessed over the WAN.

### Service Overview

- **Rendezvous server**: FastAPI + WebSocket signaling for host registration, host discovery, connect orchestration, and relay fallback signaling.
- **Relay server**: asyncio TCP byte pipe that pairs host/client sockets by session ID and forwards bytes bidirectionally.

### Connection Negotiation Summary

When rendezvous mode is enabled, NetKeyer negotiates transport in this order:

1. **Direct**: client attempts direct TCP to the host endpoint provided by rendezvous.
2. **Mapped-direct**: on direct timeout/failure, rendezvous requests host automatic TCP mapping (UPnP first, then NAT-PMP) and returns an updated mapped endpoint for a retry.
3. **Relay fallback**: if mapped endpoint is unavailable or retry fails, rendezvous signals both sides to switch to relay transport.

This keeps the keying data path as close to direct as possible while still providing deterministic fallback.

### Container Summary

| Container | Purpose | Internal Port | Host Port (default) |
|----------|---------|---------------|---------------------|
| `netkeyer-rendezvous` | HTTP/WebSocket control-plane (`/health`, `/ws/host`, `/ws/client`) | `49923` | `49923` |
| `netkeyer-relay` | Raw TCP relay service | `49921` | `49921` |
| `netkeyer-rendezvous-nginx` (optional) | Reverse proxy for rendezvous + optional TCP stream proxy for relay | `80` + `49922` | `8080` + `49922` |

## Docker Deployment (Rendezvous Services)

Compose files are split so nginx is optional:

- Base services (relay + rendezvous): [rendezvous_services/docker-compose.yml](rendezvous_services/docker-compose.yml)
- Optional nginx overlay: [rendezvous_services/docker-compose.nginx.yml](rendezvous_services/docker-compose.nginx.yml)

### Start relay + rendezvous only

```bash
cd rendezvous_services
docker compose -f docker-compose.yml up -d
```

### Start relay + rendezvous + optional bundled nginx

```bash
cd rendezvous_services
docker compose -f docker-compose.yml -f docker-compose.nginx.yml up -d
```

### Included nginx snippets

- HTTP/WebSocket reverse proxy config: [rendezvous_services/nginx/rendezvous.conf](rendezvous_services/nginx/rendezvous.conf)
- TCP stream relay proxy config: [rendezvous_services/nginx/stream-relay.conf](rendezvous_services/nginx/stream-relay.conf)

## Using an Existing nginx Installation

If you already run your own nginx, do **not** start the optional nginx compose overlay. Run only relay + rendezvous and add equivalent nginx config to your existing deployment.

### 1. Run only core services

```bash
cd rendezvous_services
docker compose -f docker-compose.yml up -d
```

### 2. Configure HTTP/WebSocket proxy to rendezvous

Point nginx to `netkeyer-rendezvous:49923` (or the host where rendezvous is published).

```nginx
upstream rendezvous_backend {
  server 127.0.0.1:49923;
}

server {
  listen 80;
  server_name _;

  location /health {
    proxy_pass http://rendezvous_backend/health;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
  }

  location /ws/ {
    proxy_pass http://rendezvous_backend/ws/;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 3600;
  }
}
```

### 3. (Optional) Configure relay TCP stream proxy

If you need nginx TCP stream proxying, add a `stream` block that forwards to relay on `49921`.

```nginx
stream {
  upstream relay_backend {
    server 127.0.0.1:49921;
  }

  server {
    listen 49922;
    proxy_pass relay_backend;
  }
}
```

### 4. NetKeyer client/server endpoint expectations

- Rendezvous URL should target your nginx/public endpoint that serves `/ws/host` and `/ws/client`.
- Relay host/port is provided to clients by rendezvous via `use_relay` signaling.
- Ensure firewall/NAT rules allow inbound traffic for whichever public rendezvous/relay ports you publish.

## MIDI Configuration

The MIDI note configuration dialog allows you to assign any MIDI note (0-127) to one or more functions:

- **Left Paddle**: Generates dits in iambic mode
- **Right Paddle**: Generates dahs in iambic mode
- **Straight Key**: Direct key on/off control
- **PTT**: Push-to-talk for non-CW modes

Default mappings (compatible with HaliKey MIDI and CTR2):

- Note 20: Left Paddle + Straight Key + PTT
- Note 21: Right Paddle + Straight Key + PTT
- Note 30: Straight Key only
- Note 31: PTT only

## About and Updates

- Window and dialog titles use `NetKeyer+Remote` branding.
- The About dialog credits show:
  - `by Eric NR4O`
  - `forked from NetKeyer by Andrew KC2G and contributors`
- `Check for Updates` is currently disabled until a new update location is configured.

## Troubleshooting

### Connection Issues

**Radio not found**:

- Ensure radio is on the same network
- Check firewall settings
- Try SmartLink if local discovery fails

**GUI client binding fails**:

- Radio needs SmartSDR or another GUI client running
- Wait a moment after connecting before binding

### Audio Issues

**No sidetone**:

- Check sidetone volume slider
- Verify system audio is not muted
- Check audio output device in your system mixer

**High latency**:

- Windows: Ensure WASAPI backend is being used
- Linux: Check PulseAudio/PipeWire configuration
- Adjust buffer size if needed

### Input Device Issues

**Serial port not found**:

- Check device permissions (Linux: add user to `dialout` group)
- Verify device is connected
- Click "Refresh" to rescan

**MIDI device not responding**:

- Verify MIDI device is connected and powered
- Check MIDI note mappings match your device
- Use "Configure MIDI Notes..." to adjust mappings

### Debug Logging

NetKeyer supports detailed debug logging controlled by the `NETKEYER_DEBUG` environment variable. This can help diagnose issues with specific subsystems.

Remote telemetry note:

- Core remote telemetry summaries are logged by default under category `remote-telemetry`, even when `NETKEYER_DEBUG` is not configured.
- Setting `NETKEYER_DEBUG=remote` still enables additional remote transport/session diagnostic logs.

**Log File Location**:

Debug messages are automatically written to a log file in the NetKeyer application data folder:

- **Windows**: `%APPDATA%\NetKeyer\debug.log`
- **Linux**: `~/.config/NetKeyer/debug.log`
- **macOS**: `~/Library/Application Support/NetKeyer/debug.log`

You can easily access the log folder via **Help → View Debug Log...** in the application menu.

**Note**: On Windows, GUI applications don't show console output when run outside a debugger. Debug messages are always written to the log file, making them accessible even when the console isn't visible.

**Available Debug Categories**:

| Category | Description |
|----------|-------------|
| `keyer` | Iambic keyer state machine (paddle state, element timing, mode transitions) |
| `midi` | MIDI input parsing and raw event processing |
| `input` | Input abstraction layer (paddle state changes, indicator updates) |
| `slice` | Transmit slice mode monitoring (CW vs PTT mode detection) |
| `sidetone` | Audio sidetone provider (tone/silence state machine, timing) |
| `audio` | Audio device management (initialization, enumeration, selection) |
| `remote` | Remote TCP client/host transport, framing, and session status |
| `remote-telemetry` | Always-on remote telemetry summaries (raw/baseline/normalized lag, jitter, accepted/stale counters) |

**Usage Examples**:

**Linux/macOS**:

```bash
# Enable all debug output
NETKEYER_DEBUG=all dotnet run

# Enable specific categories
NETKEYER_DEBUG=keyer,midi dotnet run

# Enable all MIDI-related categories using wildcard
NETKEYER_DEBUG=midi* dotnet run
```

**Windows PowerShell**:

```powershell
# Enable all debug output
$env:NETKEYER_DEBUG="all"
dotnet run

# Enable specific categories
$env:NETKEYER_DEBUG="keyer,midi"
dotnet run
```

**Windows CMD**:

```cmd
# Enable all debug output
set NETKEYER_DEBUG=all
dotnet run

# Enable specific categories
set NETKEYER_DEBUG=keyer,midi
dotnet run
```

**Common Debugging Scenarios**:

- **Paddle not working**: Use `NETKEYER_DEBUG=input,keyer` to see paddle state changes and keyer logic
- **MIDI issues**: Use `NETKEYER_DEBUG=midi,input` to see raw MIDI events and parsed paddle states
- **Audio problems**: Use `NETKEYER_DEBUG=audio,sidetone` to see device initialization and tone generation
- **Radio connection issues**: Use `NETKEYER_DEBUG=slice` to see transmit mode detection

---

## Developer Information

### Project Structure

```
NetKeyer/
├── Views/                  # XAML UI layouts
├── ViewModels/             # Application logic and data binding
│   ├── MainWindowViewModel.cs
│   ├── MidiConfigDialogViewModel.cs
│   ├── AudioDeviceDialogViewModel.cs
│   └── AboutWindowViewModel.cs
├── Models/                 # Data models
│   ├── UserSettings.cs
│   ├── MidiNoteMapping.cs
│   └── AudioDeviceInfo.cs
├── Services/               # Core application services
│   ├── InputDeviceManager.cs
│   ├── KeyingController.cs
│   ├── RadioSettingsSynchronizer.cs
│   ├── SmartLinkManager.cs
│   └── TransmitSliceMonitor.cs
├── Audio/                  # Sidetone generation
│   ├── SidetoneGeneratorFactory.cs
│   └── ISidetoneGenerator.cs
│   ├── SidetoneGenerator.cs (PortAudio)
│   ├── WasapiSidetoneGenerator.cs (Windows WASAPI)
│   ├── SidetoneProvider.cs (waveform generation)
├── Midi/                   # MIDI input handling
│   ├── MidiPaddleInput.cs
│   └── LibreMidi/          # Native shim P/Invoke layer
│       ├── NativeMethods.cs
│       └── LibreMidiInput.cs
├── native/                 # Native MIDI shim source and pre-built binaries
│   ├── netkeyer_midi_shim.c
│   ├── CMakeLists.txt
│   ├── exports.map
│   ├── build.sh            # Linux/macOS build script
│   ├── build.ps1           # Windows build script
│   ├── linux-x64/          # Pre-built binaries (not in git; build or copy from release)
│   ├── linux-arm64/
│   ├── windows-x64/
│   ├── osx-x64/
│   └── osx-arm64/
├── Keying/                 # Iambic keyer logic
│   └── IambicKeyer.cs
├── SmartLink/              # SmartLink authentication
│   ├── SmartLinkAuthService.cs
│   ├── SmartLinkModels.cs
├── Helpers/                # Utility classes
│   ├── DebugLogger.cs
│   └── UrlHelper.cs
├── lib/                    # Compiled FlexRadio libraries
```

### Input Device Support

**Serial Port (HaliKey v1)**:

- HaliKey v1: CTS (left paddle) + DSR (right paddle)

**MIDI Devices**:

- Supports any MIDI controller with configurable note mappings
  - Tested with HaliKey MIDI and CTR2-MIDI
- Note On/Off events trigger paddle/key/PTT state changes

### Iambic Keyer Implementation

- Software-based iambic keyer with Mode A and Mode B support
- State machine is based on audio timings

### Audio Sidetone

**WASAPI Backend** (Windows preferred):
- Lowest latency

**PortAudio Backend**:
- Cross-platform compatibility for Linux and macOS
- Supports Windows DirectSound and ASIO in case WASAPI doesn't work for some reason

### Settings Persistence

User settings are stored in:

- Linux: `~/.config/NetKeyer/settings.json`
- Windows: `%APPDATA%\NetKeyer\settings.json`
- macOS: `~/Library/Application Support/NetKeyer/settings.json`

Stored settings include:

- Selected radio (serial number and GUI client station)
- Input device type and selection
- MIDI note mappings
- SmartLink credentials (encrypted)

## License

FlexLib components are Copyright © 2018-2024 FlexRadio Systems. All rights reserved.
