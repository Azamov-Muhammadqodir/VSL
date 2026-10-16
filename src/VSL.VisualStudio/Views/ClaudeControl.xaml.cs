using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && ModelMenu.IsVisible)
            {
                CloseModelMenu();
                e.Handled = true;
            }
        };
    }

    // ---- Attachments ---------------------------------------------------------------------------------------

    static bool CanAttach(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(FileGroupDescriptor) || HasImage(data);

    const string FileGroupDescriptor = "FileGroupDescriptorW";

    static bool HasImage(IDataObject data) =>
        data.GetDataPresent("PNG") || data.GetDataPresent("image/png") || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib);

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
        try
        {
            AttachFrom(e.Data, vm, "dropped image", requireNoText: false);
        }
        catch (Exception ex) when (ex is ExternalException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            vm.AttachmentError = "Could not paste: " + ex.Message;
        }
    }

    /// <summary>
    /// Attaches what a paste or a drop carries: files (from Explorer), files without a path (an Outlook attachment, a
    /// file from a browser or an archive: saved to a temp folder first) and images (screenshots, "Copy image").
    /// With <paramref name="requireNoText"/>, an image that comes with real text (cells copied from Excel, a Word
    /// paragraph) is left to the text paste. Returns whether anything was attached.
    /// </summary>
    static bool AttachFrom(IDataObject data, ClaudeViewModel vm, string imageName, bool requireNoText)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            vm.AddFiles(files);
            return true;
        }
        if (data.GetDataPresent(FileGroupDescriptor) && VirtualFiles.Save(data) is { Count: > 0 } saved)
        {
            vm.AddFiles(saved);
            return true;
        }
        if (!HasImage(data) || requireNoText && !IsOnlyImageText(data))
            return false;
        if (ImageFrom(data) is not { } png)
            return false;
        var (bytes, extension) = FitForClaude(png);
        vm.AddImage($"{imageName} {vm.Attachments.Count + 1}{extension}", bytes);
        return true;
    }

    /// <summary>
    /// No text beside the image, or only the image's own address (a browser's "Copy image" can add its URL).
    /// </summary>
    static bool IsOnlyImageText(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.UnicodeText) || data.GetData(DataFormats.UnicodeText) is not string text)
            return true;
        text = text.Trim();
        return text.Length == 0
            || !text.Contains('\n') && !text.Contains(' ')
               && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                   || text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An image on the clipboard or in a drop, as PNG (the "PNG" format keeps transparency; a bitmap does not).</summary>
    static byte[]? ImageFrom(IDataObject data)
    {
        foreach (var format in new[] { "PNG", "image/png" })
        {
            if (data.GetDataPresent(format) && data.GetData(format) is MemoryStream stream && stream.Length > 0)
                return stream.ToArray();
        }
        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return Encode(new PngBitmapEncoder(), bitmap);
        return null;
    }

    static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// A screenshot of a large or high-DPI screen can pass the API's 5 MB per image as PNG: it goes as a JPEG then,
    /// made smaller until it fits.
    /// </summary>
    static (byte[] Bytes, string Extension) FitForClaude(byte[] png)
    {
        if (png.Length <= ClaudeAttachment.MaxBytes)
            return (png, ".png");
        try
        {
            using var input = new MemoryStream(png);
            BitmapSource source = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            // JPEG has no alpha: transparent parts go onto white, not black.
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
            for (var scale = 1.0; scale > 0.15; scale *= 0.75)
            {
                var scaled = scale == 1.0 ? source : new TransformedBitmap(source, new ScaleTransform(scale, scale));
                var jpeg = Encode(new JpegBitmapEncoder { QualityLevel = 88 }, scaled);
                if (jpeg.Length <= ClaudeAttachment.MaxBytes)
                    return (jpeg, ".jpg");
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
        }
        return (png, ".png");
    }

    /// <summary>
    /// Ctrl+V in the panel. Visual Studio turns the key into its Edit.Paste command (see <see cref="ClaudeToolWindow"/>),
    /// and WPF into ApplicationCommands.Paste: both come here. Attachments first; otherwise the text goes into the
    /// focused text box, or into the prompt when the focus is on a button.
    /// </summary>
    public void Paste()
    {
        var target = Keyboard.FocusedElement as TextBox;
        if (target is null || !IsAncestorOf(target))
            target = PromptBox;
        if (target == PromptBox && TryPasteAttachments())
            return;
        target.Focus();
        target.Paste();
    }

    /// <summary>Copy, Cut and Select All for the focused text box (prompt, answers, code), from Visual Studio's Edit menu.</summary>
    public bool CanRunEditCommand(RoutedUICommand command) =>
        command == ApplicationCommands.Paste ? IsKeyboardFocusWithin
        : Keyboard.FocusedElement is TextBox box && IsAncestorOf(box) && command.CanExecute(null, box);

    public void RunEditCommand(RoutedUICommand command)
    {
        if (command == ApplicationCommands.Paste)
            Paste();
        else if (Keyboard.FocusedElement is TextBox box && IsAncestorOf(box))
            command.Execute(null, box);
    }

    bool TryPasteAttachments()
    {
        if (_vm is not { } vm)
            return false;
        try
        {
            if (ClipboardData() is not { } data || !AttachFrom(data, vm, "pasted image", requireNoText: true))
                return false;
            FocusPrompt();
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            vm.AttachmentError = "Could not paste: " + ex.Message;
            return true;
        }
    }

    /// <summary>The clipboard, retried for a moment while another program still holds it open.</summary>
    static IDataObject? ClipboardData()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Clipboard.GetDataObject();
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(40);
            }
        }
    }

    void OnPromptPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || _vm is null)
            return;
        if (TryPasteAttachments())
            e.Handled = true;
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

    // ---- Menus above the prompt ----------------------------------------------------------------------------

    /// <summary>A menu card's bottom margin that puts it just above the prompt, however tall the prompt has grown.</summary>
    double AbovePrompt() => InputBox.IsVisible ? Math.Max(0, ActualHeight - InputBox.TranslatePoint(default, this).Y + 6) : 96;

    void OnModelClick(object sender, RoutedEventArgs e)
    {
        if (ModelMenu.IsVisible)
        {
            CloseModelMenu();
            return;
        }
        _vm?.CloseDialogsCommand.Execute(null);
        ModelMenuCard.Margin = new Thickness(10, 10, 10, AbovePrompt());
        ModelMenu.Visibility = Visibility.Visible;
    }

    void OnModelPicked(object sender, RoutedEventArgs e)
    {
        if (_vm is { } vm && (sender as FrameworkElement)?.DataContext is ClaudeModelChoice model)
            vm.SelectedModel = model;
        CloseModelMenu();
        FocusPrompt();
    }

    void OnModelMenuBackdrop(object sender, MouseButtonEventArgs e)
    {
        CloseModelMenu();
        e.Handled = true;
    }

    void CloseModelMenu() => ModelMenu.Visibility = Visibility.Collapsed;

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClaudeViewModel.IsModesOpen) && _vm?.IsModesOpen == true)
        {
            ModesMenuCard.Margin = new Thickness(10, 0, 10, AbovePrompt());
            CloseModelMenu();
        }
    }

    // ---- View model ----------------------------------------------------------------------------------------

    void Attach(ClaudeViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm.FocusInputRequested -= FocusPrompt;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = vm;
        if (vm is null)
            return;
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.FocusInputRequested += FocusPrompt;
        vm.PropertyChanged += OnViewModelPropertyChanged;
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
