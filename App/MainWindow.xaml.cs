using Microsoft.Win32;
using ReadingTimeManager.Core;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ReadingTimeManager;

public partial class MainWindow : Window
{
    private enum Section { Daily, Books, Total }
    private enum TotalRange { Week, Year, All }
    private enum BookRange { All, Recent, Month, Year }
    private enum BookOrder { Duration, Days, Recent, Title }

    private static readonly Brush Ink = ColorBrush(34, 38, 35);
    private static readonly Brush Muted = ColorBrush(85, 93, 85);
    private static readonly Brush Rule = ColorBrush(221, 224, 217);
    private static readonly Brush Accent = ColorBrush(46, 106, 99);
    private static readonly Brush Soft = ColorBrush(245, 245, 241);
    private readonly HistorySession session = new();
    private readonly Func<DateOnly> clock;
    private readonly bool remember;
    private DateOnly today, month, selectedDay, week, bookMonth, bookDay;
    private DateOnly? monthDetailMonth;
    private int year;
    private Section section;
    private TotalRange totalRange;
    private BookRange bookRange;
    private BookOrder bookOrder;
    private bool fiveMinutes, loading, recentWeeks;
    private string search = "";
    private Book? selectedBook;

    public MainWindow() : this(() => DateOnly.FromDateTime(DateTime.Today), true) { }

    internal MainWindow(Func<DateOnly> clock, bool remember)
    {
        this.clock = clock;
        this.remember = remember;
        InitializeComponent();
        ResetDates(clock());
        Render();
        if (remember) Loaded += RestoreLastFile;
    }

    internal FrameworkElement TestSurface => Shell;
    internal string? CurrentPath => session.Path;

    private void ResetDates(DateOnly current)
    {
        today = selectedDay = current;
        month = ReadingHistory.MonthStart(current);
        week = ReadingHistory.Monday(current);
        year = current.Year;
    }

    private async void RestoreLastFile(object sender, RoutedEventArgs args)
    {
        Loaded -= RestoreLastFile;
        if (LastFile.Read() is { Length: > 0 } path) await OpenAsync(path);
    }

    private async void PickFile(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFileDialog { Title = "打开 Kindle 阅读记录", Filter = "TSV 阅读记录 (*.tsv)|*.tsv", Multiselect = false };
        if (picker.ShowDialog(this) == true) await OpenAsync(picker.FileName);
    }

    private async void ReloadHistory(object sender, RoutedEventArgs args) => await ReloadAsync();

    internal Task ReloadAsync() => session.Path is { } path ? OpenAsync(path, preserveView: true) : Task.CompletedTask;

    private void OnDragOver(object sender, DragEventArgs args)
    {
        args.Effects = !loading && DropPath(args) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (!loading && DropPath(args) is { } path) await OpenAsync(path);
    }

    private static string? DropPath(DragEventArgs args) => args.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
        Path.GetExtension(files[0]).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? files[0] : null;

    internal async Task OpenAsync(string path, bool preserveView = false)
    {
        if (loading) return;
        loading = true;
        OpenButton.IsEnabled = ReloadButton.IsEnabled = false;
        SetNotice("正在读取文件…");
        try
        {
            await session.OpenAsync(path);
            var saved = !remember || LastFile.Write(session.Path!);
            var history = session.History!;
            if (!preserveView)
            {
                ResetDates(clock());
                selectedDay = history.LastReadingAtOrBefore(today) ?? history.LastRecordedDay ?? today;
                month = ReadingHistory.MonthStart(selectedDay);
                section = Section.Daily;
                monthDetailMonth = null;
                recentWeeks = false;
            }
            today = clock();
            selectedBook = null;
            SourceName.Text = Path.GetFileName(session.Path);
            SourceName.ToolTip = session.Path;
            SourceInfo.Text = history.RowCount == 0 ? "已读取 · 没有阅读记录" :
                $"{history.RowCount:N0} 条记录 · {history.FirstRecordedDay:yyyy-MM-dd} 至 {history.LastRecordedDay:yyyy-MM-dd}";
            SourceInfo.ToolTip = session.Path;
            var future = history.LastRecordedDay > today ? "源文件有未来日期：保留在月历和单书详情，不计入截至今天的累计或书籍列表。" : "";
            SetNotice(string.Join(" ", new[] { saved ? "" : "无法记住路径，下次请重新选择。", future }.Where(text => text.Length > 0)));
            Render(resetScroll: !preserveView);
        }
        catch (Exception error) when (error is HistoryFormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var reason = error switch
            {
                FileNotFoundException or DirectoryNotFoundException => "找不到文件。请连接 Kindle 或重新选择 TSV。",
                UnauthorizedAccessException => "没有读取权限，请检查所选文件是否可访问。",
                _ => error.Message
            };
            SetNotice($"未能打开「{Path.GetFileName(path)}」。{reason}" +
                (session.History is null ? "" : "当前仍显示上次成功导入的数据。"));
        }
        finally
        {
            loading = false;
            OpenButton.IsEnabled = true;
            ReloadButton.IsEnabled = session.Path is not null;
        }
    }

