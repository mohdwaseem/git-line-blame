using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace GitLineBlame;

/// <summary>
/// Created once per <see cref="IWpfTextView"/> by <see cref="GitBlameViewListener"/>.
/// Listens for caret movement and buffer changes, then renders a faded italic
/// <see cref="TextBlock"/> at the end of the current cursor line using the
/// "GitLineBlame" adornment layer.
///
/// Flow:
///   Caret moves to new line  →  clear layer  →  debounce 300 ms
///   Buffer edit on current line  →  clear layer  →  immediate update
///   After debounce:
///     dirty?  → show "Modified — not committed" (amber)
///     clean?  → fetch git blame (async, cached) → show author/date/message (gray)
/// </summary>
internal sealed class GitBlameAdornmentHandler
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private readonly IWpfTextView     _textView;
    private readonly IAdornmentLayer  _layer;
    private readonly DirtyLineTracker _dirtyLineTracker;
    private readonly string           _filePath;
    private readonly string           _repoRoot;

    private int                     _lastLine = -1;
    private CancellationTokenSource? _cts;

    // -------------------------------------------------------------------------
    // Shared brushes — frozen for thread safety and WPF performance
    // -------------------------------------------------------------------------

    private static readonly Brush BlameColor;    // muted gray for committed blame
    private static readonly Brush ModifiedColor; // muted amber for dirty lines

    static GitBlameAdornmentHandler()
    {
        BlameColor    = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        ModifiedColor = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x00));
        BlameColor.Freeze();
        ModifiedColor.Freeze();
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public GitBlameAdornmentHandler(
        IWpfTextView     textView,
        DirtyLineTracker dirtyLineTracker,
        string           filePath,
        string           repoRoot)
    {
        _textView         = textView;
        _dirtyLineTracker = dirtyLineTracker;
        _filePath         = filePath;
        _repoRoot         = repoRoot;

        _layer = _textView.GetAdornmentLayer("GitLineBlame");

        _textView.Caret.PositionChanged += OnCaretPositionChanged;
        _textView.TextBuffer.Changed    += OnBufferChanged;
        _textView.Closed                += OnViewClosed;
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void OnCaretPositionChanged(object? sender, CaretPositionChangedEventArgs e)
    {
        int newLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
        if (newLine == _lastLine)
            return;

        _lastLine = newLine;
        TriggerUpdate(newLine, debounceMs: 300);
    }

    private void OnBufferChanged(object? sender, TextContentChangedEventArgs e)
    {
        if (_lastLine < 0)
            return;

        // Invalidate cached blame for any lines touched by this edit.
        var snapshot = e.After;
        foreach (var change in e.Changes)
        {
            int startLine = snapshot.GetLineNumberFromPosition(change.NewPosition);
            int endLine   = snapshot.GetLineNumberFromPosition(
                                Math.Min(change.NewEnd, snapshot.Length));

            for (int i = startLine; i <= endLine; i++)
                GitBlameProvider.InvalidateLine(_filePath, i);

            // If the current cursor line was edited, re-render immediately
            // (DirtyLineTracker will have already marked it dirty).
            if (_lastLine >= startLine && _lastLine <= endLine)
            {
                TriggerUpdate(_lastLine, debounceMs: 0);
                return;
            }
        }
    }

    private void OnViewClosed(object? sender, EventArgs e)
    {
        _textView.Caret.PositionChanged -= OnCaretPositionChanged;
        _textView.TextBuffer.Changed    -= OnBufferChanged;
        _textView.Closed                -= OnViewClosed;

        _cts?.Cancel();
        _cts?.Dispose();
        _dirtyLineTracker.Dispose();
    }

    // -------------------------------------------------------------------------
    // Core update logic
    // -------------------------------------------------------------------------

    private void TriggerUpdate(int lineNumber, int debounceMs)
    {
        _layer.RemoveAllAdornments();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _ = UpdateBlameAsync(lineNumber, debounceMs, _cts.Token);
    }

    private async Task UpdateBlameAsync(int lineNumber, int debounceMs, CancellationToken cancellationToken)
    {
        try
        {
            if (debounceMs > 0)
                await Task.Delay(debounceMs, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
                return;

            // --- Dirty line check (synchronous, no git call needed) ---
            if (_dirtyLineTracker.IsLineDirty(lineNumber))
            {
#pragma warning disable VSTHRD001
                await _textView.VisualElement.Dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested || _textView.IsClosed)
                        return;
                    RenderAdornment(lineNumber, "  Modified \u2014 not committed", ModifiedColor);
                });
                return;
            }

            // --- Fetch git blame (may return from cache immediately) ---
            BlameInfo? blame = await GitBlameProvider
                .GetBlameAsync(_filePath, lineNumber, _repoRoot, cancellationToken)
                .ConfigureAwait(false);

            await _textView.VisualElement.Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || _textView.IsClosed)
                    return;
                if (blame is null)
                    return;

                string text = blame.Hash == "0000000"
                    ? "  Not committed yet"
                    : $"  {blame.Author}  \u2022  #{blame.Hash}  \u2022  {blame.RelativeDate}  \u2022  {blame.Message}";

                RenderAdornment(lineNumber, text, BlameColor);
            });
#pragma warning restore VSTHRD001
        }
        catch (OperationCanceledException) { /* user moved cursor before blame returned */ }
        catch (Exception ex)
        {
            DiagLog.Write($"UpdateBlameAsync: EXCEPTION on line {lineNumber}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // WPF rendering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Clears the adornment layer and paints a <see cref="TextBlock"/> just to
    /// the right of the last character on <paramref name="lineNumber"/>.
    /// Must be called on the UI thread.
    /// </summary>
    private void RenderAdornment(int lineNumber, string text, Brush brush)
    {
        DiagLog.Write($"RenderAdornment: line={lineNumber} text='{text}'");

        if (_textView.IsClosed)
            return;

        _layer.RemoveAllAdornments();

        var snapshot = _textView.TextSnapshot;
        if (lineNumber >= snapshot.LineCount)
            return;

        ITextSnapshotLine bufferLine = snapshot.GetLineFromLineNumber(lineNumber);

        // GetTextViewLineContainingBufferPosition returns null if the line is
        // outside the current viewport (scrolled out of view) — safe to skip.
        IWpfTextViewLine? viewLine = _textView.TextViewLines
            .GetTextViewLineContainingBufferPosition(bufferLine.Start) as IWpfTextViewLine;
        if (viewLine is null)
            return;

        var element = new TextBlock
        {
            Text                = text,
            Foreground          = brush,
            Opacity             = 0.65,
            FontStyle           = FontStyles.Italic,
            IsHitTestVisible    = false,  // clicks pass through to the editor
            VerticalAlignment   = VerticalAlignment.Center,
        };

        // Position: immediately after the line's last character, vertically
        // centred within the line height. Use viewLine.TextHeight (the actual
        // rendered text height) rather than FontSize for accurate centering.
        Canvas.SetLeft(element, viewLine.TextRight + 16);
        Canvas.SetTop(element, viewLine.Top + (viewLine.Height - viewLine.TextHeight) / 2);

        // AdornmentPositioningBehavior.TextRelative causes VS to automatically
        // reposition the element when the viewport scrolls vertically — no need
        // for a manual LayoutChanged handler.
        _layer.AddAdornment(
            AdornmentPositioningBehavior.TextRelative,
            new SnapshotSpan(bufferLine.Start, bufferLine.End),
            tag:             null,
            adornment:       element,
            removedCallback: null);
    }
}
