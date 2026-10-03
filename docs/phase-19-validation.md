# Phase 19 validation record

These measurements were collected on the development laptop with an NVIDIA
GPU and 8 logical CPUs. They separate sender and receiver work; they do not
represent a two-PC acceptance test.

## Sender-only, 1080p30

The actual `AgentMediaTransmitter` captured the desktop through D3D11 desktop
duplication, encoded H.264 with NVENC, encoded 48 kHz stereo AAC, muxed MPEG-TS,
and sent over encrypted SRT. A lightweight SRT FFmpeg sink only received and
discarded the stream; it did not decode video, compose, preview, or render WPF.
Two 35-second runs were completed.

| Measurement | Result |
| --- | --- |
| Capture rate | Latest intervals 29.96–30.03 FPS; earlier intervals 29.94–30.18 FPS |
| Encoder rate | Latest intervals 29.94–30.04 FPS; earlier intervals 29.88–30.19 FPS |
| Encoder | H.264 NVENC |
| SRT transport | Latest intervals 12.44–12.57 Mbps |
| CPU | Agent/controller/sink process-tree sample about 20.6% of 8 logical CPUs |
| RAM | Tracked peak about 400.5 MB |
| GPU | Active system-wide `nvidia-smi dmon` samples: SM about 7–9%, encoder about 10–13% |

The sender met the 27 FPS target. Windows per-process GPU counters were
invalid on this machine, so GPU percentages above are system-wide samples.
Capture-drop and encoder-drop counters were not independently available in
this harness; no claim is made about those counters.

## Receiver-only, 1080p30

A deterministic 32-second 1920×1080 30 FPS H.264 + AAC 48 kHz stereo
MPEG-TS source was generated before measurement. FFmpeg stream-copy sent it
over encrypted SRT. Agent capture and encoding were stopped. The receiver used
its normal `RemoteSrtCaptureSession`, software H.264 decode, BGRA raw output,
the real pipe reader, and the compositor's frame-ingestion/copy method. Preview
was disabled so WPF presentation could not pace the test.

| Measurement | Result |
| --- | --- |
| FFmpeg raw-output progress | 29.48–30.73 FPS across intervals |
| Pipe reader | 28.76–31.19 FPS across intervals |
| Remote source/compositor ingestion | 904 frames over 30.1 seconds, about 30.00 FPS average; logged intervals about 28.49–30.14 FPS |
| Raw BGRA | 8,294,400 bytes/frame; about 248.8 MB/s at 30 FPS |
| Reader throughput | About 239–259 MB/s |
| Latest-frame queue age | Typical windows about 0.02–0.03 ms; observed maximum 0.63 ms in the first run and 0.42 ms in the monitored run |
| Audio | About 1,417 blocks / 1,500,160 samples; peak 0.0963, average block RMS 0.0645 |
| CPU | Receiver harness plus sender and FFmpeg processes about 26.2% of 8 logical CPUs |
| RAM | Tracked peak about 264.9 MB |
| GC | 6 Gen 0, 3 Gen 1, 2 Gen 2 collections over 30 seconds |

FFmpeg's progress frame counter measures raw output frames, not an internal
decoder-only counter. No distinct internal decode FPS telemetry was available.
The test proves raw output and compositor ingestion sustain 30 FPS; it does
not measure composed horizontal/vertical output or preview FPS. The bounded
latest-frame slot kept handoff fresh without a growing queue.

## One-PC combined result

An earlier same-PC full loopback measured about 11–15 receiver FPS while the
Agent captured and encoded at about 30 FPS. The isolated sender and receiver
both meet 27 FPS, so this is classified as **ONE-PC COMBINED CAPACITY
EXCEEDED**. It is not evidence of a standalone receiver bottleneck. The brief
requires reporting two-PC acceptance separately; that test remains unverified.

## Pixel format and decoder

The receiver test selected software H.264 decoding and BGRA output. BGRA
already sustained the target, so the beta keeps the existing BGRA handoff; no
NV12 receiver conversion changes were made. Hardware decoder, BGR24, and NV12
matrices were not rerun because the decision rule says to keep BGRA after a
passing 1080p30 receiver-only run.

