# StreamHost

StreamHost streams a monitor or a single window from a Windows PC to other
people's browsers. It was built for private game streaming in a small group:
you run one exe, people you choose open a link, and that's the whole setup.
Transport is WebSocket, playback is MSE. No WebRTC, no signaling server, no
account, no cloud in the middle.

Viewers need nothing but a browser. Firefox and Chrome both work, including
with privacy addons, which is a big part of why this exists.

## What it does

- Streams a whole monitor or a single window, your choice. Monitor capture
  uses desktop duplication and switches capture method automatically, so
  fullscreen games work and the cursor is included.
- Streams the audio of whichever app you pick (the captured game, another
  app, or nothing). Your voice chat is not part of the capture unless you
  deliberately select it.
- Hardware encoding on NVIDIA (NVENC), AMD (AMF), and Intel (QSV), with
  automatic fallback to CPU encoding if the GPU encoder fails or stalls
  mid-stream. Presets from 720p30 to 1440p60 plus Native, with a separate
  low/medium/high bitrate picker that always shows the actual Mbps, so you
  know what you're sending before you start.
- Every stream gets a random viewer key baked into the link, so only people
  with the current link can watch.
- A grid page tiles several streams in one tab, and the built-in Watch
  window shows the same grid without a browser, with one-click discovery of
  other StreamHost machines on your Tailscale network.
- While the app is open but not streaming, the link serves a "not streaming
  yet" page that connects on its own when you start.

## Requirements

- Hosting: Windows 10/11, x64. No install, the release zip is
  self-contained.
- Watching: any modern browser on any OS. Linux and Mac can watch, they
  just can't host.

## Quick start

1. Download the latest release zip from the Releases page, unzip it
   anywhere, run StreamHost.exe.
2. Pick what to share and a quality preset, click Start streaming.
3. Click Copy link and send it to whoever is watching.
4. If someone can't connect, click "Fix access" in the app. It asks for
   administrator approval once and opens the stream port. setup.bat (run
   as administrator) does the same thing if you prefer a script.

That's it for people on the same network. For watching over the internet,
see the next section.

Note on links: each stream start generates a new viewer key, so the link
changes when you restart a stream. Viewers on your Tailscale network pick
up the new key automatically if they keep their tab or grid open; anyone
else needs the fresh link.

## Watching over the internet: Tailscale

StreamHost does not expose anything to the public internet, and there is no
relay server. Instead it runs over [Tailscale](https://tailscale.com), a
free mesh VPN that makes your PCs reach each other directly, encrypted,
with no port forwarding.

First-time setup, once per person:

1. Go to tailscale.com, create a free account (it signs in with Google,
   Microsoft, GitHub, or Apple).
2. Install Tailscale on the hosting PC and sign in. The PC gets a stable
   address that starts with 100.x.
3. Get the viewers onto the same tailnet. The simplest way for a small
   group is inviting them from the Tailscale admin console; the free plan
   covers a small number of users. Viewers install Tailscale, accept the
   invite, and sign in.
4. Start a stream and use Copy link. StreamHost prefers the Tailscale
   address automatically, so the link works from anywhere.

Tailscale specifics beyond that (sharing single devices between tailnets,
ACLs, and so on) are documented by Tailscale and out of scope here.

## Common issues

- The page never loads: the port isn't open on the host yet, or Tailscale
  isn't connected on one end. Click "Fix access" in the app, check both
  Tailscale icons, try again.
- Status says "LIVE, THIS PC ONLY": same thing. "Fix access" opens the
  port, then the stream restarts itself.
- The page says the stream needs its viewer key: the stream was restarted
  and the old link went stale. Send a fresh link, or have them use Find
  streams in the Watch window.
- A fullscreen game shows a frozen frame: share the whole monitor instead
  of the window, or set the game to borderless. Some exclusive-fullscreen
  setups can't be captured by anything.
- Smooth on one browser, choppy on another: check that the browser's
  hardware acceleration is on. Add ?stats=1 to the link for a diagnostics
  overlay.
- Anything else: "Copy log" in the app puts the log plus version, system,
  GPU, and encoder info on the clipboard. Open an issue and paste it.

## Reading the stats line

While streaming, the log prints a line like this every 10 seconds:

    [stats] fresh 1499 dup 1 (0.1%), pacing slips 0, source 60 fps, viewers 2

- fresh / dup: frames that carried new content vs repeats of the last one.
  High dup on a static screen is normal (nothing on it changed). High dup
  during motion means capture is the bottleneck, not the encoder.
- pacing slips: times the frame clock fell behind and had to resync.
  Should stay at 0; a climbing number means the machine is overloaded.
- source: the rate the screen capture is actually delivering.
- viewers: how many players are connected right now.

## FAQ

**Is it free?** Yes, for noncommercial use. See the license note below.

**Why not just use Discord screen share?** Quality and control. StreamHost
sends the bitrate you ask for, captures the audio of one app instead of
your whole desktop, and viewers watch in a plain browser tab they can
fullscreen on any monitor.

**Does it stream my voice chat?** No. Audio comes from the one app you
pick in the Audio dropdown. Discord, or any other app you didn't pick, is
excluded by construction.

**How many people can watch?** Your upload bandwidth divided by the
bitrate. A 12 Mbps 1080p60 stream to four viewers needs roughly 48 Mbps of
upload, since each viewer gets their own copy.

**What's the latency?** Roughly half a second to a second, tuned for
smoothness over speed.

**Is opening the port safe?** The firewall rule only admits Tailscale and
private LAN address ranges, and the stream itself requires the per-stream
key from the link. Nothing is reachable from the public internet.

**Can I run it from the command line?** Yes, any argument switches to
console mode: `StreamHost.exe --monitor 0 --encoder libx264 --port 8093`.

## Build from source

Needs the .NET 10 SDK. From the repo root:

    dotnet run --project src/StreamHost

## Support

StreamHost is a hobby project. If it's useful to you and you feel like
saying thanks: [ko-fi.com/skeptic043](https://ko-fi.com/skeptic043).

## License

PolyForm Noncommercial 1.0.0. Free for noncommercial use. See LICENSE.
