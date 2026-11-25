using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace ReadingTimeManager.Core;

public sealed class HistoryFormatException(string message) : FormatException(message);

public sealed record Book(int Number, string Id, string Title, IReadOnlyDictionary<DateOnly, long> Days)
{
    public string Name => Title.Length == 0 ? "未命名书籍" : Title;
    public IReadOnlyList<string> UsableOriginalTitles { get; internal init; } = Array.Empty<string>();
}

public sealed record BookReading(Book Book, long Seconds, int ReadDays, DateOnly LastDay);
public sealed record Summary(long Seconds, int ReadDays, int BookCount, long Average, DateOnly? PeakDay, long PeakSeconds);
public sealed record BookDetail(Summary Summary, DateOnly FirstDay, DateOnly LastDay, long MonthSeconds, long RecentSeconds);
public sealed record Bucket(string Label, long Seconds, DateOnly? Date = null, bool Current = false);

public sealed partial class ReadingHistory
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Header = "date\tbook_id\tseconds\ttitle";
    private static readonly byte[] Grays = [255, 235, 210, 180, 145, 105, 70];
    private readonly SortedDictionary<DateOnly, long> days = new();
    private readonly Dictionary<DateOnly, List<BookReading>> byDay = new();
    private readonly Book[] books;
    private readonly HistoryRow[] rows;

    private ReadingHistory(HistoryRow[] rows)
    {
        this.rows = rows;
        var entries = new Dictionary<(string Id, string TitleKey), Entry>();
        foreach (var row in rows)
        {
            var key = BookKey(row.Id, row.Title);
            if (!entries.TryGetValue(key, out var entry))
                entries.Add(key, entry = new(entries.Count + 1, row.Id, row.Title));
            if (Usable(row.Title, row.Id) && entry.Titles.Add(row.Title) && entry.Titles.Count == 1)
                entry.Title = row.Title;
            entry.Days[row.Date] = entry.Days.GetValueOrDefault(row.Date) + row.Seconds;
        }
        books = entries.Values.Select(entry => new Book(entry.Number, entry.Id,
            Usable(entry.Title, entry.Id) ? entry.Title : entry.Id,
            new ReadOnlyDictionary<DateOnly, long>(entry.Days))
        {
            UsableOriginalTitles = Array.AsReadOnly(entry.Titles.ToArray())
        }).ToArray();
        foreach (var book in books)
        foreach (var (date, seconds) in book.Days)
        {
            days[date] = days.GetValueOrDefault(date) + seconds;
            if (!byDay.TryGetValue(date, out var readings)) byDay.Add(date, readings = []);
            readings.Add(new(book, seconds, seconds > 0 ? 1 : 0, date));
        }
    }

    public int RowCount => rows.Length;
    public DateOnly? FirstRecordedDay => days.Count == 0 ? null : days.First().Key;
    public DateOnly? LastRecordedDay => days.Count == 0 ? null : days.Last().Key;
    public DateOnly? LastReadingAtOrBefore(DateOnly date) => days.Reverse()
        .Where(day => day.Key <= date && day.Value > 0).Select(day => (DateOnly?)day.Key).FirstOrDefault();
    public long DaySeconds(DateOnly date) => days.GetValueOrDefault(date);

    public static ReadingHistory Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
        try { return Parse(reader); }
        catch (DecoderFallbackException) { throw new HistoryFormatException("文件不是有效的 UTF-8 文本。请选择 Kindle 原始 TSV。"); }
    }

    private static ReadingHistory Parse(TextReader reader)
    {
        var header = reader.ReadLine();
        if (header is null) return new([]);
        if (header.StartsWith('\uFEFF')) header = header[1..];
        if (header.Length == 0 && reader.Peek() < 0) return new([]);
        if (header != Header)
            throw Invalid(1, "表头应为 date、book_id、seconds、title（以制表符分隔）。");

        var rows = new List<HistoryRow>();
        long total = 0;
        int lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0) continue;
            var fields = line.Split('\t');
            if (fields.Length != 4) throw Invalid(lineNumber, $"应有 4 列，实际有 {fields.Length} 列。");
            if (line.Any(character => character != '\t' && char.IsControl(character)))
                throw Invalid(lineNumber, "包含不支持的控制字符。");
            if (!DateOnly.TryParseExact(fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw Invalid(lineNumber, "date 不是有效的 YYYY-MM-DD 日期。");
            if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                throw Invalid(lineNumber, "seconds 应为可表示的非负整数秒数。");
            if (seconds > long.MaxValue - total) throw Invalid(lineNumber, "累计秒数超出可表示的范围。");
            total += seconds;

            rows.Add(new(date, fields[1], seconds, fields[2], fields[3]));
        }
        return new(rows.ToArray());
    }

    private static (string Id, string TitleKey) BookKey(string id, string title) =>
        (id, MissingId(id) ? title : "");

    private static bool Usable(string title, string id) => title is not ("" or "unknown") && title != id &&
        !(title.Length >= 16 && title.All(char.IsAsciiHexDigit));

    private static HistoryFormatException Invalid(int line, string reason) => new($"第 {line} 行：{reason}");

    public IEnumerable<BookReading> ReadOn(DateOnly date) => byDay.TryGetValue(date, out var readings)
        ? readings.OrderByDescending(reading => reading.Seconds).ThenBy(reading => reading.Book.Number)
        : [];

    public IEnumerable<BookReading> ReadBooks(DateOnly start, DateOnly end) => books.Select(book =>
    {
        var active = book.Days.Where(day => day.Value > 0 && day.Key >= start && day.Key <= end).ToArray();
        return new BookReading(book, active.Sum(day => day.Value), active.Length,
            active.Length == 0 ? DateOnly.MinValue : active.Max(day => day.Key));
    }).Where(reading => reading.Seconds > 0);

    public Summary Summarize(DateOnly start, DateOnly end, Book? book = null)
    {
        var values = book?.Days ?? days;
        long total = 0, peak = 0;
        int count = 0;
        DateOnly? peakDay = null;
        foreach (var (date, seconds) in values)
        {
            if (date < start || date > end || seconds == 0) continue;
            total += seconds;
            count++;
            if (seconds > peak || (seconds == peak && date < peakDay))
            {
                peak = seconds;
                peakDay = date;
            }
        }
        var bookCount = book is null ? books.Count(item => item.Days.Any(day => day.Value > 0 && day.Key >= start && day.Key <= end)) : count > 0 ? 1 : 0;
        var average = book is null ? RoundedAverage(total, count, 60) : RoundedAverage(total, count, 1);
        return new(total, count, bookCount, average, peakDay, peak);
    }

    public BookDetail Detail(Book book, DateOnly today)
    {
        var active = book.Days.Where(day => day.Value > 0).Select(day => day.Key).ToArray();
        var month = MonthStart(today);
        return new(Summarize(DateOnly.MinValue, DateOnly.MaxValue, book), active.Min(), active.Max(),
            book.Days.Where(day => MonthStart(day.Key) == month).Sum(day => day.Value),
            book.Days.Where(day => day.Key >= Shift(today, -6) && day.Key <= today).Sum(day => day.Value));
    }

    public IReadOnlyList<Bucket> Week(DateOnly monday, DateOnly today)
    {
        string[] labels = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
        return labels.Select((label, index) =>
        {
            var number = monday.DayNumber + index;
            if (number > DateOnly.MaxValue.DayNumber) return new Bucket(label, 0);
            var date = DateOnly.FromDayNumber(number);
            return new Bucket(label, date <= today ? DaySeconds(date) : 0, date, date == today);
        }).ToArray();
    }

    public IReadOnlyList<Bucket> Year(int year, DateOnly today)
    {
        var values = days.Where(day => day.Key.Year == year && day.Key <= today).ToLookup(day => day.Key.Month);
        return Enumerable.Range(1, 12).Select(month => new Bucket($"{month}月", values[month].Sum(day => day.Value),
            new DateOnly(year, month, 1), today.Year == year && today.Month == month)).ToArray();
    }

    public IReadOnlyList<Bucket> AllYears(DateOnly today)
    {
        var years = days.Where(day => day.Key <= today).GroupBy(day => day.Key.Year)
            .Select(year => new Bucket($"{year.Key}年", year.Sum(day => day.Value))).ToList();
        if (years.Count == 0) return [new("全部", 0)];
        if (years.Count <= 12) return years;
        return [new("更早", years.Take(years.Count - 10).Sum(year => year.Seconds)), .. years.TakeLast(10)];
    }

    public IReadOnlyList<Bucket> RecentWeeks(DateOnly today)
    {
        var monday = Monday(today);
        return Enumerable.Range(-7, 8).Select(offset => monday.DayNumber + offset * 7)
            .Where(number => number >= 0).Select(DateOnly.FromDayNumber).Select(date =>
                new Bucket($"{date.Month}/{date.Day}", Week(date, today).Sum(day => day.Seconds), date, date == monday)).ToArray();
    }

    public static long RoundedAverage(long seconds, int count, int precision)
    {
        if (count == 0) return 0;
        var unit = (long)count * precision;
        var rounded = (seconds / unit) + (seconds % unit >= (unit + 1) / 2 ? 1 : 0);
        return rounded > long.MaxValue / precision ? long.MaxValue : rounded * precision;
    }

    public static DateOnly Shift(DateOnly date, int days) =>
        DateOnly.FromDayNumber(Math.Clamp(date.DayNumber + days, 0, DateOnly.MaxValue.DayNumber));
    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);
    public static DateOnly MonthEnd(DateOnly date) => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
    public static int Weekday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;
    public static DateOnly Monday(DateOnly date) => date.AddDays(-Weekday(date));
    public static int HeatLevel(long seconds) => seconds switch
    {
        <= 0 => 0, < 1800 => 1, < 3600 => 2, < 7200 => 3, < 10800 => 4, < 14400 => 5, _ => 6
    };
    public static byte HeatGray(int level) => Grays[level];

    private sealed class Entry(int number, string id, string title)
    {
        public int Number { get; } = number;
        public string Id { get; } = id;
        public string Title { get; set; } = title;
        public HashSet<string> Titles { get; } = new(StringComparer.Ordinal);
        public Dictionary<DateOnly, long> Days { get; } = new();
    }

    private sealed record HistoryRow(DateOnly Date, string Id, long Seconds, string SecondsField, string Title);
}
