using ReadingTimeManager.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ReadingTimeManager;

public partial class MainWindow
{
    private static readonly string[] Weekdays = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
    private static readonly string[] HeatLabels = ["0", "<30分", "30分起", "1时起", "2时起", "3时起", "4时起"];
    private static readonly Brush[] HeatColors = Enumerable.Range(0, 7).Select(level =>
    {
        var gray = ReadingHistory.HeatGray(level);
        return ColorBrush(gray, gray, gray);
    }).ToArray();

    private void RenderDaily(ReadingHistory history)
    {
        PageTitle("每日时长", "月历热力图 · 选择日期查看当日读过的书籍");
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new() { Width = new(65, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(14) });
        columns.ColumnDefinitions.Add(new() { Width = new(35, GridUnitType.Star) });
        Page.Children.Add(columns);
        var calendarColumn = new StackPanel();
        var detailColumn = new StackPanel();
        Grid.SetColumn(detailColumn, 2);
        columns.Children.Add(calendarColumn);
        columns.Children.Add(detailColumn);
        var calendar = Card(calendarColumn);
        MonthToolbar(calendar, month, ChangeMonth, () =>
        {
            month = ReadingHistory.MonthStart(today);
            selectedDay = today;
            Render();
        });
        DrawCalendar(calendar, month, history.DaySeconds, selectedDay, date => { selectedDay = date; Render(false); });
        var side = Card(detailColumn);
        var summary = history.Summarize(month, ReadingHistory.MonthEnd(month));
        side.Children.Add(Text("本月概览", 18, bold: true));
        side.Children.Add(Text(Duration(summary.Seconds), 23, bold: true, top: 7, wrap: true));
        side.Children.Add(Text($"阅读 {summary.ReadDays} 天", 13, muted: true, top: 4));
        Spacing(side, 11);
        side.Children.Add(ActionButton("查看月份详情  ›", () => { monthDetailMonth = month; Render(); }));

        var detail = Card(detailColumn, 14);
        detail.Children.Add(Text("当日详情", 18, bold: true));
        detail.Children.Add(Text($"{selectedDay:yyyy-MM-dd} · {Weekdays[ReadingHistory.Weekday(selectedDay)]}", 13, muted: true, top: 6));
        var readings = history.ReadOn(selectedDay).Where(reading => reading.Seconds > 0).ToArray();
        detail.Children.Add(Text(Duration(history.DaySeconds(selectedDay)), 20, bold: true, top: 7, wrap: true));
        detail.Children.Add(Text(readings.Length == 0 ? "当天没有阅读记录" : $"阅读书籍 · {readings.Length} 本", 12, muted: true, top: 6));
        foreach (var reading in readings)
        {
            detail.Children.Add(new Border { Height = 1, Background = Rule, Margin = new(0, 10, 0, 7) });
            detail.Children.Add(Text(reading.Book.Name, bold: true, wrap: true));
            detail.Children.Add(Text(Duration(reading.Seconds), 13, muted: true, top: 3));
        }
    }

    private void ChangeMonth(int offset)
    {
        month = month.AddMonths(offset);
        selectedDay = month == ReadingHistory.MonthStart(today) ? today : month;
        Render();
    }

