# Spectari

Spectari streams a monitor or a single window from a Windows PC over
Tailscale or LAN to a modern browser or the built-in Watch window. It was
built for private streaming in a small group: you run one exe, people you
choose open a link, and you're done. Transport is WebSocket and playback is
MSE, with no WebRTC, signaling server, Spectari account, or cloud relay.

Once network access is in place, viewers can watch in a modern browser or
through the Spectari Watch window.

## What it does

- Streams a whole monitor or a single window. Monitor capture
  uses desktop duplication for full-rate capture and includes the cursor.
  Some exclusive-fullscreen applications still need borderless mode, and a
  few cannot be captured at all. Amnesia: The Bunker is one known example.
- Streams audio from one application you pick, or nothing. Full desktop audio
  and multiple audio sources are currently not supported.
- Hardware encoding on NVIDIA (NVENC), AMD (AMF), and Intel (QSV), with
  automatic fallback to CPU encoding if the GPU encoder fails or stalls.
  Presets run from 720p30 to 1440p60 plus Native, with a separate
  low/medium/high bitrate picker that shows the Mbps so you can see the
  upload cost before starting.
- Every stream gets a random viewer key baked into the link, so a stale or
  guessed link does not work. The Security section explains this further.
- A grid page tiles several streams in one tab, and the built-in Watch
  window shows the same grid without a browser. It automatically searches
  for live Spectari machines it can discover on your Tailscale network.
- While the app is open but not streaming, the link serves a "not streaming
  yet" page that connects on its own when you start. The Watch window checks
  your tailnet in the background and plays one soft chime when a stream you
  aren't already watching goes live. The bell button in the grid header
  turns that off.

## Requirements

- Hosting: Windows 10/11, x64. No install; the release zip is self-contained.
- Watching: any modern browser on any OS. Linux and Mac can watch, they
  just can't host.

## Quick start

1. Download the latest release zip from the Releases page, unzip it into a
   folder anywhere, and run Spectari.exe.
2. Pick what to share and a quality preset, click Start streaming.
3. Click Copy link and send it to whoever is watching. For someone who is
   only on your LAN, use the small arrow next to it and choose Copy LAN link.
4. If someone can't connect, click "Open port" in the app. It asks for
   administrator approval and opens that stream port. To also allow LAN
   viewing, tick "Allow LAN viewers" before clicking "Open port".

If you prefer a script, running setup.bat as administrator sets up
Tailscale-only access for port 8093 by default.

Note on links: each stream start generates a new viewer key, so the link
changes when you restart a stream. Viewers who joined your tailnet pick up
the new key automatically if they keep their tab or grid open. Viewers using
a shared-machine invite should use the fresh link you send.

## Watching over the internet: Tailscale

