# Changelog

## 0.9.2 (beta)

**New**
- **Usage limits.** How much of Claude's 5-hour and weekly limits is used, with the reset times: at the top of the tray menu, on the second line of the icon tooltip and at the end of every push. The exact figures are the ones Claude Code saves when you run `/usage`; in between, the use since is estimated from the session transcripts (marked `≈`), with each model weighed by what your own `/usage` figures showed. Optional notices, here and on the phone, every N percent, when a limit runs out and when it is back (**Settings → Notifications → Claude usage limits**). The program never contacts Anthropic and never touches your Claude sign-in.
- **Waiting for a background task has its own lamp:** yellow with clock hands, so it is clear without the menu that Claude is not thinking but waiting for a build or tests, and goes on by itself. If that wait lasts 30 minutes (changeable, or off), one notice comes here and on the phone: a stuck task or a server left running no longer looks like work for ever.
- **Errors are no longer "finished".** A turn ended by an API error (a usage limit, overloaded servers, a lost sign-in) turns the session red with "stopped by an error", and a notice here and on the phone says what happened, with Claude's own words. This uses a new hook event: after updating, open **Settings → Claude Code** and click **Connect / repair** once (the program reminds you at start).
- **The context of each session:** how full it is, in the session's menu entry, and one warning on this computer at 90% of the point where Claude compacts it on its own (learnt per model from Claude Code's own compactions).
- **"Done" says what was done:** the first line of Claude's last message, in the notice here and, when chat details may be sent, on the phone.

**Fixed**
- A background task that ran longer than 30 minutes (a long benchmark or build) was reported as finished while it was still running. The session now stays in the waiting state for as long as the task's process runs.

## 0.9.1 (beta)

**New**
- **Allow and don't ask again.** When Claude Code offers a rule so it does not ask again (its own second answer, such as "Yes, allow npm test for this project"), the panel near the tray has that answer too, with the rule shown under it, and the phone gets a third button, **Always**. The rule is exactly the one Claude Code offered; nothing else is added.

**Fixed**
- After a permission prompt was answered, the light stayed red until the command finished: a long build or test run looked like a session waiting for you, and a "waiting" push could follow a minute later. The session is now yellow again right after the answer: from the panel, the phone, or the Claude Code window itself.

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
