using System.Text;
using System.Text.RegularExpressions;

namespace ClawPal.Companion;

// esptool 4.x uses backspaces (not newlines) for read_flash progress. Decode
// terminal records as they arrive, including records split across pipe reads.
internal sealed class UsbToolOutput(Action<string> record)
{
    private readonly StringBuilder pending = new();
    internal void Feed(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is '\r' or '\n' or '\b') Flush();
            else if (!char.IsControl(c)) { pending.Append(c); if (pending.Length >= 4096) Flush(); }
        }
    }
    internal void Flush()
    {
        if (pending.Length == 0) return;
        string line = pending.ToString().Trim(); pending.Clear();
        if (line.Length > 0) record(line);
    }
    internal static int? Percent(string line)
    {
        var match = Regex.Match(line, @"(?<!\d)(\d{1,3})\s*%");
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) && value <= 100 ? value : null;
    }
    internal static string? ReadProgress(string line, long total, TimeSpan elapsed)
    {
        var match = Regex.Match(line, @"^(\d+)\s*\(\s*(\d+)\s*%\s*\)");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out long bytes) || bytes > total || total <= 0) return null;
        double remaining = bytes > 0 ? elapsed.TotalSeconds * (total - bytes) / bytes : 0;
        string eta = elapsed.TotalSeconds >= 8 && bytes > 0 && bytes < total ? L.Text("0_min_left", Math.Max(1, (int)Math.Ceiling(remaining / 60))) : "";
        return L.Text("backing_up_0_1_0_0_2_0_mb", bytes * 100 / total, bytes / 1048576d, total / 1048576d, elapsed, eta);
    }
}