## Low-impact 720p30 full loopback

The Agent selected a 1920×1080 display and advertised/encoded a 1280×720
30 FPS H.264 stream using NVENC. The actual encrypted SRT sender, pairing,
desktop receiver, remote source callback, and compositor ingestion were used
for a 30.2-second same-PC run.

| Measurement | Result |
| --- | --- |
| Agent capture | 29.95 FPS |
| Encoder input | 29.88 FPS |
| FFmpeg raw output | 28.92 FPS |
| Remote source callback | 29.88 FPS |
| Pipe reader | 29.88 FPS |
| Compositor ingestion | 29.88 FPS |
| Audio | 1,408 blocks; 1,490,944 sample frames; peak 0.844; average block RMS 0.0131 |
| CPU | About 31.3% across the tracked process tree on 8 logical CPUs |
| RAM | Tracked peak about 519.7 MB |
| Encoder | H.264 NVENC |

This passes the 27 FPS target and is the recommended low-impact profile. The
test used the existing preview-disabled harness to avoid presentation pacing.

## Remote recording, local output, reconnect, and pairing

The actual app media components were exercised with an encrypted local SRT
Agent loopback. The 27.331-second recording was H.264 1280×720 at 30 FPS plus
AAC 48 kHz stereo. The recording contained received remote video and audio.
The normal local RTMP streaming output and local receiver produced H.264
1920×1080 at 30 FPS plus AAC 48 kHz stereo, duration 36.266 seconds. Horizontal
and vertical compositor paths both rendered remote frames.

Stopping and restarting the Agent made the receiver observe a loss and then
resume media from the same paired device without re-pairing. The same local
stream remained live. Pair profile and DPAPI secret survived the isolated
harness restart. These were component-level tests; closing/reopening both
installed product UIs and verifying UI discovery was not tested.

The isolated Forget operation removed paired metadata and the local DPAPI
secret from its scratch profile. It did not prove that the Agent rotated or
revoked its old passphrase, so old-credential invalidation remains open.

### Installed-binary final acceptance attempts

The 0.19.2-beta installed-binary media harness successfully started the
installed Agent transmitter and established SRT, and the receiver logged an
audio block. It timed out waiting for its first decoded video frame, so it did
not launch the small sync player or produce an installed recording. No A/V
offset was measured. This is a failed/incomplete smoke, not an installed-media
pass.

A follow-up scratch-profile credential check confirmed Forget removed the
local device metadata and protected client credential. The check received no
audio blocks either before or after Forget, so it cannot establish whether the
old credential was rejected. Pairing to a restarted Agent succeeded at the
control protocol, but the returned credential matched the old credential and
the new media session produced no audio blocks. This does not pass Forget,
re-pair media, or UI persistence acceptance. Neither installed app UI was used
for this test.

## Flash/beep synchronization

The recording shows three white flash events at approximately 5.30, 15.30,
and 25.33 seconds. Desktop audio is present, but the beep onset could not be
separated reliably from other captured system sound. No average/maximum A/V
offset or drift claim is made; this acceptance item remains open.

## Other functional evidence and limits

- Earlier YouTube test: user confirmed both desktop and microphone meters
  moved and that voice and desktop audio were audible in the stream. This
  verifies that tested machine's audio route, but not remote-source recording,
  local stream fan-out, or flash/beep sync.
- Branding build: `dotnet clean`, Release build, and Release tests passed with
  0 warnings, 0 errors, and 224 tests. Self-contained win-x64 app and Agent
  publishes and both 0.19.2-beta installers were produced. Desktop setup size
  was 144,901,561 bytes; Agent setup size was 85,457,888 bytes. Fresh silent
  installs into isolated directories exited successfully. Installed product
  metadata, registration, Start Menu shortcuts, EXE version information, and
  bundled FFmpeg presence were checked. Both installed executables started;
  the desktop reached its `ARAVALS STREAM` main-window title and the Agent
  displayed `Aravals Stream — Remote Capture Agent`. Visual branding surfaces
  and installed media flow were not verified.
