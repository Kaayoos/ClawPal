using System.Text.Json;

namespace ClawPal.Companion;

// Shared wire shape with Codex, but independent provider state. Only documented
// Claude Code status-line fields and lifecycle events enter this contract.
internal sealed record ClaudeSession(string Id, string Title, string Project, string State,
    DateTimeOffset Seen, DateTimeOffset ActivityAt, int ProcessId, long ProcessStarted,
    bool Ended, CodexWindow Primary, CodexWindow Secondary, DateTimeOffset UsageAt);

internal static class ClaudeContract
{
    internal static ClaudeSession? Observe(JsonElement root, bool statusLine, ClaudeSession? prior,
        DateTimeOffset now, int processId = 0, long processStarted = 0)
    {
        var id=CodexContract.String(root,"session_id");
        if(id is null || id.Length>128 || !id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))return null;
        if(prior?.Id!=id)prior=null;
        string kind=statusLine?"statusline":CodexContract.String(root,"hook_event_name")??"";
        string? state=kind switch {
            "SessionStart"=>"recent", "UserPromptSubmit" or "PreToolUse" or "PostToolUse"=>"working",
            "PermissionRequest"=>"waiting", "Stop"=>"completed", "StopFailure" or "SessionEnd"=>"interrupted",
            "Notification" when CodexContract.String(root,"notification_type") is "permission_prompt" or "elicitation_dialog"=>"waiting",
            "Notification" when CodexContract.String(root,"notification_type")=="idle_prompt"=>"completed",
            "statusline"=>prior?.State??"recent", _=>null };
        if(state is null)return null;
        var title=MusicContract.LimitText(CodexContract.String(root,"session_name")??prior?.Title??"",63);
        var cwd=CodexContract.String(root,"cwd")??"";
        if(CodexContract.Object(root,"workspace",out var workspace))cwd=CodexContract.String(workspace,"project_dir")??cwd;
        string project=MusicContract.LimitText(cwd.Replace('\\','/').TrimEnd('/').Split('/').LastOrDefault()??"",31);
        if(project.Length==0)project=prior?.Project??"";
        CodexWindow primary=prior?.Primary??CodexWindow.Empty,secondary=prior?.Secondary??CodexWindow.Empty;
        var usageAt=prior?.UsageAt??DateTimeOffset.UnixEpoch;
        if(statusLine) {
            primary=secondary=CodexWindow.Empty;
            if(CodexContract.Object(root,"rate_limits",out var limits)) {
                primary=Window(limits,"five_hour",300,now);secondary=Window(limits,"seven_day",10080,now);
            }
            usageAt=now;
        }
        return new(id,title,project,state,now,statusLine?prior?.ActivityAt??now:now,
            processId>0?processId:prior?.ProcessId??0, processId>0?processStarted:prior?.ProcessStarted??0,
            kind=="SessionEnd",primary,secondary,usageAt);
    }
    internal static CodexWindow Window(JsonElement root,string name,int minutes,DateTimeOffset now)
    {
        if(!CodexContract.Object(root,name,out var window) || !window.TryGetProperty("used_percentage",out var used) ||
            used.ValueKind!=JsonValueKind.Number || !used.TryGetDouble(out double value) || !double.IsFinite(value) || value<0 || value>100 ||
            !window.TryGetProperty("resets_at",out var reset) || reset.ValueKind!=JsonValueKind.Number || !reset.TryGetInt64(out long at) ||
            at<=now.ToUnixTimeSeconds() || at>uint.MaxValue)return CodexWindow.Empty;
        return new(true,Math.Round(100-value,1),minutes,"",at);
    }
    internal static CodexStats Snapshot(IEnumerable<ClaudeSession> source,DateTimeOffset now,Func<ClaudeSession,bool> alive)
    {
        var sessions=source.Where(s=>s.Seen<=now.AddMinutes(1)&&s.Seen>now.AddHours(-24)).OrderByDescending(s=>s.Seen).Take(32).ToArray();
        var live=sessions.Where(s=>!s.Ended&&alive(s)).ToArray();
        bool running=live.Length>0;
        // An active session without quota data must never borrow a different
        // account's old reading. Closed sessions retain an explicitly cached view.
        var usage=(running?live:sessions).FirstOrDefault();
        var first=usage?.Primary??CodexWindow.Empty;var second=usage?.Secondary??CodexWindow.Empty;
        if(first.ResetsAt<=now.ToUnixTimeSeconds())first=CodexWindow.Empty;
        if(second.ResetsAt<=now.ToUnixTimeSeconds())second=CodexWindow.Empty;
        bool available=first.Available||second.Available;
        long age=usage is null?0:ActivityContract.Age(usage.UsageAt,now);
        var items=sessions.OrderByDescending(s=>!s.Ended&&alive(s)).ThenByDescending(s=>s.ActivityAt).Select(s=>{
            long a=ActivityContract.Age(s.ActivityAt,now);
            string state=s.State;
            if(state is "working" or "waiting" && (a>120 || s.Ended || !alive(s)))state="unknown";
            return new CodexActivityItem(s.Title,s.Project,state,a);
        }).ToArray();
        int working=items.Count(i=>i.State=="working");
        var activity=new CodexActivityStats(sessions.Length>0,!running,running?"local":"closed",0,working,sessions.Length,items.Take(3).ToArray());
        return new(running,available,available&&(!running||age>120),available?"ready":running?"no_limits":"closed","",age,first,second,activity);
    }
}
