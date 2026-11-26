# Privacy

ShinyForge runs on your computer. It has no account, no analytics, no telemetry and no advertising.

**This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it.**

Below is everything ShinyForge connects to, and why.

## Things you turn on or start yourself

| What | Connects to | What is sent |
| --- | --- | --- |
| Your consoles (hunting, save export, HOME tools) | The Switch / 3DS IP address you enter, or the controller board on a USB port | Commands for the console. Nothing leaves your network. |
| Notifications (Settings → Notifications) | The Discord webhook and/or [ntfy](https://ntfy.sh) server you enter | The alert text (for example "Shiny found: Pikachu"). Off until you enter an address. |
| Stream overlay / phone remote (Settings) | A small web server on your PC that phones on your own network can open (PIN protected) | Hunt statistics and a picture of the game. Off until you turn it on. |
| Error reports | GitHub, in your web browser | Only when you click to report a problem. The report opens as a new GitHub issue that you can read and edit before you send it. Your user name, PC name, folders, IP and e-mail addresses are removed from it first. |
| Installing tools ShinyForge needs | The tool's official download page (for example FFmpeg, Python, PKHeX, sys-botbase, 3DS Capture, Streamlabs) | A normal download request. Only when you click to install that tool. |
| Pokémon pictures and data | GitHub (PokeAPI sprites), NuGet (PKHeX.Core) | A normal download request, when you open a feature that needs them. |

## Automatic

| What | Connects to | What is sent |
| --- | --- | --- |
| Update check (at start and about once an hour) | GitHub (ShinyForge's releases and those of the tools it uses), NuGet, and Windows Package Manager (winget) for installed tools | Normal requests for the latest versions. No personal data. Updates are only installed when you agree. Turn it off in Settings → Updates → "Check for updates at start and every hour". |

DexTracker runs on a server on your own PC (`127.0.0.1`) and is not reachable from other computers.

## Third parties

The services above have their own privacy policies: [GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement),
[Discord](https://discord.com/privacy), [ntfy](https://ntfy.sh/docs/privacy/), and the sites of the tools you choose to install.
