# Phase 18 GPU Pipeline Continuation

Date: 2026-09-26  
Repository: existing `F:\Application\Aravals Stream`  
Scope: continue Gemini's in-progress Phase 18 changes; do not begin Phase 19.

## Continuation point and provenance

The user-provided handoff said Gemini had begun Direct3D 11 references, a compositor, GPU NV12 conversion, and encode-once infrastructure, and stopped while building. On inspection those files and project references existed. The first continuation build completed successfully with 0 warnings/errors and the then-current 205 tests passed. This checkout has a broad staged/unstaged/untracked state with no clean baseline that identifies per-file authorship; therefore an exact Gemini-versus-Codex file ownership list cannot be recovered from Git. Additional Codex changes in this continuation are: stable source frame leases, stable NV12 feed format across GPU failure, planar-to-NV12 repacking, route-sensitive audio fingerprints, shared immutable packet fan-out, publisher keyframe gating, safer GPU frame release, device-generation-aware texture pool returns, texture pool keys including usage/access flags, HLSL matrix upload correction, and this report.

The GPU implementation examined includes:

- `src/AravalsStream.App/Composition/D3D11/` — D3D11 device manager, compositor, shader code, GPU NV12 converter, and leased GPU frame.
- `src/AravalsStream.App/Composition/ComposedFrameHub.cs` — one feed per canvas mode, Auto GPU selection, CPU fallback.
- `src/AravalsStream.Core/Streaming/` and `src/AravalsStream.App/Streaming/` — compatibility key, shared encoder/session and independent publisher infrastructure.
- SharpDX 4.2.0 package references in the app project.

## Architecture and remaining copies

**Classification: HYBRID GPU/CPU PATH.** It is not zero-copy.

The current stream path is: capture produces a CPU BGRA buffer; the D3D compositor uploads source frames with `UpdateSubresource`; composition and BT.709 limited-range NV12 conversion run on D3D11; the NV12 staging texture is synchronously mapped and copied row-by-row into a managed byte array; the existing FFmpeg rawvideo named pipe receives those bytes; FFmpeg encodes H.264/AAC. Hardware H.264 encoding was present in the tested process (`h264_nvenc`), but the FFmpeg rawvideo pipe still crosses CPU memory and the encoder performs its own input transfer. There is no D3D11 texture-to-NVENC/QSV/AMF surface handoff.

The WPF preview remains independently throttled and uses CPU capture/preview frames. Recording can acquire the shared canvas feed, but its scaling is not wired to `GpuNv12Converter.ResizeGpu`; GPU recording resize is therefore not verified or active as a GPU path. `IVideoPipeline`/`VideoPipelineManager` describes CPU/GPU strategies but normal app construction selects Auto directly through `ComposedFrameHub`.

The central `D3D11DeviceManager` owns one process-level D3D11 device/context, adapter, generation counter, and bounded texture pools. Device-removed/reset exceptions trigger device recreation, shader/cache invalidation, and a CPU fallback on the affected feed if GPU work fails. GPU frames have reference-counted leases; source frame leases keep pooled capture data valid during texture upload. Returns from an old device generation are disposed rather than inserted into the new pool. Device-loss recovery was code-reviewed but not fault-injected on hardware.

The compositor implements source order, visibility, position, scale, rotation, crop UVs, opacity, cached source/overlay uploads, and Smart Vertical blur. A live local test exposed incorrect transform matrix packing; the upload was corrected and clean Release rebuilt. Visual verification covered an actual desktop capture and rendered alert/chat overlays in the horizontal output. Rotation/crop/scale edge cases and vertical blur were not exhaustively compared against CPU output.

The GPU converter emits tightly packed NV12 bytes after accounting for D3D row pitch, with BT.709 coefficients and studio range. The feed pins its pixel format for its lifetime: on GPU failure, CPU YUV420 output is repacked to NV12 instead of changing layouts under FFmpeg. A unit test covers planar-to-NV12 U/V interleave. Resolution alignment behavior and colorimetry were not tested with a color-chart reference.

## Encode-once and publishers

`EncoderCompatibilityKey` includes canvas dimensions/rate, video codec/backend/bitrate/rate control/keyframe interval/profile/preset/pixel format, audio mix fingerprint, and audio codec/bitrate/sample rate/channels. Audio fingerprints include routes, route gains/enabled state, channel volume/mute/sync/monitoring, and sample format.

