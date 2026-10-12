using System.Globalization;
using System.Text;

namespace ClawPal.Companion;

public sealed record GameStats(bool Active, string Name, double? Fps, string Status)
{
    public static readonly GameStats Idle = new(false, "", null, "idle");
}

public static class GameContract
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "chatgpt", "codex", "spotify", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        "msedgewebview2", "discord", "teams", "slack", "wallpaper32", "wallpaper64",
        "explorer", "dwm", "applicationframehost", "searchhost", "startmenuexperiencehost",
        "steam", "steamwebhelper", "epicgameslauncher", "riotclientservices", "battle.net",
        "code", "devenv", "blender", "obs64", "resolve", "vlc", "mpv", "potplayer64",
        "photoshop", "afterfx", "adobe premiere pro", "unity", "unrealeditor", "godot",
        "buddyos.companion", "presentmon", "windowsterminal", "powershell", "pwsh"
    };
    public static bool IsExcluded(string process)
    {
        string name = Path.GetFileName(process);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return Excluded.Contains(name);
    }
    public static bool IsKnownGamePath(string path) => new[] { @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\xboxgames\", @"\gog galaxy\games\", @"\gog games\", @"\games\" }
        .Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase));
    public static bool ShouldCapture(string process, bool knownGame, bool fullScreen, double? gpu, bool alreadyActive) =>
        !IsExcluded(process) && (knownGame || (fullScreen && gpu is { } usage && double.IsFinite(usage) && usage >= (alreadyActive ? 35 : 70)));
}

/* A two-second average from one swap chain, never a sum of multiple windows. */
public sealed class PresentMonFrames
{
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<(double Time, double Ms)>> chains = new();
    private string[] columns = [];
    private double newest;
    private long received;
    public static string[] Csv(string line)
    {
        var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }
        if (quoted) return [];
        fields.Add(field.ToString()); return fields.ToArray();
    }
    public void Add(string line, int targetPid, long receivedMs)
    {
        if (line.Length > 4096) return;
        var values = Csv(line);
        lock (gate)
        {
            if (values.Length > 1 && values[0] == "Application" && values[1] == "ProcessID") { columns = values; return; }
            if (columns.Length == 0 || values.Length != columns.Length) return;
            string Value(string name) { int i = Array.FindIndex(columns, c => c.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 ? values[i] : ""; }
            if (!int.TryParse(Value("ProcessID"), out int pid) || pid != targetPid ||
                !double.TryParse(Value("TimeInSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ||
                !double.TryParse(Value("msBetweenPresents"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ||
                !double.IsFinite(time) || time < 0 || !double.IsFinite(ms) || ms <= 0 || ms > 5000) return;
            string chain = Value("SwapChainAddress");
            if (chain.Length is 0 or > 32 || time < newest - 2) return;
            newest = Math.Max(newest, time); received = receivedMs;
            if (!chains.TryGetValue(chain, out var frames))
            {
                foreach (var key in chains.Where(p => p.Value.Count == 0 || p.Value.Last().Time < newest - 2).Select(p => p.Key).ToArray()) chains.Remove(key);
                if (chains.Count >= 16) return;
                chains[chain] = frames = new();
            }
            frames.Enqueue((time, ms));
            while (frames.Count > 4096 || (frames.Count > 0 && frames.Peek().Time < newest - 2)) frames.Dequeue();
        }
    }
    public double? Fps(long nowMs)
    {
        lock (gate)
        {
            if (nowMs - received > 3000 || nowMs < received) return null;
            var frames = chains.Values.Select(q => q.Where(f => f.Time >= newest - 2).ToArray()).OrderByDescending(q => q.Length).FirstOrDefault();
            if (frames is null || frames.Length < 5) return null;
            double duration = frames.Sum(f => f.Ms);
            double fps = 1000 * frames.Length / duration;
            return duration >= 100 && double.IsFinite(fps) && fps is > 0 and <= 2000 ? Math.Round(fps, 1) : null;
        }
    }
}
