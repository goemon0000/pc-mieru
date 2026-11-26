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
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Panel;
        Foreground = Text;
        FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");
        FontSize = 14;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = model.Title, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 12) });
        stack.Children.Add(new TextBlock { Text = model.Body, TextWrapping = TextWrapping.Wrap, Foreground = Text });
        if (model.ListHeader is { } header)
        {
            stack.Children.Add(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap, Foreground = Sub, Margin = new Thickness(0, 12, 0, 4) });
            foreach (var field in model.List)
            {
                stack.Children.Add(new TextBlock { Text = "・" + field, Foreground = Text, Margin = new Thickness(10, 0, 0, 0) });
            }
        }

        if (model.Note is { } note)
        {
            stack.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = Sub, Margin = new Thickness(0, 12, 0, 0) });
        }

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

        stack.Children.Add(row);
        _root = new Border { Background = Panel, BorderBrush = Edge, BorderThickness = new Thickness(1), Padding = new Thickness(22), Child = stack };
        Content = _root;
        ContentRendered += (_, _) => Rendered = true;
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
        _ => ProblemChoice.Close,
    };

    private static ImportDialogResult Show(ImportDialogModel model)
    {
        var window = new ImportDialogWindow(model);
        window.ShowDialog();
        return window.Choice;
    }
}
