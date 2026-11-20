using Microsoft.Win32;
using ReadingTimeManager.Core;
using System.IO;
using System.Security;

namespace ReadingTimeManager;

internal sealed class HistorySession
{
    public ReadingHistory? History { get; private set; }
    public string? Path { get; private set; }

    public async Task OpenAsync(string path)
    {
        var absolutePath = System.IO.Path.GetFullPath(path);
        var history = await Task.Run(() => ReadingHistory.Load(absolutePath));
        History = history;
        Path = absolutePath;
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
