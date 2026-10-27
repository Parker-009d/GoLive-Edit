# GoLive Edit

GoLive Edit is a desktop audio mixer with two independent operating modes: **Local Live** for sessions that do not use OBS, and **OBS Control** for controlling an OBS WebSocket connection.

## Downloads

Download the current test builds from the [Releases page](https://github.com/Parker-009d/GoLive-Edit/releases/latest):

- **Windows 10/11, 64-bit:** `GoLive-Edit.exe`. Rust and Cargo are not required on the PC. WebView2 is required and is included with current Windows installations.
- **macOS Intel, x64:** `GoLive-Edit-0.1.0-macOS-x64.dmg`. This test build is ad-hoc signed and not notarized; macOS may require approval to open it.

The Windows executable was cross-compiled and verified as a Windows 64-bit GUI application, but has not been tested on a Windows PC. The Mac app package was built and verified. Please test audio interfaces, Local Live recording, and OBS connections on your computer before a live session.

## Features

- **Local Live:** local device selection, mixer controls, audio processing, mute/solo, and recording without an OBS connection.
- **OBS Control:** OBS WebSocket connection, source mapping, mixer controls, and stream controls.
- EQ, gate, compressor, effects, and existing EQ plugin controls.
- Native audio device discovery through the desktop backend.

## Feedback and support

Please use the [Issues page](https://github.com/Parker-009d/GoLive-Edit/issues) to report a problem or ask for help. Include your operating system, app version, audio interface, and steps to reproduce. Remove private information from screenshots and logs; never post passwords, stream keys, or private recordings.

## Build from source

The app uses Tauri 2, Rust, and CPAL. Developer setup instructions are available in [Tauri's prerequisites](https://v2.tauri.app/start/prerequisites/). End users should use the Releases page.

## License

No license is included in this early test release. Contact the maintainer through GitHub Issues about permission to reuse or redistribute the source.
