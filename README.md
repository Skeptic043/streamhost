# Spectari

Spectari streams a monitor or a single window from a Windows PC over
Tailscale or LAN to a modern browser or the built-in Watch window. It was
built for private streaming in a small group. The idea is you run one exe, the people you
choose open a link, and you're done. Transport is WebSocket and playback is
MSE, with no WebRTC, signaling server, Spectari account, or cloud relay.

## What it does

- Streams a whole monitor, single window, or capture device (webcam/capture card). Monitor capture
  uses desktop duplication for full-rate capture and includes the cursor.
- Streams audio from one application you pick, all desktop audio, or nothing. Mixing several specific apps is not supported.
- Hardware encoding on NVIDIA (NVENC), AMD (AMF), and Intel (QSV), with
  automatic fallback to CPU encoding if the GPU encoder fails or stalls.
  Presets run from 720p30 to 1440p60 plus Native, with a separate bitrate
  picker that shows the Mbps so you see the upload cost before you start.
- Each stream gets a random viewer key baked into the link, so a bare or
  guessed link does not work.
- A grid tiles several streams in one browser tab, and the built-in Watch
  window shows the same grid without a browser. It finds live Spectari
  machines on your Tailscale network on its own.
- While the app is open but not streaming, the link serves a holding page
  that connects on its own when you start. The Watch window plays one soft
  chime when a stream you are not already watching goes live; the bell in the
  grid header turns that off.

## Requirements

- Hosting: Windows 10/11, x64. Download the zip, extract, and run Spectari.exe.
- Watching: any modern browser on any OS or mobile platform.

## Quick start

For LAN or a local test you can start right away. To let people watch over
the internet, set up Tailscale first (see the next section).

1. Run Spectari.exe, pick what to share and a quality preset, click Start
   streaming.
2. Click Copy link and send it to whoever is watching. For someone on your
   LAN only, use the small arrow beside it and choose Copy LAN link.
3. If someone can't connect, click Open port. It asks for administrator
   approval and opens the stream port. To also allow LAN viewers, tick
   "Allow LAN viewers" before clicking it.

Each stream start generates a new viewer key, so the link changes when you
restart a stream. Viewers on your tailnet pick up the new key automatically
if they keep their tab or grid open; anyone else needs the fresh link.

## Watching over the internet: Tailscale

