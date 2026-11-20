using ReadingTimeManager;
using ReadingTimeManager.Core;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private const string Header = "date\tbook_id\tseconds\ttitle\n";
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static readonly DateOnly Monday = new(2026, 9, 28);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is not (0 or 1 or 3))
            throw new ArgumentException("Usage: [screenshots-directory [awk-exe kindle-cache-script]]");
        var root = Path.Combine(Path.GetTempPath(), "rtm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(root, "synthetic-basic.tsv");
            File.WriteAllText(fixture, Header +
                "2026-09-28\tBOOK1\t4320\t合成样本：月球漫步\n" +
                "2026-09-27\tBOOK2\t2760\t合成样本：海洋图鉴\n");
            TestBasicFixture(fixture);
            var synthetic = Path.Combine(root, "合成阅读记录.tsv");
            var history = TestSynthetic(synthetic);
            TestInvalid(root);
            TestWpf(fixture, synthetic, root, args.FirstOrDefault());
            if (args.Length == 3) CompareAwk(history, synthetic, root, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            Console.WriteLine("PASS: synthetic fixtures, import/reload integrity, WPF navigation and off-screen layout.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void TestBasicFixture(string path)
    {
        var original = Hash(path);
        var history = ReadingHistory.Load(path);
        Equal(2, history.RowCount);
        var summary = history.Summarize(DateOnly.MinValue, Today);
        Equal(7080L, summary.Seconds);
        Equal(2, summary.ReadDays);
        Equal(2, summary.BookCount);
        Equal(3540L, summary.Average);
        Equal("合成样本：月球漫步", history.ReadOn(Monday).Single().Book.Name);
        Equal(4320L, history.Week(Monday, Today)[0].Seconds);
        Equal(2760L, history.RecentWeeks(Today)[^2].Seconds);
        Equal(original, Hash(path));
    }

    private static ReadingHistory TestSynthetic(string path)
    {
        File.WriteAllText(path, Header +
            "2026-09-27\tidA\t10\t1234567890ABCDEF\n" +
            "2026-09-28\tidA\t24\t《中文》 & \"星\" / 📖\n" +
            "2026-09-28\tidA\t6\t另一个名字\n" +
            "2026-09-28\tunknown\t300\t同名\n" +
            "2026-09-29\tunknown\t299\t另一本\n" +
            "2026-09-29\t\t31\t同名\n" +
            "2026-09-29\tidB\t0\t零秒\n" +
            "2026-09-30\tidA\t20\t\n" +
            "2026-10-01\tidA\t7200\t另一个名字\n" +
            "2024-02-29\t0a\t1800\t闰日", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var history = ReadingHistory.Load(path);
        Equal(10, history.RowCount);
        Equal(2490L, history.Summarize(DateOnly.MinValue, Today).Seconds);
        Equal(5, history.Summarize(DateOnly.MinValue, Today).BookCount);
        Equal(5, history.Summarize(DateOnly.MinValue, Today).ReadDays);
        Equal(Monday, history.Summarize(Monday, Today).PeakDay);
        Equal(330L, history.DaySeconds(Monday));
        Equal(330L, history.DaySeconds(Monday.AddDays(1)));
        Equal(0L, history.Week(Monday, Today)[3].Seconds);
        Equal(7200L, history.DaySeconds(Monday.AddDays(3)));
        Equal(0L, history.Year(2026, Today)[9].Seconds);
        Equal(690L, history.Year(2026, Today)[8].Seconds);
        Equal(0, ReadingHistory.Weekday(Monday));
        Equal(29, ReadingHistory.MonthEnd(new(2024, 2, 1)).Day);
        var book = history.ReadOn(Monday).Single(reading => reading.Book.Id == "idA").Book;
        Equal("《中文》 & \"星\" / 📖", book.Name);
        Equal(7260L, history.Detail(book, Today).Summary.Seconds);
        Equal(4, history.Detail(book, Today).Summary.ReadDays);
        Equal(1815L, history.Detail(book, Today).Summary.Average);
        Equal(60L, history.Detail(book, Today).RecentSeconds);
        Equal(2, history.ReadBooks(DateOnly.MinValue, Today).Count(reading => reading.Book.Name == "同名"));
        long[] threshold = [0, 1, 1799, 1800, 3599, 3600, 7199, 7200, 10799, 10800, 14399, 14400];
        int[] expected = [0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6];
        Equal(string.Join(',', expected), string.Join(',', threshold.Select(ReadingHistory.HeatLevel)));
        Equal("255,235,210,180,145,105,70", string.Join(',', Enumerable.Range(0, 7).Select(ReadingHistory.HeatGray)));
        return history;
    }

    private static void TestInvalid(string root)
    {
        var path = Path.Combine(root, "invalid.tsv");
        foreach (var text in new[] { "", Header, Header.Replace("\n", "\r\n"), "\uFEFF" })
        {
            File.WriteAllText(path, text);
            Equal(0, ReadingHistory.Load(path).RowCount);
        }
        foreach (var (text, error) in new (string, string)[]
        {
            ("id\tdate\tseconds\ttitle\n", "第 1 行"),
            (Header + "2026-09-28\tid\t2\n", "实际有 3 列"),
            (Header + "2026-09-28\tid\t2\t名\t尾\n", "实际有 5 列"),
            (Header + "2026-02-29\tid\t2\t不存在\n", "date"),
            (Header + "2026-09-28\tid\t-1\t负数", "seconds"),
            (Header + "2026-09-28\tid\t1.5\t小数", "seconds"),
            (Header + "2026-09-28\tid\t0\t嵌入\0", "控制字符"),
            (Header + $"2026-09-28\tid\t{long.MaxValue}\t有效\n2026-09-29\tid\t1\t溢出", "第 3 行")
        })
        {
            File.WriteAllText(path, text);
            Reject(path, error);
        }
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes(Header), 0xff]);
        Reject(path, "UTF-8");
        File.WriteAllText(path, Header + "2026-09-28\tid\t1\t有记录\r\n", Encoding.Unicode);
        Reject(path, "UTF-8");
        var manyYears = Header + string.Concat(Enumerable.Range(2000, 13).Select(year => $"{year}-01-01\tid\t60\t书\n"));
        File.WriteAllText(path, manyYears);
        var buckets = ReadingHistory.Load(path).AllYears(Today);
        Equal(11, buckets.Count);
        Equal(180L, buckets[0].Seconds);
        Equal("2003年", buckets[1].Label);
    }

    private static void TestWpf(string fixture, string synthetic, string root, string? snapshots)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(() => Today, remember: false);
        var surface = window.TestSurface;
        Pump(window.OpenAsync(Path.Combine(root, "missing.tsv")));
        Has(surface, "找不到文件");
        var emptyPath = Path.Combine(root, "empty.tsv");
        File.WriteAllText(emptyPath, Header);
        Pump(window.OpenAsync(emptyPath));
        Has(surface, "没有阅读记录");
        var hash = Hash(fixture);
        var modified = File.GetLastWriteTimeUtc(fixture);
        Pump(window.OpenAsync(fixture));
        Has(surface, "每日时长", "当日详情", "合成样本：月球漫步");
        Assert(window.Icon is not null, "missing window icon");
        Snap(surface, snapshots, "daily");
        Click(surface, button => AutomationProperties.GetName(button).StartsWith("2026-09-27"));
        Has(surface, "合成样本：海洋图鉴");
        Click(surface, button => AutomationProperties.GetName(button) == "上个月");
        Has(surface, "2026 年 8 月");
        Click(surface, button => Caption(button).Contains("查看月份详情"));
        Has(surface, "月份详情", "每日阅读趋势");
        Click(surface, button => Caption(button).Contains("阅读书籍"));
        Has(surface, "2 本书");
        Snap(surface, snapshots, "books");
        var bookList = Elements(surface).OfType<ListBox>().Single();
        var search = Elements(surface).OfType<TextBox>().Single();
        search.Text = "海洋";
        Equal(1, bookList.Items.Count);
        search.Text = "";
        Elements(surface).OfType<ComboBox>().Single().SelectedIndex = 2;
        Assert(bookList.Items[0].ToString()!.StartsWith("合成样本：月球漫步"), "recent sort");
        bookList.SelectedIndex = 0;
        var key = new KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new HeadlessSource(), 0, System.Windows.Input.Key.Enter)
        { RoutedEvent = UIElement.PreviewKeyDownEvent };
        bookList.RaiseEvent(key);
        Has(surface, "单书详情", "阅读进度和小时分布");
        Snap(surface, snapshots, "book-detail");
        Click(surface, button => Caption(button).Contains("累计时长"));
        Has(surface, "2026-09-28 至 10-04", "1小时12分");
        Snap(surface, snapshots, "total-week");
        Click(surface, button => Caption(button).Contains("最近 8 周趋势"));
        Has(surface, "每周阅读时长");
        Click(surface, button => Caption(button).Contains("返回累计时长"));
        Click(surface, button => Caption(button) == "按年");
        Has(surface, "2026 年", "1小时58分");
        Snap(surface, snapshots, "total-year");
        Click(surface, button => Caption(button) == "9月");
        Has(surface, "月份详情", "2026 年 9 月");
        Click(surface, button => Caption(button).Contains("返回累计时长"));
        Click(surface, button => Caption(button) == "全部历史");
        Has(surface, "1小时58分");
        Pump(window.ReloadAsync());
        Has(surface, "1小时58分");
        Equal(hash, Hash(fixture));
        Equal(modified, File.GetLastWriteTimeUtc(fixture));

        var broken = Path.Combine(root, "broken.tsv");
        File.WriteAllText(broken, Header + "2026-09-30\tid\t24\t正常\n损坏行\n");
        var brokenHash = Hash(broken);
        Pump(window.OpenAsync(broken));
        Has(surface, "第 3 行", "上次成功导入", "1小时58分");
        Equal(fixture, window.CurrentPath);
        Equal(brokenHash, Hash(broken));
        var before = Hash(synthetic);
        Pump(window.OpenAsync(synthetic));
        Has(surface, "未来日期", "《中文》 & \"星\" / 📖");
        Pump(window.ReloadAsync());
        Equal(before, Hash(synthetic));
        Snap(surface, snapshots, "daily-narrow", 867, 585, 1.5);
        Click(surface, button => Caption(button).Contains("阅读书籍"));
        var syntheticList = Elements(surface).OfType<ListBox>().Single();
        var limit = Elements(surface).OfType<CheckBox>().Single();
        Equal(5, syntheticList.Items.Count);
        limit.IsChecked = true;
        Equal(2, syntheticList.Items.Count);
        Click(surface, button => Caption(button) == "最近 7 天");
        Equal(1, syntheticList.Items.Count);
        application.Shutdown();
    }

    private static void CompareAwk(ReadingHistory history, string synthetic, string path, string awk, string script)
    {
        File.Copy(script, Path.Combine(path, "cache.awk"));
        var text = File.ReadAllText(synthetic, Encoding.UTF8);
        File.WriteAllText(Path.Combine(path, "input.tsv"), text, new UTF8Encoding(false));
        var start = new ProcessStartInfo(awk) { WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-F");
        start.ArgumentList.Add("\\t");
        string[] outputs = ["calendar", "summary", "months", "weeks", "days", "daybooks", "books", "books7", "booksmonth", "booksyear"];
        foreach (var variable in outputs.Select(name => $"{name}={name}").Prepend("today=2026-09-30"))
        {
            start.ArgumentList.Add("-v");
            start.ArgumentList.Add(variable);
        }
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("cache.awk");
        start.ArgumentList.Add("input.tsv");
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000)) throw new TimeoutException("AWK reference timed out.");
        Equal(0, process.ExitCode);
        var lines = File.ReadAllLines(Path.Combine(path, "days"));
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            Equal(long.Parse(fields[1]), history.DaySeconds(DateOnly.Parse(fields[0])));
        }
        var summary = history.Summarize(DateOnly.MinValue, Today);
        Equal($"{summary.Seconds}\t{summary.ReadDays}", File.ReadAllText(Path.Combine(path, "summary")).Trim());
        var expected = history.ReadBooks(DateOnly.MinValue, Today).Select(reading =>
            $"{reading.Seconds}\t{reading.Book.Title}\t{reading.Book.Id}\t{reading.Book.Number}").Order(StringComparer.Ordinal);
        Equal(string.Join('\n', expected), string.Join('\n', File.ReadAllLines(Path.Combine(path, "books")).Order(StringComparer.Ordinal)));
        Console.WriteLine("PASS: same synthetic input matches repository reading-insights-cache.awk (days, summary, books).");
    }

    private static void Snap(FrameworkElement surface, string? folder, string name, double width = 1220, double height = 780, double scale = 1)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        Equal(width, surface.ActualWidth);
        Equal(height, surface.ActualHeight);
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(output);
    }

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        foreach (var element in Elements(child)) yield return element;
    }

    private static string Caption(Button button) => button.Content as string ?? "";
    private static void Click(DependencyObject surface, Func<Button, bool> match) =>
        Elements(surface).OfType<Button>().First(match).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static void Has(DependencyObject surface, params string[] expected)
    {
        var text = string.Join('\n', Elements(surface).OfType<TextBlock>().Where(element => element.Visibility == Visibility.Visible).Select(element => element.Text));
        foreach (var value in expected) Assert(text.Contains(value, StringComparison.Ordinal), $"missing WPF text: {value}");
    }

    private static void Reject(string path, string expected)
    {
        try { ReadingHistory.Load(path); }
        catch (HistoryFormatException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"Expected format error: {expected}");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}; got {actual}");
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class HeadlessSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