- Clean shutdown check found no Agent, FFmpeg, benchmark process, or listener
  on SRT UDP port 45820 after the test runs. Test-launched UI processes were
  explicitly terminated after launch checks; the Agent tray Exit menu path
  was not exercised.
- Still unverified: reliable measured flash/beep offset, old Agent credential
  invalidation after Forget, two-PC operation, physical capture-card hardware,
  installed media flow, visual installed UI/tray branding, Agent explicit
  Exit behavior, and actual installed pairing/discovery persistence. The
  installed-binary media smoke timed out before video output; the credential
  follow-up had no audio blocks to prove old-key rejection or repaired media.

## Final 0.19.3-beta installed acceptance update — 2026-09-28

This section supersedes the earlier 0.19.2 installed-media and credential
attempts above. Both were fresh installs into `scratch/fresh-0.19.3-beta`, not
the older default Programs folders. The default Desktop folder still had
0.18.1-beta and the default Agent folder had 0.19.1-beta at the time of the
audit; those folders were not used for the passing measurements.

### Installed vs publish and media diagnosis

- First package divergence: none found. Desktop and Agent executable hashes
  and complete required-file inventories matched their respective publishes.
  Desktop EXE SHA-256:
  `876B43E1202A7E233567EA382657E4CD625433727BB4AC9CE2375CCF9BD3BF2B`.
  Agent EXE SHA-256:
  `3937230F0F774F1568893392638F17B062D090D067D1310322FDC00AEEA77866`.
- Packaged Desktop and Agent `ffmpeg.exe` files both have SHA-256
  `3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC`;
  publish and fresh-install copies matched. They are FFmpeg 9.0.2 essentials
  builds. `ffprobe.exe` and `ffplay.exe` were present in the 0.19.3 Desktop
  package.
- Runtime config remained under the signed-in user's LocalAppData profile:
  `C:\Users\sabbi\AppData\Local\AravalsStream` for Desktop settings and
  the Agent's LocalAppData profile for its identity and protected key. The
  installed test used an isolated client pairing profile. The user's original
  Agent identity files were backed up and restored with matching hashes after
  credential integration testing.
- The failed idle-input attempt reached Agent capture and submitted video
  frames, but FFmpeg had not received audio samples; it produced no encoded
  packets or SRT media bytes. This same behavior occurred with publish and
  installed assemblies. Starting deterministic desktop audio caused audio
  samples to arrive, after which video encoding and SRT output began. The first
  meaningful divergence was therefore test input state at the Agent's FFmpeg
  mux/output stage, not an installed-only binary difference. The installed
  Agent standalone SRT recording passed with active tone: 300 H.264 frames,
  1280×720 at 30 FPS, AAC 48 kHz stereo, about 10.03 seconds.
- Installed Desktop receiver with a deterministic known-good SRT source
  passed: first frame received, 298 frames in 10 seconds, 516 audio blocks,
  1,056,768 samples, peak 0.8495. The full installed 0.19.3 Agent-to-Desktop
  acceptance helper also passed 720p30 media ingestion.
- A 27.266-second installed recording smoke produced H.264 1280×720 30 FPS
  and AAC 48 kHz stereo. During the later sync-marker attempt, sampled output
  frames were black, including frames where the test clip's sampled control
  frame displayed the red visual marker. Thus this recording proves the
  recording pipeline and streams, but not visible desktop content or A/V sync.
- The idle-input failure was not fixed by changing media architecture or
  increasing a timeout. Active audio was present for the passing tests. No
  AppContext/current-directory issue was found in runtime code. Firewall rules
  were not conclusively enumerated; local SRT tests passed.

### Credential lifecycle

- Before Forget, scratch-client credential fingerprint A was `7A6C2071`.
  Forget returned `remoteRevoked=True` and confirmed local paired metadata and
  DPAPI secret removal.
- After restart and re-pair, the stable Agent Device ID remained the same and
  credential fingerprint B was `53B8B3CD`; B differed from A. The pinned TLS
  certificate remained stable across Agent restarts.
