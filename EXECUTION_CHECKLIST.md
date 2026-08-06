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
- Status: Complete
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
- Status: Complete
- Goal: address any compile/runtime issues found in Steps 1-4.
- Actions:
  - patch failures
  - re-run affected checks
- Pass criteria:
  - Steps 1-4 all green

## Step 6: Phase 2 Start (After Green)
- Status: Complete
- Goal: begin arbitration and stale-frame handling.
- Implement:
  - active-client ownership lock
  - stale-frame drop policy
  - basic telemetry for jitter/lag
- Pass criteria:
  - design + initial implementation builds cleanly

## Step 7: Telemetry Clarity Review (Future)
- Status: Planned
- Goal: evaluate telemetry metric semantics and improve clarity of on-screen lag display for direct vs relay comparisons.
- Tasks:
  - review how `raw`, `baseline`, `last_norm`, `avg_norm`, and `max_norm_60s` are computed and presented.
  - validate whether normalized values are suitable for side-by-side transport comparison.
  - consider adding additional display metrics (for example raw lag, median, p95) to reduce ambiguity.
  - define acceptance criteria for understandable transport-to-transport lag interpretation.
- Pass criteria:
  - agreed telemetry definitions documented.
  - UI/log display updated or confirmed as clear for direct and relay interpretation.

## Step 8: UI Compaction + Full Screen Review (Future)
- Status: Complete (2026-07-29)
- Goal: improve information density and usability by compacting operating screens and conducting a complete UI review across all screens/dialogs.
- Tasks:
  - review operating-page layout for spacing, grouping, and visual hierarchy to reduce wasted space.
  - identify opportunities to compact host/client status, telemetry, and control sections without reducing readability.
  - perform a full walkthrough review of setup, operating, and dialog screens for consistency in labels, sizing, alignment, and interaction flow.
  - document UI issues and prioritize quick wins versus larger redesign items.
  - validate desktop usability at typical resolutions and ensure no regressions for key workflows.
- Pass criteria:
  - prioritized UI review findings documented.
  - approved compact layout changes implemented or staged with clear follow-up tasks.

Completion summary (2026-07-29):
- Operating-page compaction completed:
  - Host status table reduced to 3 display rows.
  - Host IP column narrowed to IPv4-sized width.
  - Bottom-bar redundancy removed; host/client status messaging moved into Host Status / Client Status headers.
  - Host and client status color cues added (connected shown in green).
- CW Settings compact redesign completed:
  - Sliders replaced with compact numeric controls.
  - Controls and labels resized and normalized for readability.
  - Layout reflowed into 3 dense rows (Speed+Keyer Mode, Sidetone+Iambic Type, Pitch+Swap Paddles).
- Operating action placement finalized:
  - Disconnect moved into Host Status and Client Status headers.
  - Redundant operating-page Exit button removed.
  - Exit button/menu/window close unified to the same disconnect-then-exit path.
- Window sizing/consistency review completed:
  - Main window now auto-sizes to content on startup and mode changes.
  - About, Audio Output, and MIDI Note Mapping dialogs now auto-size to content.
  - MIDI dialog height capped with MaxHeight to avoid excessive vertical growth.

## Step 9: UI Enhancements (Future)
- Status: Complete (2026-08-06)
- Goal: streamline setup-page mode and network layout while improving client-mode clarity.
- Tasks:
  - Change radio button label 'Remote Client' to 'Client'.
  - Change radio button label 'Remote Host' to 'Host'.
  - Change the title 'Remote Connection Mode' to 'Connection Mode'.
  - Change the 'Connection Mode' box group to include the three mode select radio buttons.
  - Create a new grouping box titled 'Network Connection' that includes the 'Callsign:' input box, 'Use Rednezvous' checkbox, 'ID Server', 'Host Discovery:', 'Host ID', 'Target Host', 'Target Port', and 'Shared Token'.
  - Remove Radio Selection box from setup UI when Client mode selected.
  - Remove 'Network Connection' box from setup UI when 'Standalone' mode selected.
  - On 'Client' mode window:
    - change 'Host Discovery:' label to 'Select Host:'
    - change 'Target Host:' label to 'Host IP:'.
    - chnage 'Target Port:' label to 'Host Port:'.
    - when a host is selected, update the 'Host IP:' field with it's IP address