`StreamingOutputGroupManager`, `SharedEncoderSession`, `EncodedPacketHub`, and `PublisherSession` exist. The packet hub copies a FLV tag once into immutable managed memory and fans it to independent subscriber queues. Publisher logic forwards metadata and sequence headers and waits for an actual keyframe. However, `MainWindow` and its ordinary `StreamingOutput` lifecycle do not instantiate or route through `StreamingOutputGroupManager`. Thus the three-destination benchmark did not use encode-once: each destination still starts its own FFmpeg encoder/output path. Grouped YouTube/Twitch/Kick H, incompatible bitrate split, publisher disconnect isolation, and shared-encoder failure propagation have **not** been runtime verified. The audio routing key work in the group manager remains unexercised by the app's normal path.

## Verification

### Build and tests

Final command sequence:

```text
dotnet clean AravalsStream.sln -c Release
dotnet build AravalsStream.sln -c Release
dotnet test AravalsStream.sln -c Release
```

Result: build succeeded with 0 warnings and 0 errors; 207 tests passed, 0 failed, 0 skipped.

### Installed single-output comparison

The Phase 17B installed CPU-path baseline (0.17.2-beta) used one horizontal 1920×1080/60 output. It reported 3,322 unique / 386 repeated frames (89.35% unique), 17.6–27.9% app CPU, 223–234 MB app working set, 1.62 ms average pipe write, and receiver/publish timing ratio 0.9998. This run included alerts/chat overlays but the single-output harness did not attach a visual capture source.

The final installed 0.18.0-beta local RTMP test also used the single-output harness without a visual source, so it verifies app/package/encoder/transport timing and overlays, not capture-source visual parity. FFmpeg input arguments were `-pix_fmt nv12 -s 1920x1080 -r 60`; the output receiver probed H.264 1920×1080 and AAC 48 kHz stereo. It measured 3,068 unique / 616 repeated frames (83.0% unique), 0 scheduler drops, 13 pipe drops, 1 encoder drop, 1.78 ms average pipe write, app CPU samples 0.5–14.6%, working set 287–300 MB, real-time ratio 0.9994, and A/V end difference 22 ms. That is 6.4 percentage points fewer unique frames than the older CPU-path baseline despite the lower measured app CPU; GPU NV12 readback and FFmpeg pipe wait remain bottlenecks. A/V sync and real-time behavior remained close to 1.0 in this single-output case.

Receiver files: `%TEMP%\AravalsPhase16-c239038ea3174a4582e495d65c97d64c`. An extracted frame from the separate capture-enabled run is `%TEMP%\aravals_phase18_pattern.png`; it showed a valid captured desktop image with correct alert/chat placement and no green/purple chroma corruption.

The capture-enabled harness is a 30-second stress profile with three streams and recording, despite being invoked with its `-Single -Capture` switches. It was not a single-output comparison: Twitch and TikTok reconnected, capture and output rates fell, YouTube had 329 pipe drops and 43 encoder drops, and reported real-time ratios exceeded 1.0. This profile demonstrates that the current multi-output load is not stable enough to justify a 30-minute soak.

### Other items

- Installed package ran without Visual Studio or the .NET SDK and used its bundled FFmpeg; no outside test broadcast or credentials were used.
- After the final installed single-output run: 0 Aravals Stream processes and 0 FFmpeg processes remained.
- The installer is self-contained and includes runtime plus `ffmpeg`/`ffprobe`.
- GPU 3D / Copy / Video Encode utilization metrics are reported as N/A, not fabricated. DXGI process VRAM sampling exists in the UI but was not captured in the final local output report.
- No full H+V controlled benchmark, three-H encode-once grouping test, bitrate incompatibility test, forced CPU-only acceptance, x264 fallback acceptance, pause/record interaction test, publisher failure test, or 30-minute GPU soak was completed.

## Build artifact

Version is `0.18.0-beta` from `Directory.Build.props`. Self-contained win-x64 publish is in `publish/`. Installer: `installer/output/AravalsStream-Setup-0.18.0-beta.exe` (137,553,758 bytes; rebuilt after the transform fix). It was installed per-user at `%LOCALAPPDATA%\Programs\Aravals Stream`; the final installed local receiver test passed.

## Outstanding work / bottlenecks

1. Replace source CPU-to-GPU uploads with capture-texture sharing where capture APIs permit it.
2. Avoid the GPU NV12 staging readback and managed frame copy by integrating a tested GPU surface encoder input; retain named-pipe fallback.
3. Integrate compatibility groups into normal destination start/stop state, then verify independent publisher reconnect and shared encoder failure behavior.
4. Add actual GPU 3D/copy/encode and per-process VRAM sampling where Windows counters permit, otherwise display N/A.
5. Use GPU scaling for recording and test color range, crop/rotation/scale, blur, overlays, vertical output, CPU fallback, x264 fallback, device loss, and pause/record interactions.
6. Resolve the multi-output capture/pipe overload before running the requested 30-minute soak. Do not claim the substantial unique-frame or encoder-session reduction target yet.
