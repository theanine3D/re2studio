# RE2 Studio

A romhacking suite for **Resident Evil 2 on the Nintendo 64**. It comes as a desktop editor for Windows and Linux (RE2 Studio) and a command-line tool (`re2`), both capable of editing graphics, text, and sounds.

This repository contains **no game data**. You need your own dump of the game: *Resident Evil 2 (USA)* or *Resident Evil 2 (USA) (Rev 1)*, as a `.z64`, `.v64` or `.n64` file.

## What it can do

| Asset | View | Export | Import |
|---|---|---|---|
| Pre-rendered backgrounds (1,227 JPEGs) | ✓ | PNG | PNG |
| Foreground masks | ✓ | PNG | PNG (coverage) |
| Textures (1,054) | ✓ | PNG | PNG |
| Characters, items, scenery (3D models) | 3D viewer | glTF (skinned, with animations) | glTF (geometry, skeleton, poses) |
| Menu screens and inventory icons | ✓ | PNG, palettes | PNG, palettes |
| Sound effects and music (1,192 samples) | ✓ with playback | WAV | WAV |
| Voice dialogue (588 clips, MORT codec) | ✓ with playback | WAV | WAV |
| FMVs (271 MPEG-1 movies) | ✓ with playback* | `.m2v` | video, via ffmpeg* |
| Documents, item names and item descriptions | ✓ | text | text |
| Rooms, cameras and doors | ✓ | GameShark codes | — |

\* ffmpeg must be on your `PATH` for FMV playback and for importing anything other than `.m2v`.

Editing always goes through a **project folder**:

1. Extract the ROM into loose files.
2. Edit the files, or import replacements from the editor.
3. Build a new ROM.

Your original ROM is never modified. A build from an unedited project is byte-identical to the source ROM. The Project tab can also create and apply **BPS** patches for distributing a hack.

## Building

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

**Platform support:** Windows and **Linux** (x64). Both the editor and the command-line tool are tested on Ubuntu 22.04. The tests include a full extract-and-rebuild that reproduces the ROM byte for byte, and the whole test suite passes. macOS should work for the command-line tool but is untested.

On Linux, the editor uses standard desktop tools for three features:

| Feature | Needs one of |
|---|---|
| File browser (the **Choose...** buttons) | `zenity` or `kdialog` |
| Copy image to clipboard | `wl-copy` (Wayland) or `xclip` (X11) |
| Sound and voice playback | `paplay`, `pw-play` or `aplay` |
| FMV playback and conversion | the distribution's `ffmpeg` package (a Windows `ffmpeg.exe` visible through WSL is ignored) |

Every desktop distribution ships the OpenGL and X11/Wayland libraries the window itself needs. A minimal install (for example WSL) may need `libgl1 libxrandr2 libxcursor1 libxi6 libxinerama1 libxkbcommon0`.

```bash
dotnet build RE2Suite.sln
```

To publish self-contained, single-file executables into `dist/`:

```bash
dotnet publish Re2.Studio -c Release -o dist
dotnet publish Re2.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o dist
```

To build both for Linux (from Windows or Linux):

```bash
dotnet publish Re2.Studio -c Release -r linux-x64 -o dist-linux
dotnet publish Re2.Cli -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o dist-linux
```

## Running

- **Editor:** run `RE2 Studio.exe` (on Linux, `./"RE2 Studio"`) and choose **Open a ROM...**. It reopens the last ROM on later launches.
- **Command line:** run `re2` with no arguments to list its commands. For example:

```bash
re2 extract "Resident Evil 2 (USA) (Rev 1).z64" --out my-project
re2 build "Resident Evil 2 (USA) (Rev 1).z64" --project my-project --out modified.z64
```

## Tests

```bash
dotnet test
```

Most tests need the retail ROM and are skipped without it. To run them:

- Set the `RE2_ROM` environment variable to the ROM's path.
- For the Rev 0 / Rev 1 comparison tests, put both No-Intro dumps in the same folder:
  - `Resident Evil 2 (USA).z64`
  - `Resident Evil 2 (USA) (Rev 1).z64`

## Repository layout

| Path | Contents |
|---|---|
| `Re2.Core/` | Format readers and writers, codecs, the project extract/build pipeline, and a MIPS interpreter used to verify the game's own decoders |
| `Re2.Studio/` | The editor (Silk.NET + Dear ImGui) |
| `Re2.Cli/` | The `re2` command-line tool, including diagnostics |
| `Re2.Tests/` | xUnit tests |
| `ghidra/RE2_Import.py` | Ghidra script that loads the ROM with its overlays decompressed and mapped |
| `make-icon.py` | Regenerates `Re2.Studio/app.ico` from `icon.png` |

## Documentation

[`docs/RE2-N64-Format-Specification.md`](docs/RE2-N64-Format-Specification.md) documents how the game stores and encodes everything above: containers, compression, models, animation, audio codecs, text and lookup tables. It is written for anyone building their own tools.

## License

Copyright (C) 2026 Pedro Valencia Oseguera

RE2 Studio is free software, licensed under the [GNU General Public License v3.0](LICENSE). Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

RE2 Studio by **Theanine3D** — <https://www.youtube.com/@Theanine3D>

*Resident Evil* is a trademark of Capcom. This project is not affiliated with or endorsed by Capcom.
