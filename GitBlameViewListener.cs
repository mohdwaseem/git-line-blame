using System;
using System.ComponentModel.Composition;
using System.IO;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace GitLineBlame;

/// <summary>
/// MEF entry point for the extension.  Visual Studio discovers this class
/// automatically (via the MefComponent asset in the .vsixmanifest) and calls
/// <see cref="TextViewCreated"/> each time an editor window is opened.
///
/// The [ContentType("text")] attribute covers all text-based file types
/// (C#, C++, TypeScript, XML, JSON, …) without needing an explicit list.
/// The [TextViewRole(Document)] filter excludes auxiliary views such as the
/// Output window, Quick-Find bar, and tooltip editors.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class GitBlameViewListener : IWpfTextViewCreationListener
{
    /// <summary>
    /// Declares the WPF adornment layer.  MUST be an instance field on an
    /// MEF-exported class — MEF v1 (used by VS) cannot discover static fields
    /// or members of static classes as MEF parts.
    /// </summary>
#pragma warning disable CS0649   // assigned by MEF reflection; IDE warning is a false positive
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("GitLineBlame")]
    [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
    internal AdornmentLayerDefinition? AdornmentLayer;
#pragma warning restore CS0649

    /// <summary>
    /// Imported by MEF.  Used to retrieve the on-disk file path associated
    /// with an <see cref="ITextBuffer"/>.
    /// </summary>
    [Import]
    public ITextDocumentFactoryService TextDocumentFactory { get; set; } = null!;

    public void TextViewCreated(IWpfTextView textView)
    {
        try
        {
            DiagLog.Write($"TextViewCreated called. Log: {DiagLog.LogPath}");

            // Resolve the physical file path for this buffer.
            if (!TextDocumentFactory.TryGetTextDocument(textView.TextBuffer, out ITextDocument? document))
            {
                DiagLog.Write("  -> no ITextDocument for buffer, skipping.");
                return;
            }

            string filePath = document.FilePath;
            DiagLog.Write($"  -> filePath = {filePath}");

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                DiagLog.Write("  -> file path empty or file does not exist, skipping.");
                return;
            }

            // Walk up the directory tree looking for a .git folder OR file
            // (a .git file indicates a submodule or worktree).
            string? repoRoot = FindRepoRoot(filePath);
            if (repoRoot is null)
            {
                DiagLog.Write($"  -> no git repo found for {filePath}, skipping.");
                return;
            }

            DiagLog.Write($"  -> repoRoot = {repoRoot}");

            // DirtyLineTracker and GitBlameAdornmentHandler are per-view objects;
            // the handler subscribes to Closed and self-disposes when the tab is shut.
            var dirtyTracker = new DirtyLineTracker(textView.TextBuffer, document);
            _ = new GitBlameAdornmentHandler(textView, dirtyTracker, filePath, repoRoot);

            DiagLog.Write("  -> GitBlameAdornmentHandler created successfully.");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"  -> EXCEPTION in TextViewCreated: {ex}");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string? FindRepoRoot(string filePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (dir is not null)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            // .git can be a directory (normal repos) OR a file (submodules / worktrees)
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
