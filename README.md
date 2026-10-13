# LanPad — play local-multiplayer games on different PCs over LAN

Games like **NBA 2K14** only have local multiplayer: keyboard = player 1, each Xbox controller = one more player.
LanPad lets players sit at **different PCs**:

- **Host PC** runs the game. Every joined PC's keyboard becomes a **virtual Xbox 360 controller** on the host.
- **Join PCs** see and hear the host's screen, full screen, streamed over the LAN.
- Up to **5 players**: the host's own keyboard + 4 joined PCs (Windows allows max 4 Xbox controllers).
- Works offline on the LAN. No accounts, no internet, nothing saved per PC (fine for diskless CCBoot).

## One-time setup (CCBoot image)

1. Boot one PC in **super-client** (image write) mode.
2. Copy the `LanPad` package folder to it and run, **as Administrator**:
   `powershell -ExecutionPolicy Bypass -File install-image.ps1`
   - copies LanPad to `C:\LanPad`
   - installs the signed **ViGEmBus** driver (virtual Xbox controllers)
   - adds Windows Firewall rules (UDP in for LanPad, mpv, ffplay)
   - puts a **LanPad** shortcut on the desktop and runs the self-test
3. Save the image. Every PC can now host or join.

## How to play

**Host (the PC that runs the game)**
1. Open LanPad → **HOST GAME**. A **room PIN** appears — tell the other players.
2. Pick the **Game** from the list (✔ = found on this PC; 106 games built in). This sets:
   - the **controls** every joined PC gets (Sports / Fighting / Action / Racing — sent automatically),
   - how many PCs may join (e.g. a 2-player fighting game lets 1 PC join, because the host plays too),
   - a tip for that game (which menu has local versus / split-screen).
3. Click **Launch game**. Not found? Click **Browse exe...** once — LanPad remembers it (`games.user.ini`).
   In the game set the display to **windowed / borderless 1920x1080** (fullscreen can break the stream).
4. When players join they show up in the list. In the game's controller / versus screen each joined player is
   one Xbox controller. The host plays with this PC's keyboard.
   You can switch the game while players are connected; their controls change right away.

