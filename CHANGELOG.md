# Changelog

## 0.9.0 (beta)

The first public version.

- Tray traffic light for every running Claude Code session (console and VS Code): working, compacting, waiting for you, done, no sessions.
- Tray menu with all sessions, their state and time, named after the chat title; brings the window of a session to the front; per-session mute; a pause of everything on this computer (for an hour, until the end of the day, or until resumed).
- A keyboard shortcut (Win+Alt+C by default) opens the waiting request with the keyboard focus in it, or the tray menu.
- The settings window is as large as its pages need; long explanations are folded under "More details"; the Phone page folds the connection steps away once a test message arrived.
- The Claude Code page says when Claude Code is also installed inside WSL, which the hooks cannot reach.
- Short notices (start, compaction) are Windows notifications that close by themselves; the tray icon no longer blinks.
- Switched-off options stay readable in the dark theme.
- The settings window is sharp on a second monitor with another display scale: it draws itself for that monitor instead of being stretched.
- `--selfcheck` photographs every window with made-up sessions and writes a report on the system and its monitors, to check the look on a computer without Claude Code.
- A test or self-check instance never touches the autostart, the notification registration or the keyboard shortcut of the installed program.
- A turn that ends only to wait for Claude's own background task is no longer reported as finished (no pop-up, no push).
- The phone page has an explicit choice between the public ntfy.sh and an own server; the own server's address and sign-in are kept while ntfy.sh is used, and never sent to it.
- Windows notifications when Claude waits for you, finishes, or starts and ends compacting; the "waiting" one has an **Answer** button.
- Answer permission prompts and questions with answer options without the Claude Code window: a panel near the tray (on the monitor with the icon), or the phone through ntfy (experimental, off by default).
- Phone pushes through ntfy (ntfy.sh or your own server) only when you are probably away: no reaction on the computer, or a locked screen.
- The switch for notifications on this computer no longer turns off the phone; the phone has its own switch.
- Settings are written through a temporary file with a `config.json.bak` backup; an unreadable `config.json` is restored from it.
- The log and the requests journal keep their newer half when they grow too large, instead of starting empty.
- 7 languages, light, dark and system theme, Windows high contrast, autostart, first-run wizard, "Remove everything".
