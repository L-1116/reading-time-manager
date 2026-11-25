using ReadingTimeManager;
using ReadingTimeManager.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

internal static partial class Program
{
    private static readonly DateOnly First = DateOnly.MinValue;
    private static readonly DateOnly Last = DateOnly.MaxValue;

    private static (string Source, string Exported) TestEditing(string root)
    {
        var source = Path.Combine(root, "before-merge.tsv");
        string[] records =
        [
            "2026-09-26\tA\t00300\t纸上星辰",
            "2026-09-26\tA\t00300\t纸上星辰",
            "2026-09-26\tB\t600\t月面地图",
            "2026-09-27\tunknown\t500\t海洋航线",
            "2026-09-28\t\t900\t纸上星辰",
            "2026-09-28\tunknown\t240\t海洋航线",
            "2026-09-29\tB\t300\tB",
            "2026-09-30\tC\t720\t独立书籍",
            "2026-10-01\tA\t800\t",
            "2026-10-01\tB\t0\t月面地图",
            "2026-10-01\tZERO\t0\t零秒记录"
        ];
        File.WriteAllText(source, (Header + string.Join('\n', records)).Replace("\n", "\r\n"), new UTF8Encoding(true));
        var hash = Hash(source);
        var timestamp = File.GetLastWriteTimeUtc(source);
        var session = new HistorySession();
        session.OpenAsync(source).GetAwaiter().GetResult();
        var baseline = session.History!;
        Assert(!session.CanUndo, "fresh import contains undo steps");
        var a = BookWithId(baseline, "A");
        var b = BookWithId(baseline, "B");
        var c = BookWithId(baseline, "C");
        var unknown = BookWithId(baseline, "unknown");
        var empty = BookWithId(baseline, "");
        Book[] pair = [a, b];
        Equal(new MergePreview(6, 5, 2300, 4, 3, a.Title, b.Id), baseline.PreviewMerge(pair, a.Title, b.Id));
        RejectEdit(() => session.Merge(pair, a.Title, c.Id), "未参与");
        RejectEdit(() => session.Merge([a, a], a.Title, a.Id), "不同");
        RejectEdit(() => session.Merge(pair, "自行输入", b.Id), "有效标题");
        RejectEdit(() => session.Merge([unknown, empty], unknown.Title, "unknown"), "有效书籍 ID");
        RejectEdit(() => session.Merge([a, empty], a.Title, ""), "有效书籍 ID");
        Assert(!session.CanUndo, "rejected merge changed undo stack");

        session.Merge(pair, a.Title, b.Id);
        var first = session.History!;
        Equal(4, first.Summarize(First, Last).BookCount);
        Equal(2300L, first.Detail(BookWithId(first, "B"), Today).Summary.Seconds);
        Equal(1200L, first.ReadOn(new(2026, 9, 26)).Single().Seconds);
        CheckTime(baseline, first);

        var exported = Path.Combine(root, "reading-time.tsv");
        RejectEdit(() => AwaitExport(source.ToUpperInvariant()), "原文件");
        var hardLink = Path.Combine(root, "source-alias.tsv");
        Assert(CreateHardLink(hardLink, source, IntPtr.Zero), $"test hard link failed: {Marshal.GetLastWin32Error()}");
        RejectEdit(() => AwaitExport(hardLink), "同名");
        Equal(hash, Hash(hardLink));
        var failedDestination = Path.Combine(root, new string('a', 256) + ".tsv");
        try { AwaitExport(failedDestination); throw new InvalidOperationException("Expected export to fail"); }
        catch (IOException) { }
        Assert(!File.Exists(failedDestination) && !Directory.EnumerateFiles(root, ".reading-time-*.tmp").Any(),
            "failed export retained temporary or output files");
        var racedDestination = Path.Combine(root, "concurrent.tsv");
        var attempts = new[] { session.ExportAsync(racedDestination), session.ExportAsync(racedDestination) };
        try { Task.WhenAll(attempts).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is HistoryEditException or IOException) { }
        Equal(1, attempts.Count(attempt => attempt.IsCompletedSuccessfully));
        CheckTime(baseline, ReadingHistory.Load(racedDestination));
        Assert(!Directory.EnumerateFiles(root, ".reading-time-*.tmp").Any(), "competing export retained a temporary file");
        AwaitExport(exported);
        var outputText = File.ReadAllText(exported);
        var expected = records.Select(record =>
        {
            var fields = record.Split('\t');
            return fields[1] is "A" or "B" ? $"{fields[0]}\tB\t{fields[2]}\t纸上星辰" : record;
        });
        Equal(Header + string.Join('\n', expected) + "\n", outputText);
        Equal((byte)'d', File.ReadAllBytes(exported)[0]); // UTF-8 without BOM.
        var reopened = ReadingHistory.Load(exported);
        CheckTime(baseline, reopened);
        Equal(a.Title, BookWithId(reopened, "B").Title);
        Equal(c.Title, BookWithId(reopened, "C").Title);
        RejectEdit(() => AwaitExport(exported), "同名");
        RejectEdit(() => reopened.Export(Path.Combine(root, "unmodified.tsv"), exported, reopened), "尚未修改");

        Book[] triple = [BookWithId(first, "B"), BookWithId(first, "unknown"), BookWithId(first, "")];
        Equal(new MergePreview(5, 3, 3940, 6, 5, unknown.Title, b.Id), first.PreviewMerge(triple, unknown.Title, b.Id));
        session.Merge(triple, unknown.Title, b.Id);
        var second = session.History!;
        CheckTime(baseline, second);
        Equal(2, second.Summarize(First, Last).BookCount);
        Equal(3940L, second.Detail(BookWithId(second, "B"), Today).Summary.Seconds);
        Equal(5, second.Detail(BookWithId(second, "B"), Today).Summary.ReadDays);
        Equal(1140L, second.ReadOn(Monday).Single().Seconds);
        Assert(session.Undo() && ReferenceEquals(first, session.History), "first undo did not restore the previous statistics");
        Equal(a.Title, BookWithId(session.History!, "B").Title);
        Assert(session.Undo() && ReferenceEquals(baseline, session.History), "second undo did not restore original statistics");
        Equal(5, session.History!.Summarize(First, Last).BookCount);
        Equal(a.Title, BookWithId(session.History, "A").Title);
        Equal(b.Title, BookWithId(session.History, "B").Title);
        Assert(!session.Undo() && !session.CanUndo, "undo stack did not empty");
        Equal(outputText, File.ReadAllText(exported));
        session.Merge(pair, a.Title, b.Id);
        session.OpenAsync(source).GetAwaiter().GetResult();
        Assert(!session.CanUndo, "re-import did not clear history");
        Equal(hash, Hash(source));
        Equal(timestamp, File.GetLastWriteTimeUtc(source));

        var ambiguous = Path.Combine(root, "conflict.tsv");
        File.WriteAllText(ambiguous, Header + "2026-09-26\tX\t10\t标题一\n2026-09-26\tX\t20\t标题二\n" +
            "2026-09-27\tY\t30\t独立标题\n2026-09-28\t\t10\t\n2026-09-28\tUNKNOWN\t10\t占位\n" +
            "2026-09-28\t001\t10\t002\n2026-09-28\t2\t10\t其他\n");
        var conflict = ReadingHistory.Load(ambiguous);
        var x = BookWithId(conflict, "X");
        var y = BookWithId(conflict, "Y");
        Equal(30L, conflict.Detail(x, Today).Summary.Seconds); // Existing Kindle identity aggregation.
        RejectEdit(() => conflict.MergeBooks([x, y], x.Title, y.Id), "多个原标题");
        RejectEdit(() => conflict.MergeBooks([BookWithId(conflict, ""), y], y.Title, y.Id), "缺少可确认");
        RejectEdit(() => conflict.MergeBooks([BookWithId(conflict, "UNKNOWN"), y], y.Title, "UNKNOWN"), "有效书籍 ID");
        RejectEdit(() => conflict.MergeBooks([BookWithId(conflict, "001"), BookWithId(conflict, "2")], "002", "2"), "有效标题");
        RejectEdit(() => first.Export(Path.Combine(root, "bad-baseline.tsv"), source, conflict), "秒数发生变化");
        var oversizedPath = Path.Combine(root, "oversized.tsv");
        File.WriteAllText(oversizedPath, Header + "2026-09-26\tX\t9007199254740991\t标题一\n2026-09-26\tY\t2\t标题二\n");
        var oversized = ReadingHistory.Load(oversizedPath);
        var oversizedMerge = oversized.MergeBooks([BookWithId(oversized, "X"), BookWithId(oversized, "Y")], "标题一", "X");
        RejectEdit(() => oversizedMerge.Export(Path.Combine(root, "oversized-export.tsv"), oversizedPath, oversized), "AWK 可精确");
        Console.WriteLine("PASS: two- and three-book merges, identity validation, undo, round-trip, time invariants, no-clobber and failure cleanup.");
        return (source, exported);

        void AwaitExport(string path) => session.ExportAsync(path).GetAwaiter().GetResult();
    }

