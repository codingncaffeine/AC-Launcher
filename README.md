# AC Launcher

![AC Launcher for Linux](docs/banner.jpg)

A native Linux launcher for Asheron's Call on emulator servers (ACE and GDLE).

AC Launcher keeps your accounts and servers in one place and starts one or many game clients with a single
click. The Windows game client runs through [umu-launcher](https://github.com/Open-Wine-Components/umu-launcher),
which runs Proton without Steam, so no Steam install is needed.

## Features

- **Accounts** — save any number of accounts and tick the servers each one should launch on.
- **Multi-launch** — press Launch (or F5) to start every ticked account on every ticked server, one after another
  with a configurable pause; each running client can be stopped on its own.
- **Server lists** — the community-published server lists are downloaded and cached, so the launcher still works
  offline. Add your own servers, copy a published one to customise it, or hide the ones you never use.
- **Server status** — each server is checked in the background and shown as up or down, with its response time.
- **Proton without Steam** — pick any version of GE-Proton or UMU-Proton (Valve's Proton), from the latest back to
  older releases, or follow the latest automatically. Downloads are checked against their published SHA-512 and
  kept in the launcher's own folder; Proton builds already on the computer can be used too. The launcher keeps
  its own Wine prefix.
- **Windowed start** — optionally sets the client to start in a window, which avoids the fullscreen DirectX error
  many systems hit on first launch.
- **Log window** — everything the launcher, umu and Proton report, in one place.

## Requirements

- Linux (x86-64) with a Vulkan-capable graphics driver.
- An installed Asheron's Call client: the folder that holds `acclient.exe` and the `.dat` files.
- umu-launcher. If it is not installed (for example `pacman -S umu-launcher` on Arch), AC Launcher downloads umu's
  self-contained release on first launch; that release needs `python3`.

The first launch downloads the Steam Linux Runtime and Proton, about 1 GB, once.

## Installing

Downloads are on the [releases page](https://github.com/codingncaffeine/AC-Launcher/releases).

- **Debian / Ubuntu** — `sudo apt install ./ac-launcher_<version>_amd64.deb`
- **Arch Linux** — install `ac-launcher-bin` from the AUR, e.g. `yay -S ac-launcher-bin`
- **Any distribution** — extract `ac-launcher-<version>-linux-x64.tar.gz` and run `ACLauncher` inside it

## Getting started

1. Start AC Launcher and choose your **Asheron's Call folder** at the bottom of the window.
2. **Add account**, then expand it and tick the servers it should play on.
3. Press **Launch**.

## Where things are kept

| What | Where |
| --- | --- |
| Settings, accounts, your servers | `~/.config/ac-launcher/` (`accounts.json` is readable only by you) |
| Wine prefix, Proton versions, umu copy | `~/.local/share/ac-launcher/` |
| Cached server lists | `~/.cache/ac-launcher/` |
| Log | `~/.local/state/ac-launcher/logs/` |

The client stores its own settings in the prefix, under
`drive_c/users/steamuser/Documents/Asheron's Call/UserPreferences.ini`.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build ACLauncher.slnx -c Release
./src/ACLauncher/bin/Release/net10.0/ACLauncher
```

Run the tests with `dotnet run --project tests/ACLauncher.Tests -c Release`.

## Acknowledgements

Inspired by ThwargLauncher, the Windows launcher for Asheron's Call emulator servers.
