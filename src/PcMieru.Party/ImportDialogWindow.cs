using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcMieru.Party.I18n;
using PcMieru.Party.Import;

namespace PcMieru.Party;

/// <summary>
/// The small window of the first start (DESIGN-C 4.1 item 2): the three-choice question and the message with Retry and Close. Built in code from an
/// <see cref="ImportDialogModel"/>, owner-less, centred, dark like the Party window. It only reports which button was pressed; it touches no file.
/// </summary>
internal sealed class ImportDialogWindow : Window
{
    private static readonly Brush Panel = Frozen(Color.FromRgb(0x10, 0x13, 0x19));
    private static readonly Brush Edge = Frozen(Color.FromArgb(0x99, 0x5D, 0xD8, 0xBB));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xDD, 0xE5, 0xF8));
    private static readonly Brush Sub = Frozen(Color.FromRgb(0x9A, 0xA7, 0xC4));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x5D, 0xD8, 0xBB));
    private static readonly Brush AccentFill = Frozen(Color.FromArgb(0x55, 0x5D, 0xD8, 0xBB));
    private static readonly Brush ButtonFill = Frozen(Color.FromRgb(0x1C, 0x21, 0x2C));

    private readonly ImportDialogModel _model;
    private readonly Border _root;
    private ImportDialogResult? _chosen;

    public ImportDialogWindow(ImportDialogModel model)
    {
        _model = model;
        Title = model.Title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 24);   // never taller than the screen: the list scrolls, the buttons stay
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Panel;
        Foreground = Text;
        FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");
        FontSize = 14;

        // Three rows: the title and the text (fixed), the list and the note (they scroll when the screen is too short), the buttons (always visible).
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = model.Title, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 12) });
        head.Children.Add(new TextBlock { Text = model.Body, TextWrapping = TextWrapping.Wrap, Foreground = Text });

        var stack = new StackPanel();
        if (model.ListHeader is { } header)
        {
            stack.Children.Add(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap, Foreground = Sub, Margin = new Thickness(0, 12, 0, 4) });
            foreach (var field in model.List)
            {
                stack.Children.Add(new TextBlock { Text = "・" + field, TextWrapping = TextWrapping.Wrap, Foreground = Text, Margin = new Thickness(10, 0, 0, 0) });
            }
        }

        if (model.Note is { } note)
        {
            stack.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = Sub, Margin = new Thickness(0, 12, 0, 0) });
        }

        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in model.Buttons)
        {
            var result = b.Result;
            var button = new Button
            {
                Content = b.Text,
                MinWidth = 96,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = b.IsDefault,
                IsCancel = b.IsCancel,
                Foreground = Text,
                Background = b.IsDefault ? AccentFill : ButtonFill,
                BorderBrush = b.IsDefault ? Accent : Edge,
            };
            button.Click += (_, _) =>
            {
                _chosen = result;
                Close();
            };
            row.Children.Add(button);
        }

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(head, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetRow(row, 2);
        layout.Children.Add(head);
        layout.Children.Add(scroll);
        layout.Children.Add(row);
        _root = new Border { Background = Panel, BorderBrush = Edge, BorderThickness = new Thickness(1), Padding = new Thickness(22), Child = layout };
        Content = _root;
        ContentRendered += (_, _) => Rendered = true;
    }

    /// <summary>Shows the window of <paramref name="model"/> modally (no owner, no Application needed) and returns the button that was pressed.</summary>
    internal static ImportDialogResult ShowFor(ImportDialogModel model)
    {
        var window = new ImportDialogWindow(model);
        window.ShowDialog();
        return window.Choice;
    }

    /// <summary>The button that was pressed. The cross or Escape without a press is the model's cancel (Later or Close): never a write.</summary>
    public ImportDialogResult Choice => _chosen ?? _model.CancelResult;

    /// <summary>True once the window was drawn (the smoke run checks it).</summary>
    public bool Rendered { get; private set; }

    /// <summary>The content of the window as a bitmap, for <c>--smoke --shot</c>.</summary>
    public BitmapSource RenderShot(double scale)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(_root.ActualWidth * scale), (int)Math.Ceiling(_root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(_root);
        return bitmap;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>The questions of the first start answered with windows (the flow itself is <see cref="FirstStartFlow"/>, which the tests drive with a script).</summary>
internal sealed class WpfFirstStartPrompts : IFirstStartPrompts
{
    public ContinueChoice AskContinue(ImportPlan plan) => Show(ImportDialogModels.Ask(plan)) switch
    {
        ImportDialogResult.Continue => ContinueChoice.Continue,
        ImportDialogResult.StartFresh => ContinueChoice.StartFresh,
        _ => ContinueChoice.Later,
    };

    /// <summary>Yes or No, and No is what Enter and Escape give.</summary>
    public bool ConfirmStartFresh() =>
        MessageBox.Show(Strings.ImportFreshAsk, Strings.ImportTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public ProblemChoice ShowProblem(ProblemView problem) => Show(ImportDialogModels.Problem(problem)) switch
    {
        ImportDialogResult.Retry => ProblemChoice.Retry,
        ImportDialogResult.StartFresh => ProblemChoice.StartFresh,
        ImportDialogResult.OpenReadOnly => ProblemChoice.OpenReadOnly,
        _ => ProblemChoice.Close,
    };

    private static ImportDialogResult Show(ImportDialogModel model) => ImportDialogWindow.ShowFor(model);
}

/// <summary>The two things the sign-in switch says to a person, answered with the same small window (the flow itself is <see cref="RunSwitchFlow"/>, which the tests drive with a script).</summary>
internal sealed class WpfRunSwitchPrompts : IRunSwitchPrompts
{
    /// <summary>Switch is the button; Not now, the cross and Escape all leave the sign-in start as it is.</summary>
    public bool AskSwitch() => ImportDialogWindow.ShowFor(ImportDialogModels.RunSwitchAsk()) == ImportDialogResult.Switch;

    public void ShowCheckNotice() => ImportDialogWindow.ShowFor(ImportDialogModels.RunSwitchCheck());

    public void ShowNotSwitchedNotice() => ImportDialogWindow.ShowFor(ImportDialogModels.RunSwitchNotSwitched());
}
