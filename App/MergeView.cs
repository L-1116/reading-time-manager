using Microsoft.Win32;
using ReadingTimeManager.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ReadingTimeManager;

public partial class MainWindow
{
    private readonly HashSet<int> mergeSelection = [];
    private bool mergeMode;
    private Button? previewMergeButton;
    private TextBlock? mergeSelectionStatus;

    private void AddMergeToolbar(StackPanel card, ReadingHistory history)
    {
        var tools = new WrapPanel { Margin = new(0, 0, 0, 12) };
        card.Children.Add(tools);
        if (!mergeMode)
        {
            tools.Children.Add(ActionButton("合并书籍", () => { mergeMode = true; Render(false); },
                history.ReadBooks(DateOnly.MinValue, today).Take(2).Count() == 2));
            return;
        }
        var cancel = ActionButton("取消选择", () => { EndMergeSelection(); Render(false); });
        cancel.Margin = new(8, 0, 10, 0);
        previewMergeButton = ActionButton("预览合并…", () => ShowMergeDialog(history), selected: true);
        mergeSelectionStatus = Text("", 13, muted: true, top: 8, wrap: true);
        tools.Children.Add(previewMergeButton);
        tools.Children.Add(cancel);
        tools.Children.Add(mergeSelectionStatus);
        UpdateMergeSelection();
    }

    private void SetMergeChecked(Book book, bool selected)
    {
        if (!mergeMode) return;
        if (selected) mergeSelection.Add(book.Number);
        else mergeSelection.Remove(book.Number);
        UpdateMergeSelection();
    }

    private void UpdateMergeSelection()
    {
        previewMergeButton!.IsEnabled = mergeSelection.Count >= 2;
        mergeSelectionStatus!.Text = $"已勾选 {mergeSelection.Count} 本 · 至少选择 2 本，筛选时保留勾选";
    }

    private void EndMergeSelection()
    {
        mergeMode = false;
        mergeSelection.Clear();
        previewMergeButton = null;
        mergeSelectionStatus = null;
    }

