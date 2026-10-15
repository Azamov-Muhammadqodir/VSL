using VSL.Editor;

namespace VSL.ViewModels;

/// <summary>A side-by-side comparison tab (Git > Compare with Unmodified, Git Changes double-click).</summary>
public sealed class DiffViewModel : DocumentTab
{
    public DiffViewModel(string filePath, string leftText, string rightText, string leftLabel, string rightLabel, string keySuffix)
    {
        FilePath = filePath;
        KeySuffix = keySuffix;
        View = new DiffView(filePath, leftText, rightText, leftLabel, rightLabel);
    }

    public string FilePath { get; }

    string KeySuffix { get; }

    public DiffView View { get; }

    public override string Key => $"diff:{FilePath}:{KeySuffix}";

    public override string Title => $"{Path.GetFileName(FilePath)} (diff)";

    public override string ToolTip => $"{FilePath} — {KeySuffix}";
}
