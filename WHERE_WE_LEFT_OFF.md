# Where We Left Off

## Current status
- Remote mode Phase 1 foundation is present in this branch.
- Default remote TCP port was changed to 49920.
- Core remote service scaffolding exists under Services/Remote.
- Main settings and sidetone gating hooks were added.

## Confirmed key decisions
- Keep three modes: Off, Remote Client, Remote Host.
- Client keeps local sidetone on and sends paddle events over TCP.
- Host receives remote paddle events and mutes local sidetone.
- Support up to 5 connected clients.
- Use length-prefixed JSON messages first, then optimize later if needed.

## Implemented artifacts now present
- Services/Remote/RemoteConnectionMode.cs
- Services/Remote/RemoteOptions.cs (DefaultPort = 49920)
- Services/Remote/RemoteProtocolModels.cs
- Services/Remote/RemoteFrameCodec.cs
- Services/Remote/RemoteClientService.cs
- Services/Remote/RemoteHostService.cs
- Services/Remote/RemoteClientSession.cs
- Services/Remote/IRemoteClientService.cs
- Services/Remote/IRemoteHostService.cs
- Services/Remote/RemotePaddleStateEventArgs.cs
- Models/UserSettings.cs remote fields
- Services/KeyingController.cs SetSidetoneEnabled(bool)
- native/build.ps1 hardened for VS dev shell + generator fallback

## Blocker encountered during prior session
- Native shim build failed due missing Windows SDK libraries in build environment.
- Symptom noted: kernel32.lib not found and WindowsSdkDir not set.

## Last known recovery path
1. Ensure VS 2022 Build Tools has Windows SDK components installed.
2. Verify environment variables and kernel32.lib path in a fresh VS dev shell.
3. Build native shim from native/build.ps1.
4. Confirm native/windows-x64/netkeyer_midi_shim.dll exists.
5. Run dotnet restore and dotnet build.

## Immediate next steps
1. Verify this machine can build native shim successfully.
2. Run full app build and fix any compile/runtime integration issues.
3. Validate two-machine remote keying flow on LAN.
4. Implement Phase 2 client arbitration and stale-frame handling.

## Reference transcript
- Full recovered transcript: RESTORED_CHAT_bf500a43.md