    private void ShowMergeDialog(ReadingHistory history)
    {
        if (busy || !ReferenceEquals(session.History, history) || mergeSelection.Count < 2) return;
        var selected = SelectedBooks(history);
        var dialog = new MergeWindow(history, selected, (Style)FindResource(typeof(Button))) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() != true || dialog.Preview is not { } preview) return;
        ApplySelectedMerge(preview.Title, preview.Id);
    }

    private BookReading[] SelectedBooks(ReadingHistory history) => history.ReadBooks(DateOnly.MinValue, DateOnly.MaxValue)
        .Where(reading => mergeSelection.Contains(reading.Book.Number)).ToArray();

    internal void ApplySelectedMerge(string title, string id)
    {
        if (busy || !mergeMode || session.History is not { } history) return;
        var selected = SelectedBooks(history).Select(reading => reading.Book).ToArray();
        try
        {
            session.Merge(selected, title, id);
            search = "";
            RefreshAfterEdit($"已合并 {selected.Length} 本书为「{title}」。");
        }
        catch (HistoryEditException error) { SetNotice(error.Message); }
    }

    private static string DisplayId(string id) => id.Length == 0 ? "（空 ID）" : id;

    internal sealed class MergeWindow : Window
    {
        private sealed record Choice(string Value, string Label);
        public MergePreview? Preview { get; private set; }

        public MergeWindow(ReadingHistory history, BookReading[] selected, Style buttonStyle)
        {
            Title = "合并书籍";
            Width = 750;
            Height = 780;
            MinWidth = 480;
            MinHeight = 430;
            MaxHeight = SystemParameters.WorkArea.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new("Segoe UI, Microsoft YaHei UI");
            FontSize = 14;
            Background = Soft;
            Foreground = Ink;
            UseLayoutRounding = true;
            Resources[typeof(Button)] = buttonStyle;

            var layout = new DockPanel();
            Content = new Border { Background = Soft, Padding = new(20, 16, 20, 16), Child = layout };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new(0, 12, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 0) };
            var confirm = ActionButton("确认合并", () => DialogResult = true, enabled: false, selected: true);
            confirm.IsDefault = true;
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);

            var content = new StackPanel();
            layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content });
            var participants = Card(content);
            participants.Children.Add(Text("参与合并的书籍", 17, bold: true));
            var list = new ListBox { MaxHeight = 130, BorderThickness = new(0), Margin = new(0, 4, 0, 0) };
            AutomationProperties.SetName(list, "参与合并的书籍，按完整历史统计");
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            participants.Children.Add(list);
            foreach (var reading in selected)
                list.Items.Add(Text($"{reading.Book.Name}  ·  ID {DisplayId(reading.Book.Id)}  ·  {Duration(reading.Seconds)}  ·  {reading.ReadDays} 天",
                    14, wrap: true));

            var settings = Card(content, 12);
            var titles = selected.SelectMany(reading => reading.Book.UsableOriginalTitles).Distinct(StringComparer.Ordinal)
                .Select(title => new Choice(title, title)).ToArray();
            var ids = selected.Select(reading => reading.Book.Id).Distinct(StringComparer.Ordinal)
                .Select(id => new Choice(id, ReadingHistory.CanKeepId(id) ? id : $"{DisplayId(id)} · 不可保留")).ToArray();
            var titleChoice = AddChoice(settings, "保留的书籍标题", titles);
            var idChoice = AddChoice(settings, "保留的书籍 ID", ids);
            titleChoice.SelectedIndex = 0;
            idChoice.SelectedIndex = Array.FindIndex(ids, choice => ReadingHistory.CanKeepId(choice.Value));
            var previewCard = Card(content, 12);
            previewCard.Children.Add(Text("合并预览", 17, bold: true));
            var previewText = Text("", 14, top: 7, wrap: true);
            AutomationProperties.SetLiveSetting(previewText, AutomationLiveSetting.Polite);
            previewCard.Children.Add(previewText);
            previewCard.Children.Add(Text("合并后同一天只算一个阅读日，即使当天读过多本参与合并的书。", 13, muted: true, top: 6, wrap: true));

            var cover = Card(content, 12);
            cover.Children.Add(Text("封面与后续计时", 17, bold: true));
            var coverText = Text("", 13, muted: true, top: 7, wrap: true);
            cover.Children.Add(coverText);
            cover.Children.Add(Text("标题主要用于统计。缺少可用 ID 或原始封面来源时，部分版本会尝试书名或文件名。其他参与书籍的封面可能不再显示。专属封面关联单独保存，通常按 ID 查找，合并后其他 ID 的关联不会转移。", 13, muted: true, top: 6, wrap: true));
            cover.Children.Add(Text("仅凭 TSV 无法确定最终显示的封面。Kindle 继续按电子书原 ID 计时，再读使用另一个 ID 的书时，可能重新出现独立条目。", 13, muted: true, top: 6, wrap: true));

            var books = selected.Select(reading => reading.Book).ToArray();
            titleChoice.SelectionChanged += (_, _) => RefreshPreview();
            idChoice.SelectionChanged += (_, _) => RefreshPreview();
            RefreshPreview();

            void RefreshPreview()
            {
                var id = (idChoice.SelectedItem as Choice)?.Value ?? "";
                coverText.Text = ReadingHistory.CanKeepId(id)
                    ? $"Kindle 通常先按所选 ID「{id}」查找已有原始封面缓存或书库封面。"
                    : "请选择一个有效 ID 以查看封面匹配说明。";
                try
                {
                    Preview = history.PreviewMerge(books, (titleChoice.SelectedItem as Choice)?.Value ?? "", id);
                    previewText.Text = $"整个 TSV 的书籍数（含零秒和未来日期）  {Preview.BooksBefore} → {Preview.BooksAfter}\n" +
                        $"参与书籍总时长  {Duration(Preview.Seconds)} → {Duration(Preview.Seconds)}\n" +
                        $"阅读日  {Preview.ReadDaysBefore} 天（各书相加） → {Preview.ReadDaysAfter} 天（不同日期）\n" +
                        $"最终标题  {Preview.Title}\n最终 ID  {Preview.Id}";
                }
                catch (HistoryEditException error)
                {
                    Preview = null;
                    previewText.Text = error.Message;
                }
                confirm.IsEnabled = Preview is not null;
            }
        }

        private static ComboBox AddChoice(Panel panel, string caption, Choice[] choices)
        {
            panel.Children.Add(Text(caption, 14, bold: true, top: panel.Children.Count > 0 ? 10 : 0));
            var input = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(Choice.Label),
                Margin = new(0, 4, 0, 0), MinHeight = 32 };
            AutomationProperties.SetName(input, caption);
            panel.Children.Add(input);
            return input;
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Page.IsEnabled = OpenButton.IsEnabled = !busy;
        ReloadButton.IsEnabled = !busy && session.Path is not null;
        RefreshEditControls();
    }

    private void RefreshEditControls()
    {
        UndoButton.IsEnabled = ExportButton.IsEnabled = !busy && session.CanUndo;
        SessionStatus.Text = session.CanUndo ? "本地 · 会话内有修改" : "本地 · 原文件受保护";
    }

    private void RefreshAfterEdit(string notice)
    {
        EndMergeSelection();
        selectedBook = null;
        SetNotice(notice);
        RefreshEditControls();
        Render();
    }

    private void UndoMerge(object sender, RoutedEventArgs args)
    {
        if (!busy && session.Undo()) RefreshAfterEdit("已撤销上一次合并。");
    }

    private async void ExportHistory(object sender, RoutedEventArgs args)
    {
        if (busy || !session.CanUndo) return;
        var picker = new SaveFileDialog { Title = "导出 Kindle 阅读记录", FileName = "reading-time.tsv", DefaultExt = ".tsv",
            Filter = "TSV 阅读记录 (*.tsv)|*.tsv", AddExtension = true, OverwritePrompt = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        SetNotice("正在验证并导出完整阅读记录…");
        try
        {
            await session.ExportAsync(picker.FileName);
            SetNotice($"已导出完整 TSV · {picker.FileName}");
            if (MessageBox.Show(this, "完整 TSV 已保存。替换 Kindle 文件前请参阅随附 README 的安全步骤。\n\n打开导出文件夹？",
                "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(picker.FileName)!) { UseShellExecute = true }); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    SetNotice($"已导出完整 TSV · {picker.FileName}。无法打开文件夹。");
                }
            }
        }
        catch (Exception error) when (error is HistoryEditException or HistoryFormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetNotice($"导出失败。{error.Message}");
        }
        finally { SetBusy(false); }
    }
}
