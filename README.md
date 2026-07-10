# StreamHost

StreamHost streams a monitor or a single window from a Windows PC to other
people's browsers, over Tailscale or a private LAN. Transport is WebSocket,
playback is Media Source Extensions (MSE). No WebRTC, no signaling server, no
account.

## Features

- Streams a whole monitor or a single window. Monitor capture uses desktop
  duplication and switches capture method automatically, so
  exclusive-fullscreen games work; the cursor is included.
- Audio comes from whichever app you pick (the captured window, another
  running app, or none), separate from your voice chat.
- Hardware encoding (NVIDIA NVENC, AMD AMF, Intel QSV) with automatic
  fallback to CPU (libx264) if the GPU encoder fails or stalls mid-stream.
  An encoder selector can force a specific one, or leave it on Auto.
- A grid page tiles several streams in one browser tab, side by side or
  stacked.
- A built-in Watch window shows the same grid without a browser and can
  list live StreamHost machines on your Tailscale network.

## Requirements

- Hosting a stream: Windows 10/11, x64.
- Watching a stream: any modern browser. Friends on Linux or Mac can watch
  fine, they just can't host.

## Quick start

1. Download the latest release zip, unzip it, run StreamHost.exe.
2. Pick a source and quality preset, click Start.
3. Copy the link and send it to a friend. Every stream start generates a
   new per-stream key, so the link changes each time, always copy a fresh
   one after restarting a stream.
4. If a friend can't connect, click "Fix access" in the app (asks for
   administrator approval once and configures the port automatically), or
   run setup.bat as administrator as a manual fallback.

## Console mode

Any command-line argument switches StreamHost to console mode instead of
opening the window, for example:

    StreamHost.exe --monitor 0 --encoder libx264 --port 8093

## Build from source

Needs the .NET 10 SDK. From the repo root:

    dotnet run --project src/StreamHost

## License

PolyForm Noncommercial 1.0.0. Free for noncommercial use. See LICENSE.
