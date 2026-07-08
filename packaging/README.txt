STREAMHOST
==========

Stream a game or monitor to friends' browsers over a private network.
Voice chat stays in your voice app; StreamHost only captures and sends
the audio of the application you select.

SETUP (once, ~2 minutes)
------------------------
1. Right-click setup.bat -> Run as administrator.
   This reserves the stream port and adds a firewall rule limited to
   Tailscale and private LAN address ranges.
2. Install Tailscale (tailscale.com/download) and accept the invite you
   received by email.

STREAMING
---------
1. Double-click StreamHost.exe.
2. Pick your game from the list, pick a quality preset, Start streaming.
3. Copy link -> paste it to your friends. They open it in any browser.
   Click the video for sound; hover for volume and fullscreen.

The window minimizes to the tray and keeps streaming. The app waits for
the first captured frame before reporting the stream as live — if it
stops with a capture error instead, use "Copy log" and send that along.

WATCHING MULTIPLE STREAMS — THE GRID
-------------------------------------
Anyone streaming also serves a grid page ("Copy grid link" in the app,
or add "grid" to a stream link). Open it, paste each friend's stream
link once — every live stream tiles into one tab. Reorder tiles with
the arrow buttons; the list is remembered by your browser.

TIPS
----
- FULLSCREEN GAMES: share the whole monitor. Monitor shares detect
  exclusive-fullscreen games automatically and switch capture method
  as needed (the cursor disappears from the stream in that mode).
  Pick the game in the Audio list so the monitor share has sound.
- The Audio dropdown chooses whose sound the stream carries: the
  captured window, any other running app, or nothing.
- "Watch streams" in the app opens the built-in viewer (same grid as
  the browser version, identical playback for everyone).
- Add ?stats=1 to a stream link for a diagnostics overlay.
- Weak upload? Pick a 30 fps preset.
- The app remembers your source/preset/audio/port between runs.
- CUSTOM PORT (optional): setup.bat covers port 8093. For another port,
  open Command Prompt in this folder and run:  setup.bat 8094
  (that's the same setup.bat, given the port as an argument — not a
  separate file). Then set the same port in the app. If you use
  start-stream.bat, edit the PORT line there to match.
- Streaming at the same time as a friend is fine, whatever the ports —
  ports only matter per PC.

IF SOMETHING BREAKS
-------------------
Every run writes a log file (path is printed at the top of the app's
log panel; tray icon -> Open logs folder). "Copy log" puts the current
session's log on the clipboard — paste that when reporting a problem.

- Friends' page never loads: setup.bat not run as admin, or Tailscale
  not connected on one end.
- Status shows "THIS PC ONLY": run setup.bat for that port, restart
  the stream.
- Page loads but video never starts: firewall — re-run setup.bat.
