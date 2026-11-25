using Microsoft.Win32;
using ReadingTimeManager.Core;
using System.IO;
using System.Security;

namespace ReadingTimeManager;

internal sealed class HistorySession
{
    private readonly Stack<ReadingHistory> undo = new();
    private ReadingHistory? original;

    public ReadingHistory? History { get; private set; }
    public string? Path { get; private set; }
    public bool CanUndo => undo.Count > 0;

    public async Task OpenAsync(string path)
    {
        var absolutePath = System.IO.Path.GetFullPath(path);
        var history = await Task.Run(() => ReadingHistory.Load(absolutePath));
        History = original = history;
        Path = absolutePath;
        undo.Clear();
    }

    public void Merge(IReadOnlyCollection<Book> books, string title, string id)
    {
        var previous = History ?? throw new HistoryEditException("请先导入 TSV。");
        var edited = previous.MergeBooks(books, title, id);
        undo.Push(previous);
        History = edited;
    }

    public bool Undo()
    {
        if (!undo.TryPop(out var previous)) return false;
        History = previous;
        return true;
    }

    public Task ExportAsync(string destination)
    {
        if (!CanUndo || History is not { } history || original is not { } baseline || Path is not { } source)
            throw new HistoryEditException("当前阅读记录尚未修改。");
        return Task.Run(() => history.Export(destination, source, baseline));
    }
}

internal static class LastFile
{
    private const string KeyName = @"Software\ReadingTimeManager";
    private const string ValueName = "LastTsvPath";

    public static string? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyName);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception error) when (IsRegistryError(error)) { return null; }
    }

    public static bool Write(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyName);
            key.SetValue(ValueName, path, RegistryValueKind.String);
            return true;
        }
        catch (Exception error) when (IsRegistryError(error)) { return false; }
    }

    private static bool IsRegistryError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException;
}
