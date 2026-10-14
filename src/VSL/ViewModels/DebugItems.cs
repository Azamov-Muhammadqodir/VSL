using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VSL.ViewModels;

public enum DebugMode
{
    Design,
    Running,
    Break,
}

/// <summary>A row in the Locals window; children load from the debugger when expanded.</summary>
public partial class VariableNode : ObservableObject
{
    Func<Task<IReadOnlyList<VariableNode>>>? _loader;

    public VariableNode(string name, string value, string? type, Func<Task<IReadOnlyList<VariableNode>>>? loader)
    {
        Name = name;
        Value = value;
        Type = type ?? "";
        _loader = loader;
        if (loader is not null)
            Children.Add(new VariableNode("", "Loading...", null, null));
    }

    public string Name { get; }
    public string Value { get; }
    public string Type { get; }
    public ObservableCollection<VariableNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var loader = Interlocked.Exchange(ref _loader, null);
        if (loader is null)
            return;
        IReadOnlyList<VariableNode> items;
        try
        {
            items = await loader();
        }
        catch (Exception ex)
        {
            items = [new VariableNode("", ex.Message, null, null)];
        }
        Children.Clear();
        foreach (var item in items)
            Children.Add(item);
    }
}

/// <summary>A Watch window expression, re-evaluated whenever the debugger stops or the frame changes.</summary>
public partial class WatchItem(string expression) : ObservableObject
{
    public string Expression { get; } = expression;

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial string Type { get; set; } = "";
}

/// <summary>A Call Stack row.</summary>
public sealed record StackFrameItem(int Id, string Name, string? SourcePath, int Line, int Column)
{
    public bool HasSource => SourcePath is not null && File.Exists(SourcePath);

    public string Display => HasSource ? $"{Name} Line {Line}" : $"[External Code] {Name}";

    public string Location => HasSource ? $"{Path.GetFileName(SourcePath)}:{Line}" : "";
}
