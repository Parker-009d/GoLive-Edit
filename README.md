<div align="center">

# GoLive Edit

**One two-channel audio workspace. Use it with or without OBS.**

[![Download GoLive Edit](https://img.shields.io/badge/Download-GoLive%20Edit-176b5d?style=for-the-badge)](https://github.com/Parker-009d/GoLive-Edit/releases/latest)

**Local Live · OBS Control · Recording · Audio Processing**

</div>

## One app, two independent ways to work

GoLive Edit brings a standalone local mixer and OBS controls together in one desktop app. **OBS is optional for Local Live.** Use the app for an acoustic session or a simple voice-and-instrument mix; choose OBS Control when your session needs OBS.

### Local Live — mix without OBS

Choose an audio input and output, assign available inputs to the two channel strips, and shape the mix with faders, mute/solo, EQ, gate, and compression. Record the processed local mix from inside the app. This mode is designed to work independently of OBS.

### OBS Control — manage an OBS mix

Connect to OBS over its WebSocket, map OBS audio sources to the channel strips, adjust levels and processing, and use the app’s stream controls. The custom EQ plugin and VST controls are part of the OBS workflow.

## Get started

1. Download GoLive Edit for your computer from the [latest release](https://github.com/Parker-009d/GoLive-Edit/releases/latest).
2. Open the app and choose **Local Live** for a standalone session or **OBS Control** for an OBS session.
3. In Local Live, find your audio devices, choose the available input and output, and assign channel inputs. In OBS Control, connect to OBS and choose a source for each channel.
4. Set your channel and master levels, then record locally or use OBS stream controls as appropriate.

Local Live sends sound to the selected output. To send that mix into OBS, configure OBS to capture that output or a virtual audio route.

## Downloads

- **Windows 10/11, 64-bit:** [GoLive-Edit.exe](https://github.com/Parker-009d/GoLive-Edit/releases/latest). It runs without Rust or Cargo installed. WebView2 is required and is included with most current Windows installations.
- **macOS Intel, x64:** [GoLive-Edit-0.1.0-macOS-x64.dmg](https://github.com/Parker-009d/GoLive-Edit/releases/latest). This build is ad-hoc signed, not notarized; macOS may ask you to approve opening it.
- **Source and current downloads:** available as a ZIP on the [release page](https://github.com/Parker-009d/GoLive-Edit/releases/latest).

## Build status

The Mac app was built, installed, and signature-verified on a Mac. The Windows x64 app was cross-compiled and tested on a Windows PC. Please verify audio device access, Local Live playback and recording, and OBS Control with your setup before relying on the app for a live session.

## Feedback and support

Report problems or ask questions in [GitHub Issues](https://github.com/Parker-009d/GoLive-Edit/issues). Include your operating system, app version, audio interface, and steps to reproduce. Do not post passwords, stream keys, or private recordings.

## Build from source

GoLive Edit uses Tauri 2, Rust, and CPAL. See [Tauri prerequisites](https://v2.tauri.app/start/prerequisites/) for developer setup. The complete source is included in the release ZIP.

## License

This repository does not include a license. Contact the maintainer through GitHub Issues to ask about reuse or redistribution.
