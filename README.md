# Claude Code Traffic Light

A tray traffic light for [Claude Code](https://claude.com/claude-code) on Windows. One small `.exe`, no installer.

**Beta (0.9).** Everything described here works and was tested, but not yet on many different computers (display scaling, screen readers, several monitors). Changes are listed in [CHANGELOG.md](CHANGELOG.md); problems and ideas are welcome in [Issues](https://github.com/BelalovBM/claude-code-traffic-light/issues) (the report of `--selfcheck`, see below, helps a lot with anything about the look).

| Colour | Meaning |
|---|---|
| 🟡 yellow | Claude Code is working |
| 🔵 blue | Claude Code is compacting the conversation (can take a few minutes); you get a pop-up when it starts and when it ends |
| 🔴 red | Claude Code is waiting for you (for example a permission prompt) |
| 🟢 green | the task is finished, ready for the next one |
| ⚪ grey | no active sessions |

It works with Claude Code in the console and in the VS Code extension, and shows every running session at once. The icon shows the most urgent state.


## Quick start

1. Download `ClaudeCodeTrafficLight.exe` from the [latest release](https://github.com/BelalovBM/claude-code-traffic-light/releases/latest) and run it. No administrator rights are needed.
2. Answer **Yes** to "Connect to Claude Code". The app adds hooks to `~/.claude/settings.json` (the original is backed up once as `settings.json.trafficlight.bak`).
3. Start Claude Code as usual. Sessions started before connecting need a restart.

Claude Code started inside WSL is not supported yet: it keeps its settings in the Linux home folder, out of reach of the hooks. If the program finds such an installation in a running WSL distribution, the **Claude Code** page of the settings says so.

Windows SmartScreen may warn about an unsigned file: choose **More info → Run anyway**. The source code is here, and the build is a single script.

## Features

- Several sessions at once: tray menu lists them with state and time, named after the chat title.
- **Show window**: brings the terminal or VS Code window of the chosen session to the front.
- Sound and pop-up when Claude waits for you or finishes; each can be switched off, globally or per session. The tray menu pauses everything on this computer for an hour, until the end of the day or until you resume it; the phone is not affected: it has its own switch.
- A keyboard shortcut (Win+Alt+C by default, changed or switched off on the **General** page) opens the waiting request with the keyboard focus in it, or the tray menu when nothing waits.
- When Claude ends its turn only to wait for a background task it started (a build, tests), the session stays yellow ("waiting for its background task") and nothing reports it as finished; if the task does not report back within 30 minutes (a server left running), the session counts as finished.
- Push to your phone through [ntfy](https://ntfy.sh), only when you are probably away (a notification on the computer that you did not react to, a panel left untouched for a minute, or a locked screen): after a permission prompt stays unanswered for N minutes, or when a task that ran at least N minutes finishes. A quick on/off switch is in the tray menu.
- 7 languages (English, Russian, Spanish, German, French, Portuguese, Chinese), light/dark/system theme, autostart.

## Phone notifications (ntfy)

1. Install the free ntfy app (Android / iOS).
2. In the tray app open **Settings → Phone**, enable ntfy, scan the QR code or subscribe to the shown topic manually.
3. Press **Send test**.

The topic name works like a password: anyone who knows it can read and send messages to it. A long random one is generated for you; keep it private. By default a push contains only the project folder name; the chat title and Claude's message text are sent only if you enable "Include Claude's message text". You can use your own ntfy server.

## Your own ntfy server (optional)

In **Settings → Phone → ntfy server** choose **My own ntfy server** and enter its address, for example `https://ntfy.example.org`. For a server with access control enter a user name and password, or an access token (it starts with `tk_`; leave the user empty). The secrets are stored encrypted, and they stay remembered when you switch back to the public ntfy.sh (which never receives them). The phone listens on one server only, so after a switch subscribe to the topic again in the ntfy app.

A minimal Docker setup with access control:

```
docker run -d --name ntfy -p 8080:80 -v ntfy-data:/var/cache/ntfy \
  -e NTFY_BASE_URL=http://localhost:8080 -e NTFY_CACHE_FILE=/var/cache/ntfy/cache.db \
  -e NTFY_AUTH_FILE=/var/cache/ntfy/user.db -e NTFY_AUTH_DEFAULT_ACCESS=deny-all \
  binwiederhier/ntfy serve
docker exec -it ntfy ntfy user add alice
docker exec ntfy ntfy access alice "ccl-*" read-write
docker exec ntfy ntfy access alice "ccr-*" read-write
docker exec ntfy ntfy access everyone "ccr-*" write-only
```

Topics created by this app start with `ccl-` (notifications) and `ccr-` (answers to permission prompts). The last rule lets the Allow/Deny buttons on the phone post an answer without signing in; only your own account can read those topics. Use `https` for a server that is reachable from the internet.

This was tested against the official ntfy image with a normal user, an access token and exactly these access rules (password and token modes, sending, and answering).

## Answer permission prompts and questions without the window (off by default)

When Claude Code asks permission to run a command or change a file, you can answer without going back to its window, in two ways. Both are off by default and are switched on in **Settings → Permission prompts**:

- **On this computer:** a small panel near the tray shows the request in full (a command, or a question with its answers) with **Allow** / **Deny** or one button per answer. It does not take the keyboard focus. **Later** closes it; the tray menu entry of that session opens it again. A typed answer ("Other") is not possible there: the panel offers to open the session window.
- **From the phone (experimental, needs ntfy):** the phone gets a push with the same buttons.

A question with answer options (a single question, one choice) works the same way: the options become the buttons or menu items (the phone shows at most three), and the chosen answer goes back to Claude Code. Questions of other shapes stay on the screen.

Think before enabling it:
- Whoever can tap **Allow** lets Claude Code act on your computer. Security rests on secret topic names on the ntfy server (they work like passwords), and the push contains the tool name and the command or file path. The ntfy server sees each request and your answer. Your own ntfy server with a password is safer than the public one.
- A fresh secret reply topic and a one-time token per request are used, so an old or guessed token does nothing. An answer applies once and never creates a permanent rule.
- Nothing is ever approved without a valid answer: on timeout, if you answer on the computer first, if the session is muted (for the phone) or the app is not running, Claude Code simply shows its normal prompt.
- The feature adds a `PermissionRequest` hook to Claude Code's settings only while it is switched on.

How it was checked: the whole cycle (request, push with buttons, tap, decision, wrong and replayed tokens, timeout, answer on the computer first) was tested against a local ntfy-compatible server. How the action buttons behave on your phone depends on the ntfy app, so use **Send test request** first.

## How it differs from Remote Control

Claude Code's own Remote Control lets you continue a session from claude.ai or the Claude app: you see the whole conversation and can type into it. This program does something smaller:

- It shows at a glance, in the tray, which of all your sessions works, waits or is done, without opening any of them.
- It tells you only when something needs you: a pop-up on the computer, and a push to the phone only when you did not react there.
- A permission prompt or a question with answer options can be answered with one tap, on the computer or on the phone, without the conversation.
- It needs no account features: the notifications go through ntfy, which can be your own server.

The two do not get in each other's way: use the traffic light to know when to look, and Remote Control when you need the whole session.

## How it works

Claude Code runs a hook on every event. The hook is this same `.exe` started with `--hook`; it reads the event and passes it to the running tray app through a local named pipe. If the tray app is not running, the hook exits silently and never disturbs Claude Code. Nothing leaves your computer except the optional ntfy push. No telemetry.

As a fallback, the app also reads the small per-process files Claude Code keeps in `~/.claude/sessions` (session id, folder, busy/idle). This lets it find sessions that were already running when it started, and notice that a task was interrupted (which fires no hook). That file format is not a documented interface and may change between Claude Code versions; if it does, only this fallback stops working, the hooks are unaffected.

## Uninstall

1. **Settings → Claude Code → Remove everything…** disconnects Claude Code (only this app's hooks are removed, other hooks stay), turns off autostart, removes the notification registration (the program shows its notifications under its own application id, a single key under `HKCU\Software\Classes\AppUserModelId`), deletes its settings, log and requests journal (they sit next to the .exe) and exits. The original `settings.json.trafficlight.bak` backup is kept.
2. Delete the `.exe` (its folder opens automatically).

To only disconnect from Claude Code and keep the program, use **Disconnect** on the same page.

## Privacy

The program collects nothing and sends nothing about you or how you use it: no telemetry, no update checks. It uses the network only when you set up the phone: then it sends notifications to the ntfy server you chose (ntfy.sh or your own) and listens there for your answers. What the notifications contain (the name of a session, the text of a request) is described on the Phone and Permission prompts pages. Everything else stays on the computer: the settings, the log and the requests journal sit next to the program and are removed by **Remove everything**.

## Code signing policy

Releases are built by GitHub Actions from the tagged source code (`.github/workflows/build.yml`); the build is deterministic, so anyone can build the same commit and get a byte-identical file. Signing of the release files through [SignPath Foundation](https://signpath.org) has been requested; until it is granted, the files are unsigned. Once it is: free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [BelalovBM](https://github.com/BelalovBM)
- Approvers (every signing request is approved by hand): [BelalovBM](https://github.com/BelalovBM)

Privacy: this program does not transfer any information to other networked systems unless the user sets it up to (the phone notifications through ntfy); see [Privacy](#privacy).

## Self-check

`ClaudeCodeTrafficLight.exe --selfcheck` shows the program with made-up sessions, takes a picture of every window (settings pages in both themes, the tray menu, the request panel, a notification) and writes `report.txt` with the Windows version, the scale of each monitor and the theme, all into a `selfcheck-<date>` folder next to the program. It needs no Claude Code and changes nothing on the computer. It takes about 40 seconds; leave the mouse and keyboard alone meanwhile. Pictures of the menu and the notification are taken from the screen, so a bit of the desktop around them may be in them.

## Build from source

Requires the .NET SDK only for building (the result runs on the .NET Framework 4.8 that ships with Windows):

```powershell
powershell -File build.ps1      # -> bin\ClaudeCodeTrafficLight.exe
powershell -File tools\make-icon.ps1   # regenerates src\app.ico
```

To add a language, copy `lang\en.txt` to `lang\<code>.txt` and translate it. A `lang` folder next to the `.exe` overrides the built-in texts.

## License

Copyright © 2026 BelalovBM. Licensed under the [GNU General Public License v3.0](LICENSE).

## Support the project

If you like the program and want to support the development of similar projects, you can donate. See [DONATE.md](DONATE.md).

TRON network (TRX, TRC-20 tokens such as USDT): `TUKeo9zL3YwwmBdh2pr2HfetANm32UtD4x`
