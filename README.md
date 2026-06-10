# MusicBox

MusicBox is a Windows desktop app for writing, converting, and generating music scores in one workflow.

It is designed for practical music creation instead of heavyweight professional notation software: you can edit directly on staff, convert the result into other formats, and use smart composition to quickly sketch new ideas.

![MusicBox Screenshot](Assets/readme-screenshot.png)

## ✨ Features

- Staff editor for writing and editing notes directly on the score
- Score conversion to jianpu preview, guitar tab preview, PDF, and MusicXML
- Smart composition workspace for quickly generating multiple musical ideas
- Chinese and English UI

## 📄 Main Pages

| Page | Purpose | Typical Use |
| --- | --- | --- |
| Editor | Main staff notation workspace | Write, edit, and refine a score |
| Convert | Format conversion and export | Create jianpu preview, export PDF or TXT |
| Compose Workbench | Smart idea generation | Generate and compare composition candidates |
| Settings | App preferences | Theme, language, and related options |

## 🎼 Editor

The Editor page is the main workspace for writing music on staff notation.

- Create and edit notes and rests directly on the staff
- Change duration, accidentals, ornaments, articulation, slurs, tempo, key signature, and time signature
- Adjust layout-related options such as snap division and display settings
- Preview playback with timeline control and volume control
- Import and export MusicXML

Typical workflow:

1. Open the Editor page.
2. Choose note or rest mode.
3. Select duration and note attributes.
4. Click the staff to place notes and adjust them as needed.
5. Preview playback and continue refining the score.

## 🔄 Convert

The Convert page turns the current score into other readable or exportable formats.

- Import from the current staff score or from external files
- Convert staff notation into jianpu preview
- Convert staff notation into guitar tab preview
- Export PDF
- Export MusicXML
- Export guitar tab as TXT
- Support print preview and printing

Typical workflow:

1. Open the Convert page.
2. Import the current score or open a supported file.
3. Review the generated preview.
4. Export the result in the format you need.

## 🎹 Compose Workbench

The Compose Workbench page helps you generate multiple draft ideas quickly.

- Generate 3 composition candidates at a time
- Use mood-based automatic tonality selection
- Build longer results with section planning instead of looping one short phrase
- Add bass, harmony, expression marks, and sustain pedal marks automatically
- Preview, keep, retry, rate, and apply candidates to the Editor page

### How It Works

The smart composition system is not just random note generation. Its basic idea is:

1. Build a request from mood, length, tempo, tonality, and other user settings.
2. If auto tonality is enabled, choose a suitable key and mode based on mood.
3. Plan the overall structure first, so the result has sections such as statement, development, contrast, climax, and resolution.
4. Generate harmonic movement for each section, then decide rhythm, melodic direction, density, and cadence feeling from that role.
5. Build melody mostly by scale degree logic, while limiting excessive leaps and keeping the melodic line smoother.
6. Add bass and harmony with voice-leading control so the result feels more musical and less mechanical.
7. Add expression marks and pedal marks so the output is closer to an editable score draft instead of a bare MIDI-like sequence.

Besides generation itself, MusicBox also tries to rank candidates so the better and less repetitive ones appear first.

Typical workflow:

1. Open the Compose Workbench page.
2. Choose mood and length.
3. Generate candidates.
4. Listen, compare, keep or retry candidates, then apply one to the Editor page.

## 🖥️ System Requirements

- Windows 11
- x64

Note:

- The current GitHub Release workflow packages the `win-x64` build.

## 📦 Download

Releases are available on the repository `Releases` page.

- `latest`: newest development build from the default branch
- `v*`: versioned releases such as `v0.1.0`
- Download `MusicBox-win-x64-*.zip`
- Extract it to a normal folder
- Run `MusicBox.exe`

If Windows shows a SmartScreen warning, click `More info`, then `Run anyway`.

## 🛠️ Build From Source

```powershell
dotnet restore .\MusicBox.csproj
dotnet build .\MusicBox.csproj -c Release -p:Platform=x64
dotnet publish .\MusicBox.csproj -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -p:PublishReadyToRun=false
```

To create a release-style zip package:

```powershell
.\scripts\package-release.ps1 -Version dev
```

The package will be created in the `release/` directory.

## 🚀 Release Automation

This repository includes a GitHub Actions release workflow:

- Every push to the default branch updates the `latest` prerelease
- Pushing a tag such as `v0.1.0` creates a versioned release
- The workflow uploads both the zip package and its `.sha256` checksum file

## 📌 Status

The project is still actively evolving.

- Smart composition currently focuses more on practical pop, folk, and ambient-style sketch generation
- Export and conversion output will continue to be refined

If you run into freezes, crashes, or hard-to-locate errors, adding clearer dialog-based diagnostics can help narrow down the exact failing step.