Spectari reaches remote viewers over [Tailscale](https://tailscale.com), a
free mesh VPN that connects your PCs directly and encrypted, with no port
forwarding and no relay server.

One-time setup on the hosting PC:

1. Create a free account at tailscale.com (sign in with Google, Microsoft,
   GitHub, or Apple).
2. Install Tailscale on the hosting PC and sign in. The PC gets a stable
   address that starts with 100.x.
3. Start streaming.

There are two ways for a viewer to connect.

### Viewers who just watch: share your PC with them

Best for most viewers. They keep their own Tailscale account instead of
joining yours, and they do not count against your user limit (Tailscale
labels machine sharing as beta).

1. The viewer creates their own free Tailscale account and installs Tailscale.
2. In the Tailscale admin console, find the hosting PC under Machines, pick
   Share, and send them the invite privately.
3. They accept, and your PC appears in their Tailscale as a shared machine.
4. Send them the stream link. If they run Spectari, the Watch window finds a
   discoverable host on its own.

Sharing is one-way: they can reach your PC, not the reverse. Remove them from
the same menu to stop.

### People who also stream: invite them to your network

Invite them into your Tailscale network instead (admin console, Users,
Invite). The free plan covers a small number of users. Everyone on the
network finds each other's streams with Find streams in the Watch window and
picks up rotated keys automatically. Someone already using Tailscale
elsewhere can add this network as a second account from the tray app and
switch between them.

### Other networks (advanced)

Under the hood Spectari just serves the stream on an IP and port, so Tailscale
is not the only way to reach it. Anyone comfortable with their own networking
can point viewers at any reachable IP and port. A self-hosted VPN such as
WireGuard behaves just like Tailscale: turn on "Allow LAN viewers" and send the
full link. Port forwarding works too, but it puts the stream on the public
internet behind only the per-stream key. I recommend you do NOT do it unless you
understand the risks. Advanced setups beyond Tailscale and LAN are unsupported.

## Watching several streams at once

Two ways:

- In the app: run Spectari and click Watch streams. The Watch window shows a
  grid and finds live streams on your tailnet on its own. Use Find streams to
  add one by link.
- In a browser: open `http://<host-address>:<port>/grid` and paste each
  stream's full copied link into the add bar. Each live stream becomes a tile.

## Security

The model in plain terms:

- **What can reach the host.** Out of the box the stream port accepts only
  Tailscale addresses, and nothing is reachable from the public internet. LAN
  access is an explicit opt-in: tick "Allow LAN viewers" and click Open port.
- **What the viewer key does.** Each stream start generates a random key that
  becomes part of the link, so a bare or guessed address is refused. A link
  with an old key still works for viewers on your tailnet, whose page fetches
  the current key from the status endpoint; a LAN-only viewer needs the fresh
  link after a restart. The status endpoint and the page files are not
  key-gated, which is what lets grid pages check whether a host is live.
- **Devices with network access are trusted.** Anyone who can reach the host
  over Tailscale or the permitted LAN can watch. Tailnet members pick up the
  current key automatically, which is how Find streams and key rotation work.
  The key mainly protects against stale links and casual access on an allowed
  LAN.
- **Tailscale is recognized by address range.** Spectari treats 100.64.0.0/10
  as Tailscale. Another VPN or a carrier-grade NAT that puts addresses from
  that range on your machine would get the same trust, so keep that in mind if
  you run one.
- **What the logs contain.** Technical diagnostics, with personal information
  removed.

## Common issues

- **Nobody can connect, or status says "LIVE, THIS PC ONLY."** The port isn't
  open, or firewall state drifted. Click Open port and check Tailscale is
  connected on both ends. It configures one port at a time and LAN access does
  not carry across ports, so if you change the port or want to add LAN
  viewers, click Open port again on the new port with "Allow LAN viewers"
  ticked.
- **The page says the stream needs its viewer key.** The stream restarted and
  a LAN-only viewer's link went stale. Send a fresh link, or have them use
  Find streams in the Watch window. Tailnet viewers heal this on their own.
- **A fullscreen game shows a frozen frame.** Share the whole monitor instead
  of the window, or set the game to borderless. A few exclusive-fullscreen
  setups cannot be captured by anything; Amnesia: The Bunker is one known
  example. If you hit an app that won't capture, open an issue so it can be
  noted.
- **Smooth on one browser, choppy on another.** Check that the browser's
  hardware acceleration is on. Add `&stats=1` to the link or click the LIVE
  badge for a diagnostics overlay.
- **A 60 fps window share shows lower source fps.** Windows window capture can
  deliver fewer fresh frames. Share the whole monitor for the full-rate path.
- **Anything else.** Use Copy log and open a GitHub issue with it.

## FAQ

**Is it free?** Yes, for noncommercial use. See the license below.

**Does it capture my microphone?** Not directly. Audio comes from the one app
you pick, or from all desktop audio. Your mic is only in the
stream if you route it into that output yourself, for example mic monitoring
through your speakers.

**The stream audio is quiet or silent even though the app is playing.** Check
that app's volume in the Windows volume mixer. Capture taps the app's sound
after Windows applies its per-app volume, so turning it down there quiets the
stream too. Use the app's own volume slider instead if you want it quiet
locally but audible to viewers.

**How many people can watch?** Up to a hard cap of 24 at once, but bandwidth is
typically the real limit first. Each viewer gets a full copy of the stream,
so your upload divided by the bitrate is the practical cap. A 12 Mbps 1080p60 stream to
four viewers needs roughly 48 Mbps upload. Upload is usually a fraction of
download, so a typical cable connection handles around 3 to 5 viewers,
symmetric fiber 10 or more, and lower bitrates stretch further.

**What's the latency?** Roughly half a second to a second, tuned for
smoothness over speed.

**Can I run it from the command line?** Yes. Any argument switches to console
mode: `Spectari.exe --monitor 0 --encoder libx264 --port 8093`.

## Build from source

Needs the .NET 10 SDK. From the repo root:

    dotnet run --project src/Spectari

## Support

Spectari is a hobby project. If it's useful and you feel like saying thanks:
[ko-fi.com/skeptic043](https://ko-fi.com/skeptic043).

## License

Spectari is free for personal and other noncommercial use under
[PolyForm Noncommercial 1.0.0](LICENSE). Commercial use is not included;
contact the maintainer through GitHub about commercial licensing.

Release packages also include an unmodified FFmpeg executable as separate
software under GPLv3 or later. The exact source, license, binary hash, and
build-provider provenance are recorded in
[the FFmpeg source manifest](packaging/ffmpeg-sources.json).