- Attempting media with A after revocation/restart was rejected: zero frames,
  decoder exit code -5. B delivered video and audio. After another Agent
  restart, the same B remained valid without another pairing code and media
  resumed. These checks used installed 0.19.3 assemblies through the product
  pairing/Forget/session APIs and a scratch client profile; the Settings UI
  click path itself was not exercised.

### Remaining acceptance and package state

- Reconnect: component/harness stop/restart with persisted B succeeded without
  re-pairing. Installed app UI status transition was not visually checked.
- A/V E1–E5: unmeasurable. The recorded marker run's sampled frames were
  black, so no visual event onset could be compared with the five audio
  chirps. Average, maximum offset, and drift are not available.
- Branding manual inspection: not performed for Main EXE/taskbar/Alt-Tab/window/
  Start Menu, Agent window/taskbar/tray/Start Menu, or installer icons.
- Agent Tray → Exit was not exercised. The Agent was launched in-process by
  the harness for receiver/credential tests, then disposed; this does not
  verify the installed tray command or port-release behavior through the UI.
- Release clean/build/test: clean and build completed with 0 warnings and
  0 errors; all 224 tests passed. Product version is 0.19.3-beta,
  assembly version 0.19.3.0.
- Fresh installers:
  `installer/output/AravalsStream-Setup-0.19.3-beta.exe` (144,897,295 bytes)
  and `installer/output/AravalsRemoteCapture-Setup-0.19.3-beta.exe`
  (85,452,468 bytes). Both installed successfully into isolated fresh
  directories. A final process/port check found no AravalsStream, Agent,
  FFmpeg, or harness process and no listener on UDP 45820/45821 or TCP 1935.

Phase 19 remains open because visible installed video, measured A/V sync,
installed UI reconnect, branding inspection, and explicit Agent tray Exit
were not verified.

## Final 0.19.4-beta media acceptance update — 2026-09-28

This update supersedes the visual, silence, and sync findings in the earlier
0.19.4-beta acceptance attempt above. It used the fresh 0.19.4-beta installed
Desktop and Agent assemblies through a scratch acceptance harness. It does
not represent manual use of the installed product UIs.

### Visible remote video

- The Agent selected display index 0, `\\.\DISPLAY1`, primary, adapter 0 / output 0,
  bounds `{X=0,Y=0,Width=1920,Height=1080}`. The visible normal test window
  `Aravals Remote Capture Visual Test` had bounds
  `{X=491,Y=236,Width=938,Height=607}` on `\\.\DISPLAY1`, and remained visible
  and foreground during capture.
- Sample frames at approximately 3, 8, and 13 seconds all show the test
  window, changing frame counter, and moving cyan rectangle. This proves the
  installed Agent media path captured visible source content.
- The silent recording decoded 386 changing video frames over 15.805 seconds;
  H.264 metadata is 1280×720 at 30/1. Same-PC delivered cadence was below
  target, consistent with the already accepted **ONE-PC COMBINED CAPACITY
  EXCEEDED** classification. It is not a release blocker under this brief.

### Controlled silence and tone transition

- For the silent scenario, the harness closed the installed Agent's WASAPI
  capture object, drained its audio timeline to zero pending samples, and
  injected no samples at the installed Agent timeline boundary. This isolates
  filler behavior from ambient desktop sound. The receiver decoded 612 audio
  blocks and all 612 were silent (decoded peak 0); video continued with 386
  changing frames. The Agent submitted 1,291 audio blocks during the run.
- There is no runtime `SilenceBlocksGenerated` diagnostic counter in this
  build. Therefore that named counter is **not exposed**; the measured
  receiver-side all-silent decoded blocks and Agent submitted blocks are the
  available evidence, rather than a claimed value for the missing counter.
- In the silence→tone→silence scenario, the test injected a 1 kHz tone during
  source seconds 5–10. Receiver, mixer, and recording tap all observed it;
  394 of 630 decoded blocks were silent and peak tone level was about 0.90.
  Video continued with 400 decoded frames and the same SRT session remained
  active. Frequency analysis of the recording found initial silence through
  about 6.064 seconds, tone afterward, and silence beginning about 11.830
  seconds (with brief gaps through 12.15 seconds). The recorded transition is
  present, with same-PC timing latency/stretch; no FFmpeg restart or SRT
  reconnect was observed.
