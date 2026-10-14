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
    }

    public EditorHost Host { get; }

    public string FilePath => Host.FilePath;

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>VS marks unsaved documents with a trailing asterisk.</summary>
    public string Title => IsDirty ? FileName + "*" : FileName;

    public bool IsDirty => Host.IsDirty;

    public void Dispose() => Host.Dispose();
}
