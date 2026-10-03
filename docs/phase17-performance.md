# Phase 17 performance notes

## Pipeline and measured bottlenecks

The physical capture registry keys sessions by source identity, so two canvas outputs can read one display or camera session. Desktop Duplication and Windows Graphics Capture currently copy a GPU texture to a CPU staging surface. The compositor produces one frame per active canvas, and every output reads the latest immutable snapshot through a five-slot bounded store. The capture side has a three-slot bounded store per physical source. Recording shares the same canvas feed; when its requested resolution differs, it scales the shared YUV frame into one reusable buffer before its pipe write.

The 0.16 pipeline composed at a fixed 60 FPS for each active canvas even when all consumers requested 30 FPS, allocated a full BGRA canvas for every composition, and separately composed a lower-resolution recording. Its raw BGRA pipe input moved 8.3 MB per 1080p frame per output. The 0.17 feed follows the highest active requested FPS and converts one composed frame to YUV420 for fan-out. The YUV pipe input is 3.1 MB per 1080p frame. One FFmpeg process still runs per destination so reconnection and failures remain isolated.

The rawvideo demuxer normally derives timestamps from frame count. Under four-output load, a blocked named-pipe writer supplied fewer frames than wall time, making recordings short. Recordings now apply FFmpeg wall-clock `setpts` and `asetpts`, asynchronous audio resampling, and variable frame rate output. This preserves elapsed recording time when frames are late. It does not create missing motion: low composed or encoded FPS under excessive load remains visible and is reported as dropped frames.

## GPU compositor investigation

The current FFmpeg distribution is a static executable with no `libavutil` or `libswscale` DLLs. Capture uses D3D11, but the present encoder boundary is a CPU named pipe. A full GPU path would require shared D3D11 textures, a compositor on the same device, hardware frame transfer into each selected encoder, and correct recovery when devices or encoders reset. No GPU compositor was installed in this phase. The measured CPU path, source readback, color conversion, and FFmpeg pipe throughput establish a baseline for a later prototype. GPU utilization and VRAM counters are not exposed by the current media API and are not inferred from CPU figures.

## Identical-output encoded fan-out feasibility

Identical canvas, dimensions, FPS, encoder, bitrate, keyframe interval, pixel format, audio routing, codec, and bitrate are required before sharing encoded packets. FFmpeg's tee muxer can send one encoded stream to several destinations, but changing or reconnecting one destination would couple its lifecycle to all others. A safe implementation would need a shared encoder plus bounded per-destination packet queues and independent mux/reconnect workers. That is a separate media architecture and was not introduced in this phase.

## Bounds and limitations

- Canvas frame stores: five YUV420 slots per active canvas; no queue of old video frames.
- Physical source stores: three BGRA slots per active visual source.
- Chat history, alert queue, activity list, and audio tap queues already have limits. The alert image cache now keeps at most four assets and four million decoded pixels per asset.
- Preview rendering and meter/stats updates are throttled separately from output timing. Minimizing the window suspends capture only if no streaming or recording consumer remains.
- AUTO uses logical cores, available memory, and a tested hardware encoder to choose Eco, Balanced, or Quality preview behavior. It never changes the user's configured output resolution, FPS, or bitrate.
- The maximum sustainable output count remains hardware dependent. The app warns about demanding setups and shows sustained overload recommendations; it does not claim all configured combinations can run at full FPS.

## Measurements on the available PC

The test host was an Intel i5-9300H (4 cores, 8 logical processors), 15.8 GiB RAM, and NVIDIA GTX 1660 Ti plus Intel UHD 630. Local RTMP receivers were used; these numbers do not include internet variability. Process CPU is a percentage of all eight logical processors. Working set includes the WPF app, excluding FFmpeg child processes. Ambient desktop activity varied between runs.

| Workload | Installed 0.16 | 0.17 Release | Notes |
| --- | ---: | ---: | --- |
| Idle visible, static desktop | 2.17% CPU, 553 MB | 1.78% CPU, 283 MB | Separate short samples; initial dynamic desktop sample was 33.11% vs 14.48% CPU. |
| Idle minimized | 1.81% CPU, 395 MB | 0.05% CPU, 272 MB | No active output. |
| One 1080p60 output | No equivalent 0.16 harness | 23.02% CPU, 310 MB | Hardware encoded local RTMP; output pipe sampled near 60 FPS. |
| Three streams plus recording, overlay scene | 32.39–43.17% CPU, 462–655 MB | 38.97% CPU, 317 MB | Baseline runs were 9 seconds, updated run 60 seconds; CPU is not a controlled comparison. |
| Three streams plus recording, display capture | No equivalent 0.16 harness | 41.50% CPU, 384 MB | 30-second local test. |
| Software x264 constrained simulation | No equivalent 0.16 harness | 22.86% app CPU, 13.71% child encoder CPU | One 1080p30 stream plus 1080p30 recording; not physical low-end testing. |

The same deterministic 1920×1080 display plus 650×420 chat overlay compositor benchmark took 9.74 ms/frame with the installed 0.16 Core DLL and 7.01 ms/frame with 0.17 (50 timed frames after 10 warmups, separate process runs). The fast path copies an opaque full-canvas display row directly; other transforms retain the general compositor. This benchmark excludes capture, overlays' WPF rendering, YUV conversion, and FFmpeg.

The 60-second three-stream run recorded 60.95 seconds of media over 60.88 seconds of measured wall time. It contained 1,393 unique video frames and 436 scheduled-frame drops, so temporal continuity was fixed while full 30 FPS motion was not sustained. The 30-second captured-display run recorded 30.87 seconds over 30.83 seconds of wall time, with 759 frames and 167 scheduled drops after the display fast path. The constrained x264 run recorded 30.5 seconds over 30.53 seconds of wall time, with 668 frames and 247 scheduled drops. These are overload signals, not proof of a particular low-end hardware target. A 30–60 minute memory soak, GPU/VRAM counters, and a controlled single-output 0.16 baseline remain unmeasured.

The self-contained 0.17 installed build passed a separate 60-second three-output acceptance run with alerts, chat, recording, and minimize/restore. The recording was 60.733 seconds for 60.721 seconds of wall time. Its measured app average was 37.43% CPU and 341 MB working set, with 1,436 recorded frames and 387 scheduled drops. All three local receivers contained H.264 and AAC (48 kHz stereo), and the vertical receiver was 1080×1920. During this overlay-only scene, preview FPS was already zero, so minimizing did not materially lower total CPU; the active composition and encoders dominated. A captured-display run checks the visible-preview transition separately.

The installed 30-second display-capture multi-output run averaged 51.59% app CPU and 404 MB working set. Its recording had 759 unique frames and 164 scheduled drops over 30.761 seconds of wall time. Preview FPS was 18.7–23.5 while visible and zero while minimized. The visible versus minimized CPU averages (51.67% versus 51.19%) are effectively unchanged in this short run, confirming that preview work was removed but was a small fraction of total load. This is a useful limit on the minimize optimization, not a claim of whole-app CPU savings under heavy output load.

The installed single-output 1080p60 test averaged 23.19% app CPU and 313 MB working set. Output pipe telemetry sampled 57.9 and 59.8 FPS, with composition times 13.1 and 9.0 ms/frame in those intervals. The installed idle test averaged 2.47% CPU and 283 MB while visible, and 0.066% CPU and 273 MB while minimized. Its clean shutdown left no FFmpeg process.