- The silence recording is
  `scratch/phase19installedacceptance/final-results/AravalsStream_2026-09-28_19-39-58_H.mkv`.
  The transition recording is
  `scratch/phase19installedacceptance/final-results/AravalsStream_2026-09-28_19-40-14_H.mkv`.
  Both contain H.264 1280×720 30/1 and AAC 48 kHz stereo.

### Recorded A/V sync

Five cyan visual markers and 1 kHz chirps were analyzed in the final recording.
Video onset was detected by cyan pixel threshold in a fixed crop; audio onset
was detected by 1 kHz band-pass energy. Offsets are audio onset minus video
onset, rounded to the nearest 10 ms:

| Event | Video onset | Audio onset | A/V offset |
| --- | ---: | ---: | ---: |
| E1 | 4.00 s | 4.18 s | +180 ms |
| E2 | 6.90 s | 7.13 s | +230 ms |
| E3 | 9.97 s | 10.09 s | +120 ms |
| E4 | 12.93 s | 13.12 s | +190 ms |
| E5 | 15.90 s | 16.11 s | +210 ms |

Mean signed offset is +186 ms; mean absolute offset is 186 ms; maximum
absolute offset is 230 ms; E1-to-E5 drift is +30 ms. This is a small stable
fixed offset with no meaningful progressive drift. The sync recording is
`scratch/phase19installedacceptance/final-results/AravalsStream_2026-09-28_19-40-31_H.mkv`.

### Still-open acceptance

The app-control surface was unavailable for manual UI work during this pass.
Installed UI pairing persistence, reconnect status transitions, Forget
workflow, Agent tray Exit, and manual visual inspection of Desktop, Agent, and
installer branding remain unverified. Two-PC operation and physical HDMI
capture hardware also remain unverified. No product code or installer changed
in this acceptance pass; version remains 0.19.4-beta. Phase 19 remains open.

## Final UI and branding acceptance attempt — 2026-09-28

The Windows app-control inventory returned no native apps (`apps=[]`), so the
installed pairing persistence, reconnect UI, Forget workflow, sync-offset
entry, Agent tray Exit, and manual window/taskbar/Alt-Tab/Start Menu/About
inspection could not be operated or observed. No application was launched by
this pass, and no product or installer files changed.

Static resource inspection found:

- The shared supplied ICO has 16×16, 20×20, 24×24, 32×32, 40×40, 48×48,
  64×64, 128×128, and 256×256, all 32-bit entries. The 16×16, 24×24, and
  32×32 variants are recognizable; transparent corner pixels were confirmed.
- Extracted associated icons from the fresh 0.19.4-beta Desktop EXE, Agent EXE,
  and both setup EXEs show the shared teal Aravals mark. Project and installer
  definitions point to that ICO. This is static icon-resource evidence, not
  the requested manual inspection of Explorer, taskbar, menus, dialogs, or
  installer screens.
- The existing Audio Matrix sync field is per audio channel, accepts values
  from −500 through +5000 ms, and passes the channel offset to its sync buffer.
  Therefore −180 ms is representable without a global default change. The
  installed UI entry and resulting Remote audio compensation were not
  exercised.

Phase 19 remains open for installed pairing persistence, installed reconnect,
Forget/re-pair, audio-offset UI behavior, Agent tray Exit, and manual branding
inspection. Final product version remains 0.19.4-beta.

## Final 0.19.4-beta silence-timeline and installed-media update — 2026-09-28

- `ContinuousAudioTimeline` supplies one continuous 48 kHz stereo clock. The
  Agent writes fixed 10 ms blocks to FFmpeg whether WASAPI produces samples or
  not. Five deterministic timeline tests were added. The final solution has
  229 passing tests.
- Fresh isolated 0.19.4-beta Desktop and Agent installs were built and opened
  successfully. Installed EXE metadata reports file version 0.19.4.0 and
  product version 0.19.4-beta.
