# Changelog

## 0.9.0 (beta)

The first public version. A beta: everything described works and was tested, but not yet on many different computers.

**What it does**
- A tray traffic light for every Claude Code session, in the console and in VS Code: yellow working, blue compacting, red waiting for you, green done.
- A tray menu with all sessions, named after the chat title: bring a session's window to the front, mute one session, pause everything on this computer for an hour, the rest of the day or until resumed.
- Windows notifications when Claude waits for you or finishes; the "waiting" one has an **Answer** button.
- Permission prompts and questions with answer options can be answered without the Claude Code window: from a panel near the tray, from the keyboard (Win+Alt+C), or from the phone (experimental, off by default).
- Pushes to the phone through ntfy (ntfy.sh or your own server), only when you are probably away: no reaction on the computer, or a locked screen. A turn that only waits for Claude's own background task (a build, tests) is not reported as finished.

**The program**
- One portable .exe: no installer, no administrator rights, no telemetry.
- 7 languages; light, dark and system theme; Windows high contrast; sharp on monitors with different display scales.
- `--selfcheck` photographs every window and writes a report, to check the look on a computer without Claude Code.

**Download**
`ClaudeCodeTrafficLight.exe` below; check it against `SHA256SUMS.txt`. The file is built by GitHub Actions from this tag and is byte-identical to the author's local build. It is not signed yet, so Windows SmartScreen may warn: **More info → Run anyway**.
