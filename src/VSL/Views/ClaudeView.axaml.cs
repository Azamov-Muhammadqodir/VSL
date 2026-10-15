using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    }

    bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    void Attach(ClaudeViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm.FocusInputRequested -= FocusPrompt;
        }
        _vm = vm;
        if (vm is null)
            return;
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.FocusInputRequested += FocusPrompt;
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
