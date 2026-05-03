# OpenAgent.App

A phone-app-style iOS-only .NET MAUI client for the OpenAgent voice WebSocket. Design: [../../docs/plans/2026-04-29-ios-voice-app-design.md](../../docs/plans/2026-04-29-ios-voice-app-design.md). Implementation plan: [../../docs/plans/2026-04-29-ios-voice-app.md](../../docs/plans/2026-04-29-ios-voice-app.md).

## Architecture

Two projects. `OpenAgent.App.Core` (`net10.0`, no MAUI dependencies) holds DTOs, the REST and WebSocket clients, the call state machine, parsers, and the conversation cache — fully testable on Windows. `OpenAgent.App` is the MAUI iOS head that holds Pages, ViewModels, the iOS Keychain credential store, and the AVAudioEngine call-audio implementation. CI runs on a hosted GitHub Actions macOS runner; tagged builds upload to TestFlight.

## Build and test on Windows (Core only)

```
cd src/app
dotnet build OpenAgent.App.Core/OpenAgent.App.Core.csproj
dotnet test OpenAgent.App.Tests/OpenAgent.App.Tests.csproj
```

Tests cover QR parsing, voice event parsing, call state machine, reconnect backoff, conversation cache, and the REST API client.

## Build the iOS head

Requires a Mac with Xcode and the .NET 10 SDK plus the MAUI workload.

```
dotnet workload install maui-ios
cd src/app
dotnet build OpenAgent.App/OpenAgent.App.csproj -c Debug -f net10.0-ios
```

## TestFlight tag flow

```
git tag app-v0.1.0
git push --tags
```

The push triggers `.github/workflows/ios-build.yml` which builds, signs with the distribution certificate, archives, and uploads to TestFlight via the App Store Connect API.

## Required GitHub Actions secrets

Set under repo Settings -> Secrets and variables -> Actions.

- `IOS_DIST_CERT_P12` — base64 of the Apple Distribution `.p12` certificate. Generate on a Mac: `base64 -i cert.p12 | pbcopy`.
- `IOS_DIST_CERT_PASSWORD` — password for the `.p12`.
- `IOS_PROVISIONING_PROFILE` — base64 of the `.mobileprovision` file from the Apple Developer Portal: `base64 -i profile.mobileprovision | pbcopy`.
- `APPSTORE_API_KEY_ID` — App Store Connect API key ID (10-char alphanumeric).
- `APPSTORE_API_ISSUER_ID` — Issuer ID (UUID, on the API Keys page).
- `APPSTORE_API_KEY_P8` — contents of the `.p8` private key file (paste the entire file, including BEGIN/END lines).

## Untested-from-Windows boundary

The following has no automated test coverage and is verified manually on TestFlight:

- `IosKeychainCredentialStore` — relies on the iOS Security framework.
- `IosCallAudio` — AVAudioEngine + AVAudioConverter; verified by the TestFlight rubric in the plan.
- The MAUI head's XAML pages, navigation, and ZXing camera integration.
- Real WebSocket against a live agent.
- Background audio behaviour (lock screen, dynamic island).

## Manual TestFlight smoke checklist (v1 acceptance)

1. Capture: speak "one two three four five", verify the agent's audio reply acknowledges what you said.
2. Playback: agent reply is intelligible, no chipmunk pitch, no chopping.
3. Barge-in: speaking over the agent cuts its audio immediately.
4. Background: lock the phone mid-call; audio continues, dynamic island shows recording.
5. Mute: mute/unmute mid-call works.