- In the installed 15-second no-test-WAV scenario, receiver video frames and
  audio blocks continued, and the recording contains H.264 1280×720 30/1 and
  AAC 48 kHz stereo. The Windows loopback endpoint had nonzero audio during
  this scenario (receiver peak 0.4371), so this run does not prove a physically
  silent desktop. The recording was 17.066 seconds and observed receiver frame
  delivery was about 13 FPS, below the 30 FPS acceptance target.
- During the silence/tone/silence WAV scenario, 279 raw remote audio blocks
  reached the Desktop, with nonzero receiver, mixer, and recording-tap peaks
  (0.3135). Its recording contains AAC 48 kHz stereo and H.264 1280×720. The
  endpoint also had sound outside the injected test interval; therefore the
  expected silence→sound→silence sequence and timestamp continuity were not
  isolated and remain unverified. Video continued during the scenario.
- The five-chirp run recorded H.264 and AAC, but the visual test window was not
  present in sampled captured frames. E1–E5 offsets, mean offsets, maximum
  offset, and drift cannot be measured from this recording. Installed visible
  RemotePcSource acceptance remains unverified.
- Manual installed UI pairing persistence/reconnect, Forget confirmation,
  Agent tray Exit, and visual branding inspection could not be exercised in
  this environment. No completion claim is made for these gates.
- Fresh installer outputs:
  `installer/output/AravalsStream-Setup-0.19.4-beta.exe` (144,896,271 bytes)
  and `installer/output/AravalsRemoteCapture-Setup-0.19.4-beta.exe`
  (85,457,535 bytes). Both installed successfully into fresh isolated
  directories.
- `dotnet clean`, Release build, and Release test passed after product changes:
  0 warnings, 0 errors, and 229/229 tests. Subsequent edits were confined to
  the scratch diagnostic harness and this documentation.
- Final cleanup after the latest run found 0 matching Desktop, Agent, FFmpeg,
  or harness processes. UDP 45820/45821 were free and no TCP 1935 listener was
  present.

## Final pairing revocation acceptance — 0.19.8-beta

This final test used fresh 0.19.8-beta Desktop and Agent binaries and separate
DPAPI-backed test stores under `scratch/phase19-final-pairing/profiles-run3/`.
The Agent ran with a temporary `USERPROFILE`; Desktop metadata and protected
credentials used the isolated test directory. The real store paths were
`C:\Users\sabbi\AppData\Local\AravalsStream\remote-devices.json` and
`C:\Users\sabbi\AppData\Local\AravalsStream\RemoteAgent`.
Path, size, and modification-time snapshots matched before and after the test.

| Check | Result |
| --- | --- |
| DeviceId before/after Forget and re-pair | `78d169e7-2860-4624-afb0-9dd61b48badd` (stable) |
| Credential A fingerprint | `B443358B` |
| A baseline and restart authentication | PASS; video and audio arrived |
| Production Forget local removal and remote revocation | PASS |
| A rejected immediately, after Agent restart, and after B activation | PASS; 0 video frames and 0 audio blocks in each rejection window |
| Credential B fingerprint | `C2B91652` |
| A != B | PASS |
| B media authorization | PASS; video frames and audio blocks arrived before and after Agent restart |
| B persistence after restart | PASS; protected Desktop credential matched the Agent identity |
| Real profile unchanged | PASS |
| Acceptance FFmpeg after cleanup | 0 |

Two production defects found by this gate were corrected in 0.19.8-beta. The
pairing listener now permits revocation based on the current credential proof
after its in-memory paired flag resets on Agent restart. After revocation, the
control plane reuses its running discovery announcer and updates its
advertisement instead of starting a second announcer. No transport or media
architecture changed.

The disposable profiles were removed after the result snapshot was recorded.
Release build completed with 0 warnings and 0 errors; all 235 tests passed.
Fresh 0.19.8-beta installers were produced at:

- `installer/output/AravalsStream-Setup-0.19.8-beta.exe`
- `installer/output/AravalsRemoteCapture-Setup-0.19.8-beta.exe`

Manual visual QA remains pending. Two-PC operation and physical HDMI capture
were not tested.
