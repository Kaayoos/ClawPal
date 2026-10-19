# Claude Code integration

ClawPal supports native Windows Claude Code CLI and local Code-tab sessions in
Claude Desktop, including its Microsoft Store installation. WSL and remote
sessions are not currently supported.

## Live account limits

In Companion settings, enable **Live Claude limits** and save. This is off by
default, including on upgrades. The setting explains that ClawPal uses your
existing Claude sign-in to request subscription limits from Anthropic. It works
with each Windows user's own files; no developer account or API key is bundled.

The reader makes a read-only GET to
`https://api.anthropic.com/api/oauth/usage`. This is an undocumented endpoint
also used by Claude Desktop, so future Anthropic changes can require an update
to ClawPal. It reports five-hour and weekly utilization and reset timestamps.
It sends no model prompts and does not estimate plan limits from token counts.

During active sessions, requests are at least one minute apart. With no active
hook sessions, the interval is five minutes. HTTP failures back off; a 429 obeys
`Retry-After`, or waits five minutes when no delay is provided. Network calls
time out after twelve seconds and responses are capped at 64 KiB.

Desktop detection covers `%APPDATA%/Claude` and the Store's per-user
`Packages/Claude_*/LocalCache/Roaming/Claude` folder. ClawPal reads the current
account marker and matches the active session's organization. The encrypted
Desktop token cache is decoded in memory through the same user's Windows DPAPI
key and authenticated AES-GCM. V2 caches are preferred; legacy account-scoped
caches are supported. Ambiguous organizations fail closed. The native CLI uses
its `.credentials.json`, respecting `CLAUDE_CONFIG_DIR` when set in ClawPal's
environment. An active CLI session never borrows a Desktop account's quota.

Only the access token is used, only for that fixed HTTPS endpoint. Redirects are
disabled. ClawPal never saves the token, logs it, includes it in diagnostics,
or sends it to the display. It does not read browser cookies, change Claude's
credential files, or refresh tokens. Claude manages login and renewal; after a
401/403 ClawPal waits for Claude to replace the rejected token.

Successful readings include their actual age. A failed refresh preserves the
last reading as cached for at most thirty minutes and never refreshes its
timestamp. Each window also expires at its own reset time. Changing accounts
clears the prior live reading. Newer CLI status-line data takes precedence over
older cached readings.

## Session activity and local fallback

Click **Connect / repair Claude Code** to install session hooks and a status-line
wrapper. Existing hooks and status-line output are preserved. The CLI supplies
documented `rate_limits.five_hour` and `rate_limits.seven_day` fields after its
first API response on supported plans. See the official
[status-line documentation](https://code.claude.com/docs/en/statusline) and
[hooks documentation](https://code.claude.com/docs/en/hooks).

Desktop does not supply those CLI status-line readings. When live access is off
or unavailable, its local `plan-usage-history.json` is a fallback. ClawPal matches
schema 2 history to the active account and organization, marks it cached, and
expires it after thirty minutes. Desktop can leave that file unchanged for long
periods. Reset times are absent and are never invented. A stale Desktop history
sample cannot overwrite newer limits from the same session's status line.

Hook snapshots keep only a local session ID, session name, project basename,
lifecycle state, process identity, timestamps, and quota windows. Prompts,
transcripts, and tool arguments are discarded. The paired display receives at
most three activity rows, counts, remaining percentages, and reset times. Working
and waiting states expire after two minutes without a matching event. Closed or
crashed processes cannot remain live because process creation time is checked.

Project settings, managed policy, disabled hooks, or a different configuration
directory can override the user-level bridge. Restart existing sessions after
connecting if activity does not appear. Enabling the Claude app alone does not
install hooks or enable live account access.

## Disconnecting

Turn off **Live Claude limits** and save to stop authenticated requests while
keeping local activity. Disabling the Claude app stops all its collection and
transmission. **Disconnect Claude Code**, or `--disconnect-claude`, also disables
live requests, removes only ClawPal's hooks, and restores the original status
line. Other settings remain intact. Disconnect before deleting ClawPal's private
directory so Claude does not keep calling a missing executable.

The prior status-line configuration and first settings backup are kept under
`%USERPROFILE%/.clawpal/claude`; do not publish that directory. The bridge emits
no decisions or model context and cannot approve Claude tools.

The integration retains app ID 7 and the existing bounded `claude` telemetry
shape. The live-reader fix works with existing Claude-capable firmware; firmware
0.9.1 also includes the updated Claude logo and help text.
