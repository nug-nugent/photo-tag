# PhotoTag TODO

Work that's ready to pick up, roughly in priority order. Each item is meant to be self-contained: read
[`AGENTS.md`](AGENTS.md) for how the project is built, tested and shipped, then take one item into its own branch and PR.

Sizes are rough: **S** = an hour or two, **M** = a session, **L** = several sessions.

## 1. Shipping

### 1.1 Code signing (M, needs paid accounts)
**Why:** unsigned builds trigger SmartScreen on Windows and Gatekeeper on macOS on first launch.
**What:** macOS: Apple Developer ID + notarisation (`vpk pack --signAppIdentity/--signInstallIdentity/--notaryProfile`).
Windows: Azure Trusted Signing (`vpk pack --azureTrustedSignFile`). Secrets go in GitHub Actions secrets; the owner sets
up the accounts.
**Done when:** a fresh download opens without warnings on both.

## 2. Photos on a NAS

The owner keeps the photos on a NAS (a Synology DS223j, mapped as a drive). PhotoTag opens the network share; the NAS
does snapshots and off-site backup, and PhotoTag should **not** implement backup itself. **Two PCs (a desktop and a
laptop) use it as equals:** both run PhotoTag on the same share and both edit tags, people, places and favourites.
Each keeps its own index and notices the other's edits through the folder watcher and the 30-second poll.

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
