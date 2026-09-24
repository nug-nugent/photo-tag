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

### 1.3 Smaller downloads with .NET trimming (M)
**Why:** installers are ~70 MB (mostly the .NET runtime and ExifTool's 35 MB).
**What:** try `PublishTrimmed` for the App. Expect trim warnings (warnings are errors here): `AppSettings` needs a
System.Text.Json source-generated context; MetadataExtractor/XmpCore may need `TrimmerRootAssembly`. Run every package's
`--self-check` and the full test suite against a trimmed build before accepting it.
**Done when:** meaningfully smaller installers, all release self-checks green.

### 1.4 Linux/macOS without Perl (S to investigate)
**Why:** the bundled ExifTool on macOS/Linux is the Perl version and relies on the system `perl`. Most distros have it;
Apple has deprecated scripting runtimes in macOS and could remove it.
**What:** make the self-check and the app's ExifTool-missing message distinguish "Perl missing" from "ExifTool missing";
document it. If macOS ever drops Perl, bundle a relocatable Perl.

## 2. Photos on a NAS

The owner is planning to keep photos on a NAS (PhotoTag opens the network share; the NAS does snapshots and off-site
backup; PhotoTag should **not** implement backup itself).

### 2.1 Behave well on network shares (M)
**Why:** everything has only been tested on local disks.
**What:** test against an SMB share (a Windows shared folder is a fine stand-in): opening folders, thumbnails, indexing,
tag writes. Handle the share being asleep/offline without hanging the UI (timeouts, clear status messages), and
measure first-scan and thumbnail speed.
**Where:** `PhotoFiles`, `LibraryIndex.ScanAsync`, `ThumbnailCache`, `MainWindowViewModel.ShowPhotosAsync`.

### 2.2 "Library moved" (M)
**Why:** moving photos from the PC to the NAS, or the same share appearing as `Z:\` one day and `\\nas\photos` the next,
makes the index treat everything as new files.
**What:** detect or let the user declare "this folder is now at that path" and rewrite `photos.path`/`folder` in the index
(keeping tags, favourites, counts) instead of rescanning from scratch.
**Where:** `LibraryIndex` (schema v1; bump `user_version` if the schema changes).

### 2.3 Notice changes made outside PhotoTag (M)
**What:** watch the open folder (`FileSystemWatcher`, with polling fallback for network shares) and refresh the grid,
the details panel and the index when files or sidecars change.

## 3. Tagging features

### 3.1 Text search in the UI (S)
Search only matches whole tags (plus the *Untagged* and *♥ Favourites* toggles). Consider searching titles,
descriptions and file names too (`LibraryIndex` stores title/description already).

### 3.2 Tag management (M)
A panel listing every tag with its count (`LibraryIndex.GetKeywordsAsync`), with **rename**, **merge** (e.g. "beach" into
"Beach") and **delete everywhere**, implemented on top of `BulkMetadataEditor` so progress and cancel work as for
bulk edits.

### 3.3 Bulk title/description (S)
The bulk panel only does tags and favourites (`BulkDetailsViewModel`). Add "set title/description on all" with a clear
warning that it overwrites.

### 3.4 Undo (M)
Especially for bulk edits: keep the previous values (`BulkResult.After` has the new ones; capture the old ones in
`BulkMetadataEditor.ApplyAsync`) and offer "Undo" in the status bar after an edit.

### 3.5 Hierarchical keywords (M)
Lightroom writes `lr:hierarchicalSubject` ("Places|UK|Cornwall"). Read it, show it sensibly, and keep it in step when
tags are edited so Lightroom users don't lose structure.

### 3.6 People and places (L)
The original WPF prototype planned People, Location, Town, County and Region fields. Map these to IPTC Core/Extension
(`XMP-iptcExt:PersonInImage`, `XMP-iptcCore:Location`, `XMP-photoshop:City/State/Country`) and show GPS on a small map.

## 4. Browsing

### 4.1 Large viewer (M)
Double-click (or Enter/Space) opens a full-window view with ←/→ to move between photos, using `PhotoRenderer` at screen
size. A favourite shortcut (e.g. F) there and in the grid would make tagging much faster.

### 4.2 Sort and group (S)
Sort the grid by file name (current) or date taken (from the index), optionally grouped by day.

### 4.3 More formats (S each)
TIFF (probably via SkiaSharp or ExifTool previews). **Not HEIC:** the owner explicitly decided against it (it would need
Magick.NET, ~30 MB per platform).

## 5. Housekeeping

### 5.1 Thumbnail cache clean-up (S)
`ThumbnailCache` never deletes anything. Stale entries accumulate (keys include size and modified time, so every edited
photo leaves an old thumbnail). Add size-based eviction (oldest-accessed first) or a periodic sweep.

### 5.2 A log file (S)
There's no logging, which will make user bug reports hard to diagnose. Add a small rolling log in the app-data folder
(ExifTool errors, scan failures, update checks) and a "Show log" link in ⚙ Settings.

### 5.3 A proper settings window (S)
The ⚙ flyout is getting crowded (date-modified, updates, version). Move to a small settings window when the next setting
arrives.

### 5.4 Unpin xunit.v3 (S, blocked)
`Directory.Packages.props` pins `xunit.v3` to 3.2.2 because `Avalonia.Headless.XUnit` 12 fails with 4.x. Upgrade when
Avalonia ships a compatible version.
