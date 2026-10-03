# One-run Windows, Linux and macOS releases

Open [Build & Release — Windows, Linux, macOS](https://github.com/Sabbir-Hasan-670/aravals-stream/actions/workflows/release.yml) in GitHub Actions and choose **Run workflow**.

1. Enter a new semantic version, for example `0.20.1-port.1`.
2. Keep the source ref `main`, or use a reviewed commit containing all projects and release scripts. Source workflow files must match the default branch so GitHub's workflow token can publish the release.
3. Run once. Windows, Linux x64, Mac Intel and Mac Apple Silicon build on their native GitHub runners in parallel.
4. After every build/test/package succeeds, one GitHub Release is published with five application packages, checksums, build information and automatic change notes.

Windows supplies the full existing workstation installer and Remote Capture Agent. Linux/macOS supply development desktop packages, clearly labeled in their filenames and release notes. Until full portable acceptance is complete, the combined release is always a prerelease.

All projects/installers use the requested version in a temporary runner checkout. The workflow freezes a single source commit for every platform. No local compiler, GitHub personal token or separate manual release upload is required: the final release job uses GitHub's scoped workflow token with `contents: write`; build jobs use read access.

A failed platform build prevents public publication. Uploads are assembled in a draft and verified before publication. A failed upload can be retried for that same source commit while the draft remains unpublished. A published version is never overwritten; use a new version for the next release.

Linux/macOS packages include the .NET desktop runtime, but require separately installed FFmpeg and native desktop permissions. macOS signing/notarization, Wayland capture and remaining full workstation parity are tracked in [port status](cross-platform-port.md).
