# GoLiv Edit Windows host

This is a Windows host port using WinForms/WebView2 for the existing mixer interface, NAudio/WASAPI for device input and Local Live WAV capture, and OBS WebSocket v5 for assigned source gain/mute controls.

## Build on Windows

Install the .NET 8 SDK and Microsoft Edge WebView2 Runtime, then run `build-windows.ps1`. The script creates a self-contained x64 single-file executable in `dist\win-x64`. The Edge WebView2 Runtime remains a prerequisite.

Windows 0.2.2 uses the same mixer state and channel controls as the Mac build. Local Live applies channel Gate, parametric EQ, compressor, four assigned FX sends/returns, channel and master faders, mute, and solo to stereo WAV recording. OBS Control applies channel volume and mute plus the matching compressor and GoLive VST processor for Gate, EQ, and FX to each assigned OBS source. The Windows workflow builds the OBS VST processor as `Resources\\GoLiveFX.dll` and includes it with the app. Microsoft Edge WebView2 Runtime remains required. A hosted Windows runner can compile and launch-check the app; audio-device behavior requires a real Windows audio interface to verify.

## Microsoft Store

GoLiv Edit already has a published Microsoft Store listing. Update that product in Partner Center; do not create a second listing or delete the existing product. The currently published Store package is managed separately from this repository's GitHub Windows download, so check the package version in Partner Center before replacing it.

The EXE is an app binary, not an installer. A Store package update must use the exact identity and publisher assigned to the existing product, a higher package version, required Store assets, and a valid MSIX package. Build and package on Windows, then run the Windows App Certification Kit and verify Local Live capture, recording, OBS connection, and phone remote before submission. The current EXE is not yet Windows-PC verified, so it should not be uploaded as a production Store update until those checks pass.

Store listing: https://apps.microsoft.com/detail/9MWPT10ZWLVZ

