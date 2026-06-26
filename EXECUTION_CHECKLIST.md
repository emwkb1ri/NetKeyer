# NetKeyer Remote Feature Execution Checklist

Purpose: finish environment validation and complete the next implementation/testing steps on this branch.

## Step 1: Build Native Shim
- Status: Complete
- Goal: produce native/windows-x64/netkeyer_midi_shim.dll reliably on this machine.
- Command:
  - cd native
  - .\build.ps1
- Pass criteria:
  - Script exits successfully.
  - native/windows-x64/netkeyer_midi_shim.dll exists.

## Step 2: Build Managed App
- Status: Complete (with warnings)
- Goal: verify full project compile after remote-mode integration.
- Command:
  - cd ..
  - dotnet restore
  - dotnet build
- Pass criteria:
  - restore succeeds
  - build succeeds with no blocking errors

## Step 3: Smoke-Check Remote Defaults
- Status: Complete
- Goal: confirm configured defaults and key integration points are present.
- Verify:
  - remote default port = 49920
  - settings default to RemoteDefaults.DefaultPort
  - sidetone gate method exists in keying controller
- Pass criteria:
  - all checks true in source

## Step 4: Run Two-Machine LAN Smoke Test
- Status: In Progress
- Goal: validate basic client-host remote keying path.
- Procedure:
  - Machine A: Remote Client mode, set host IP + port 49920
  - Machine B: Remote Host mode, listen on port 49920
  - Connect both, key paddles on A, confirm keying on B
- Pass criteria:
  - host receives events
  - keying works
  - host sidetone stays muted

## Step 5: Stabilization Fixes
- Status: Pending
- Goal: address any compile/runtime issues found in Steps 1-4.
- Actions:
  - patch failures
  - re-run affected checks
- Pass criteria:
  - Steps 1-4 all green

## Step 6: Phase 2 Start (After Green)
- Status: Pending
- Goal: begin arbitration and stale-frame handling.
- Implement:
  - active-client ownership lock
  - stale-frame drop policy
  - basic telemetry for jitter/lag
- Pass criteria:
  - design + initial implementation builds cleanly

## Execution Log
- 2026-06-25: Checklist created and execution started.
- 2026-06-25: Step 1 passed. native/build.ps1 completed and native/windows-x64/netkeyer_midi_shim.dll exists.
- 2026-06-25: Step 2 passed. dotnet restore and dotnet build succeeded.
- 2026-06-25: Build warnings observed:
  - Services/SmartLinkManager.cs:171 warning CS1998 (async method without await)
  - ViewModels/MainWindowViewModel.cs:1457 warning CS4014 (unawaited call)
- 2026-06-25: Step 3 passed. Verified default port 49920 and sidetone gate method.
- 2026-06-25: Step 4 started.
- 2026-06-25: Machine A verified on this workstation (IP 192.168.1.93 on Ethernet 2).
- 2026-06-25: LAN reachability to Machine B (192.168.1.73) passed.
- 2026-06-25: Host sidetone mute/restore fix added in MainWindowViewModel: save current volume, force 0 on host start, restore on exit.
- 2026-06-25: Updated host mute behavior to decouple local NetKeyer mute from TXCWMonitorGain sync; SmartSDR/Flex sidetone is no longer auto-muted by host-mode local mute logic.