Spectari does not expose anything to the public internet, and there is no
relay server. Instead it runs over [Tailscale](https://tailscale.com), a
free mesh VPN that makes your PCs reach each other directly, encrypted,
with no port forwarding.

One-time setup on the hosting PC:

1. Go to tailscale.com, create a free account (you can sign in with Google,
   Microsoft, GitHub, or Apple).
2. Install Tailscale on the hosting PC and sign in. The PC gets a stable
   address that starts with 100.x.
3. Start streaming.

For connecting as a viewer, there are two ways. Which one fits depends on
the person.

### Viewers who just watch: share your PC with them

This is the way to go for most viewers. They keep their own Tailscale
account and network instead of joining yours, nothing about their setup
changes, and they do not count against your account's user limit. Tailscale
currently labels machine sharing as beta.

1. The viewer creates their own free Tailscale account and installs
   Tailscale on their machine.
2. You open the Tailscale admin console (login.tailscale.com) and find the
   hosting PC under Machines. Pick Share from its menu. Send the share
   invite it gives you to the viewer privately.
3. The viewer accepts the invite. Your PC now shows up in their Tailscale
   as a shared machine.
4. You send them the stream link. If they have Spectari installed, opening
   Watch automatically searches for reachable live streams, so a discoverable
   host appears there without pasting its link.

Sharing is one-way: they can reach your PC, your PC cannot reach their
devices. To stop sharing later, remove them from the same menu.

### People who also stream: invite them to your network

If someone hosts streams too, invite them into your Tailscale network
instead (admin console, Users, Invite). The free plan covers a small
number of users. They install Tailscale, accept the invite, and sign in.
Everyone on the same network can find each other's streams with Find
streams in the Watch window, and picks up rotated viewer keys
automatically.

If an invited person already uses Tailscale for something else, a device
can only be active on one network at a time, but the Tailscale app supports
multiple accounts. They can click the Tailscale tray icon, click the account
name, choose "Add account," and sign in with the invited account. Switching
back is as simple as selecting their previous account from the tray app.
Viewers you shared your PC with skip this
entirely, which is one reason sharing is the default recommendation.

Tailscale specifics beyond that (ACLs and so on) are documented by
Tailscale and out of scope here.

## Watching several streams at once

The grid tiles multiple streams in one browser tab. Open
`http://<host-address>:<port>/grid` in a browser, then paste each stream's
full copied link (the key is part of it) into the add bar. Every live
stream you add shows up as a tile. Viewers using the app's Watch window
already see this same grid, with a stream finder built in.

## Security

The model in plain terms, so you can decide whether it fits your use:

- **What can reach the host.** Out of the box, the stream port accepts only
  Tailscale addresses, and nothing is reachable from the public internet.
  LAN access is an explicit opt-in. Tick "Allow LAN viewers" and click
  "Open port" to let the port accept your local network.
- **What the viewer key does.** Each stream start generates a random key
  that becomes part of the link. The stream page and the video itself
  require it, so a bare address or an old link does not work. The status
  endpoint and the page files themselves are not key-gated; that is what
  lets grid pages check whether a host is live.
- **Devices with network access are trusted.** Only devices that can reach
  the host over Tailscale or the permitted LAN can access it at all. Direct
  tailnet members pick up the current viewer key automatically; that is how
  Find streams and key rotation work, and it is deliberate. A viewer using
  a shared-machine invite should use the full link you send. In practice,
  reachability is the real gate: anyone you let onto your tailnet, or share
  your PC with, can watch. The key mainly protects against stale links and
  casual access from an allowed LAN.
- **Tailscale is recognized by address range.** Spectari treats the
  100.64.0.0/10 range as Tailscale. Tailscale uses that range, but does
  not own it exclusively; another VPN or a carrier-grade NAT setup that
  puts addresses from it on your machine would receive the same trust.
  Keep that in mind if you run one.
- **What Copy log shares.** The "Copy log" button scrubs viewer keys, your
  Windows user name, file paths, and Tailscale addresses before anything
  reaches the clipboard. It keeps the machine name, stream name, and app
  names, since reports are rarely debuggable without them. The files opened
  through "Open logs" are not scrubbed.

## Common issues

- The page never loads: the port isn't open on the host yet, or Tailscale
  isn't connected on one end. Click "Open port" in the app, check both
  Tailscale icons, try again.
- Status says "LIVE, THIS PC ONLY": same thing. Click "Open port"; the
  stream restarts itself after the port is configured.
- The page says the stream needs its viewer key: the stream was restarted
  and the old link went stale. Send a fresh link, or have them use Find
  streams in the Watch window.
- Status looks fine but nobody can connect: firewall state can drift (a
  Windows reset, another program, a network profile change). Click "Open
  port" again. It configures one port at a time, so after changing the
  port, click it again for the new one; LAN access in particular does not
  carry over from a previous port. And if you streamed Tailscale-only but
  now want LAN viewers, tick "Allow LAN viewers" and click "Open port" again
  on the same port.
- A fullscreen game shows a frozen frame: share the whole monitor instead
  of the window, or set the game to borderless. Some exclusive-fullscreen
  setups cannot be captured by anything. Amnesia: The Bunker is one known
  example.
- Smooth on one browser, choppy on another: check that the browser's
  hardware acceleration is on. Add `&stats=1` to the end of the link or
  click the green LIVE badge for a diagnostics overlay.
- A 60 fps window share reports lower source fps: Windows window capture can
  deliver fewer fresh frames on some systems. Share the whole monitor for the
  full-rate capture path.
- Anything else: "Copy log" in the app puts the log plus version, system,
  GPU, and encoder info on the clipboard. Feel free to open an issue and
  paste the copied report for troubleshooting.

## FAQ

**Is it free?** Yes, for noncommercial use. See the license note below.

**Why not just use Discord screen share?** Quality and control. Spectari
gives you direct bitrate and quality controls without requiring a
subscription, and viewers can fullscreen a plain browser tab on any monitor.

**Does it include my microphone or voice chat?** Spectari does not capture
your microphone. Audio comes from the one app you pick in the Audio dropdown.
Any other app you did not pick, including Discord, is excluded.

**The stream audio is quiet or silent even though the app is playing.**
Check that app's volume in the Windows volume mixer. The capture taps the
app's sound after Windows applies that per-app volume, so turning it down
or to zero there quiets or mutes the stream too. If you want it quiet on
your own speakers but audible to viewers, use the app's own in-game or
in-app volume slider instead.

**How many people can watch?** Your upload bandwidth divided by the
bitrate, since each viewer gets their own full copy. A 12 Mbps 1080p60
stream to four viewers needs roughly 48 Mbps of upload. Check your upload
speed specifically, it is usually a small fraction of your download. On a
typical cable connection the realistic ceiling is around 3 to 5 viewers;
symmetric fiber can feed 10 or more, and lower bitrates stretch further.
Spectari is built for a handful of friends, not an audience.

**What's the latency?** Roughly half a second to a second, tuned for
smoothness over speed.

**Is opening the port safe?** The firewall rule only admits Tailscale
addresses (plus private LAN ranges if you opt in), and the stream itself
requires the per-stream key from the link. Nothing is reachable from the
public internet. The Security section above has the full model.

**Can I run it from the command line?** Yes, any argument switches to
console mode: `Spectari.exe --monitor 0 --encoder libx264 --port 8093`.

## Build from source

Needs the .NET 10 SDK. From the repo root:

    dotnet run --project src/Spectari

## Support

Spectari is a hobby project. If it's useful to you and you feel like
saying thanks: [ko-fi.com/skeptic043](https://ko-fi.com/skeptic043).

## License

Spectari is free for personal and other noncommercial use under
[PolyForm Noncommercial 1.0.0](LICENSE). Commercial use is not included.
Contact the maintainer through GitHub regarding commercial licensing.
