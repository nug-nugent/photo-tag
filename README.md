# PhotoTag

[![CI](https://github.com/nug-nugent/photo-tag/actions/workflows/ci.yml/badge.svg)](https://github.com/nug-nugent/photo-tag/actions/workflows/ci.yml)

A fast, cross-platform desktop app for browsing and tagging photo folders.
Built with [Avalonia](https://avaloniaui.net/) on .NET 10, so it runs on Windows, macOS and Linux.

## Download

Get the latest version from [**Releases**](https://github.com/nug-nugent/photo-tag/releases/latest): installers for
Windows (x64 and ARM), macOS (Apple silicon and Intel) and Linux (AppImage). Everything PhotoTag needs, ExifTool
included, is bundled, and installed copies update themselves (you can turn that off under ⚙ Settings).

PhotoTag isn't code-signed yet, so the first launch shows a warning. On Windows click **More info → Run anyway**; on
macOS open **System Settings → Privacy & Security** and click **Open Anyway**. The release notes have the details.

## Status

Early days. Today it can:

- Browse a folder tree (subfolders load lazily, off the UI thread)
- Show a virtualized thumbnail grid that stays responsive with thousands of photos, with a ♥ on favourites
- Show a details panel for the selected photo: preview, date taken, camera, exposure, size and GPS
- Edit tags, title, description and place (location, city, state/province, country), and mark favourites (♥).
  Changes are written straight into the photo file.
- Select many photos (Ctrl/⌘-click, Shift-click, arrow keys, Ctrl/⌘+A) and tag or favourite them, or give them a
  title, description or place, all at once, with progress and Cancel in the status bar, and Undo (or Ctrl/⌘+Z)
  afterwards
- Show and tag camera RAW files (Canon CR2/CR3, Nikon NEF, Sony ARW, Fujifilm RAF, Olympus ORF, Panasonic RW2,
  Pentax PEF, DNG…). RAW+JPEG pairs appear as one photo.
- Keep a library index (SQLite), so the folder tree shows photo and tagged counts, you can search across
  every subfolder by tag, title, description or file name, list the untagged photos or your favourites (on
  their own or with a search), and tag suggestions cover your whole library
- Manage tags across the library (**Tags…**): see every tag with its count, rename or merge tags (including
  tidying "beach" and "Beach" into one), or delete a tag from every photo. These can be undone too.

Tags are written as XMP (read by Lightroom, digiKam, Windows and macOS) and, for JPEGs, also as IPTC for older
software. Pixels are never re-encoded. A favourite is saved as a 5★ rating (`xmp:Rating`), so Lightroom, Windows
Explorer and other apps show favourites as 5 stars, and photos rated 5★ elsewhere appear as favourites. A lower rating
set in another app is kept until you favourite that photo. Lightroom's nested keywords ("Places|UK|Cornwall") aren't
shown, but they're kept in step when you rename or remove a tag, so Lightroom doesn't bring the old tag back.

**Camera RAW files are never modified.** Their tags go in an `.xmp` sidecar beside them (`IMG_0001.CR2` →
`IMG_0001.xmp`), as Lightroom and Capture One do; darktable-style `IMG_0001.CR2.xmp` sidecars are read too. When a
RAW+JPEG pair is tagged, the JPEG gets the tags inside it and the RAW gets them in its sidecar. RAW previews are the
full-colour JPEG the camera embeds in every RAW file, so they look like the camera's own JPEG rather than an edited
conversion; showing them needs ExifTool.

Because tags live inside the photo files (or their sidecars), **backing up the files backs up the tags**. Saving tags updates each
file's "date modified" so backup and sync tools notice the change: many edits (a new favourite, one tag swapped for
another of the same length) leave the file size unchanged, so without the date change some tools would skip them.
If you'd rather keep the original dates, turn on *Keep each photo's "date modified"* under ⚙ Settings, but check
first that your backup tool compares file contents or checksums, not just dates and sizes.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for tag editing,
[ExifTool](https://exiftool.org/) on your PATH (Windows: `winget install OliverBetz.ExifTool`; macOS:
`brew install exiftool`; Debian/Ubuntu: `sudo apt install libimage-exiftool-perl`). Without ExifTool the app
still browses, read-only. Release builds will bundle ExifTool, so end users won't need to install it.

```bash
dotnet run --project src/PhotoTag.App
```

You can also pass a folder on the command line: `dotnet run --project src/PhotoTag.App -- "D:\Photos"`.
Otherwise it reopens the last folder you used.

```bash
dotnet test --solution PhotoTag.slnx
```

Tests that need ExifTool skip when it isn't installed. CI sets `PHOTOTAG_REQUIRE_EXIFTOOL=1`, so there they fail instead.
RAW tests use public-domain (CC0) sample files from seven cameras, courtesy of [raw.pixls.us](https://raw.pixls.us). They're
listed with checksums in `tests/samples/raw-samples.json` and downloaded (about 60 MB) into `tests/.samples/` on first use.
To use a specific ExifTool, set `PHOTOTAG_EXIFTOOL` to its full path.

## Releasing

Push a version tag and GitHub Actions does the rest:

```bash
git tag v0.2.0
git push origin v0.2.0
```

The [release workflow](.github/workflows/release.yml) builds all five packages on matching machines, bundles the
ExifTool pinned in `build/exiftool.json` (checksum-verified), runs each packaged app's `--self-check`, and only then
publishes the GitHub Release that installed copies update from. Pull requests that touch packaging run the same
workflow as a trial, without publishing. The icon is drawn by `dotnet run build/MakeIcons.cs`.

## How it's put together

| Project | What it does |
|---|---|
| `src/PhotoTag.Core` | No UI code. Finds files (`PhotoFiles`), renders and caches thumbnails (`ImageRenderer`, `ThumbnailCache`), reads EXIF/IPTC/XMP (`PhotoMetadata`), writes it (`PhotoMetadataWriter` via a long-running `ExifTool` process; `BulkMetadataEditor` for many photos), and indexes it (`LibraryIndex`, SQLite). |
| `src/PhotoTag.App` | Avalonia UI with MVVM (CommunityToolkit.Mvvm) and compiled bindings. |
| `tests/PhotoTag.Core.Tests` | xUnit v3 tests. They build real JPEGs with hand-made EXIF/XMP segments, and round-trip writes through real ExifTool. |
| `tests/PhotoTag.App.Tests` | Headless UI tests that type, click and move focus in the real main window, then check the file. |

### Why it's fast

- **Only on-screen tiles exist.** `ItemsRepeater` with `UniformGridLayout` virtualizes the grid. Thumbnails load
  when a tile appears (`ElementPrepared`) and are freed when it scrolls away (`ElementClearing`).
- **Photos are never fully decoded for a thumbnail.** JPEGs are decoded at 1/8–1/2 scale via libjpeg's DCT
  scaling (SkiaSharp), then resized and rotated per EXIF orientation.
- **Thumbnails are cached on disk** under the local app-data folder (`PhotoTag/thumbnails`). The cache is keyed on
  path, size and modified time, so an edited photo gets a fresh thumbnail automatically.
- **The library index is incremental.** Opening a folder scans its whole tree in the background, but files whose
  size and modified time are unchanged are skipped (a rescan of 1,600 photos takes about 25 ms). PhotoTag's own edits
  go straight into the index, and it lives in the local app-data folder (`PhotoTag/library.db`).
- **Loading is bounded and cancellable.** At most (cores − 1) thumbnails are generated at once. Requests for tiles
  that scroll off screen are cancelled before any work is done.

## Roadmap

See [TODO.md](TODO.md) for what's next, from publishing the first release and code signing to NAS support, tag
management and a full-screen viewer. Contributors (human or AI) should start with [AGENTS.md](AGENTS.md).
