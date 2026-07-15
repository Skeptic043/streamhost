STREAMHOST
==========

Stream a game or monitor to friends' browsers over a private network.
Voice chat stays in your voice app; StreamHost only captures and sends
the audio of the application you select.

SETUP (once, ~2 minutes)
------------------------
1. Install Tailscale (tailscale.com/download) and accept the invite you
   received by email.
2. Double-click StreamHost.exe and Start a stream. If a friend can't
   connect, click "Open port" in the app: it asks Windows for
   administrator approval once and configures the stream port for you
   (the port is what StreamHost serves the stream on and what viewers'
   browsers connect to). You normally do this once for each port you use.
   If you'd rather do it up front, or "Open port" doesn't work for
   some reason, right-click setup.bat -> Run as administrator instead.
   It does the same thing: reserves the stream port and adds a firewall
   rule scoped to Tailscale addresses.

   The firewall rule allows only Tailscale by default. To also let devices
   on your local network (LAN) reach the stream, tick "Allow LAN viewers"
   in the app before clicking "Open port". setup.bat always sets up
   Tailscale-only; use the app for LAN access.

STREAMING
---------
1. Double-click StreamHost.exe.
2. Pick your game from the list, pick a quality preset, Start streaming.
   The encoder dropdown shows Auto, CPU, and the hardware encoders detected
   on this PC; leave it on Auto unless you have a reason to force one.
   Live status shows which encoder is actually running, including if it
   falls back to CPU mid-stream.
3. Copy link -> paste it to your friends. They open it in any browser.
   For a viewer who is only on your LAN, use the arrow next to Copy link
   and choose Copy LAN link.
   Hover the video for sound and volume controls, and fullscreen.
   The link includes a per-stream key, a new one is generated every time
   you start a stream. Direct members of your Tailscale network pick the
   new key up automatically if their tab or grid stays open. A viewer using
   a shared-machine invite needs the fresh link you send.
   While the app is open but not streaming, the link shows "not streaming
   yet" and connects on its own once you start.

Minimizing keeps the app on the taskbar and keeps streaming; closing the
window stops your stream. If StreamHost is already running, launching it
again just brings that window back instead of opening a second copy. The
app waits for the first captured frame before reporting the stream as
live. If it stops with a capture error instead, use "Copy log" (it
includes version, system, GPU, and encoder info along with the recent
log) and send that along.

WATCHING MULTIPLE STREAMS, THE GRID
------------------------------------
Anyone streaming also serves a grid page at http://<their address>:<port>/grid
(a stream link with /grid in place of everything after the port).
Open it, paste each friend's stream link once, every live stream tiles
into one tab. Easier: the app's "Watch streams" window is the same grid
with a stream finder built in. Reorder tiles with
the arrow buttons, and switch between side-by-side and stacked layout;
the list is remembered by your browser.

TIPS
----
- FULLSCREEN GAMES: share the whole monitor. Monitor shares use the
  full-rate desktop-duplication path and capture the cursor. If an
  exclusive-fullscreen game freezes, switch the game to borderless mode.
  Pick the game in the Audio list so the monitor share has sound.
- WINDOW SHARES AT 60 FPS: Windows window capture can deliver fewer fresh
  frames than the target rate on some systems. Share the whole monitor for
  the full-rate capture path.
- The Audio dropdown chooses whose sound the stream carries: the
  captured window, any other running app, or nothing.
- "Watch streams" in the app opens the built-in viewer (same grid as
  the browser version, identical playback for everyone), including a
  "Find streams" panel that lists live streams from other StreamHost
  machines on your Tailscale network for one-click adding.
- Add &stats=1 to the end of a stream link for a diagnostics overlay
  (the link already ends in ?k=..., so it's & not ?).
- The app remembers your source/preset/audio/port between runs.
- CUSTOM PORT (optional): setup.bat covers port 8093. For another port,
  open Command Prompt in this folder and run:  setup.bat 8094
  (that's the same setup.bat, given the port as an argument, not a
  separate file). Then set the same port in the app. If you use
  start-stream.bat, edit the PORT line there to match.
- Streaming at the same time as a friend is fine, whatever the ports;
  ports only matter per PC.

IF SOMETHING BREAKS
-------------------
Every run writes a log file (its path is printed at the top of the app's
log panel). "Copy log" puts the session log plus version, system, GPU,
and encoder info on the clipboard. Paste it when reporting a problem.

- Stream page never loads: the port isn't open yet, or Tailscale isn't
  connected on one end. Click "Open port" in the app (or run setup.bat
  as admin) and try again.
- Status shows "THIS PC ONLY": click "Open port" for that port; the app
  restarts an active stream automatically. If you run setup.bat instead,
  restart the stream yourself.
- Page loads but video never starts: same as above, the port that
  viewers' browsers connect to isn't reachable yet. Click "Open port" again
  or run setup.bat.
- "Port ... is already in use": another program (often another copy of
  StreamHost still running) has that port. Close it, or set a different
  port in the app.
- Everything looks fine on your end but nobody can connect: firewall
  state can drift (a Windows reset, another program, a network profile
  change). Click "Open port" again. It configures one port at a time, so
  after changing the port, run it again for the new one.

License: free for personal and other noncommercial use under PolyForm
Noncommercial 1.0.0 (see LICENSE.txt). Commercial use is not included;
contact the maintainer through GitHub regarding commercial licensing.
