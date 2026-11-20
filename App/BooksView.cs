using ReadingTimeManager.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ReadingTimeManager;

public partial class MainWindow
{
    private sealed record BookRow(BookReading Reading)
    {
        public string Name => Reading.Book.Name;
        public string DaysText => $"{Reading.ReadDays} 个阅读日 · 最近 {Reading.LastDay:yyyy-MM-dd}";
        public string TimeText => Duration(Reading.Seconds);
        public override string ToString() => $"{Name}，{TimeText}，{DaysText}";
    }

    private void RenderBooks(ReadingHistory history)
    {
        if (selectedBook is { } book) { RenderBookDetail(history, book); return; }
        PageTitle("阅读书籍", "按书籍汇总 · 搜索、排序或打开单书详情");
        var card = Card(Page);
        var filters = new WrapPanel();
        card.Children.Add(filters);
        var toolbar = new Grid { Margin = new(0, 12, 0, 0) };
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var searchField = new TextBox { Text = search, Padding = new(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 34, BorderBrush = Rule, Margin = new(0, 0, 10, 0) };
        AutomationProperties.SetName(searchField, "搜索书名或书籍 ID");
        var searchBox = new Grid();
        searchBox.Children.Add(searchField);
        var hint = Text("搜索书名或书籍 ID", 13, muted: true);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new(9, 0, 0, 0);
        hint.IsHitTestVisible = false;
        searchBox.Children.Add(hint);
        toolbar.Children.Add(searchBox);
        var sort = new ComboBox { ItemsSource = new[] { "时长最长", "阅读天数最多", "最近阅读", "书名" },
            SelectedIndex = (int)bookOrder, MinWidth = 125, Padding = new(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(sort, "排序方式");
        Grid.SetColumn(sort, 1);
        toolbar.Children.Add(sort);
        card.Children.Add(toolbar);
        var minimum = new CheckBox { Content = "仅显示满 5 分钟的书籍（Kindle 筛选）", IsChecked = fiveMinutes, Margin = new(0, 13, 0, 0) };
        card.Children.Add(minimum);
        var status = Text("", 13, muted: true, top: 13);
        card.Children.Add(status);
        var list = new ListBox { ItemTemplate = (DataTemplate)FindResource("BookRow"), BorderThickness = new(0),
            Margin = new(0, 6, 0, 0), MaxHeight = 468 };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        list.ItemContainerStyle = itemStyle;
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(list, origin) is ListBoxItem { Content: BookRow row })
                OpenBook(row.Reading.Book);
        };
        list.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || list.SelectedItem is not BookRow row) return;
            args.Handled = true;
            OpenBook(row.Reading.Book);
        };
        AutomationProperties.SetName(list, "书籍列表，选择后按 Enter 查看详情");
        card.Children.Add(list);
        BookRange? lastRange = null;
        BookReading[] rangeReadings = [];
        (string Caption, BookRange Range)[] ranges = [("全部", BookRange.All), ("最近 7 天", BookRange.Recent), ("本月", BookRange.Month), ("本年", BookRange.Year)];
        foreach (var (caption, range) in ranges)
        {
            var button = ActionButton(caption, () => { bookRange = range; RefreshList(); }, selected: range == bookRange);
            button.Margin = new(0, 0, 6, 4);
            filters.Children.Add(button);
        }
        searchField.TextChanged += (_, _) => { search = searchField.Text; RefreshList(); };
        sort.SelectionChanged += (_, _) => { bookOrder = (BookOrder)sort.SelectedIndex; RefreshList(); };
        minimum.Checked += (_, _) => { fiveMinutes = true; RefreshList(); };
        minimum.Unchecked += (_, _) => { fiveMinutes = false; RefreshList(); };
        RefreshList();

        void RefreshList()
        {
            if (lastRange != bookRange)
            {
                var start = bookRange switch
                {
                    BookRange.Recent => ReadingHistory.Shift(today, -6), BookRange.Month => ReadingHistory.MonthStart(today),
                    BookRange.Year => new DateOnly(today.Year, 1, 1), _ => DateOnly.MinValue
                };
                rangeReadings = history.ReadBooks(start, today).ToArray();
                lastRange = bookRange;
            }
            var needle = search.Trim();
            var readings = rangeReadings.Where(reading => (!fiveMinutes || reading.Seconds >= 300) &&
                (reading.Book.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase) || reading.Book.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)));
            var sorted = bookOrder switch
            {
                BookOrder.Days => readings.OrderByDescending(reading => reading.ReadDays),
                BookOrder.Recent => readings.OrderByDescending(reading => reading.LastDay),
                BookOrder.Title => readings.OrderBy(reading => reading.Book.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => readings.OrderByDescending(reading => reading.Seconds)
            };
            var rows = sorted.ThenBy(reading => reading.Book.Number).Select(reading => new BookRow(reading)).ToArray();
            list.ItemsSource = rows;
            status.Text = rows.Length == 0 ? "没有符合当前条件的书籍" : $"{rows.Length:N0} 本书 · 时长和天数按所选范围统计";
            for (int index = 0; index < ranges.Length; index++) MarkSelected((Button)filters.Children[index], ranges[index].Range == bookRange);
        }
    }

    private void OpenBook(Book book)
    {
        selectedBook = book;
        var detail = session.History!.Detail(book, today);
        bookMonth = ReadingHistory.MonthStart(detail.LastDay);
        bookDay = detail.LastDay;
        Render();
    }

    private void RenderBookDetail(ReadingHistory history, Book book)
    {
        AddBack("返回阅读书籍", () => { selectedBook = null; Render(); });
        PageTitle(book.Name, book.Id is "" or "unknown" ? "单书详情 · 无有效书籍 ID" : $"单书详情 · {book.Id}");
        var detail = history.Detail(book, today);
        var stats = Card(Page);
        Metrics(stats, ("累计时长", Duration(detail.Summary.Seconds)), ("阅读天数", $"{detail.Summary.ReadDays} 天"),
            ("日均（阅读日）", Duration(detail.Summary.Average)));
        Spacing(stats, 8);
        Metrics(stats, ("首次阅读", $"{detail.FirstDay:yyyy-MM-dd}"), ("最近阅读", $"{detail.LastDay:yyyy-MM-dd}"),
            ("本月 / 最近 7 天", $"{Duration(detail.MonthSeconds)} / {Duration(detail.RecentSeconds)}"));
        stats.Children.Add(Text("阅读进度和小时分布不在此 TSV 中。", 12, muted: true, top: 11));
        var calendar = Card(Page, 14);
        MonthToolbar(calendar, bookMonth, offset => { bookMonth = bookMonth.AddMonths(offset); bookDay = bookMonth; Render(false); });
        DrawCalendar(calendar, bookMonth, date => book.Days.GetValueOrDefault(date), bookDay, date => { bookDay = date; Render(false); });
        calendar.Children.Add(Text($"{bookDay:yyyy-MM-dd} · {Duration(book.Days.GetValueOrDefault(bookDay))}", 14, bold: true, top: 9));
    }
}
