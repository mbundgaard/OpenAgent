# iOS Voice App — Design

**Date:** 2026-04-29
**Status:** Approved (brainstorming complete, plan to follow)
**Location:** `src/app/`

## Goal

A simple iOS-only .NET MAUI app that talks to an OpenAgent instance over its existing voice WebSocket. The app does one thing: pick or create a conversation and have a voice call with the agent. No text input, no transcription view by default, no chat history browsing — phone-app-like.

## Non-Goals

- Android, iPad-specific layout, macOS Catalyst.
- Text chat, transcript export, file browsing.
- Picking provider/model per call from the app.
- CallKit integration, push notifications, agent-initiated outbound calls.
- Login methods other than QR onboarding.

## Onboarding

QR scan only. The QR encodes a URL of the shape `https://host[:port]/?token=<api-key>`, which matches what `Program.cs` already prints at agent startup (the agent prints `#token=` for the web client, but the app accepts both `?token=` and `#token=` for flexibility). The app parses `{baseUrl, token}`, stores them in iOS Keychain, and routes to the conversation list.

A manual-entry sheet sits behind a small "Enter manually" link as a fallback (two text fields, URL + token).

## Conversation list

Shows **all** conversations the agent knows about, not just app-sourced ones, sorted by `lastMessageAt` desc. Each row:

- **Title** — `intention` if set, else first user message truncated to ~60 chars, else "Untitled".
- **Right-side timestamp** — relative ("2h ago", "yesterday", `dd MMM` for older).
- **Source badge** — small pill: `app`, `telegram`, `whatsapp`, `webhook`, etc.

Pull-to-refresh. Floating `+` FAB → new conversation. Swipe-left → delete (with confirm). Long-press → rename sheet, calls `PATCH /api/conversations/{id}` with `intention`.

Cached on every successful load to `FileSystem.AppDataDirectory/conversations.cache.json`. On load failure the cached list is shown with an "Offline" banner.

## Call screen

Modal over the conversation list. Layout:

- Top: agent name (defaults to `Connection.Name` if any; otherwise the literal `"Agent"` — extending the agent API to expose `personality.name` is out of scope for v1).
- Middle: 160dp circle avatar with the agent's first letter. Pulsing ring when state is `userSpeaking` or `assistantSpeaking`. Spinner when `connecting`/`thinking`. Static when `listening`.
- State line: `Connecting…`, `Listening`, `You are speaking`, `Thinking…`, `Speaking…`, `Reconnecting…`.
- Bottom: two circular buttons. Mute (left) — purely client-side, stops sending audio frames over the WS. End (right, red) — closes WS, pops back to list.
- Hidden by default: a `▼ Transcript` pill below the state line expands a chat-bubble transcript pane, fed by the `transcript_delta`/`transcript_done` source-flip rule from `useVoiceSession.ts`.

A new conversation tap on `+` immediately opens the call screen with a fresh GUID; the conversation gets created server-side via the existing `GetOrCreate` on first WebSocket connect. No name prompt up front.

## Settings

Three rows: server URL (read-only + "Reconfigure" button → re-onboarding), API token (masked + reveal toggle), app version. That's it.

## Networking

Single `ApiClient` in `OpenAgent.App.Core/Api`. Wraps `HttpClient`, adds `X-Api-Key` from `CredentialStore`, base URL from `CredentialStore`. Snake-case JSON via `JsonNamingPolicy.SnakeCaseLower`.

Endpoints used:
- `GET /api/conversations`
- `DELETE /api/conversations/{id}`
- `PATCH /api/conversations/{id}` (rename via `intention`)
- `WS {baseUrl}/ws/conversations/{id}/voice?api_key=...` (auth via query param matches the web client and the existing endpoint contract).

`VoiceWebSocketClient` wraps `ClientWebSocket`, exposes:
- `SendAudioAsync(ReadOnlyMemory<byte> pcm16)`
- `ReceiveEventsAsync()` returning a typed `VoiceEvent` discriminated union (`SessionReady`, `SpeechStarted`, `SpeechStopped`, `AudioDone`, `TranscriptDelta`, `TranscriptDone`, `Error`, `ThinkingStarted`, `ThinkingStopped`, `AudioReceived`).

No REST retries — single failure surfaces a banner; user retries by pulling.

## Audio

PCM16 mono 24 kHz both directions. The client validates `session_ready.input_codec/output_codec == "pcm16"` and `input_sample_rate/output_sample_rate == 24000`; anything else surfaces an error and ends the call (matches `useVoiceSession.ts:209` behaviour).

