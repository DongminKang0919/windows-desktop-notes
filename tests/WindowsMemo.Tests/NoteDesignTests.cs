using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class NoteDesignTests
{
    public static void Run(Action<string, Action> check)
    {
        check("입력 캐시: 문서 교체·선택 교체·100,000자 제한과 긴 제목", () =>
        {
            using var view = new NoteView();
            Assert(view.InsertPlainText(new string('가', NoteView.MaxCharacters - 2)));
            Assert(view.NoteTitle.Length == 80 && !view.InsertPlainText("초과"));
            view.TextEditor.SelectAll(); Assert(view.InsertPlainText("교체"));
            view.TextEditor.Document = new FlowDocument(new Paragraph(new Run(new string('나', NoteView.MaxCharacters - 2))));
            Assert(!view.InsertPlainText("초과"));
            view.TextEditor.SelectAll(); Assert(view.InsertPlainText("새 문서"));
            Assert(view.NoteTitle == "새 문서");
            view.SetPaper("Sage"); int changes = 0; view.AppearanceChanged += (_, _) => changes++;
            view.SetPaper("Sage"); Assert(changes == 0);
        });
        check("체크리스트: 완료 전환·제거·서식 보존·RTF 왕복", () =>
        {
            using var view = new NoteView();
            view.InsertPlainText("책 읽기"); view.TextEditor.SelectAll();
            view.TextEditor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
            view.TextEditor.CaretPosition = view.TextEditor.Document.ContentEnd;
            view.ToggleChecklist(); Assert(Text(view).Trim() == "☐ 책 읽기");
            view.ToggleChecklistDone(); Assert(Text(view).Trim() == "☑ 책 읽기");
            var restored = RtfDocumentCodec.Load(RtfDocumentCodec.Save(view.TextEditor.Document));
            Assert(new TextRange(restored.ContentStart, restored.ContentEnd).Text.Trim() == "☑ 책 읽기");
            view.ToggleChecklistDone(); view.ToggleChecklist(); Assert(Text(view).Trim() == "책 읽기");
            var start = view.TextEditor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var body = new TextRange(start, start.GetNextInsertionPosition(LogicalDirection.Forward)!);
            Assert(body.GetPropertyValue(TextElement.FontWeightProperty).Equals(FontWeights.Bold));
        });
        check("저장 실패 안내: 접기 차단·재시도 유지·성공 후 접기 복원", () =>
        {
            using var view = new NoteView();
            view.SetCollapsed(true); view.SetSaveStatus("저장 실패", true);
            view.SetCollapsed(true);
            Assert(!view.IsCollapsed);
            Assert(((FrameworkElement)view.FindName("Footer")).Visibility == Visibility.Visible);
            Assert(((Button)view.FindName("SaveButton")).Visibility == Visibility.Visible);
            var menu = ((Button)view.FindName("MoreButton")).ContextMenu!;
            var collapse = menu.Items.OfType<MenuItem>().Single(item => item.Header.ToString()!.StartsWith("접기 /"));
            Assert(!collapse.IsEnabled);
            view.SetSaveStatus("저장 재시도 중", true); view.SetCollapsed(true);
            Assert(!view.IsCollapsed && !collapse.IsEnabled);
            view.SetSaveStatus("저장 완료", false); view.SetCollapsed(true);
            Assert(view.IsCollapsed && collapse.IsEnabled);
        });
        check("포스트잇: 접힘·저장 실패 표시·색상과 320 DIP 레이아웃", () =>
        {
            using var view = new NoteView(); view.InsertPlainText("오늘의 생각\n한글로 적는 작은 기록");
            view.SetPaper("Sage"); view.SetCollapsed(true);
            Assert(view.IsCollapsed && view.NoteTitle == "오늘의 생각" && view.Paper == "Sage");
            view.SetSaveStatus("저장 실패", true); Assert(!view.IsCollapsed);
            view.SetSaveStatus("저장 완료", false);
            view.Measure(new Size(320, 300)); view.Arrange(new Rect(0,0,320,300)); view.UpdateLayout();
            Assert(view.TextEditor.ActualWidth > 240 && view.TextEditor.ActualHeight > 180);
            Assert(((Button)view.FindName("MoreButton")).ActualWidth >= 28);
        });
    }
    public static void RenderPreviews()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "ui-previews"); Directory.CreateDirectory(directory);
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromRgb(235, 232, 224)) };
            var views = new List<NoteView>();
            foreach (string paper in new[] { "Butter", "Sage", "Peach" })
            {
                var view = new NoteView { Width = 320, Height = 300, Margin = new Thickness(16) };
                view.SetPaper(paper); view.InsertPlainText(paper == "Butter" ? "오늘의 작은 계획\n\n☐ 산책하며 생각 정리하기\n☑ 읽고 싶었던 책 주문하기\n\n서두르지 않아도 괜찮아요."
                    : paper == "Sage" ? "문득 떠오른 생각\n\n좋은 아이디어는\n가까운 곳에 적어두기.\n\n금요일 오후, 따뜻한 차 한 잔" : "잊지 말기\n\n☐ 다음 주 일정 확인\n☐ 가족에게 전화하기\n\n작은 일부터 하나씩.");
                panel.Children.Add(view); views.Add(view);
            }
            panel.Measure(new Size(1056, 332)); panel.Arrange(new Rect(0, 0, 1056, 332)); panel.UpdateLayout();
            Save(panel, 1056, 332, scale, Path.Combine(directory, $"papers-{scale * 100:0}.png"));
            foreach (var view in views) view.Dispose(); panel.Children.Clear();
        }
        using var collapsed = new NoteView(); collapsed.InsertPlainText("접어둔 메모 · 더블클릭으로 펼치기"); collapsed.SetCollapsed(true);
        collapsed.Measure(new Size(320, 52)); collapsed.Arrange(new Rect(0,0,320,52)); collapsed.UpdateLayout();
        Save(collapsed, 320, 52, 2, Path.Combine(directory, "collapsed-200.png"));
        using var active = new NoteView(); active.InsertPlainText("잠깐의 메모\n\n도구는 필요할 때 가까이.\n본문을 눌러 바로 적어보세요.");
        // Render the same tool visibility used on hover without moving the user's pointer.
        void ShowTools(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button) button.SetCurrentValue(UIElement.OpacityProperty, 1.0);
                ShowTools(child);
            }
        }
        active.Measure(new Size(320,300)); active.Arrange(new Rect(0,0,320,300)); active.UpdateLayout(); ShowTools(active);
        Save(active, 320, 300, 2, Path.Combine(directory, "tools-200.png"));
        Console.WriteLine($"UI previews: {directory}");
    }
    internal static void Save(Visual visual, int width, int height, double scale, string path)
    {
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static string Text(NoteView view) => new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd).Text;
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Note design assertion failed."); }
}
