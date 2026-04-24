using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GitLineBlame;

/// <summary>
/// Executes <c>git blame -L line,line --porcelain</c> asynchronously and caches
/// results in memory.  A single static cache is shared across all open editor
/// windows; it is invalidated per-line via <see cref="InvalidateLine"/> (called
/// by <see cref="DirtyLineTracker"/> through the handler when the buffer changes)
/// and per-file via <see cref="InvalidateFile"/>.
/// </summary>
internal static class GitBlameProvider
{
    // Key: "absoluteFilePath:lineNumber" (0-indexed)
    private static readonly ConcurrentDictionary<string, BlameInfo> _cache = new(
        StringComparer.OrdinalIgnoreCase);

    // Lazily resolved path to the git executable.
    private static string? _cachedGitPath;

    // Candidate git executable paths tried in order.
    private static readonly string[] GitCandidates =
    [
        "git", // assumes git is on the system PATH (most common case)
        // VS 2026 bundled git fallbacks (VS 2026 installs to \18\ folder):
        @"C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe",
        @"C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe",
        @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe",
        // Preview edition
        @"C:\Program Files\Microsoft Visual Studio\18\Preview\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe",
    ];

    // -------------------------------------------------------------------------
    // Cache management
    // -------------------------------------------------------------------------

    public static void InvalidateLine(string filePath, int lineNumber)
        => _cache.TryRemove(CacheKey(filePath, lineNumber), out _);

    public static void InvalidateFile(string filePath)
    {
        string prefix = filePath + ":";
        foreach (string key in _cache.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _cache.TryRemove(key, out _);
    }

    // -------------------------------------------------------------------------
    // Main API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns blame information for <paramref name="lineNumber"/> (0-indexed) in
    /// <paramref name="filePath"/>, or <c>null</c> if git is unavailable, the file
    /// is not tracked, or any other error occurs.
    /// </summary>
    public static async Task<BlameInfo?> GetBlameAsync(
        string filePath,
        int lineNumber,
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        string key = CacheKey(filePath, lineNumber);
        if (_cache.TryGetValue(key, out BlameInfo? cached))
            return cached;

        string? git = FindGit();
        if (git is null)
        {
            DiagLog.Write($"GetBlameAsync: git executable not found for line {lineNumber}.");
            return null;
        }

        try
        {
            // git blame is 1-indexed; lineNumber coming in is 0-indexed.
            int gitLine = lineNumber + 1;

            // Use ArgumentList (not Arguments string) so paths with spaces or
            // special characters are handled safely without shell quoting issues.
            var psi = new ProcessStartInfo
            {
                FileName               = git,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = repoRoot,
            };
            psi.ArgumentList.Add("blame");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add($"{gitLine},{gitLine}");
            psi.ArgumentList.Add("--porcelain");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(filePath);

            DiagLog.Write($"GetBlameAsync: running git blame line {gitLine} for {filePath}");

            string output = await RunProcessAsync(psi, cancellationToken)
                .ConfigureAwait(false);

            DiagLog.Write($"GetBlameAsync: git output ({output.Length} chars): {output.Replace("\n", "\\n")[..Math.Min(200, output.Length)]}");

            if (string.IsNullOrWhiteSpace(output))
            {
                DiagLog.Write("GetBlameAsync: empty output, returning null.");
                return null;
            }

            BlameInfo? info = ParsePorcelain(output);
            DiagLog.Write($"GetBlameAsync: parsed -> {info}");
            if (info is not null)
                _cache.TryAdd(key, info);

            return info;
        }
        catch (OperationCanceledException)
        {
            throw; // propagate so callers can detect cancellation
        }
        catch (Exception ex)
        {
            DiagLog.Write($"GetBlameAsync: EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Process execution
    // -------------------------------------------------------------------------

    private static async Task<string> RunProcessAsync(
        ProcessStartInfo psi,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.Exited += (_, _) =>
        {
            try
            {
                string output = process.StandardOutput.ReadToEnd();
                tcs.TrySetResult(output);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        process.Start();

        // Hard kill + cancel after 5 seconds to avoid blocking the editor.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken, timeout.Token);

        linked.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            tcs.TrySetCanceled(linked.Token);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Porcelain parser
    // -------------------------------------------------------------------------

    private static BlameInfo? ParsePorcelain(string output)
    {
        string? hash        = null;
        string? author      = null;
        long    authorTime  = 0;
        string? summary     = null;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (hash is null && line.Length >= 40 && !line.StartsWith('\t'))
            {
                // First non-tab line is: "<40-char-hash> <orig-line> <final-line> [count]"
                int spaceIdx = line.IndexOf(' ');
                hash = spaceIdx > 0 ? line[..spaceIdx] : line;
                if (hash.Length > 7) hash = hash[..7];
                continue;
            }

            if (line.StartsWith("author ", StringComparison.Ordinal))
                author = line[7..].Trim();
            else if (line.StartsWith("author-time ", StringComparison.Ordinal))
                _ = long.TryParse(line[12..].Trim(), out authorTime);
            else if (line.StartsWith("summary ", StringComparison.Ordinal))
                summary = line[8..].Trim();
        }

        if (hash is null || author is null)
            return null;

        // git returns all-zero hash for lines not yet committed
        if (hash == "0000000")
            return new BlameInfo("0000000", "Not committed yet", "", "");

        string relDate = authorTime > 0 ? ToRelativeDate(authorTime) : "";
        string message = summary is { Length: > 60 }
            ? summary[..60] + "…"
            : summary ?? "";

        return new BlameInfo(hash, author, relDate, message);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string ToRelativeDate(long unixTimestamp)
    {
        var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        return elapsed.TotalSeconds switch
        {
            < 60          => "just now",
            < 3_600       => $"{(int)elapsed.TotalMinutes} min ago",
            < 86_400      => $"{(int)elapsed.TotalHours} hr ago",
            < 30 * 86_400 => $"{(int)elapsed.TotalDays} days ago",
            < 365 * 86_400 => $"{(int)(elapsed.TotalDays / 30)} months ago",
            _              => $"{(int)(elapsed.TotalDays / 365)} years ago",
        };
    }

    private static string CacheKey(string filePath, int lineNumber)
        => $"{filePath}:{lineNumber}";

    /// <summary>
    /// Resolves the git executable once and caches the result.
    /// Tries PATH first, then VS 2026 bundled git locations.
    /// </summary>
    private static string? FindGit()
    {
        if (_cachedGitPath is not null)
            return _cachedGitPath;

        foreach (string candidate in GitCandidates)
        {
            try
            {
                if (candidate == "git")
                {
                    // Probe by running 'git --version'
                    var psi = new ProcessStartInfo("git")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };
                    psi.ArgumentList.Add("--version");
                    using var probe = Process.Start(psi);
                    if (probe is not null)
                    {
                        probe.WaitForExit(2000);
                        if (probe.ExitCode == 0)
                        {
                            DiagLog.Write("FindGit: found 'git' on PATH.");
                            _cachedGitPath = "git";
                            return "git";
                        }
                    }
                }
                else if (File.Exists(candidate))
                {
                    DiagLog.Write($"FindGit: found bundled git at {candidate}");
                    _cachedGitPath = candidate;
                    return candidate;
                }
            }
            catch { /* try next candidate */ }
        }

        DiagLog.Write("FindGit: git not found on PATH or in any VS bundled location.");
        return null; // git not found — extension silently does nothing
    }
}
