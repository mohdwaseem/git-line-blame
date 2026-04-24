using System;
using System.IO;

namespace GitLineBlame;

/// <summary>
/// Lightweight append-only logger that writes timestamped messages to
/// <c>%TEMP%\GitLineBlame.log</c>. Useful for diagnosing issues without
/// attaching a debugger. Errors writing the log are silently ignored.
/// </summary>
internal static class DiagLog
{
    internal static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "GitLineBlame.log");

    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