    private static void TestKindleEditing(string source, string exported, string root, string awk, string script, string[] daemonArgs)
    {
        var before = CompareAwk(ReadingHistory.Load(source), source, Path.Combine(root, "before-awk"), awk, script);
        var after = CompareAwk(ReadingHistory.Load(exported), exported, Path.Combine(root, "after-awk"), awk, script);
        foreach (var key in new[] { "calendar", "summary", "months", "weeks", "days" })
            Equal(string.Join('\n', before[key]), string.Join('\n', after[key]));
        Equal("1500\t纸上星辰\tB\t1", after["books"].Single(line => line.Contains("\tB\t")));
        Equal(3, after["daybooks"].Count(line => line.Contains("\tB\t") && long.Parse(line.Split('\t')[1]) > 0));
        foreach (var key in new[] { "books", "books7", "booksmonth", "booksyear", "daybooks" })
            Equal(string.Join('\n', before[key].Where(IsIndependent).Select(WithoutSequence)),
                string.Join('\n', after[key].Where(IsIndependent).Select(WithoutSequence)));

        foreach (var (daemon, index) in daemonArgs.Skip(1).Select((path, index) => (path, index)))
        {
            var sandbox = Path.Combine(root, $"daemon-{index}");
            Directory.CreateDirectory(sandbox);
            var append = Path.Combine(sandbox, "reading-time.tsv");
            File.Copy(exported, append);
            RunDaemonAppend(Path.GetFullPath(daemonArgs[0]), daemon, sandbox);
            var bytes = File.ReadAllBytes(append);
            Assert(bytes.AsSpan().StartsWith(File.ReadAllBytes(exported)), "daemon rewrote history");
            var restarted = ReadingHistory.Load(append);
            Equal(13, restarted.RowCount);
            Equal(135L, restarted.DaySeconds(Today) - ReadingHistory.Load(exported).DaySeconds(Today));
            Equal("纸上星辰", BookWithId(restarted, "B").Title);
            Equal("Original_A", BookWithId(restarted, "A").Title);
            Equal(60L, restarted.Detail(BookWithId(restarted, "A"), Today).Summary.Seconds);
            Equal(2375L, restarted.Detail(BookWithId(restarted, "B"), Today).Summary.Seconds);
            CompareAwk(restarted, append, Path.Combine(sandbox, "appended-awk"), awk, script);
            Console.WriteLine($"PASS: daemon {Path.GetFileName(daemon)} appends existing/new cdeKey and flushes pending seconds on TERM.");
        }
        Console.WriteLine("PASS: Kindle cumulative, day, month, week, per-book/day caches and unaffected book rows.");

        static bool IsIndependent(string line) => line.Contains("\tC\t", StringComparison.Ordinal);
        static string WithoutSequence(string line) => line[..line.LastIndexOf('\t')];
    }

