<div align="center">

# GoLive Edit

**A standalone desktop audio mixer for Local Live sessions and OBS Control.**

[![Download GoLive Edit](https://img.shields.io/badge/Download-GoLive%20Edit-176b5d?style=for-the-badge)](https://github.com/Parker-009d/GoLive-Edit/releases/latest)

**Windows · macOS · Local Live · OBS Control**

</div>

## Download

Get the latest Windows and Mac downloads from the [GoLive Edit Releases page](https://github.com/Parker-009d/GoLive-Edit/releases/latest).

- **Windows 10/11, 64-bit:** `GoLive-Edit.exe`. Copy it to your PC and run it; Rust and Cargo are not required. WebView2 is required and is included with most current Windows installations.
- **macOS Intel, x64:** `GoLive-Edit-0.1.0-macOS-x64.dmg`. The app is ad-hoc signed, not notarized; macOS may ask you to approve opening it.

## What it does

- **Local Live:** choose audio devices and mix/record without connecting to OBS.
- **OBS Control:** connect to OBS WebSocket, map sources, control the mixer, and use OBS stream controls.
- Channel faders, mute/solo, gate, compressor, EQ, effects, and the existing EQ plugin controls.

## Build status

The Windows x64 build is cross-compiled and has not yet been tested on a Windows PC. The macOS Intel app bundle was built, installed, and signature-verified; its DMG checksum was verified. Please test audio devices, Local Live recording, and OBS Control on your computer before using the app in a live session.

## Feedback and support

Report problems or ask for help in [GitHub Issues](https://github.com/Parker-009d/GoLive-Edit/issues). Include your operating system, app version, audio interface, and steps to reproduce. Do not post passwords, stream keys, or private recordings.

## Build from source

GoLive Edit uses Tauri 2, Rust, and CPAL. See [Tauri prerequisites](https://v2.tauri.app/start/prerequisites/) for developer setup. The source and current build files are available in the release ZIP.

## License

This repository does not include a license. Contact the maintainer through GitHub Issues to ask about reuse or redistribution.
