using System.Globalization;

namespace ReadingTimeManager.Core;

public sealed class HistoryEditException(string message) : InvalidOperationException(message);

public sealed record MergePreview(int BooksBefore, int BooksAfter, long Seconds, int ReadDaysBefore, int ReadDaysAfter,
    string Title, string Id);

public sealed partial class ReadingHistory
{
    public static bool CanKeepId(string id) => !MissingId(id) && !string.IsNullOrWhiteSpace(id) &&
        id.Equals(id.Trim(), StringComparison.Ordinal) && !id.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
        !id.Contains('\\');

    private static bool MissingId(string id) => id is "" or "unknown";

    public MergePreview PreviewMerge(IReadOnlyCollection<Book> selected, string title, string id)
    {
        ValidateMerge(selected, title, id);
        var active = selected.SelectMany(book => book.Days).Where(day => day.Value > 0).ToArray();
        return new(books.Length, books.Length - selected.Count + 1, active.Sum(day => day.Value),
            active.Length, active.Select(day => day.Key).Distinct().Count(), title, id);
    }

    public ReadingHistory MergeBooks(IReadOnlyCollection<Book> selected, string title, string id)
    {
        ValidateMerge(selected, title, id);
        var keys = selected.Select(book => BookKey(book.Id, book.Title)).ToHashSet();
        return new(rows.Select(row => keys.Contains(BookKey(row.Id, row.Title)) ? row with { Id = id, Title = title } : row).ToArray());
    }

    private void ValidateMerge(IReadOnlyCollection<Book> selected, string title, string id)
    {
        if (selected.Count < 2 || selected.Select(book => book.Number).Distinct().Count() != selected.Count ||
            selected.Any(book => book.Number < 1 || book.Number > books.Length || !ReferenceEquals(books[book.Number - 1], book)))
            throw new HistoryEditException("请从当前书籍列表勾选至少两本不同的书籍。");
        if (!CanKeepId(id))
            throw new HistoryEditException("请选择参与合并的有效书籍 ID。空 ID、unknown 和格式异常的 ID 不能作为目标。");
        if (books.Any(book => book.Id == id && !selected.Contains(book)))
            throw new HistoryEditException($"ID「{id}」已属于未参与合并的书籍。");
        if (!selected.Any(book => book.Id == id))
            throw new HistoryEditException("请从参与合并的书籍中选择要保留的 ID。");
        if (!selected.Any(book => book.UsableOriginalTitles.Contains(title, StringComparer.Ordinal)) || !UsableForMerge(title, id))
            throw new HistoryEditException("请选择参与书籍中的有效标题。Kindle 会把空标题、占位标题和等同于保留 ID 的标题显示为 ID。");

        foreach (var book in selected)
        {
            if (book.UsableOriginalTitles.Count > 1)
                throw new HistoryEditException($"ID「{book.Id}」对应多个原标题，无法确认这些记录是否属于同一本书，合并已阻止。");
            if (MissingId(book.Id) ? !UsableForMerge(book.Title, book.Id) : !CanKeepId(book.Id))
                throw new HistoryEditException($"「{book.Name}」的原始记录缺少可确认的书籍身份，合并已阻止。");
        }
    }

    private static bool UsableForMerge(string title, string id) =>
        Usable(title, id) && !string.IsNullOrWhiteSpace(title) &&
        // AWK compares two numeric fields by value, e.g. a title of 001 against an ID of 1.
        !(double.TryParse(title, NumberStyles.Float, CultureInfo.InvariantCulture, out var titleNumber) &&
          double.TryParse(id, NumberStyles.Float, CultureInfo.InvariantCulture, out var idNumber) &&
          double.IsFinite(titleNumber) && titleNumber == idNumber);

    public void Export(string destination, string sourcePath, ReadingHistory original)
    {
        destination = Path.GetFullPath(destination);
        if (destination.Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new HistoryEditException("请选择新的保存位置。导入的原文件受保护。");
        if (!Path.GetExtension(destination).Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            throw new HistoryEditException("请使用 .tsv 扩展名保存阅读记录。");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new HistoryEditException("该位置已有同名文件或文件夹，请换一个文件名或位置。");
        if (rows.SequenceEqual(original.rows))
            throw new HistoryEditException("当前阅读记录尚未修改。");

        VerifyTime(original);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".reading-time-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new StreamWriter(stream, Utf8, leaveOpen: true) { NewLine = "\n" };
                writer.WriteLine(Header);
                foreach (var row in rows)
                {
                    writer.Write(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(row.Id);
                    writer.Write('\t');
                    writer.Write(row.SecondsField);
                    writer.Write('\t');
                    writer.WriteLine(row.Title);
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            var reloaded = Load(temporary);
            reloaded.VerifyTime(original);
            if (!reloaded.rows.SequenceEqual(rows))
                throw new HistoryEditException("临时文件的记录与当前数据不一致，导出已阻止。");

            // No replacement, including hard links, symbolic links and files created during validation.
            File.Move(temporary, destination, overwrite: false);
            created = false;
        }
        finally
        {
            if (created) File.Delete(temporary);
        }
    }

    private void VerifyTime(ReadingHistory original)
    {
        const long largestExactAwkTotal = 9_007_199_254_740_991;
        var total = days.Values.Sum();
        if (total > largestExactAwkTotal)
            throw new HistoryEditException("累计秒数超过 Kindle AWK 可精确统计的范围，导出已阻止。");
        if (rows.Length != original.rows.Length || !days.SequenceEqual(original.days) ||
            total != original.days.Values.Sum() ||
            !rows.Select(row => (row.Date, row.SecondsField))
                .SequenceEqual(original.rows.Select(row => (row.Date, row.SecondsField))))
            throw new HistoryEditException("记录数量、日期或阅读秒数发生变化，导出已阻止。");
    }
}
