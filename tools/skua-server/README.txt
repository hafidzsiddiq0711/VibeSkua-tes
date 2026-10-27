Skua Server (Windows)
=====================

Runs your AQW bots on this PC/VPS without game windows (headless), controlled from
Skua Mobile on your phone. No RDP needed after setup.

Setup (once)
1. Unzip this folder somewhere permanent, e.g. C:\SkuaServer.
2. Install Tailscale on this PC and on the phone (https://tailscale.com/download),
   and log in with the same account on both. Note this PC's Tailscale IP (100.x.y.z).
3. Right-click install-autostart.bat -> Run as administrator.
   (Starts the server at every login and opens port 6100 in the Windows firewall.)
   Or just double-click start-skua-server.bat to run it once.
4. The server window shows a TOKEN. In Skua Mobile: menu (...) -> Servers -> Add,
   enter the Tailscale IP, port 6100 and the token.
5. Edit an account -> "Runs on" -> this server. Press Play.

Notes
- Scripts, options and CoreBots settings are shared with VibeSkua on this PC
  (%APPDATA%\VibeSkua). Use "Update scripts" from the phone or VibeSkua as usual.
- Logs: the logs folder next to this file (one file per bot).
- Settings: server.json (port, max bots, token). Delete the token line to make a new one.
- Keep the token private: with it, anyone who can reach this port controls the bots.
  Over Tailscale only your own devices can reach it.
