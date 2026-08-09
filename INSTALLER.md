# NetKeyer Installer Setup

This project uses [Velopack](https://velopack.io) to create cross-platform installers with automatic update support for Windows, Linux, and macOS.

## Quick Start

### Building Locally

**Build all platforms (Windows, Linux x64/ARM64):**
```bash
# On Linux/macOS:
./build-installer.sh 1.0.0

# On Windows (PowerShell):
./build-installer.ps1 -Version "1.0.0"
```

**Build specific platform:**
```bash
# Linux/macOS:
./build-installer.sh 1.0.0 windows     # Windows only
./build-installer.sh 1.0.0 linux       # Linux x64 + ARM64

# Windows (PowerShell):
./build-installer.ps1 -Version "1.0.0" -Platform windows
./build-installer.ps1 -Version "1.0.0" -Platform linux
```

### Output Files

Installers are created in `NetKeyer/Releases/<platform>/`:

**Windows (`win-x64/`):**
- `NetKeyer-1.0.0-Setup.exe` - Windows installer
- `NetKeyer-1.0.0-full.nupkg` - Full release package
- `NetKeyer-1.0.0-delta.nupkg` - Delta update
- `RELEASES` - Update manifest

**Linux (`linux-x64/` and `linux-arm64/`):**
- `NetKeyer-1.0.0.AppImage` - Portable Linux executable
- `NetKeyer-1.0.0-full.nupkg` - Full release package
- `NetKeyer-1.0.0-delta.nupkg` - Delta update
- `RELEASES` - Update manifest

**macOS (`osx-x64/` and `osx-arm64/` - must build on Mac):**
- `NetKeyer-1.0.0.dmg` - macOS installer (Intel x64 or Apple Silicon ARM64)
- Package files and manifest

## GitHub Actions

The project includes automated multi-platform builds via GitHub Actions.

### Automatic Builds on Version Tags

Create and push a version tag to trigger builds for all platforms:
```bash
git tag v1.0.0
git push origin v1.0.0
```

This will:
1. Build installers for Windows x64, Linux x64/ARM64, and macOS x64/ARM64 in parallel
2. Create a GitHub Release with all installers
3. Upload all platform installers as release assets

### Manual Builds

Go to the Actions tab in GitHub and run the "Build Multi-Platform Installers" workflow manually, specifying the version number. This will build all platforms.

## Distribution

### Option 1: GitHub Releases (Recommended)

Upload all platform files to a GitHub Release:
1. Create a new release on GitHub
2. Upload all files from `NetKeyer/Releases/*/` (all platform subdirectories)
3. Distribute platform-specific installers to users:
   - Windows: `NetKeyer-X.Y.Z-Setup.exe` (x64)
   - Linux: `NetKeyer-X.Y.Z-x64.AppImage` or `NetKeyer-X.Y.Z-arm64.AppImage`
   - macOS: `NetKeyer-X.Y.Z-x64.dmg` (Intel) or `NetKeyer-X.Y.Z-arm64.dmg` (Apple Silicon)

Auto-updates will work automatically since Velopack can read from GitHub Releases.

### Pre-Publish Validation: Check for Updates

Use this checklist before publishing a release to confirm About -> Check for Updates will work for end users.

1. Build and package two sequential versions (example: `2.1.32` then `2.1.33`).
2. Publish both versions to GitHub Releases with complete Velopack assets per platform/channel:
    - installer (`Setup.exe`, `.AppImage`, `.dmg`)
    - `*-full.nupkg`
    - `*-delta.nupkg` (if produced)
    - `RELEASES`
3. Install the older version (`2.1.32`) using the Velopack installer package (do not use `dotnet run`).
4. Launch the installed app and open Help -> About.
5. Click `Check for Updates`.
6. Confirm expected behavior:
    - App reports update availability for `2.1.33`.
    - Download progresses and completes.
    - Restart/apply prompt appears.
    - After restart, About shows the new revision.

Expected non-release behavior:

- If launched from development (`dotnet run`), update checks may report development-mode/not-installed and will not apply updates.

Pre-publish sanity checks:

- Ensure repo update URL constant points to the current fork repository:
  - `Helpers/AppReleaseInfo.cs` -> `GitHubRepositoryUrl`
- Keep `--packId "NetKeyer"` stable across releases to preserve update continuity.
- Ensure release version monotonically increases (no downgrade/reuse of prior version number).

### Pre-Publish Validation: Rendezvous Manual-Mode Networking

Use this checklist when publishing builds that depend on rendezvous services over WAN.

1. In `rendezvous_services/docker-compose.yml`, verify manual-mode preset is active:
    - `RENDEZVOUS_ENABLE_PORT_MAP: "false"`
2. Configure router static forwards:
    - TCP `49920` -> rendezvous host
    - TCP `49921` -> relay host
3. Start services:
    - `cd rendezvous_services`
    - `docker compose -f docker-compose.yml up -d`
4. Verify health endpoint:
    - `GET http://<rendezvous-host>:49920/health`
    - Confirm `port_mapping.enabled` is `false` (manual-mode expected).
5. Validate mixed-network host discovery and connection:
    - Register one host from LAN and one host from WAN.
    - From a LAN client, refresh Select Host and confirm both hosts appear.
    - Connect to each host and confirm keying path is established.

### Option 2: Custom Web Server

Upload all files from `NetKeyer/Releases/` to a web-accessible directory:
```
https://yoursite.com/releases/
  ├── NetKeyer-1.0.0-Setup.exe
  ├── NetKeyer-1.0.0-full.nupkg
  ├── NetKeyer-1.0.0-delta.nupkg
  └── RELEASES
```

Update the application code to point to your update URL (see below).

## Implementing Auto-Updates

To add a "Check for Updates" feature to your app, add this code to your MainWindowViewModel:

```csharp
using Velopack;

[RelayCommand]
private async Task CheckForUpdatesAsync()
{
    try
    {
        var mgr = new UpdateManager("https://github.com/yourname/netkeyer/releases");

        var updateInfo = await mgr.CheckForUpdatesAsync();
        if (updateInfo == null)
        {
            // No updates available
            return;
        }

        // Download the update
        await mgr.DownloadUpdatesAsync(updateInfo);

        // Ask user if they want to restart and install
        // (Show a dialog here)

        // Restart and apply update
        mgr.ApplyUpdatesAndRestart(updateInfo);
    }
    catch (Exception ex)
    {
        // Handle errors (no internet, etc.)
        Console.WriteLine($"Update check failed: {ex.Message}");
    }
}
```

## Cross-Platform Building

**Yes!** Velopack supports extensive cross-compilation:

- **From Linux/macOS**: Can build Windows and Linux installers
- **From Windows**: Can build Windows and Linux installers
- **From macOS**: Can build macOS, Windows, and Linux installers
- **Note**: macOS installers require building on macOS due to code signing requirements

The build scripts automatically handle cross-compilation:
- Bash script (`build-installer.sh`) works on Linux/macOS
- PowerShell script (`build-installer.ps1`) works on Windows/Linux/macOS
- GitHub Actions uses platform-specific runners for optimal results

## Versioning

Version numbers should follow [Semantic Versioning](https://semver.org/):
- **Major.Minor.Patch** (e.g., `1.0.0`)
- Increment **Major** for breaking changes
- Increment **Minor** for new features
- Increment **Patch** for bug fixes

## Prerequisites

- .NET 8.0 SDK
- `vpk` tool (installed automatically by build scripts)

## Native Dependencies

The application includes native library dependencies that are automatically bundled:
- **OpenAL** (audio library) - Included via `OpenAL.Soft` NuGet package for Windows x64/x86/ARM64
- The build automatically copies the correct OpenAL DLL for the target platform

## Troubleshooting

### Build Fails: "vpk command not found"

Run: `dotnet tool install -g vpk`

### Updates Not Working

Check the following:

- All files from `NetKeyer/Releases/<platform>/` were uploaded together, including `RELEASES`.
- The installed app came from a Velopack installer, not a development run.
- The newer release version is greater than the installed version.
- `Helpers/AppReleaseInfo.cs` points to the correct GitHub repository URL for this fork.
- `packId` has not changed between releases.

### Icon Not Showing

Make sure `NetKeyer/Assets/icon.ico` exists. Update the `--icon` parameter in build scripts if your icon is elsewhere.

## Learn More

- [Velopack Documentation](https://docs.velopack.io)
- [GitHub Actions Documentation](https://docs.github.com/en/actions)