    private void SetNotice(string message)
    {
        NoticeText.Text = message;
        Notice.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NavigateDaily(object sender, RoutedEventArgs args) => Navigate(Section.Daily);
    private void NavigateBooks(object sender, RoutedEventArgs args) => Navigate(Section.Books);
    private void NavigateTotal(object sender, RoutedEventArgs args) => Navigate(Section.Total);

    private void Navigate(Section destination)
    {
        section = destination;
        selectedBook = null;
        monthDetailMonth = null;
        recentWeeks = false;
        Render();
    }

    private void Render(bool resetScroll = true)
    {
        if (resetScroll) PageScroll.ScrollToTop();
        Page.Children.Clear();
        MarkSelected(DailyNav, section == Section.Daily);
        MarkSelected(BooksNav, section == Section.Books);
        MarkSelected(TotalNav, section == Section.Total);
        if (session.History is not { } history)
        {
            PageTitle("Kindle 阅读记录", "从现有的阅读记录开始");
            var empty = Card(Page);
            empty.Children.Add(Text("打开 reading-time.tsv", 22, bold: true));
            empty.Children.Add(Text("从 Kindle 的 reading-time 文件夹选择 reading-time.tsv，或将文件拖到窗口。", 14, muted: true, top: 8, wrap: true));
            empty.Children.Add(Text("仅浏览，原始记录不会被修改。", 13, muted: true, top: 8));
            return;
        }
        if (monthDetailMonth is { } detailMonth)
        {
            RenderMonthDetail(history, detailMonth);
            return;
        }
        switch (section)
        {
            case Section.Daily: RenderDaily(history); break;
            case Section.Books: RenderBooks(history); break;
            case Section.Total: RenderTotal(history); break;
        }
    }

    private static string Duration(long seconds) => seconds switch
    {
        >= 3600 => $"{seconds / 3600}小时{seconds % 3600 / 60}分{(seconds % 60 == 0 ? "" : $"{seconds % 60}秒")}",
        >= 60 => $"{seconds / 60}分{(seconds % 60 == 0 ? "" : $"{seconds % 60}秒")}",
        _ => $"{seconds}秒"
    };

    private static string MonthLabel(DateOnly date) => $"{date.Year} 年 {date.Month} 月";

    private void PageTitle(string title, string subtitle)
    {
        Page.Children.Add(Text(title, 30, bold: true, wrap: true));
        var description = Text(subtitle, 13, muted: true, top: 5, wrap: true);
        description.Margin = new(0, 5, 0, 18);
        Page.Children.Add(description);
    }

    private static Brush ColorBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static TextBlock Text(string text, double size = 14, bool bold = false, bool muted = false, double top = 0, bool wrap = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = muted ? Muted : Ink, Margin = new(0, top, 0, 0), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
    };

    private static StackPanel Card(Panel parent, double spacing = 0)
    {
        var panel = new StackPanel();
        parent.Children.Add(new Border { Background = Brushes.White, BorderBrush = Rule, BorderThickness = new(1),
            Padding = new(17), CornerRadius = new(11), Margin = new(0, spacing, 0, 0), Child = panel });
        return panel;
    }

    private static Button ActionButton(string caption, Action action, bool enabled = true, bool selected = false)
    {
        var button = new Button { Content = caption, IsEnabled = enabled };
        button.Click += (_, _) => action();
        MarkSelected(button, selected);
        return button;
    }

    private static void MarkSelected(Button button, bool selected)
    {
        button.Background = selected ? Ink : Brushes.White;
        button.BorderBrush = selected ? Ink : Rule;
        button.Foreground = selected ? Brushes.White : Ink;
        AutomationProperties.SetItemStatus(button, selected ? "已选中" : "");
    }

    private static void Metrics(Panel parent, params (string Label, string Value)[] values)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = values.Length };
        for (int index = 0; index < values.Length; index++)
        {
            var (label, value) = values[index];
            var stack = new StackPanel();
            stack.Children.Add(Text(label, 12, muted: true, wrap: true));
            stack.Children.Add(Text(value, 20, bold: true, top: 5, wrap: true));
            grid.Children.Add(new Border { Background = Soft, CornerRadius = new(7), Padding = new(11, 9, 11, 10),
                Margin = new(0, 0, index == values.Length - 1 ? 0 : 8, 0), Child = stack });
        }
        parent.Children.Add(grid);
    }

    private static void Spacing(Panel parent, double height = 13) => parent.Children.Add(new Border { Height = height });
}
