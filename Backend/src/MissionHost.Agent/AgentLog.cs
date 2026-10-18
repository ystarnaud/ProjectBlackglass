using System.Globalization;
using System.Text;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// Structured, one-line events on standard output (journald keeps them): "level=… event=… key=value …". Values are reduced to
/// a safe character set and clipped, so a value can never break a line or carry a payload. Callers pass identifiers, states,
/// counts and hash prefixes only: never a secret, a credential or result content (AgentSourceAuditTests checks the calls).
/// </summary>
public sealed class AgentLog(TextWriter output, TimeProvider? clock = null)
{
    readonly object gate = new();
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public static AgentLog Console { get; } = new(System.Console.Out);

    public void Info(string evt, params (string Key, object? Value)[] fields) => Write("info", evt, fields);
    public void Warn(string evt, params (string Key, object? Value)[] fields) => Write("warn", evt, fields);
    /// <summary>An operational alert (A10-D5, A10-D8): an operator should look.</summary>
    public void Alert(string evt, params (string Key, object? Value)[] fields) => Write("alert", evt, fields);

    void Write(string level, string evt, (string Key, object? Value)[] fields)
    {
        var line = new StringBuilder(160);
        line.Append("ts=").Append(clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        line.Append(" level=").Append(level).Append(" event=").Append(Safe(evt));
        foreach (var (key, value) in fields) line.Append(' ').Append(Safe(key)).Append('=').Append(Safe(Format(value)));
        lock (gate) output.WriteLine(line.ToString());
    }

    static string Format(object? value) => value switch
    {
        null => "-",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };

    static string Safe(string s)
    {
        var clipped = s.Length > 128 ? s[..128] : s;
        var b = new StringBuilder(clipped.Length);
        foreach (var c in clipped) b.Append(char.IsAsciiLetterOrDigit(c) || "._:@/+-,".Contains(c) ? c : '?');
        return b.Length == 0 ? "-" : b.ToString();
    }
}
