using ReadingTimeManager.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ReadingTimeManager;

public partial class MainWindow
{
    private void RenderTotal(ReadingHistory history)
    {
        if (recentWeeks) { RenderRecentWeeks(history); return; }
        PageTitle("累计时长", "按周、年或全部历史查看阅读趋势");
        var toolbar = new WrapPanel { Margin = new(0, 0, 0, 12) };
        Page.Children.Add(toolbar);
        (string Caption, TotalRange Range)[] ranges = [("按周", TotalRange.Week), ("按年", TotalRange.Year), ("全部历史", TotalRange.All)];
        foreach (var (caption, range) in ranges)
        {
            var button = ActionButton(caption, () => { totalRange = range; Render(); }, selected: range == totalRange);
            button.Margin = new(0, 0, 6, 0);
            toolbar.Children.Add(button);
        }

        var currentWeek = ReadingHistory.Monday(today);
        var weekEnd = ReadingHistory.Shift(week, 6);
        var weekMode = totalRange == TotalRange.Week;
        var start = totalRange switch { TotalRange.Week => week, TotalRange.Year => new(year, 1, 1), _ => DateOnly.MinValue };
        var end = totalRange switch { TotalRange.Week => weekEnd, TotalRange.Year => new(year, 12, 31), _ => today };
        var summary = history.Summarize(start, end < today ? end : today);
        Metrics(Card(Page), ("阅读时长", Duration(summary.Seconds)), ("阅读天数", $"{summary.ReadDays} 天"),
            ("阅读书籍", $"{summary.BookCount} 本"), ("日均（阅读日）", Duration(summary.Average)));

        var chart = Card(Page, 14);
        var title = new DockPanel();
        chart.Children.Add(title);
        if (totalRange != TotalRange.All)
        {
            var arrows = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(arrows, Dock.Right);
            arrows.Children.Add(Arrow("‹", weekMode ? "上一周" : "上一年", () => ChangeTotal(-1), weekMode ? week > DateOnly.MinValue : year > 1));
            arrows.Children.Add(Arrow("›", weekMode ? "下一周" : "下一年", () => ChangeTotal(1), weekMode ? week < currentWeek : year < today.Year));
            title.Children.Add(arrows);
        }
        var heading = Text(totalRange switch
        {
            TotalRange.Week => $"{week:yyyy-MM-dd} 至 {weekEnd:MM-dd}", TotalRange.Year => $"{year} 年", _ => "全部历史"
        }, 18, bold: true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(heading);

        switch (totalRange)
        {
            case TotalRange.Week:
                DrawChart(chart, history.Week(week, today), date =>
                {
                    selectedDay = date; month = ReadingHistory.MonthStart(date); Navigate(Section.Daily);
                });
                Spacing(chart);
                chart.Children.Add(ActionButton("最近 8 周趋势  ›", () => { recentWeeks = true; Render(); }));
                break;
            case TotalRange.Year:
                DrawChart(chart, history.Year(year, today), date => { monthDetailMonth = date; Render(); });
                break;
            default: DrawChart(chart, history.AllYears(today)); break;
        }
    }

    private void ChangeTotal(int delta)
    {
        if (totalRange == TotalRange.Week) week = week.AddDays(delta * 7);
        else year += delta;
        Render();
    }

    private void RenderRecentWeeks(ReadingHistory history)
    {
        AddBack("返回累计时长", () => { recentWeeks = false; Render(); });
        PageTitle("最近 8 周趋势", "周一为每周起点 · 本周仅统计到今天");
        var weeks = history.RecentWeeks(today);
        var current = weeks[^1].Seconds;
        var previous = weeks.Count > 1 ? weeks[^2].Seconds : 0;
        var difference = current - previous;
        var metrics = Card(Page);
        Metrics(metrics, ("本周时长", Duration(current)), ("上周时长", Duration(previous)),
            ("较上周", (difference > 0 ? "+" : difference < 0 ? "−" : "") + Duration(Math.Abs(difference))));
        Spacing(metrics, 8);
        Metrics(metrics, ("8 周平均", Duration(ReadingHistory.RoundedAverage(weeks.Sum(bucket => bucket.Seconds), 8, 60))),
            ("最高一周", Duration(weeks.Max(bucket => bucket.Seconds))),
            ("本周阅读", $"{history.Week(ReadingHistory.Monday(today), today).Count(day => day.Seconds > 0)} 天"));
        var chart = Card(Page, 14);
        chart.Children.Add(Text("每周阅读时长", 18, bold: true));
        DrawChart(chart, weeks, date => { week = date; totalRange = TotalRange.Week; recentWeeks = false; Render(); });
    }

    private static void DrawChart(Panel parent, IReadOnlyList<Bucket> buckets, Action<DateOnly>? open = null)
    {
        const double plotHeight = 171;
        var maxMinutes = buckets.Max(bucket => bucket.Seconds / 60);
        var target = Math.Max(1, Math.Ceiling(maxMinutes * 1.10 / 3));
        var step = target <= 15 ? Math.Ceiling(target / 5) * 5 : target <= 60 ? Math.Ceiling(target / 15) * 15 : Math.Ceiling(target / 60) * 60;
        var chart = new Grid { Height = plotHeight + 29, Margin = new(0, 16, 0, 0) };
        chart.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        chart.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        chart.RowDefinitions.Add(new() { Height = new(plotHeight) });
        chart.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var ticks = new Grid { Margin = new(0, 0, 8, 0) };
        Grid.SetRowSpan(ticks, 2);
        chart.Children.Add(ticks);
        var plot = new Grid();
        Grid.SetColumn(plot, 1);
        chart.Children.Add(plot);
        for (int index = 0; index <= 3; index++)
        {
            var top = plotHeight * (3 - index) / 3;
            var label = Text(ChartTick((long)(step * index)), 11, muted: true);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Margin = new(0, top - 7, 0, 0);
            ticks.Children.Add(label);
            plot.Children.Add(new Border { BorderBrush = Rule, BorderThickness = new(0, 1, 0, 0), Height = 1,
                VerticalAlignment = VerticalAlignment.Top, Margin = new(0, top, 0, 0) });
        }
        var columns = new UniformGrid { Rows = 1 };
        plot.Children.Add(columns);
        var labels = new UniformGrid { Rows = 1 };
        Grid.SetColumn(labels, 1);
        Grid.SetRow(labels, 1);
        chart.Children.Add(labels);
        for (int index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            var exact = $"{bucket.Label} · {Duration(bucket.Seconds)}";
            var height = plotHeight * (bucket.Seconds / 60) / (3 * step);
            var bar = new Border { Width = buckets.Count > 15 ? 8 : 27, CornerRadius = new(4, 4, 0, 0), Height = height > 0 ? Math.Max(3, height) : 0,
                Background = bucket.Current ? Ink : HeatColors[5], VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
            var slot = new Grid { Background = System.Windows.Media.Brushes.Transparent, ToolTip = exact };
            slot.Children.Add(bar);
            if (height > 0 && buckets.Count <= 12)
            {
                var value = Text(ChartTick(bucket.Seconds / 60), 11, muted: !bucket.Current);
                value.HorizontalAlignment = HorizontalAlignment.Center;
                value.VerticalAlignment = VerticalAlignment.Top;
                value.Margin = new(0, Math.Max(0, plotHeight - height - 19), 0, 0);
                slot.Children.Add(value);
            }
            columns.Children.Add(slot);
            var label = buckets.Count <= 15 || index == 0 || index == buckets.Count - 1 || (index + 1) % 5 == 0 ? bucket.Label : "";
            if (open is not null && bucket.Date is { } date)
            {
                var button = ActionButton(label, () => open(date));
                button.Padding = new(0);
                button.MinHeight = 0;
                button.FontSize = 11;
                button.Background = System.Windows.Media.Brushes.Transparent;
                button.BorderBrush = System.Windows.Media.Brushes.Transparent;
                button.ToolTip = exact;
                AutomationProperties.SetName(button, exact);
                button.IsTabStop = label.Length != 0;
                labels.Children.Add(button);
                slot.Cursor = System.Windows.Input.Cursors.Hand;
                slot.MouseLeftButtonUp += (_, _) => open(date);
            }
            else
            {
                var caption = Text(label, 11, muted: true, top: 7);
                caption.TextAlignment = TextAlignment.Center;
                caption.ToolTip = exact;
                labels.Children.Add(caption);
            }
        }
        parent.Children.Add(chart);
        if (maxMinutes == 0) parent.Children.Add(Text("本范围没有达到 1 分钟的柱值。悬停可查看精确秒数。", 12, muted: true, top: 4, wrap: true));
    }

    private static string ChartTick(long minutes) => minutes switch
    {
        0 => "0", < 60 => $"{minutes} 分", _ when minutes % 60 == 0 => $"{minutes / 60} 时", _ => $"{minutes / 60} 时 {minutes % 60} 分"
    };
}
