using CommunityToolkit.Mvvm.ComponentModel;
using VSL.Editor;

namespace VSL.ViewModels;

public partial class DocumentViewModel : ObservableObject, IDisposable
{
    public DocumentViewModel(EditorHost host)
    {
        Host = host;
        host.DirtyChanged += () =>
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(Title));
        };
        host.ZoomChanged += () => OnPropertyChanged(nameof(ZoomText));
        host.DiagnosticsChanged += () =>
        {
            OnPropertyChanged(nameof(IssuesText));
            OnPropertyChanged(nameof(IssuesIcon));
            OnPropertyChanged(nameof(HasIssues));
        };
    }

    /// <summary>Presets of VS's zoom drop-down in the editor's bottom-left corner.</summary>
    public static IReadOnlyList<int> ZoomLevels { get; } = [20, 50, 70, 100, 150, 200, 400];

    public string ZoomText => $"{Math.Round(Host.Zoom * 100)} %";

    public bool HasIssues => Host.Diagnostics.Any(d => d.Severity is Lsp.DiagnosticSeverity.Error or Lsp.DiagnosticSeverity.Warning);

    /// <summary>VS's document health indicator next to the zoom box.</summary>
    public string IssuesText
    {
        get
        {
            var errors = Host.Diagnostics.Count(d => d.Severity == Lsp.DiagnosticSeverity.Error);
            var warnings = Host.Diagnostics.Count(d => d.Severity == Lsp.DiagnosticSeverity.Warning);
            return errors + warnings == 0 ? "No issues found" : $"{errors} error(s), {warnings} warning(s)";
        }
    }

    public string IssuesIcon => Host.Diagnostics.Any(d => d.Severity == Lsp.DiagnosticSeverity.Error) ? "Error"
        : Host.Diagnostics.Any(d => d.Severity == Lsp.DiagnosticSeverity.Warning) ? "Warning"
        : "Ok";

    /// <summary>Pinned tabs stay at the left and survive "Close All But Pinned".</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsPinned { get; set; }

    public EditorHost Host { get; }

    public string FilePath => Host.FilePath;

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>VS marks unsaved documents with a trailing asterisk.</summary>
    public string Title => IsDirty ? FileName + "*" : FileName;

    public bool IsDirty => Host.IsDirty;

    public void Dispose() => Host.Dispose();
}