    private static void MonthToolbar(Panel parent, DateOnly displayed, Action<int> change, Action? current = null)
    {
        var toolbar = new DockPanel();
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(navigation, Dock.Right);
        if (current is not null)
        {
            var button = ActionButton("今天", current);
            button.Margin = new(0, 0, 6, 0);
            navigation.Children.Add(button);
        }
        navigation.Children.Add(Arrow("‹", "上个月", () => change(-1), displayed > DateOnly.MinValue));
        navigation.Children.Add(Arrow("›", "下个月", () => change(1), displayed < new DateOnly(9999, 12, 1)));
        toolbar.Children.Add(navigation);
        var title = Text(MonthLabel(displayed), 19, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(title);
        parent.Children.Add(toolbar);
    }

    private static Button Arrow(string caption, string label, Action action, bool enabled = true)
    {
        var button = ActionButton(caption, action, enabled);
        button.FontSize = 18;
        button.Padding = new(11, 2, 11, 2);
        button.Margin = new(0, 0, 4, 0);
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static void DrawCalendar(Panel parent, DateOnly displayed, Func<DateOnly, long> secondsForDay,
        DateOnly? selected, Action<DateOnly>? choose)
    {
        var headings = new UniformGrid { Columns = 7, Margin = new(0, 14, 0, 5) };
        foreach (var day in Weekdays)
        {
            var heading = Text(day, 12, muted: true);
            heading.HorizontalAlignment = HorizontalAlignment.Center;
            headings.Children.Add(heading);
        }
        parent.Children.Add(headings);
        var grid = new UniformGrid { Columns = 7 };
        var offset = ReadingHistory.Weekday(displayed);
        var monthDays = ReadingHistory.MonthEnd(displayed).Day;
        var slots = (offset + monthDays + 6) / 7 * 7;
        for (int index = 0; index < slots; index++)
        {
            int day = index - offset + 1;
            if (day < 1 || day > monthDays)
            {
                grid.Children.Add(new Border { Height = 49, CornerRadius = new(6), Background = Soft, Margin = new(2) });
                continue;
            }
            var date = displayed.AddDays(day - 1);
            var seconds = secondsForDay(date);
            var level = ReadingHistory.HeatLevel(seconds);
            var foreground = level >= 5 ? Brushes.White : Brushes.Black;
            var label = $"{date:yyyy-MM-dd} · {Duration(seconds)}";
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = day.ToString(), FontWeight = FontWeights.SemiBold, Foreground = foreground, TextAlignment = TextAlignment.Center });
            if (seconds > 0)
            {
                var compact = seconds >= 3600 ? $"{seconds / 3600}时" + (seconds < 360000 ? $"{seconds % 3600 / 60}分" : "") :
                    seconds >= 60 ? $"{seconds / 60}分" : $"{seconds}秒";
                stack.Children.Add(new TextBlock { Text = compact, FontSize = 11, Foreground = foreground, TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis });
            }
            var cell = new Button { Content = stack, Height = 49, Padding = new(2), Margin = new(2), ToolTip = label,
                Background = HeatColors[level], BorderBrush = date == selected ? Accent : Rule,
                BorderThickness = new(date == selected ? 2 : 1) };
            AutomationProperties.SetName(cell, label);
            if (choose is not null) cell.Click += (_, _) => choose(date);
            grid.Children.Add(cell);
        }
        parent.Children.Add(grid);
        var key = new WrapPanel { Margin = new(1, 13, 0, 0) };
        for (int level = 0; level < HeatLabels.Length; level++)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 8, 4) };
            item.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new(3), Background = HeatColors[level], BorderThickness = new(1), BorderBrush = Rule, Margin = new(0, 0, 4, 0) });
            item.Children.Add(Text(HeatLabels[level], 11, muted: true));
            key.Children.Add(item);
        }
        parent.Children.Add(key);
    }

    private void RenderMonthDetail(ReadingHistory history, DateOnly displayed)
    {
        AddBack(section == Section.Total ? "返回累计时长" : "返回每日时长", () => { monthDetailMonth = null; Render(); });
        PageTitle("月份详情", MonthLabel(displayed));
        var end = ReadingHistory.MonthEnd(displayed);
        var summary = history.Summarize(displayed, end);
        var ranked = history.ReadBooks(displayed, end).GroupBy(reading => reading.Book.Title)
            .Select(group => (Title: group.Key, Seconds: group.Sum(reading => reading.Seconds)))
            .OrderByDescending(group => group.Seconds).ThenBy(group => group.Title, StringComparer.Ordinal).ToArray();
        Metrics(Card(Page), ("本月时长", Duration(summary.Seconds)), ("阅读天数", $"{summary.ReadDays} 天"),
            ("日均（阅读日）", Duration(summary.Average)), ("阅读书名数", $"{ranked.Length} 本"));
        var chart = Card(Page, 14);
        chart.Children.Add(Text("每日阅读趋势", 18, bold: true));
        DrawChart(chart, Enumerable.Range(1, end.Day).Select(day =>
        {
            var date = displayed.AddDays(day - 1);
            return new Bucket(day.ToString(), history.DaySeconds(date), date, date == today);
        }).ToArray(), date => { selectedDay = date; month = displayed; Navigate(Section.Daily); });
        var rankings = Card(Page, 14);
        rankings.Children.Add(Text("当月读得最多", 18, bold: true));
        rankings.Children.Add(Text(summary.PeakDay is { } peak ? $"单日最高 · {peak:MM-dd} · {Duration(summary.PeakSeconds)}" : "本月没有阅读记录", 13, muted: true, top: 6));
        foreach (var (title, seconds) in ranked.Take(3))
            rankings.Children.Add(Text($"{(title.Length == 0 ? "未命名书籍" : title)}  ·  {Duration(seconds)}", top: 9, wrap: true));
    }

    private void AddBack(string label, Action action)
    {
        var back = ActionButton($"‹  {label}", action);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new(0, 0, 0, 12);
        Page.Children.Add(back);
    }
}
