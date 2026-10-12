# Claude Code bridge

The integration supports native Windows CLI and local Code-tab sessions in Claude
Desktop (including the Microsoft Store installation). The CLI uses documented
status-line `rate_limits.five_hour` and `rate_limits.seven_day` fields and session
hooks. See https://code.claude.com/docs/en/statusline and
https://code.claude.com/docs/en/hooks. Claude must supply subscription usage;
the first API response populates it. No private usage endpoint is polled.

Desktop does not supply this CLI status-line data. Its local
`plan-usage-history.json` provides periodically saved five-hour and weekly usage.
ClawPal reads schema 2, matches the active hook session to Desktop’s current
account and organization, and sends only remaining percentages and the reading’s
age. These values are always marked cached; they update when Desktop writes a
new sample, not after every token. History older than 30 minutes is unavailable.
Reset times are absent from this file and stay unavailable. Open Claude’s usage
panel or tray usage menu if Desktop has not refreshed its own reading.

This file format is observed implementation detail, not an Anthropic public API.
It was checked with Desktop 2.31226.1. Unknown formats fail closed. The adapter
reads bounded files every ten seconds, never changes Desktop files, never
decrypts login data, and does not make network or model requests. Only the
current-account marker and local session IDs are used from Desktop metadata;
other fields are discarded. They remain on the PC. An unrelated CLI session
cannot inherit Desktop’s account limits. When multiple sources are active, the
most recently reporting live session selects whether the Desktop adapter applies.

When Claude is enabled, the companion connects an existing Claude configuration
automatically. Companion settings also offer Connect / repair. Close and reopen
existing Claude sessions after first setup. Project-level settings, managed
policies or disabled hooks can override user-level configuration; those sessions
will not report data. CLAUDE_CONFIG_DIR is respected when present in the companion’s
environment. WSL, remote sessions and ordinary Desktop Chat activity are not handled by this native Windows bridge.

The bridge adds its own hook commands, preserves unrelated hooks and wraps the
previous status-line command through the same available Windows shell. The prior
status-line JSON and first settings backup are stored privately in
%USERPROFILE%/.clawpal/claude. Do not publish that directory. Existing commands
receive their original stdin; ClawPal does not log it. The bridge fails open and
emits no hook decisions, messages or model context. It cannot approve tools.

Only session identifier, session name, project folder basename, lifecycle state,
process identity, timestamps and the two quota windows are retained locally.
The paired display receives at most three session rows, counts, remaining limits
and reset times. The process identity and session identifier stay on the PC.
Working and waiting states expire after two minutes without a matching event;
closed or crashed sessions cannot remain live because process creation time is
checked. CLI quotas age into cached readings after two minutes and disappear when
their reset time passes. Desktop percentages follow the separate cache policy above. Local snapshots older than a day are excluded.

Disabling the Claude app stops collection and transmission. The lightweight hook
entry points remain installed so existing status-line output still works. To
remove the bridge completely, remove only commands containing --claude-event
from Claude settings.json and restore statusLine from previous-statusline.json
(remove the property if the saved value is null). Keep any unrelated edits.

Schema 6 adds stable app ID 7, keeping all earlier IDs and preferences. The
companion sends Claude data only to firmware 0.7.0 or newer. The bounded telemetry
budget becomes 6144 bytes; firmware shares the existing typed Codex limit parser.
