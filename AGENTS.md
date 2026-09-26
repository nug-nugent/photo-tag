# PhotoTag: notes for coding agents

A cross-platform desktop app for browsing and tagging photo folders. Avalonia 12 on .NET 10; Windows, macOS and Linux.
Tags are written into the photos (XMP, plus IPTC for JPEGs) via ExifTool; RAW files get `.xmp` sidecars. The
[README](README.md) describes features and design; [TODO.md](TODO.md) lists work ready to pick up.

## Layout

| Path | What |
|---|---|
| `src/PhotoTag.Core` | No UI code. `PhotoFiles` (discovery, RAW+JPEG pairing, sidecars), `PhotoMetadata` (read), `PhotoMetadataWriter` + `ExifTool` (write, via a long-running `-stay_open` process), `BulkMetadataEditor`, `ImageRenderer`/`PhotoRenderer`/`RawPreviewExtractor` (thumbnails, previews), `ThumbnailCache`, `LibraryIndex` (SQLite). |
| `src/PhotoTag.App` | Avalonia UI, MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]` partial properties) and compiled bindings (`x:DataType` everywhere). `MainWindow.axaml` is the only window; view models in `ViewModels/`. Also `AppUpdater` (Velopack), `SelfCheck` (`--self-check`). |
| `tests/PhotoTag.Core.Tests` | xUnit v3. `TestImages` builds real JPEGs with hand-made EXIF/XMP; `RawSamples` downloads CC0 RAW files. |
| `tests/PhotoTag.App.Tests` | Headless UI tests (Avalonia.Headless.XUnit) that drive the real `MainWindow` with keyboard and mouse. `UiTestBase` has the helpers. |
| `tools/Screenshots` | Renders the main window to PNGs for design work (see "How we work"). |
| `build/` | `BundleExifTool.cs` (release bundling), `MakeIcons.cs` (app icon), `MakePlaces.cs` (GeoNames place data for "Fill from GPS", committed as `src/PhotoTag.Core/Places/places.bin`), `exiftool.json` (pinned ExifTool + checksums). |
| `.github/workflows/` | `ci.yml` (build + test on 3 OSes), `release.yml` (installers for 5 platforms). |

## Build and test

```bash
dotnet build PhotoTag.slnx
dotnet test --solution PhotoTag.slnx
dotnet run --project src/PhotoTag.App
```

- **ExifTool must be on PATH** for the tag-writing and RAW tests; without it they skip. On Windows it's installed at
  `%LocalAppData%\Programs\ExifTool`, which shells started before that install may not have on PATH.
- To reproduce CI exactly: `PHOTOTAG_REQUIRE_EXIFTOOL=1 PHOTOTAG_REQUIRE_SAMPLES=1 dotnet test --solution PhotoTag.slnx -c Release`
  (missing ExifTool or RAW samples then fail instead of skipping).
- RAW samples (~60 MB) download on first use into the git-ignored `tests/.samples/`.
- `TreatWarningsAsErrors` is on, including xUnit analyzers (pass `TestContext.Current.CancellationToken` to async APIs in tests).
- The test runner is Microsoft.Testing.Platform (`global.json`), so it's `dotnet test --project …` / `--solution …`.

## How we work

- **One branch and PR per piece of work**; the owner reviews and merges. Don't commit to `main`. End commits with the
  co-author line and PRs with the Claude Code footer.
- **Run the full test suite before pushing**, and several times for anything touching concurrency (the index scan runs
  alongside tag writes). Check that new tests can fail (break the code briefly and confirm they catch it).
- **UI behaviour is tested headlessly** (`tests/PhotoTag.App.Tests`). **Don't drive the real desktop app with synthetic
  mouse/keyboard input**: the owner may be using the machine, and it has collided before. Headless mode doesn't really
  decode bitmaps, so check image sizes and orientation in Core tests instead.
- **To see the UI, render it:** `dotnet run --project tools/Screenshots` draws the real window with Skia (no window
  appears, no input) in each state (folder, one photo, several, Tags panel, settings, search, smallest size), light
  and dark, into `artifacts/screenshots`. It builds a sample library from `tests/.samples` (run the tests once first)
  and needs ExifTool. Look at the PNGs before and after any UI change.
- **Releases:** push a `vX.Y.Z` tag. PRs touching packaging run `release.yml` as a trial (no publishing). Each package
  runs `PhotoTag --self-check` on a matching runner before anything is published.
- Keep Core free of UI code, and keep the photo grid virtualized (`ItemsRepeater`); folders can hold thousands of photos.

## Decisions already made (don't re-litigate without the owner)

- **Tags live in the files**, not a database: XMP for everything, plus IPTC for JPEGs, via **bundled ExifTool** (chosen
  over writing XMP ourselves or sidecars for everything). The SQLite index is a rebuildable cache.
- **RAW files are never modified**: tags go in `IMG_0001.xmp` sidecars (Lightroom naming; darktable's `IMG_0001.CR2.xmp`
  is read too). A new sidecar is seeded with tags already embedded in the RAW. RAW previews are the camera's embedded JPEG.
- **RAW+JPEG pairs are one photo** (shown and indexed as the JPEG; edits go to both).
- **Favourites, not star ratings.** The owner chose a single ♥ favourite over 1–5★ ratings. It's stored as
  `xmp:Rating = 5` (unfavouriting clears it), so other apps show favourites as 5★ and 5★ photos from elsewhere are
  favourites. Lower ratings set in other apps are preserved until the photo is favourited: `PhotoMetadata.Rating` and
  `MetadataChanges.Rating` are `internal` for that (e.g. seeding a new RAW sidecar), and the UI only sees
  `IsFavourite`/`Favourite`. Don't bring back a rating UI without the owner.
- **Nested keywords are only kept in step.** The owner doesn't use Lightroom or digiKam, so PhotoTag doesn't show
  or create `lr:hierarchicalSubject`. It only updates it when a tag is renamed or removed, keeping the rest of each
  path (`KeywordHierarchy`). Don't add a nested-tag UI without the owner.
- **People and places are separate fields, not tags** (the owner's choice). Places are Location, City,
  State/Province and Country: `XMP-iptcCore:Location` and `XMP-photoshop:City/State/Country`, plus the IPTC fields for
  JPEGs. The UI says "State/Province". People are `XMP-iptcExt:PersonInImage` (XMP only: the older IPTC fields have
  no equivalent), edited like tags; search matches any part of a name, and the Tags & People panel manages both.
  Names only, no face recognition, and no reading of other apps' face regions (the owner has none). GPS gets an
  "Open in map" link, not a map inside the app.
- **Places from GPS are offline first** (option C): bundled GeoNames data (every place with 500+ people, plus every
  UK village; about 3 MB) fills City, State/Province (the county, for the UK) and Country, only where empty. An online
  "look up exact place" button (OpenStreetMap, one request per click) is still to do. Don't send positions anywhere
  without the owner agreeing to that button.
- **No HEIC support.** The owner decided against it (it would need Magick.NET, ~30 MB per platform).
- **Saving tags updates "date modified"** so backup tools notice; keeping it is an opt-in setting.
- **PhotoTag doesn't do backups.** The owner plans a NAS with snapshots and off-site copies; PhotoTag should work well
  with photos on a share (see TODO.md).
- **Releases are unsigned** for now, auto-update from GitHub Releases (Velopack), MIT licence.

## Gotchas

- **SourceForge's web download pages return 403 to GitHub's runners.** Use `downloads.sourceforge.net` directly
  (`build/exiftool.json`).
- **Windows runners:** temp (C:) and the workspace (D:) are different drives, so `Directory.Move` between them fails.
- **Velopack needs versions ≥ 0.0.1** (trial builds are `0.0.1-trial.N`).
- **`xunit.v3` is pinned to 3.2.2**: `Avalonia.Headless.XUnit` 12 breaks with 4.x.
- **MetadataExtractor reports truncated files as a plain `IOException`**, the same as a locked file; see
  `LibraryIndex.ReadOrEmpty`.
- **RAW files contain several EXIF sub-IFDs**: take each value from the first one that has it (`PhotoMetadata.ReadEmbedded`).
  Fujifilm RAF sizes come from the RAF header (`FujifilmRaf`).
- **Headless rendering outside the tests:** no top-level `await` (continuations go to a dispatcher nothing pumps),
  tick `AvaloniaHeadlessPlatform.ForceRenderTimerTick()` while waiting (layout only runs on render ticks), and always
  dispose ExifTool: a leftover `exiftool` child keeps the output pipe open, so the run looks hung.
- **Styles match exact types:** `TextBlock.caption` doesn't style a `SelectableTextBlock`; list both.
- **Line endings:** `.gitattributes` normalises to LF in the repo; Windows checkouts get CRLF. Scripts that edit files
  should cope with both.