**Join (every other player)**
1. Open LanPad → **JOIN GAME**, type the **room PIN**, double-click the host in the list
   (or type the host's IP).
2. The host's screen opens full screen and your keyboard is now a controller (**Pad mode ON**).
   A **key cheat sheet** sits in a corner on top of the game: which key is which Xbox button, and Pad mode on/off
   (clicks go through it; turn it off or move it in Settings).
   - **F12** = Pad mode on/off (off = keyboard works normally)
   - **Shift + F12** = hide / show the key cheat sheet
   - **Ctrl + F12** = leave the game

**Controls — the host's game picks the layout (all: WASD = move, arrows = right stick, Enter/Backspace = Start/Back, 1-4 = D-pad)**

| Layout | Used by | Buttons |
|---|---|---|
| Sports | NBA 2K, FIFA/FC, PES, WWE, emulators | K L J I = A B X Y, Q E = LB RB, Space = LT, Left Shift = RT (sprint) |
| Fighting | Tekken, MK, Street Fighter, DBFZ, Naruto, One Piece… | U I O P = X Y RB LB, J K L ; = A B RT LT (Tekken: U I J K = 1 2 3 4) |
| Action | co-op, platformers, party, beat 'em ups | Space = A (jump), J = X, K = B, I = Y, Q E = LB RB, U O = LT RT |
| Racing | Rocket League, Sonic Racing, F1, DiRT | W = gas, S = brake, A/D = steer, Space = A, J K I = X B Y |

The full game list is in `games.ini` (and `LanPad Supported Games.txt`).
Change keys for one game, or add your own game, in `games.user.ini` (same format, kept on updates), e.g.
```
[Tekken 7]
A=Space

[My Game]
Players=4
Layout=Action
Exe=D:\Games\My Game\game.exe
```

## Settings (`C:\LanPad\lanpad.ini`)

Most players only need the **⚙ Settings** button on the LanPad start screen: stream quality
(frame rate, bitrate, encoder), the smooth-gameplay tweaks and the join options. **Save** writes them to `lanpad.ini`.

**⌨ Controls** (start screen, or **Edit keys** while hosting) sets the keys: pick a layout (Sports, Fighting, Action,
Racing), click a button, press the key. Esc = cancel, right-click = no key. The Pad mode key (F12) can be changed there too.
- On the **host**, Save sends the new keys to every joined player right away.
- On a **joined PC**, turn on **Use my own keys when I join** to always play with this PC's keys instead of the host's.

**Smooth gameplay tweaks** (`[Performance]`, all on by default, all undone when you stop hosting / leave):

| Setting | What it does |
|---|---|
| `HighPriority=true` | LanPad runs at High priority (keyboard hook, input), ffmpeg and mpv above normal, input threads in the Windows "Games" class. |
| `NoThrottling=true` | Windows 11 slows down hidden / background programs; ffmpeg, the sound player and LanPad behind the game opt out. Also a 1 ms Windows timer. |
| `KeepAwake=true` | No sleep, screen saver or screen off during a game. |
| `InputResend=2` | Each controller change is sent 2 more times, 10 ms apart, so a lost packet costs 10 ms instead of a dropped press (0-4; raise it on Wi-Fi). |

Other settings:

- `RoomPin=random` (new PIN each time), `off`, or a fixed number like `1234`
- `Game=NBA 2K14` (last picked game), `GameFolders=G:\Offline Games;D:\Games;...` (where games are searched)
- `HostPlays=true` (the host keyboard is a player; `false` = all players join from other PCs), `MaxPlayers=4`
- `BitrateKbps=15000`, `Fps=60`, `Encoder=auto|amf|x264`
- `[Keys]` — the Sports layout, any key per button, several keys with commas: `A=K,NumPad2`
- `[Keys.Fighting]`, `[Keys.Action]`, `[Keys.Racing]` — your changes to the built-in layouts (written by the Controls page)
- `ToggleKey=F12`, `AutoPadMode=true`, `Fullscreen=true`, `Viewer=mpv|ffplay`, `OwnKeys=false`
- `Overlay=true` (key cheat sheet on top of the game), `OverlayCorner=TopRight|TopLeft|BottomRight|BottomLeft`, `OverlayOpacity=85`

## How it works

```
JOIN PC: keyboard -> LanPad (hook) -> UDP 47800 -> HOST: LanPad -> ViGEmBus virtual Xbox pad -> game
HOST:    screen (ddagrab) + sound (WASAPI loopback) -> ffmpeg (AMD AMF H.264) -> UDP multicast
         239.255.77.<host IP last number> : 5004 video, 5006 sound -> mpv on every JOIN PC
```

- Controller input on the LAN: under 1 ms (measured on this PC's loopback).
- Screen delay measured on a 32 Hz test display: ~0.12–0.17 s. Expect less on 60 Hz monitors.
- If a join PC stops sending (crash, cable), its buttons are released after 1 s and the slot is freed after 10 s.

## Troubleshooting

| Problem | Fix |
|---|---|
| "ViGEmBus driver is not installed" | Run `install-image.ps1` as admin (in super-client mode). |
| Join PC shows no picture | Firewall rules missing (run the installer) or the switch blocks multicast. Try `Viewer=ffplay`. |
| Picture is laggy / blocky | Settings → lower the bitrate (e.g. 8 Mbps), then the frame rate, or set the game to 1280x720. |
| Buttons sometimes missed (Wi-Fi) | Settings → Controller packet repeat → 3 or 4 extra copies. |
| Game doesn't see the controllers | Start hosting **before** starting the game, then check the game's controller screen. |
| Keys stuck / typing doesn't work | Press **F12** to turn Pad mode off. |
| Host list is empty | Type the host IP instead; check both PCs are on the same LAN. |

Logs: `%TEMP%\LanPad.log`, `%TEMP%\LanPad-ffmpeg.log`, `%TEMP%\LanPad-mpv.log`.
Self-test: `LanPad.exe --selftest` (writes `selftest.log`). Stream delay test: `LanPad.exe --streamtest`.

## Build (developers)

Open the folder in **VS Code** and press **Ctrl+Shift+B** (needs the .NET 8 SDK), or:
`powershell -File scripts\package.ps1` → ready package in `dist\LanPad`.
The app icon `src\LanPad\LanPad.ico` is drawn by `scripts\make-icon.ps1` (run it again after changing the design).
Third-party tools (downloaded from official sources, checksums verified): FFmpeg 9.0.2 (gyan.dev),
mpv 2026-10-13 (shinchiro build), ViGEmBus 1.22.0 (signed by Nefarius Software Solutions).

## Uninstall the driver

Settings → Apps → **ViGEm Bus Driver** → Uninstall.
