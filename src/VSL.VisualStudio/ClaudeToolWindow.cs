using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using VSL.VisualStudio.Views;

namespace VSL.VisualStudio;

/// <summary>The Claude Code tool window.</summary>
[Guid("129eb224-74f6-4db9-82d4-4be09955c56d")]
public sealed class ClaudeToolWindow : ToolWindowPane
{
    public const string Title = "Claude Code";

    readonly ClaudeControl _control;

    public ClaudeToolWindow(VisualStudioHost host)
        : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = Title;
        BitmapImageMoniker = Microsoft.VisualStudio.Imaging.KnownMonikers.Comment;
        Content = _control = new ClaudeControl { DataContext = host.Claude, Git = host.Git };
    }

    /// <summary>
    /// Visual Studio turns Ctrl+V, Ctrl+C, Ctrl+X and Ctrl+A into its own Edit commands before WPF sees the keys, so
    /// the panel answers them here: paste attaches images and files (or pastes text), the others act on the focused
    /// text box. When the panel has nothing to do, the command goes on to Visual Studio as usual.
    /// </summary>
    protected override void Initialize()
    {
        base.Initialize();
        if (GetService(typeof(IMenuCommandService)) is not OleMenuCommandService commands)
            return;
        Add(VSConstants.VSStd97CmdID.Paste, ApplicationCommands.Paste);
        Add(VSConstants.VSStd97CmdID.Copy, ApplicationCommands.Copy);
        Add(VSConstants.VSStd97CmdID.Cut, ApplicationCommands.Cut);
        Add(VSConstants.VSStd97CmdID.SelectAll, ApplicationCommands.SelectAll);

        void Add(VSConstants.VSStd97CmdID id, RoutedUICommand wpf)
        {
            var command = new OleMenuCommand((_, _) => _control.RunEditCommand(wpf), new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)id));
            command.BeforeQueryStatus += (_, _) =>
            {
                var handles = _control.CanRunEditCommand(wpf);
                command.Supported = handles;
                command.Enabled = handles;
                command.Visible = handles;
            };
            commands.AddCommand(command);
        }
    }
}
