using System.Collections.Generic;
using Microsoft.VisualStudio.Text;

namespace GitLineBlame;

/// <summary>
/// Tracks which line numbers in an <see cref="ITextBuffer"/> have been locally
/// modified since the last save to disk.  Thread-safe for reads; all writes
/// happen on the UI thread (buffer.Changed and FileActionOccurred both fire
/// on the UI thread in Visual Studio).
/// </summary>
internal sealed class DirtyLineTracker
{
    private readonly HashSet<int> _dirtyLines = new();
    private readonly ITextBuffer _buffer;
    private readonly ITextDocument _document;

    public DirtyLineTracker(ITextBuffer buffer, ITextDocument document)
    {
        _buffer   = buffer;
        _document = document;

        _buffer.Changed                  += OnBufferChanged;
        _document.FileActionOccurred     += OnFileActionOccurred;
    }

    /// <summary>Returns <c>true</c> if <paramref name="lineNumber"/> (0-indexed) has
    /// unsaved edits.</summary>
    public bool IsLineDirty(int lineNumber) => _dirtyLines.Contains(lineNumber);

    private void OnBufferChanged(object? sender, TextContentChangedEventArgs e)
    {
        var snapshot = e.After;
        foreach (var change in e.Changes)
        {
            int startLine = snapshot.GetLineNumberFromPosition(change.NewPosition);
            int endLine   = snapshot.GetLineNumberFromPosition(
                                System.Math.Min(change.NewEnd, snapshot.Length));

            for (int i = startLine; i <= endLine; i++)
                _dirtyLines.Add(i);
        }
    }

    private void OnFileActionOccurred(object? sender, TextDocumentFileActionEventArgs e)
    {
        if (e.FileActionType == FileActionTypes.ContentSavedToDisk)
            _dirtyLines.Clear();
    }

    public void Dispose()
    {
        _buffer.Changed              -= OnBufferChanged;
        _document.FileActionOccurred -= OnFileActionOccurred;
    }
}