- Pass criteria:
  - Setup-page grouping and labels match the updated mode/network structure.
  - Conditional visibility behavior is correct for Standalone, Host, and Client modes.
  - Client-mode network labels and control ordering match the specified wording/layout.

Completion summary (2026-08-06):
- Released in program revision 2.1.32.
- Setup-page grouping and mode-label updates completed:
  - Connection Mode section uses labels Standalone / Client / Host.
  - Mode selector ordering finalized as Standalone, Client, Host.
- Network Connection section created and wired:
  - Callsign, Use Rendezvous, ID Server/Port, Host ID, Host IP/Host Port, and Shared Token grouped under Network Connection.
  - Client host discovery label updated to Select Host.
  - Selecting a discovered host updates Host IP and Host Port when endpoint metadata is available.
- Mode-conditional visibility updates completed:
  - Radio Selection is hidden when Client mode is selected.
  - Network Connection is hidden when Standalone mode is selected.
- Host-mode operating behavior refinements completed:
  - CW Settings are hidden in Host mode when no local key input device is connected.
  - Host sidetone behavior is now source-aware:
    - local HaliKey keying enables sidetone,
    - remote keying suppresses sidetone,
    - transition gating avoids per-frame sidetone toggles that caused keying jitter.

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
- 2026-06-26: Added network status enhancements: host client list with IP/callsign/status history, client/host identity fields, and client host identity display.
- 2026-06-26: Step 4 and Step 5 marked complete after LAN smoke validation and stabilization/UI refinements (window sizing, host/client status views, reconnect dedupe, client status formatting, dark-theme status bar readability).
- 2026-06-26: Step 6 implementation started: added active-client ownership lock with configurable hold time (0.5s to 30.0s, default 1.0s), stale-frame drop policy on host receive path, and baseline remote lag/jitter/drop telemetry.
- 2026-06-26: Step 6 completed: active-client ownership lock, stale-frame drop policy, host/client telemetry surfaces, rolling max lag (60s), and accepted-frames-last-60s idle decay updates are implemented and validated by successful build.
- 2026-06-29: Telemetry measurement path corrected for cross-system clock skew by preserving raw apparent-age deltas and normalizing lag to a per-client baseline; lag/jitter/max-lag values now reflect observed delay variation.
- 2026-06-29: Telemetry logging updated to always emit remote telemetry entries by default (`remote-telemetry`) without requiring NETKEYER_DEBUG filters.
- 2026-06-29: Telemetry UI readability/layout refinements: high-contrast bold magenta telemetry text and a two-line telemetry layout with aligned second-line indentation (`accepted 60s` and `stale` moved to line 2).
- 2026-07-29: Step 8 completed. UI compaction and full-screen review updates implemented across setup/operating views and dialogs, including status de-duplication, compact CW layout, dynamic sizing behavior, and control placement refinements.
- 2026-07-30: Remote non-CW/PTT behavior corrections documented as program revision 2.1.27 in [changelog.md](changelog.md) and [README.md](README.md), including host transmit-mode propagation to clients, client PTT-only behavior in non-CW mode, and host bottom-bar PTT indicator alignment.
- 2026-07-30: Added additional revision 2.1.27 documentation updates in [changelog.md](changelog.md) and [README.md](README.md) for non-CW-to-non-CW mode text synchronization, remote client host-identity mode display at connect, and remote client non-CW LED parity with host PTT-closure behavior.
- 2026-08-06: Step 9 completed and documented as program revision 2.1.32 in [changelog.md](changelog.md) and [README.md](README.md). Setup-page Connection Mode/Network Connection UX updates, client-mode visibility/label refinements, host-discovery Host IP/Port fill behavior, host CW Settings visibility gating, and source-aware host sidetone/keying behavior are implemented and validated.
