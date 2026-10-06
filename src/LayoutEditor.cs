using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal sealed class LayoutEditor : Window
    {
        private readonly LayoutSettings _draft;
        private readonly bool _compact;
        private readonly int _accountCount;
        private readonly Action<LayoutSettings> _preview;
        private readonly Action<LayoutSettings> _save;
        private readonly StackPanel _cards = new StackPanel();
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ComboBox _columns;
        private readonly CheckBox _hideUnavailable;
        private bool _building;
        internal bool Saved { get; private set; }
        internal LayoutSettings Draft { get { return _draft; } }
        private LayoutModeSettings Mode { get { return _draft.Mode(_compact); } }

        internal LayoutEditor(LayoutSettings settings, bool compact, int accountCount,
            Action<LayoutSettings> preview, Action<LayoutSettings> save)
        {
            _draft = settings.Copy(); _compact = compact; _accountCount = accountCount; _preview = preview; _save = save;
            LayoutSettings original = settings.Copy();
            Closed += delegate { if (!Saved) _preview(original.Copy()); };
            Title = (compact ? "위젯" : "전체 화면") + " 배치 편집";
            Width = 550; Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
            MinWidth = 430; MinHeight = 360;
            Background = Brush("#181818"); Foreground = Brush("#F4F4F5"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            StyleControls();
            Grid root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            StackPanel top = new StackPanel(); root.Children.Add(top);
            top.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.Bold });
            top.Children.Add(new TextBlock { Text = "변경 내용은 미터기에 바로 미리 표시됩니다. ☰를 끌어 순서를 바꾸세요.\n계정을 숨겨도 연결은 유지됩니다. 다른 화면의 배치는 따로 저장됩니다.", Foreground = Brush("#AAAAAF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
            WrapPanel options = new WrapPanel(); top.Children.Add(options);
            options.Children.Add(new TextBlock { Text = "최대 열 수", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _columns = Choice(new[] { "1열", "2열", "3열" }, Mode.Columns - 1);
            _columns.SelectionChanged += delegate { if (!_building) { Mode.Columns = _columns.SelectedIndex + 1; Preview(); } };
            options.Children.Add(_columns);
            options.Children.Add(new TextBlock { Text = "좁은 창은 자동 줄바꿈", Foreground = Brush("#AAAAAF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            _hideUnavailable = new CheckBox { Content = "계정에서 제공하지 않는 한도 자동 숨김", IsChecked = Mode.HideUnavailable, Margin = new Thickness(0, 12, 0, 12) };
            _hideUnavailable.Click += delegate { Mode.HideUnavailable = _hideUnavailable.IsChecked == true; Preview(); };
            top.Children.Add(_hideUnavailable);
            WrapPanel presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; top.Children.Add(presets);
            ComboBox account = Choice(Enumerable.Range(1, accountCount).Select(n => "계정 " + n).ToArray(), 0);
            presets.Children.Add(account);
            presets.Children.Add(Button("한 계정 중심", delegate { Mode.UseSingleAccount(account.SelectedIndex + 1, compact); Rebuild(); Preview(); }));
            presets.Children.Add(Button("기본 배치", delegate {
                if (_compact) _draft.Widget = LayoutSettings.DefaultMode(true); else _draft.Expanded = LayoutSettings.DefaultMode(false);
                Rebuild(); Preview();
            }));
            ScrollViewer scroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            StackPanel bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(bottom, 2); root.Children.Add(bottom);
            _status.Foreground = Brush("#E8BA78"); bottom.Children.Add(_status);
            DockPanel actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; bottom.Children.Add(actions);
            System.Windows.Controls.Button cancel = Button("취소", delegate { Close(); }); cancel.IsCancel = true;
            DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
            System.Windows.Controls.Button saveButton = Button("배치 저장", delegate { SaveAndClose(); });
            saveButton.Background = Brush("#285844"); saveButton.IsDefault = true;
            DockPanel.SetDock(saveButton, Dock.Right); actions.Children.Add(saveButton);
            actions.Children.Add(new TextBlock { Text = "취소하면 편집 전 배치로 돌아갑니다.", Foreground = Brush("#AAAAAF"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            Rebuild();
        }

        internal bool TrySave()
        {
            try { _save(_draft.Copy()); Saved = true; return true; }
            catch (Exception ex) { _status.Text = "저장하지 못했습니다. 다시 시도해 주세요. " + ex.Message; return false; }
        }
        private void SaveAndClose() { if (TrySave()) Close(); }
        internal void MoveCard(string id, string target) { Mode.Move(id, target); Rebuild(); Preview(); }
        private void Preview()
        {
            _status.Text = Mode.Cards.Any(card => card.Visible && (card.Id == "pc" || Int32.Parse(card.Id.Substring(7)) <= _accountCount))
                ? "" : "모든 카드를 숨겼습니다. 목록에서 다시 켤 수 있습니다.";
            _preview(_draft.Copy());
        }
        private void Rebuild()
        {
            _building = true;
            _columns.SelectedIndex = Mode.Columns - 1;
            _hideUnavailable.IsChecked = Mode.HideUnavailable;
            _cards.Children.Clear();
            LayoutCardSettings[] visibleChoices = Mode.Cards.Where(card => card.Id == "pc" || Int32.Parse(card.Id.Substring(7)) <= _accountCount).ToArray();
            for (int index = 0; index < visibleChoices.Length; index++)
            {
                LayoutCardSettings card = visibleChoices[index];
                int position = index;
                Border row = new Border { Background = Brush("#252525"), BorderBrush = Brush("#414141"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 4, 10), AllowDrop = true };
                StackPanel body = new StackPanel(); row.Child = body;
                DockPanel head = new DockPanel(); body.Children.Add(head);
                TextBlock handle = new TextBlock { Text = "☰", FontSize = 22, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 12, 0), ToolTip = "끌어서 카드 순서 변경" };
                head.Children.Add(handle);
                Point start = new Point();
                bool armed = false;
                handle.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { start = e.GetPosition(handle); armed = true; };
                handle.MouseLeftButtonUp += delegate { armed = false; };
                handle.MouseMove += delegate(object sender, MouseEventArgs e) {
                    if (!armed || e.LeftButton != MouseButtonState.Pressed) return;
                    Point now = e.GetPosition(handle);
                    if (Math.Abs(now.X - start.X) + Math.Abs(now.Y - start.Y) < 6) return;
                    armed = false;
                    DragDrop.DoDragDrop(handle, new DataObject("CodexMeter.Card", card.Id), DragDropEffects.Move);
                };
                row.DragOver += delegate(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("CodexMeter.Card") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
                row.Drop += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent("CodexMeter.Card")) MoveCard(e.Data.GetData("CodexMeter.Card") as string, card.Id); e.Handled = true; };
                System.Windows.Controls.Button down = Button("↓", delegate { MoveCard(card.Id, visibleChoices[position + 1].Id); }); down.IsEnabled = index < visibleChoices.Length - 1;
                DockPanel.SetDock(down, Dock.Right); head.Children.Add(down);
                System.Windows.Controls.Button up = Button("↑", delegate { MoveCard(card.Id, visibleChoices[position - 1].Id); }); up.IsEnabled = index > 0;
                DockPanel.SetDock(up, Dock.Right); head.Children.Add(up);
                CheckBox enabled = new CheckBox { Content = card.Id == "pc" ? "PC 상태" : "계정 " + card.Id.Substring(7), IsChecked = card.Visible, FontWeight = FontWeights.Bold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
                enabled.Click += delegate { card.Visible = enabled.IsChecked == true; Preview(); }; head.Children.Add(enabled);
                WrapPanel sizes = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) }; body.Children.Add(sizes);
                sizes.Children.Add(new TextBlock { Text = "폭", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                ComboBox span = Choice(new[] { "1칸", "2칸", "3칸" }, card.Span - 1);
                span.SelectionChanged += delegate { card.Span = span.SelectedIndex + 1; Preview(); }; sizes.Children.Add(span);
                sizes.Children.Add(new TextBlock { Text = "높이", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
                ComboBox size = Choice(new[] { "짧게", "보통", "길게" }, card.Size);
                size.SelectionChanged += delegate { card.Size = size.SelectedIndex; Preview(); }; sizes.Children.Add(size);
                WrapPanel sections = new WrapPanel(); body.Children.Add(sections);
                string[] keys = card.Id == "pc" ? new[] { "cpu", "gpu", "ram", "disk", "network" } :
                    (_compact ? new[] { "short", "weekly", "credits" } : new[] { "short", "weekly", "credits", "stats", "calendar" });
                string[] names = card.Id == "pc" ? new[] { "CPU", "GPU", "RAM", "디스크", "네트워크" } : new[] { "5시간·단기", "주간", "초기화권", "사용 통계", "달력·최근 7일" };
                for (int n = 0; n < keys.Length; n++)
                {
                    string key = keys[n];
                    CheckBox show = new CheckBox { Content = names[n], IsChecked = card.Shows(key), Margin = new Thickness(0, 7, 14, 3) };
                    show.Click += delegate { card.SetSection(key, show.IsChecked == true); Preview(); };
                    sections.Children.Add(show);
                }
                _cards.Children.Add(row);
            }
            _building = false;
        }
        private void StyleControls()
        {
            Style check = new Style(typeof(CheckBox)); check.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            Resources.Add(typeof(CheckBox), check);
            Style item = new Style(typeof(ComboBoxItem)); item.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            Resources.Add(typeof(ComboBoxItem), item);
        }
        private static ComboBox Choice(string[] values, int selected)
        {
            return new ComboBox { ItemsSource = values, SelectedIndex = selected, MinWidth = 82, FontSize = 13, Padding = new Thickness(7, 3, 7, 3), Foreground = Brushes.Black, VerticalContentAlignment = VerticalAlignment.Center };
        }
        private static System.Windows.Controls.Button Button(string title, Action action)
        {
            System.Windows.Controls.Button button = new System.Windows.Controls.Button { Content = title, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(5, 0, 0, 0), Background = Brush("#353535"), Foreground = Brushes.White, BorderBrush = Brush("#555555"), Cursor = Cursors.Hand };
            button.Click += delegate { action(); }; return button;
        }
        private static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
    }
}
