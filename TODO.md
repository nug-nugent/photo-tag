# PhotoTag TODO

Work that's ready to pick up, roughly in priority order. Each item is meant to be self-contained: read
[`AGENTS.md`](AGENTS.md) for how the project is built, tested and shipped, then take one item into its own branch and PR.

Sizes are rough: **S** = an hour or two, **M** = a session, **L** = several sessions.

## 1. Shipping

### 1.1 Publish the first release and prove install + update work (S, needs the owner)
**Why:** the release workflow has only run as a trial. Real installs and Velopack's auto-update have never been exercised.
**What:** tag and push `v0.1.0`; install it on Windows (and a Mac, if available); then push a small `v0.1.1` and check the
installed copy offers *"PhotoTag 0.1.1 is ready to install"* and updates on restart.
**Where:** `.github/workflows/release.yml`, `src/PhotoTag.App/AppUpdater.cs`, `UpdatesViewModel.cs`.
**Done when:** a v0.1.0 install updates itself to v0.1.1 on at least Windows. Fix anything found (e.g. delta packages,
the "download previous release" step, which has never run for real).

### 1.2 Code signing (M, needs paid accounts)
**Why:** unsigned builds trigger SmartScreen on Windows and Gatekeeper on macOS on first launch.
**What:** macOS: Apple Developer ID + notarisation (`vpk pack --signAppIdentity/--signInstallIdentity/--notaryProfile`).
Windows: Azure Trusted Signing (`vpk pack --azureTrustedSignFile`). Secrets go in GitHub Actions secrets; the owner sets
up the accounts.
**Done when:** a fresh download opens without warnings on both.

## 2. Photos on a NAS

The owner is planning to keep photos on a NAS (PhotoTag opens the network share; the NAS does snapshots and off-site
backup; PhotoTag should **not** implement backup itself).

### 2.1 Try it on the real NAS (S, needs the NAS)
**Why:** network shares are tested over Windows' loopback share (`\\localhost\C$`, see `NetworkShareTests`), which has
SMB but no network: no latency, no NAS that sleeps.
**What:** open the NAS share (by drive letter and by `\\nas\...`) and check first scan, thumbnails and tag writes feel
fine; that a sleeping NAS shows "Waiting for …" rather than freezing; that pulling the network cable gives a
clear message and loses nothing from the index; and that other apps' changes on the share show up (notifications,
or the 30-second poll). Tune the scan's parallelism for network drives (`LibraryIndex`, now 8) if needed. The log
(⚙ Settings → Show log) records how long each folder took to answer and each scan took, and what failed.

## 5. Housekeeping

### 5.1 Unpin xunit.v3 (S, blocked)
`Directory.Packages.props` pins `xunit.v3` to 3.2.2 because `Avalonia.Headless.XUnit` 12 fails with 4.x. Upgrade when
Avalonia ships a compatible version.
