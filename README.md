<div align="center">

# GoLive Edit

**Your mixer. Your flow. Your phone.**

A two-channel desktop mixer for real sessions: work independently in **Local Live**, bring **OBS Control** into the same app when you need it, and take the controls to your phone over your trusted local network.

[![Get GoLive Edit](https://img.shields.io/badge/GET%20GOLIVE%20EDIT-v0.2.0-42cdb1?style=for-the-badge)](https://github.com/Parker-009d/GoLive-Edit/releases/latest)

**LOCAL LIVE · OBS CONTROL · PHONE REMOTE · RECORDING · PROCESSING**

</div>

## One mixer. Two ways to go live.

### Local Live — play, mix, and record without OBS

Choose your audio interface, assign inputs to two channel strips, shape your sound with channel processing, and record the local mix inside GoLive Edit. Local Live runs independently of OBS, making it a fit for acoustic sessions, practice, podcasts, and small live setups.

### OBS Control — bring your OBS audio into the same workflow

Connect to OBS over WebSocket, map OBS sources to the mixer channels, adjust levels and processing, and use OBS stream controls. The existing custom EQ plugin and VST workflows are available in OBS Control.

### Phone Remote — control the host mixer from across the room

Pair a phone on the same Wi-Fi or hotspot, then use it to switch modes and control the running mixer. Channel and master changes are reflected on the desktop and phone, so you operate one shared mix instead of repeating adjustments. Audio stays on the host; the phone carries controls and meter/analyzer data.

## Choose your release

| Release | What it brings | Best for |
| --- | --- | --- |
| **v0.1.0 — The Core Mixer** | Two-channel Local Live and OBS Control, local recording, channel processing, and desktop builds. | Getting started with the GoLive Edit mixer. |
| **v0.2.0 — The Connected Mixer** | Everything in v0.1.0, plus paired phone remote control, synchronized mixer state, remote parametric EQ with reset, and analyzer visualization. Windows build tested on a Windows PC. | Controlling the same live mix from the computer and a phone. |

**v0.2.0 is the latest release.** The original v0.1.0 release remains available in the [release history](https://github.com/Parker-009d/GoLive-Edit/releases).

## What’s in v0.2.0

- **One shared mixer:** phone and desktop controls update the same running host mixer, with active states reflected on both screens.
- **Remote modes:** switch between Local Live and OBS Control from the phone.
- **Remote processing:** control channel and master mixer settings and edit the parametric EQ remotely.
- **EQ tools:** reset the EQ and view the host input analyzer with a predicted post-EQ curve.
- **Smoother EQ movement:** remote EQ changes are batched to keep adjustments responsive.
- **Trusted device pairing:** connect over a local Wi-Fi/hotspot connection with a QR code and one-time pairing code; paired devices can reconnect without a new code.
- **Privacy by design:** no audio is sent over Wi-Fi. Router port forwarding is not required.

## Get started

1. Download GoLive Edit for your computer from the [latest release](https://github.com/Parker-009d/GoLive-Edit/releases/latest).
2. Open the app and choose **Local Live** or **OBS Control**.
3. Select your audio interface and inputs, or connect OBS and map your sources.
4. Set channel and master levels, then start your local recording or use the OBS stream controls.
5. To use your phone, start Remote Control on the host, connect both devices to the same Wi-Fi/hotspot, scan the QR code, and pair.

Local Live sends sound to the output selected on the host. To feed that mix into OBS, configure OBS to capture that output or a virtual audio route.

## Downloads and compatibility

- **Windows 10/11 · 64-bit:** standalone EXE; Rust and Cargo are not required. WebView2 is required and is included with most current Windows installations. The v0.2.0 build was tested on a Windows PC.
- **macOS Intel · x64:** DMG build, ad-hoc signed and not notarized; macOS may ask you to approve opening it.
- **Source:** included in the v0.2.0 source-and-downloads archive.

## Phone remote security and network notes

Remote Control uses HTTPS/WSS on the selected private LAN address. The host uses a private certificate, so a phone browser may display a trust warning. Continue only for the address shown in GoLive Edit on your own trusted network. The host and phone must be able to reach one another; guest-network/client isolation or a firewall may block them. No public internet connection or router port forwarding is needed.

## Feedback and support

Found a problem or have an idea? [Open a GitHub Issue](https://github.com/Parker-009d/GoLive-Edit/issues). Include your operating system, app version, audio interface, and steps to reproduce. Never post passwords, stream keys, or private recordings.

## Build from source

GoLive Edit uses Tauri 2, Rust, and CPAL. See the [Tauri prerequisites](https://v2.tauri.app/start/prerequisites/) for developer setup.

## License

This repository does not currently include a license. Contact the maintainer through GitHub Issues to ask about reuse or redistribution.
