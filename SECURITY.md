# Security

## Reporting a vulnerability

Please report security issues privately through GitHub:
**Security → Report a vulnerability** on the [AC Launcher repository](https://github.com/codingncaffeine/AC-Launcher/security/advisories/new).
Do not open a public issue for them.

## What AC Launcher protects, and how

**Passwords**
- Account passwords are kept in the desktop keyring (GNOME Keyring, KWallet, KeePassXC or anything else that
  implements the freedesktop Secret Service), through libsecret's `secret-tool`. Passwords reach `secret-tool` on
  standard input, never on a command line.
- When no keyring is available, passwords are kept in `~/.config/ac-launcher/accounts.json`, readable only by you.
  Passwords already in that file move into the keyring as soon as one is available.
- Passwords are masked in the launcher's own log, including anything umu-launcher, Proton or Wine print.

**Files**
- The launcher's folders (`~/.config/ac-launcher`, `~/.local/share/ac-launcher`, `~/.cache/ac-launcher`,
  `~/.local/state/ac-launcher`) are created readable only by you, and every file it writes is too. Folders and files
  left readable by an earlier version are tightened on start.
- Settings are written to a temporary file and renamed into place, so a crash never leaves a half-written file.

**Downloads**
- Server lists are only fetched over HTTPS. A list decides where your password is sent, so a list served over plain
  HTTP is never used.
- Proton builds are only installed when they can be checked against a published checksum: the release's own
  `.sha512sum` file, GitHub's SHA-256 digest of the file, or both when both exist. Releases that publish neither are
  shown but cannot be installed.
- umu-launcher's self-contained release is checked against GitHub's SHA-256 digest before it is unpacked.
- Server lists are parsed with document type declarations disabled and a size limit; responses that are read whole
  are capped.

**Links**
- Discord and website links, whether from a published list or typed in, are only opened if they are `http` or
  `https` links.

**Builds**
- NuGet lock files record every package and its content hash. CI and release builds restore in locked mode, which
  fails if a project's dependencies change without its lock file, or if a downloaded package's content differs from
  the recorded hash. Packages are audited for known vulnerabilities on every restore.
- The .NET security analyzers run on every build, with warnings treated as errors, and CI runs the tests, a
  vulnerable-package check and ShellCheck on every push.
- Release packages are checked by installing them into clean Ubuntu, Debian and Arch systems, with `lintian` and
  `namcap`.

## Known limitations

- **The game client takes the password on its command line.** That is how the Asheron's Call client accepts
  credentials (`-v <password>` for ACE servers, `-a user:password` for GDLE), so while the game runs, other users on
  the same computer can read the password from the process list. On a shared computer, mount `/proc` with
  `hidepid=2`, or use a password you do not use anywhere else.
- **`PROTON_LOG`** makes Proton write the game's full command line, including the password, to a log file in your
  home folder. The launcher warns when it is set.
- The game client and Proton run with your user's permissions; the launcher does not sandbox them.
