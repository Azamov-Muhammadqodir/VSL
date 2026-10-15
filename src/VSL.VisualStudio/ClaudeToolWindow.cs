using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VSL.VisualStudio.Views;

namespace VSL.VisualStudio;

/// <summary>The Claude Code tool window.</summary>
[Guid("129eb224-74f6-4db9-82d4-4be09955c56d")]
public sealed class ClaudeToolWindow : ToolWindowPane
{
    public const string Title = "Claude Code";

    public ClaudeToolWindow(VisualStudioHost host)
        : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = Title;
        BitmapImageMoniker = Microsoft.VisualStudio.Imaging.KnownMonikers.Comment;
        Content = new ClaudeControl { DataContext = host.Claude };
    }
}