    private static void RunDaemonAppend(string shell, string script, string sandbox)
    {
        // Use the real daemon bootstrap, metadata parser, flush, report and TERM handlers.
        // The device polling loop is replaced with fixed metadata and accumulated buckets.
        var source = File.ReadAllText(script).Replace("\r\n", "\n");
        var trap = source.IndexOf("trap cleanup EXIT\n", StringComparison.Ordinal);
        Assert(trap >= 0, "daemon cleanup trap not found");
        var prefix = source[..(trap + "trap cleanup EXIT\n".Length)]
            .Replace("BASE=\"/mnt/us/reading-time\"", "BASE=\"$PWD\"", StringComparison.Ordinal)
            .Replace("${READING_BASE:-/mnt/us/reading-time}", "$PWD", StringComparison.Ordinal);
        Assert(!prefix.Contains("/mnt/us/reading-time"), "daemon base was not relocated");
        var usesJournal = prefix.Contains("/reading-data.sh", StringComparison.Ordinal);
        var intervals = Path.Combine(sandbox, "reading-intervals.tsv");
        const string intervalPrefix = "start_epoch\tseconds\tbook_id\ttitle\tutc_offset\trun_id\n" +
            "1790294400\t60\tA\tOriginal_A\t0\tprevious\n";
        if (usesJournal)
        {
            var helpers = Directory.CreateDirectory(Path.Combine(sandbox, "bin"));
            var helperSource = Path.Combine(Path.GetDirectoryName(script)!, "reading-data.sh");
            File.WriteAllText(Path.Combine(helpers.FullName, "reading-data.sh"), File.ReadAllText(helperSource).Replace("\r\n", "\n"));
            File.WriteAllText(intervals, intervalPrefix);
        }
        var midnight = new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(sandbox, "append.sh"), prefix + $$"""

            record_bucket() {
                if type rr_credit >/dev/null 2>&1; then
                    rr_credit "$1" "$2" "$book_id" "$book_title"
                else
                    bucket_date={{Today:yyyy-MM-dd}}; bucket_id="$book_id"; bucket_title="$book_title"; bucket="$2"
                fi
            }
            read_book 'file:///mnt/us/documents/Original_A_A.kfx' '{"cdeKey":"A"}'
            record_bucket {{midnight}} 60
            flush
            read_book 'file:///mnt/us/documents/Original_B_B.kfx' '{"cdeKey":"B"}'
            record_bucket {{midnight + 600}} 75
            kill -TERM $$
            exit 19
            """ + "\n");
        var start = new ProcessStartInfo(shell) { WorkingDirectory = sandbox, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("PATH=/usr/bin:/bin:$PATH; export PATH; READING_RELEASE=. TZ=UTC /bin/sh append.sh");
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Daemon fixture timed out.");
        }
        Equal(0, process.ExitCode);
        if (usesJournal)
        {
            Assert(!Directory.Exists(Path.Combine(sandbox, ".reading-write-v983")), "daemon left a pending journal after TERM");
            Assert(File.ReadAllText(intervals).StartsWith(intervalPrefix, StringComparison.Ordinal), "daemon rewrote old interval IDs");
            Equal(4, File.ReadAllLines(intervals).Length);
        }
    }

    private static void CheckTime(ReadingHistory expected, ReadingHistory actual)
    {
        Equal(expected.RowCount, actual.RowCount);
        Equal(expected.Summarize(First, Last).Seconds, actual.Summarize(First, Last).Seconds);
        Equal(expected.Summarize(First, Last).ReadDays, actual.Summarize(First, Last).ReadDays);
        for (var day = expected.FirstRecordedDay!.Value; day <= expected.LastRecordedDay; day = day.AddDays(1))
            Equal(expected.DaySeconds(day), actual.DaySeconds(day));
    }

    private static void TestWpfEditing(MainWindow window, FrameworkElement surface, string fixture, string? snapshots)
    {
        Assert(!ButtonFor("撤销").IsEnabled && !ButtonFor("导出 TSV").IsEnabled, "fresh import edit buttons");
        Click(surface, button => Caption(button) == "合并书籍");
        Has(surface, "已勾选 0 本");
        var preview = ButtonFor("预览合并…");
        Assert(!preview.IsEnabled, "preview without two selected books");
        Snap(surface, null, "merge-selection");
        var list = Elements(surface).OfType<ListBox>().Single();
        var boxes = Visuals(list).OfType<CheckBox>().Where(box => box.Visibility == Visibility.Visible).ToArray();
        Equal(2, boxes.Length);
        boxes[0].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Has(surface, "已勾选 1 本");
        Assert(!preview.IsEnabled, "preview after selecting one book");
        var searchBox = Elements(surface).OfType<TextBox>().Single();
        searchBox.Text = "海洋";
        Has(surface, "已勾选 1 本");
        searchBox.Text = "";
        Snap(surface, null, "restore-filter");
        boxes = Visuals(list).OfType<CheckBox>().Where(box => box.Visibility == Visibility.Visible).ToArray();
        Equal(1, boxes.Count(box => box.IsChecked == true));
        boxes.Single(box => box.IsChecked == false).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Assert(preview.IsEnabled, "preview remained disabled after selecting two books");
        Snap(surface, snapshots, "merge-selection");

        var history = ReadingHistory.Load(fixture);
        var readings = history.ReadBooks(First, Last).ToArray();
        var dialog = new MainWindow.MergeWindow(history, readings, (Style)window.FindResource(typeof(Button)));
        var dialogContent = (FrameworkElement)dialog.Content;
        var choices = Elements(dialogContent).OfType<ComboBox>().ToArray();
        Equal(2, choices.Length);
        choices[0].SelectedIndex = 1;
        choices[1].SelectedIndex = 0;
        Equal(readings[1].Book.Title, dialog.Preview!.Title);
        Equal(readings[0].Book.Id, dialog.Preview!.Id);
        Has(dialogContent, "最终 ID", "原始封面", "同一天只算一个阅读日", "重新出现独立条目");
        Snap(dialogContent, snapshots, "merge-confirm", 730, 740);
        window.ApplySelectedMerge(dialog.Preview.Title, dialog.Preview.Id);
        Has(surface, "已合并 2 本书", "1 本书");
        Assert(ButtonFor("撤销").IsEnabled && ButtonFor("导出 TSV").IsEnabled, "edit buttons disabled after merge");
        Click(surface, button => Caption(button).Contains("每日时长"));
        Has(surface, readings[1].Book.Title);
        Click(surface, button => Caption(button).Contains("累计时长"));
        Has(surface, "1小时12分");
        Click(surface, button => Caption(button) == "撤销");
        Has(surface, "已撤销", "1小时12分");
        Assert(!ButtonFor("撤销").IsEnabled && !ButtonFor("导出 TSV").IsEnabled, "edit buttons enabled after undo");
        Click(surface, button => Caption(button).Contains("阅读书籍"));
        Has(surface, "2 本书");

        Button ButtonFor(string caption) => Elements(surface).OfType<Button>().Single(button => Caption(button) == caption);
        static IEnumerable<DependencyObject> Visuals(DependencyObject root)
        {
            yield return root;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index)))
                yield return child;
        }
    }

    private static Book BookWithId(ReadingHistory history, string id) =>
        history.ReadBooks(First, Last).Single(reading => reading.Book.Id == id).Book;

    private static void RejectEdit(Action operation, string message)
    {
        try { operation(); }
        catch (HistoryEditException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"Expected edit error: {message}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string file, IntPtr reserved);
}