iOS implementation:
- `AVAudioSession` category `PlayAndRecord`, mode `VoiceChat`, options `AllowBluetooth | DefaultToSpeaker`.
- `AVAudioEngine` with mic input tap → format converter → 24 kHz PCM16 → `VoiceWebSocketClient.SendAudioAsync`.
- Inbound frames → `AVAudioPlayerNode` schedule queue.
- On `speech_started` we flush the player node queue (barge-in support, matches web client at `useVoiceSession.ts:224`).

## Background & lifecycle

- `Info.plist` declares `UIBackgroundModes = ["audio"]`, `NSMicrophoneUsageDescription`, `NSCameraUsageDescription` (for QR scanner).
- Audio session activated on call start, deactivated on call end. Backgrounding mid-call keeps the WS and audio alive; iOS auto-shows recording status in the Dynamic Island.
- Termination mid-call closes WS; whatever transcript reached the agent persists in its DB.

## Error handling

- **Mid-call WS drop.** Auto-reconnect with exponential backoff `1s, 2s, 4s, 8s` (max 5 tries). UI shows "Reconnecting…". On giving up, return to list with a brief banner.
- **401 / WS close 1008/4001.** Show alert "Token rejected" with a "Reconfigure" button → QR scanner. Do not auto-wipe credentials.
- **List load fails.** Show last cached list with an "Offline — showing cached" banner.
- **Microphone permission denied.** Call screen shows an explainer with a "Open Settings" button (deep-links to the app's settings page).
- **Camera permission denied during onboarding.** Same — explainer + "Open Settings", with manual entry still available.

## Project layout

```
src/app/
  OpenAgent.App/                   # net10.0-ios MAUI head
    Platforms/iOS/                 # AppDelegate, Info.plist, audio + Keychain glue
    Resources/AppIcon, Splash      # Placeholder branding
    MauiProgram.cs                 # DI wiring
    App.xaml, AppShell.xaml        # Shell navigation
    Pages/                         # OnboardingPage, ConversationsPage, CallPage, SettingsPage
    ViewModels/                    # One per page (CommunityToolkit.Mvvm)
  OpenAgent.App.Core/              # net10.0 — buildable & testable from Windows
    Api/                           # ApiClient, VoiceWebSocketClient, VoiceEvent union
    Models/                        # Snake-case JSON DTOs
    Services/                      # CredentialStore (interface), ConversationCache, ICallEngine
    State/                         # CallStateMachine, ReconnectBackoff
  OpenAgent.App.Tests/             # xUnit, runs on Windows
```

`OpenAgent.App.Core` is a `net10.0` library with **no MAUI dependencies** so it builds and tests on Windows. The MAUI head shrinks to UI + iOS audio + Keychain implementations of the Core interfaces.

## Build & deploy

- `.github/workflows/ios-build.yml` runs on `macos-14`. Installs the .NET 10 SDK + iOS workload, restores, builds.
- Push to `master` → build only (sanity check).
- Push tag `app-v*` → build + upload to TestFlight via App Store Connect API key.
- Required GitHub secrets: `APPSTORE_API_KEY_ID`, `APPSTORE_API_ISSUER_ID`, `APPSTORE_API_KEY_P8`, `IOS_DIST_CERT_P12`, `IOS_DIST_CERT_PASSWORD`, `IOS_PROVISIONING_PROFILE`.
- The workflow also attaches the `.ipa` as a build artifact for sideloading via Apple Configurator if needed.

## Testing strategy

**Windows-runnable (`OpenAgent.App.Tests`):**
- URL/token parsing from QR payloads (handles `?token=`, `#token=`, malformed inputs).
- Transcript source-flip routing (delta routing matches `useVoiceSession.ts` rules).
- `CallStateMachine` transitions on every event combination.
- `ReconnectBackoff` schedule + cap behaviour.
- `ConversationCache` round-trip, corrupted-cache recovery.
- DTO JSON round-trips against canned fixtures of real agent responses.

**Not testable from Windows (must be exercised on a real device):**
- `AVAudioSession` activation, route changes (Bluetooth headset, lock screen).
- Keychain read/write.
- Camera + QR scanning.
- Real WebSocket against the agent.
- Background audio behavior.

The README will be explicit about that boundary, and the implementation plan calls for at least one TestFlight smoke run before declaring v1 done.

## Open follow-ups (post-v1)

- Agent-side QR-code rendering (today the user must generate a QR from the printed startup URL with any external tool).
- Exposing `personality.name` via API for a friendlier call screen title.
- Outbound (agent-initiated) calls — the protocol can already deliver them; needs APNs and probably CallKit to be useful.
- Per-conversation provider/voice picker.
