# One-run release packaging

Open Build & Release — Windows, Linux, macOS in GitHub Actions and choose Run workflow.

1. Enter a new semantic version, for example `1.0.0`, and choose stable or development.
2. Use reviewed source on `main`. Workflow files must match the default branch so GitHub's workflow token can publish the release.
3. One run freezes a source commit, tests/builds on Windows, Linux x64, Mac Intel and Mac Apple Silicon, and packages the Linux Relay.
4. After all jobs succeed, downloads, checksums, build information and automatic change notes publish in one GitHub Release.

Stable releases require a version without a suffix and publish Windows Desktop and Agent installers plus Linux event Relay. They set `prerelease: false` and GitHub latest status. Incomplete Linux/macOS desktop packages are excluded. See [1.0.0 supported scope and limits](stable-1.0.0.md).

Development releases include portable desktop packages and remain prereleases. Linux/macOS require FFmpeg and native permissions; full workstation parity, Wayland capture, macOS system audio and Mac signing/notarization remain incomplete.

All components/installers use the requested version. Builds and publishes treat warnings as errors. A failed job prevents publication; uploads are assembled and checked in a draft. An unpublished draft can be retried for the same source commit. Published versions are never overwritten.

Release packaging does not deploy websites, Relay servers or infrastructure. The website consumes GitHub latest stable metadata when its prepared source is separately deployed.

## Desktop in-app updates

Windows Desktop checks published GitHub Releases at startup and every six hours when automatic checks are enabled. The header also has a manual **Check Updates** button. A new release appears in an in-app notification; **Download and install** fetches the Windows installer, verifies it against the release's `SHA256SUMS.txt`, launches Inno Setup, and closes the Desktop after stopping active outputs. No browser download is needed after an updater-capable Desktop version is installed.

The Desktop updater checks stable releases only. This updater is for the Windows workstation installer only. Existing installations without the updater need one normal installer upgrade before in-app updates become available.
