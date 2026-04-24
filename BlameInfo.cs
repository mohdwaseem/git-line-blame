namespace GitLineBlame;

/// <summary>
/// Parsed result from a single <c>git blame --porcelain</c> call on one line.
/// </summary>
internal sealed record BlameInfo(
    string Hash,         // 7-char short commit hash, or "0000000" for uncommitted
    string Author,       // author name from the commit
    string RelativeDate, // human-readable relative date, e.g. "3 days ago"
    string Message       // first line of commit message, truncated to 60 chars
);
