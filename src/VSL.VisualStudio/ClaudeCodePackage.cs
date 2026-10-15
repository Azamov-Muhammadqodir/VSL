using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VSL.VisualStudio;

/// <summary>
/// Claude Code for Visual Studio: the Claude Code tool window (View > Other Windows > Claude Code, Ctrl+\ Ctrl+K)
/// and Ask Claude in the editor's context menu.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Tabbed with Solution Explorer, where VS puts its own chat windows.
[ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
[ProvideBindingPath]
public sealed class ClaudeCodePackage : AsyncPackage
{
    public const string PackageGuidString = "31c3e855-194a-456e-a97a-f98c671e7c8e";

    static readonly Guid CommandSet = new("63fdda5c-caa1-42fe-b0ca-724e9f26da87");
    const int ShowClaudeCommandId = 0x0100;
    const int AskClaudeCommandId = 0x0101;
    const int AskClaudeAboutItemsCommandId = 0x0102;

    VisualStudioHost? _host;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        _host = new VisualStudioHost(this);
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand((_, _) => _ = ShowClaudeAsync(), new CommandID(CommandSet, ShowClaudeCommandId)));
            commands.AddCommand(new MenuCommand((_, _) => _ = ShowClaudeAsync(), new CommandID(CommandSet, AskClaudeCommandId)));
            commands.AddCommand(new MenuCommand((_, _) => _ = AskAboutSelectedItemsAsync(), new CommandID(CommandSet, AskClaudeAboutItemsCommandId)));
        }
    }

    /// <summary>Opens the panel with the current file and selection as context, and puts the caret in the prompt.</summary>
    internal async Task ShowClaudeAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        await ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, create: true, DisposalToken);
        var claude = Host.Claude;
        claude.RefreshContext();
        claude.RequestFocus();
    }

    /// <summary>Solution Explorer > Ask Claude: the selected files, folders or projects go into the prompt as @mentions.</summary>
    async Task AskAboutSelectedItemsAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        var paths = Host.SelectedSolutionItems();
        await ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, create: true, DisposalToken);
        if (paths.Count > 0)
            Host.Claude.AddFiles(paths);
        Host.Claude.RequestFocus();
    }

    internal VisualStudioHost Host => _host ?? throw new InvalidOperationException("The package is not initialized.");

    public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType) =>
        toolWindowType == typeof(ClaudeToolWindow).GUID ? this : null!;

    protected override string GetToolWindowTitle(Type toolWindowType, int id) =>
        toolWindowType == typeof(ClaudeToolWindow) ? ClaudeToolWindow.Title : base.GetToolWindowTitle(toolWindowType, id);

    protected override async Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return Host;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _host is { } host)
        {
            _host = null;
            ThreadHelper.JoinableTaskFactory.Run(host.DisposeAsync().AsTask);
        }
        base.Dispose(disposing);
    }
}
