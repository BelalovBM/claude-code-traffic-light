# Changelog

## 0.9.9 (beta)

**New**
- **Use at API prices.** A block of its own under the limits shows the tokens Claude Code used today and in the last 30 days, and what they would cost at Anthropic's API prices: on a subscription, to see what the same work would cost by the token; with a sign-in by API key, which has no limits to show, close to the bill. Every kind of token at its own price (input, writing the cache for 5 minutes or an hour, reading it, the reply with its thinking), web searches and fast mode too. **Settings → Claude Code → Use at API prices** switches it off.
- **Statistics.** **Statistics…** in the tray menu opens a window with the use of today, 7 or 30 days: the total (on a subscription in the share of the weekly limit, with the dollars at API prices next to it), what it went on (reading the cache, writing it, the replies), what made it dearer (a context over 600k tokens, breaks longer than the cache lasts, each with what helps; the compactions), the sessions and the days.
- **Prices of the models in the settings**, with where each came from (the program, LiteLLM, you). They are loaded once a day from the open price file of LiteLLM on GitHub (one anonymous request; can be switched off) and at once with **Update prices from LiteLLM**; the card says when they were last loaded. LiteLLM goes first: a price typed in stays only for a model LiteLLM does not have.

**Fixed**
- How full the context is could show 100% at half the real size. Newer Claude Code prepares a compaction in the background and notes the size at which it started; that size was taken for the point where Claude Code compacts. Only compactions made at that point count now, and the limits learnt before are learnt again from the transcripts.
- A "finished" notice right after a prompt, when Claude Code put to use a compaction it had prepared in the background: the restart of the session that follows a compaction was taken for the end of the turn.
- Reading the cache on Sonnet 5.5 is priced at $0.10 per million tokens (it was counted at $0.20).
- The settings page stayed empty when the statistics window had been opened first.
- An indented check box in the settings could cut off its last line (in English, "Refine the limits by itself …").

## 0.9.8 (beta)

**Fixed**
- An idle session could show "compacting" for twenty minutes without compacting anything. Newer Claude Code prepares a compaction in the background and sends the same "compaction starts" event for it, but no "compaction done" until the summary is used. An automatic compaction announced while a session is idle is now taken for that preparation, and the session stays as it was; `/compact` and a compaction within a turn show as before.
- The background `/usage` took an answer without the 5-hour line (Claude Code leaves it out when Anthropic reports no 5-hour figure) for a failure, and three such answers in a row brought the notice "Could not refine the limits". The weekly figure alone now counts, an answer with no figures at all (Claude Code worked, Anthropic sent none this time) is tried again later and told only if it lasts six times in a row, and the whole answer goes to the log.

## 0.9.7 (beta)

**New**
- **Refine the limits by itself** (Settings → Notifications → Claude usage limits, off by default): the app runs Claude Code's own `/usage` hidden whenever the estimate may be more than 1% off (a rise of 10 percentage points, 2 from 90% on; a model without figures; an exact figure an hour old, a quarter of an hour from 90% on), so the limits in the menu stay close to exact. The threshold comes down by itself when the estimate starts to miss (a model, an effort or a size of context it was not learnt on) and goes back up as it gets right. A brake keeps it rare whatever happens: at least 5 minutes between runs, longer after failures, kept across restarts; a last limit of 300 a day, above what the brake lets through, with a notice if it is ever reached. It asks no model, uses no limit, is not saved as a session and runs no hooks; Claude Code asks Anthropic with its own sign-in. Claude Code is found by itself or chosen by hand. If it fails three times in a row, one notice says why.
- **One switch for the limits.** **Watch Claude's usage limits** turns all of it on or off; the push line, the notices and the background `/usage` are under it. "Ran out", "back" and "a new window" are one choice now. A sign-in without a subscription (an API key) has no such limits, and the app shows none.
- A notice when a used 5-hour or weekly window ends and a new one begins.
- Waiting notices say what Claude Code asks in the program's language: "Claude asks a question" with the question itself, instead of "Claude needs your permission to use AskUserQuestion".
- In the panel near the tray, "Also sent to the phone" stands out in bold and colour.

**Fixed**
- The usage limits after an early reset by Anthropic (the use dropped while the reset time stayed): the use before the reset no longer counts in the new window, which made the weekly estimate fall behind.
- A model used less than the main one (Sonnet next to Opus) kept nearly the main one's weight; it now gets its own from its figures, and the 5-hour estimate no longer runs ahead.
- After `/compact` the session's menu entry kept the context size from before the compaction until the next reply.
- The clock lamp could stay on for a session that had published a page on claude.ai: Claude Code lists its watch on that page as a background task that never ends. Tasks Claude Code itself lists as finished no longer count either.
- A stray frame line across the panel near the tray when it grew, and across a settings card when its **More** opened.
- In the settings, the labels of number fields wrapped onto two lines; they now take the row up to the field, which stands at the right. A page could get a horizontal scroll bar when its **More** opened; pages now scroll only up and down.
- The usage figures of the background `/usage` that repeat the last ones replace them instead of piling up, so the history the estimate learns from is not pushed out.

## 0.9.6 (beta)

**New**
- **New versions:** **Settings → General → Look for new versions** (off by default) looks once a day with one anonymous request to GitHub; a newer version gets an entry at the top of the tray menu and one notice. **About → Check for new versions** looks once, whether or not that is on. Nothing is downloaded or installed by itself.

**Fixed**
- The **About** page could need a scroll bar; the settings window now leaves room below the last card, and grows when a page still does not fit and the screen allows it.

## 0.9.5 (beta)

**Changed**
- Every panel near the tray, for a question as well as for a permission prompt, has **Open the session window** in the same place above **Later**.

## 0.9.4 (beta)

**Fixed**
- In the panel with a permission prompt, Allow and Deny touched each other and ended short of the full-width button below; they now have a gap and line up with it.

## 0.9.3 (beta)

**New**
- The panel with a permission prompt has **Open the session window**, to look at what led to the request before answering (a question already had it).

**Fixed**
- A background task that Claude stopped itself (TaskStop) kept the session on the clock lamp for half an hour after the work was done. Such a task now counts as ended, and where Claude Code reports its background tasks itself, that list is used.

## 0.9.2 (beta)

**New**
- **Usage limits.** How much of Claude's 5-hour and weekly limits is used, with the reset times: at the top of the tray menu, on the second line of the icon tooltip and at the end of every push. The exact figures are the ones Claude Code saves when you run `/usage`; in between, the use since is estimated from the session transcripts (marked `≈`), with each model weighed by what your own `/usage` figures on your plan showed (nothing about the size of the limits is built in). Optional notices, here and on the phone, every N percent, when a limit runs out and when it is back (**Settings → Notifications → Claude usage limits**). The program never contacts Anthropic and never touches your Claude sign-in.
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
