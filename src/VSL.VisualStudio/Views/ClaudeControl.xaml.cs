using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VSL.Claude;
using VSL.ViewModels;

namespace VSL.VisualStudio.Views;

/// <summary>
/// The Claude Code tool window (VSL's ClaudeView in WPF): keyboard handling for the prompt, attachments by button,
/// paste and drag and drop, and keeping the conversation scrolled.
/// </summary>
public partial class ClaudeControl : UserControl
{
    /// <summary>Corner radius for the panel's button templates.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(ClaudeControl),
        new FrameworkPropertyMetadata(default(CornerRadius)));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    /// <summary>The layer a button fades in under the pointer.</summary>
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached("HoverBrush", typeof(Brush), typeof(ClaudeControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80))));

    public static Brush GetHoverBrush(DependencyObject element) => (Brush)element.GetValue(HoverBrushProperty);

    public static void SetHoverBrush(DependencyObject element, Brush value) => element.SetValue(HoverBrushProperty, value);

    ClaudeViewModel? _vm;
    bool _stickToBottom = true;

    public ClaudeControl()
    {
        InitializeComponent();
        PromptBox.PreviewKeyDown += OnPromptKeyDown;
        NewAccountBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _vm?.AddAccountCommand.CanExecute(null) == true)
            {
                _vm.AddAccountCommand.Execute(null);
                e.Handled = true;
            }
        };
        PromptBox.GotKeyboardFocus += (_, _) => _vm?.RefreshContext();
        // Ctrl+V reaches the prompt as Visual Studio's Edit.Paste: images and files on the clipboard are attached.
        CommandManager.AddPreviewExecutedHandler(PromptBox, OnPromptPreviewExecuted);
        SuggestionList.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(ApplySelectedSuggestion, DispatcherPriority.Input);
        HistoryList.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (HistoryList.SelectedItem is ClaudeConversation conversation)
                _vm?.ResumeConversationCommand.Execute(conversation);
            HistoryList.SelectedItem = null;
        }, DispatcherPriority.Input);
        ChatScroll.ScrollChanged += (_, e) =>
        {
            // Following the end unless the user scrolled up (content growing keeps the offset where it was).
            if (e.ExtentHeightChange == 0)
                _stickToBottom = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 48;
        };
        ChatScroll.PreviewMouseWheel += OnChatMouseWheel;
        DataContextChanged += (_, _) =>
        {
            Attach(DataContext as ClaudeViewModel);
            if (IsLoaded)
                _vm?.WarmUp();
        };
        Loaded += (_, _) =>
        {
            UpdateThemeBrushes();
            _vm?.WarmUp();
        };
        Microsoft.VisualStudio.PlatformUI.VSColorTheme.ThemeChanged += _ => Dispatcher.BeginInvoke(UpdateThemeBrushes);
        // A narrow panel keeps its buttons and drops the title.
        SizeChanged += (_, _) => HeaderTitle.Visibility = ActualWidth < 420 ? Visibility.Collapsed : Visibility.Visible;

        // Images and files can be dropped anywhere on the panel.
        PreviewDragOver += OnDragOver;
        PreviewDragEnter += OnDragOver;
        PreviewDragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        PreviewDrop += OnDrop;
    }

    // ---- Attachments ---------------------------------------------------------------------------------------

    static bool CanAttach(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG");

    void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = _vm is not null && CanAttach(e.Data);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_vm is not { } vm)
            return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            vm.AddFiles(files);
            return;
        }
        if (ImageFrom(e.Data) is { } png)
            vm.AddImage("dropped image.png", png);
    }

    /// <summary>An image on the clipboard or in a drop, as PNG (the "PNG" format keeps transparency; a bitmap does not).</summary>
    static byte[]? ImageFrom(IDataObject data)
    {
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream stream)
            return stream.ToArray();
        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        return null;
    }

    void OnPromptPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || _vm is not { } vm)
            return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null)
                return;
            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                vm.AddFiles(files);
                e.Handled = true;
            }
            else if (!data.GetDataPresent(DataFormats.UnicodeText) && ImageFrom(data) is { } png)
            {
                vm.AddImage($"pasted image {vm.Attachments.Count + 1}.png", png);
                e.Handled = true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or NotSupportedException)
        {
            vm.AttachmentError = "Could not paste: " + ex.Message;
            e.Handled = true;
        }
    }

    void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var dialog = new OpenFileDialog
        {
            Title = Loc.Instance.Translate("Attach to the prompt"),
            Multiselect = true,
            Filter = $"{Loc.Instance.Translate("Images")}|*.png;*.jpg;*.jpeg;*.gif;*.webp|{Loc.Instance.Translate("All files")}|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            vm.AddFiles(dialog.FileNames);
    }

    // ---- View model ----------------------------------------------------------------------------------------

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

    /// <summary>
    /// Secondary text in solid colors between the theme's text and background (about three quarters and three fifths of
    /// the way to the text), so it keeps ClearType and stays sharp in any theme.
    /// </summary>
    void UpdateThemeBrushes()
    {
        if (TryFindResource(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey) is not SolidColorBrush text
            || TryFindResource(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundBrushKey) is not SolidColorBrush background)
            return;
        Resources["ClaudeMutedBrush"] = Mix(text.Color, background.Color, 0.74);
        Resources["ClaudeFaintBrush"] = Mix(text.Color, background.Color, 0.58);

        static SolidColorBrush Mix(Color a, Color b, double amount)
        {
            byte Channel(byte x, byte y) => (byte)Math.Round(y + (x - y) * amount);
            var brush = new SolidColorBrush(Color.FromRgb(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B)));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>English / O'zbekcha: the whole panel switches at once.</summary>
    void OnLanguageClick(object sender, RoutedEventArgs e) => Loc.Instance.Language = Loc.Instance.Language == "uz" ? "en" : "uz";

    /// <summary>A starter card on the empty conversation: its prompt goes into the input, ready to send or change.</summary>
    void OnStarterClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || (sender as FrameworkElement)?.Tag is not string prompt)
            return;
        vm.Prompt = prompt;
        FocusPrompt();
    }

    /// <summary>A click outside a menu or dialog closes it.</summary>
    void OnBackdropPressed(object sender, MouseButtonEventArgs e)
    {
        _vm?.CloseDialogsCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Puts the caret in the prompt (Ask Claude, new conversation, a resumed one).</summary>
    public void FocusPrompt() => Dispatcher.BeginInvoke(() =>
    {
        PromptBox.Focus();
        Keyboard.Focus(PromptBox);
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
        if (_stickToBottom)
            Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>
    /// Answers, code and results are text boxes and nested scroll viewers that would keep the wheel: unless one
    /// of them can still scroll that way, the conversation scrolls.
    /// </summary>
    void OnChatMouseWheel(object sender, MouseWheelEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != ChatScroll; element = Parent(element))
        {
            if (element is ScrollViewer { ScrollableHeight: > 0 } inner
                && (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
                return;
        }
        ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;

        static DependencyObject? Parent(DependencyObject element) =>
            element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
    }

    // ---- Keyboard ------------------------------------------------------------------------------------------

    void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var suggesting = vm.Suggestions.Count > 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Enter when shift:
                // Shift+Enter: a new line.
                var at = PromptBox.CaretIndex;
                PromptBox.SelectedText = "";
                PromptBox.Text = PromptBox.Text.Insert(at, Environment.NewLine);
                PromptBox.CaretIndex = at + Environment.NewLine.Length;
                e.Handled = true;
                return;
            case Key.Enter or Key.Tab when suggesting && !shift:
                ApplySelectedSuggestion();
                e.Handled = true;
                return;
            case Key.Enter:
                if (vm.SendCommand.CanExecute(null))
                    vm.SendCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Tab when shift:
                vm.CycleModeCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Down or Key.Up when suggesting:
                var count = vm.Suggestions.Count;
                SuggestionList.SelectedIndex = SuggestionList.SelectedIndex < 0
                    ? (key == Key.Down ? 0 : count - 1)
                    : (SuggestionList.SelectedIndex + (key == Key.Down ? 1 : -1) + count) % count;
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
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
