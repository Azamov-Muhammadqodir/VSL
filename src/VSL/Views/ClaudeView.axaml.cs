using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VSL.Claude;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>The Claude Code tool window: keyboard handling for the prompt and keeping the conversation scrolled.</summary>
public partial class ClaudeView : UserControl
{
    ClaudeViewModel? _vm;
    bool _stickToBottom = true;

    public ClaudeView()
    {
        InitializeComponent();
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        PromptBox.GotFocus += (_, _) => _vm?.RefreshContext();
        SuggestionList.Tapped += (_, _) => ApplySelectedSuggestion();
        HistoryList.Tapped += (_, _) =>
        {
            if (HistoryList.SelectedItem is ClaudeConversation conversation)
                _vm?.ResumeConversationCommand.Execute(conversation);
        };
        ChatScroll.ScrollChanged += (_, _) =>
            _stickToBottom = ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - 48;
        DataContextChanged += (_, _) =>
        {
            Attach(DataContext as ClaudeViewModel);
            if (IsAttachedToVisualTree())
                _vm?.WarmUp();
        };
        AttachedToVisualTree += (_, _) => _vm?.WarmUp();

        // Images and files can be dropped anywhere on the panel.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    static bool CanAttach(IDataTransfer data) =>
        data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap);

    void OnDragOver(object? sender, DragEventArgs e)
    {
        var ok = _vm is not null && CanAttach(e.DataTransfer);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = ok;
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (_vm is not { } vm)
            return;
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            vm.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
            return;
        }
        if (e.DataTransfer.TryGetBitmap() is { } bitmap)
            vm.AddImage("dropped image.png", ToPng(bitmap));
    }

    static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>Ctrl+V: an image or files on the clipboard are attached; text is pasted as usual.</summary>
    async Task PasteAsync()
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                vm.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
                return;
            }
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                vm.AddImage($"pasted image {vm.Attachments.Count + 1}.png", ToPng(bitmap));
                return;
            }
        }
        catch (Exception ex)
        {
            vm.AttachmentError = "Could not paste: " + ex.Message;
            return;
        }
        PromptBox.Paste();
    }

    async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach to the prompt",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        vm.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    void Attach(ClaudeViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm.FocusInputRequested -= FocusPrompt;
            _vm.OpenUrlRequested -= OpenUrl;
        }
        _vm = vm;
        if (vm is null)
            return;
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.FocusInputRequested += FocusPrompt;
        vm.OpenUrlRequested += OpenUrl;
    }

    void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
    }

    /// <summary>Puts the caret in the prompt (Ctrl+Esc, Ask Claude, new conversation).</summary>
    public void FocusPrompt() => Dispatcher.UIThread.Post(() =>
    {
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text?.Length ?? 0;
    }, DispatcherPriority.Background);

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (var item in e.NewItems.OfType<TextChatItem>())
                item.PropertyChanged += OnStreamingTextChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            _stickToBottom = true;
        ScrollToEndIfFollowing();
    }

    void OnStreamingTextChanged(object? sender, PropertyChangedEventArgs e) => ScrollToEndIfFollowing();

    void ScrollToEndIfFollowing()
    {
        if (!_stickToBottom)
            return;
        Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var suggesting = vm.Suggestions.Count > 0;
        switch (e.Key)
        {
            case Key.V when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _ = PasteAsync();
                return;
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                // Shift+Enter: a new line (the TextBox inserts it).
                return;
            case Key.Enter or Key.Tab when suggesting && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                ApplySelectedSuggestion();
                e.Handled = true;
                return;
            case Key.Enter:
                if (vm.SendCommand.CanExecute(null))
                    vm.SendCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                vm.CycleModeCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Down or Key.Up when suggesting:
                var count = vm.Suggestions.Count;
                var index = SuggestionList.SelectedIndex < 0 ? (e.Key == Key.Down ? 0 : count - 1) : (SuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + count) % count;
                SuggestionList.SelectedIndex = index;
                e.Handled = true;
                return;
            case Key.Escape when suggesting:
                vm.Suggestions.Clear();
                e.Handled = true;
                return;
            case Key.Escape when vm.IsRunning:
                vm.StopCommand.Execute(null);
                e.Handled = true;
                return;
        }
    }

    void ApplySelectedSuggestion()
    {
        if (_vm is not { } vm || vm.Suggestions.Count == 0)
            return;
        var suggestion = SuggestionList.SelectedItem as ClaudeSuggestion ?? vm.Suggestions[0];
        vm.ApplySuggestionCommand.Execute(suggestion);
    }
}
