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
   connect, click "Fix access" in the app: it asks Windows for
   administrator approval once and configures the stream port for you
   (the port is what StreamHost serves the stream on and what viewers'
   browsers connect to). You only do this once per PC.
   If you'd rather do it up front, or "Fix access" doesn't work for
   some reason, right-click setup.bat -> Run as administrator instead.
   It does the same thing: reserves the stream port and adds a firewall
   rule scoped to Tailscale addresses.

   The firewall rule allows only Tailscale by default. To also let devices
   on your local network (LAN) reach the stream, tick "Allow LAN viewers"
   in the app before clicking "Fix access". setup.bat always sets up
   Tailscale-only; use the app for LAN access.

STREAMING
---------
1. Double-click StreamHost.exe.
2. Pick your game from the list, pick a quality preset, Start streaming.
   The encoder dropdown picks NVIDIA NVENC, AMD AMF, Intel QSV, or CPU
   (libx264); leave it on Auto unless you have a reason to force one.
   Live status shows which encoder is actually running, including if it
   falls back to CPU mid-stream.
3. Copy link -> paste it to your friends. They open it in any browser.
   Hover the video for sound and volume controls, and fullscreen.
   The link includes a per-stream key, a new one is generated every time
   you start a stream. Viewers on your Tailscale network pick the new key
   up automatically if their tab or grid stays open; anyone else needs a
   fresh link (or "Find streams" in the Watch window).
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
- FULLSCREEN GAMES: share the whole monitor. Monitor shares detect
  exclusive-fullscreen games automatically and switch capture method
  as needed. The cursor is captured either way.
  Pick the game in the Audio list so the monitor share has sound.
- The Audio dropdown chooses whose sound the stream carries: the
  captured window, any other running app, or nothing.
- "Watch streams" in the app opens the built-in viewer (same grid as
  the browser version, identical playback for everyone), including a
  "Find streams" panel that lists live streams from other StreamHost
  machines on your Tailscale network for one-click adding.
- Add ?stats=1 to a stream link for a diagnostics overlay.
- Weak upload? Pick a 30 fps preset.
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
  connected on one end. Click "Fix access" in the app (or run setup.bat
  as admin) and try again.
- Status shows "THIS PC ONLY": use "Fix access" (or setup.bat) for that
  port, restart the stream.
- Page loads but video never starts: same as above, the port that
  viewers' browsers connect to isn't reachable yet. Re-run "Fix access"
  or setup.bat.
- "Port ... is already in use": another program (often another copy of
  StreamHost still running) has that port. Close it, or set a different
  port in the app.
- Everything looks fine on your end but nobody can connect: firewall
  state can drift (a Windows reset, another program, a network profile
  change). Run "Fix access" again. It configures one port at a time, so
  after changing the port, run it again for the new one.
