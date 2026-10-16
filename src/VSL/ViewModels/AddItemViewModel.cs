using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VSL.Workspace;

namespace VSL.ViewModels;

/// <summary>A template in the Add New Item list, with its file icon.</summary>
public sealed record ItemTemplateEntry(ItemTemplate Template)
{
    public string Name => Template.Name;

    public string Description => Template.Description;

    public string Extension => Template.Extension;

    public string Icon => "material:" + Views.MaterialIcons.ForFile(Template.SampleFile);
}

/// <summary>Visual Studio's Add New Item dialog: pick a template, name the file ("Class1.cs" until edited).</summary>
public sealed partial class AddItemViewModel : ObservableObject
{
    readonly IReadOnlyList<ItemTemplateEntry> _all;
    bool _nameEdited;
    bool _suggesting;

    public AddItemViewModel(string folder, ItemTemplate initial)
    {
        Folder = folder;
        _all = ItemTemplates.All.Select(t => new ItemTemplateEntry(t)).ToList();
        foreach (var entry in _all)
            Templates.Add(entry);
        SelectedTemplate = _all.FirstOrDefault(e => e.Template == initial) ?? _all[0];
    }

    public string Folder { get; }

    public ObservableCollection<ItemTemplateEntry> Templates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial ItemTemplateEntry? SelectedTemplate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>Why the name cannot be used ("already exists"...), or null.</summary>
    public string? Error => SelectedTemplate is { } t ? ItemTemplates.Validate(Folder, ItemTemplates.FileName(t.Template, Name)) : "Choose a template.";

    public bool CanAdd => Error is null;

    public NewItemChoice? Result => SelectedTemplate is { } t && CanAdd ? new NewItemChoice(t.Template, Name) : null;

    partial void OnSelectedTemplateChanged(ItemTemplateEntry? value)
    {
        // Like VS, the suggested name follows the template until the name is typed.
        if (value is not null && !_nameEdited)
        {
            _suggesting = true;
            Name = ItemTemplates.SuggestName(value.Template, Folder);
            _suggesting = false;
        }
        OnPropertyChanged(nameof(Error));
    }

    partial void OnNameChanged(string value)
    {
        if (!_suggesting)
            _nameEdited = true;
        OnPropertyChanged(nameof(Error));
    }

    partial void OnSearchChanged(string value)
    {
        var selected = SelectedTemplate;
        Templates.Clear();
        foreach (var entry in _all.Where(e => value.Length == 0
                     || e.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
                     || e.Extension.Contains(value, StringComparison.OrdinalIgnoreCase)))
            Templates.Add(entry);
        SelectedTemplate = selected is not null && Templates.Contains(selected) ? selected : Templates.FirstOrDefault();
    }
}
