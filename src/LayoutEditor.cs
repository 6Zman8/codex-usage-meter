using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal sealed class LayoutEditor : Window
    {
        private readonly LayoutSettings _draft, _original;
        private readonly int _accountCount;
        private readonly Action<LayoutSettings, bool, string> _preview;
        private readonly Action<LayoutSettings> _save;
        private readonly Window _dashboard;
        private readonly DashboardLayoutView _layoutView;
        private readonly FrameworkElement _dashboardRoot;
        private readonly AdornerDecorator _previewDecorator = new AdornerDecorator();
        private readonly StackPanel _inspector = new StackPanel();
        private readonly Grid _editorRoot;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _previewSize = new TextBlock();
        private readonly ScrollViewer _previewScroll = new ScrollViewer { Name = "LayoutPreviewScroll", CanContentScroll = false };
        private readonly Viewbox _previewZoom = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
        private readonly TextBlock _zoomValue = new TextBlock { Name = "LayoutZoomValue", MinWidth = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private Button _maximize, _zoomOut, _zoomIn, _zoomFit;
        private double _zoomScale = 1;
        private bool _fitZoom = true, _maximized;
        private Rect _normalBounds;
        private readonly Dictionary<Border, LayoutCardAdorner> _adorners = new Dictionary<Border, LayoutCardAdorner>();
        private readonly Dictionary<UIElement, object> _previewHitTests = new Dictionary<UIElement, object>();
        private readonly Dictionary<Button, bool> _buttonFocus = new Dictionary<Button, bool>();
        private readonly double _originalWidth, _originalHeight;
        private readonly bool _originalCompact;
        private readonly object _originalForeground, _originalFont;
        private bool _compact, _restored, _refreshing, _addedResources;
        private string _selected;
        private Button _expandedTab, _widgetTab;
        private bool _editingItems;
        private string _selectedItem;
        private LayoutContentAdorner _contentAdorner;
        private LayoutTile _contentTile;
        private string _itemListSignature;
        internal bool Saved { get; private set; }
        internal LayoutSettings Draft { get { return _draft; } }
        internal bool Compact { get { return _compact; } }
        internal double PreviewZoom { get { return _zoomScale; } }
        private LayoutModeSettings Mode { get { return _draft.Mode(_compact); } }

        internal LayoutEditor(LayoutSettings settings, bool compact, int accountCount, Window dashboard,
            DashboardLayoutView layoutView, Action<LayoutSettings, bool, string> preview, Action<LayoutSettings> save)
        {
            _draft = settings.Copy(); _original = settings.Copy(); _compact = compact; _originalCompact = compact;
            _accountCount = accountCount; _dashboard = dashboard; _layoutView = layoutView; _preview = preview; _save = save;
            _dashboardRoot = (FrameworkElement)dashboard.Content;
            if (_dashboardRoot == null) throw new InvalidOperationException("대시보드 미리보기를 열 수 없습니다.");
            _originalWidth = _dashboardRoot.Width; _originalHeight = _dashboardRoot.Height;
            _originalForeground = _dashboardRoot.ReadLocalValue(Control.ForegroundProperty);
            _originalFont = _dashboardRoot.ReadLocalValue(Control.FontFamilyProperty);
            Title = "대시보드 배치 편집"; Width = Math.Min(1220, SystemParameters.WorkArea.Width - 40);
            Height = Math.Min(880, SystemParameters.WorkArea.Height - 40); MinWidth = 780; MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = Brush("#151515"); Foreground = Brush("#F4F4F5"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
            DarkTheme.Apply(this);
            Grid root = new Grid(); _editorRoot = root; root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
            root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) }); Content = root;
            Border titleBar = new Border { Background = Brush("#1E1E21"), Padding = new Thickness(22, 12, 18, 12) }; root.Children.Add(titleBar);
            titleBar.Name = "LayoutTitleBar";
            titleBar.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.ClickCount == 2) { ToggleMaximize(); e.Handled = true; }
                else if (!_maximized && e.LeftButton == MouseButtonState.Pressed) DragMove();
            };
            DockPanel title = new DockPanel(); titleBar.Child = title;
            Button close = MakeButton("×", delegate { Close(); }); DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
            _maximize = MakeButton("□", ToggleMaximize); _maximize.Name = "LayoutMaximizeButton"; _maximize.ToolTip = "최대화";
            DockPanel.SetDock(_maximize, Dock.Right); title.Children.Add(_maximize);
            StateChanged += delegate { if (WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; if (!_maximized) ToggleMaximize(); } };
            TextBlock heading = new TextBlock { Text = "배치 편집", FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0) }; title.Children.Add(heading);
            StackPanel tabs = new StackPanel { Orientation = Orientation.Horizontal };
            _expandedTab = MakeButton("전체 화면", delegate { SwitchMode(false); }); _widgetTab = MakeButton("위젯", delegate { SwitchMode(true); });
            tabs.Children.Add(_expandedTab); tabs.Children.Add(_widgetTab); title.Children.Add(tabs);

            Grid middle = new Grid(); Grid.SetRow(middle, 1); root.Children.Add(middle);
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(242) }); middle.ColumnDefinitions.Add(new ColumnDefinition());
            Border sidebar = new Border { Background = Brush("#1D1D20"), BorderBrush = Brush("#35353A"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(16) };
            sidebar.Child = new ScrollViewer { Content = _inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; middle.Children.Add(sidebar);
            Grid stage = new Grid { Margin = new Thickness(18, 12, 18, 12) }; Grid.SetColumn(stage, 1); middle.Children.Add(stage);
            stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); stage.RowDefinitions.Add(new RowDefinition()); stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel toolRows = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; stage.Children.Add(toolRows);
            DockPanel stageTools = new DockPanel(); toolRows.Children.Add(stageTools);
            ComboBox viewport = new ComboBox { ItemsSource = new[] { "기본 창 크기", "좁은 창", "넓은 창" }, SelectedIndex = 0, Width = 140 };
            viewport.SelectionChanged += delegate { SetViewport(viewport.SelectedIndex); };
            DockPanel.SetDock(viewport, Dock.Right); stageTools.Children.Add(viewport);
            _previewSize.Foreground = Brush("#B5B5BF"); _previewSize.VerticalAlignment = VerticalAlignment.Center; stageTools.Children.Add(_previewSize);
            WrapPanel zoomTools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; toolRows.Children.Add(zoomTools);
            _zoomOut = MakeButton("−", delegate { SetPreviewZoom(_zoomScale - 0.1); }); _zoomOut.Name = "LayoutZoomOut"; _zoomOut.ToolTip = "축소";
            _zoomIn = MakeButton("+", delegate { SetPreviewZoom(_zoomScale + 0.1); }); _zoomIn.Name = "LayoutZoomIn"; _zoomIn.ToolTip = "확대";
            Button actualSize = MakeButton("100%", delegate { SetPreviewZoom(1); }); actualSize.Name = "LayoutZoomActual"; actualSize.ToolTip = "실제 크기";
            _zoomFit = MakeButton("화면에 맞춤", FitPreview); _zoomFit.Name = "LayoutZoomFit";
            zoomTools.Children.Add(_zoomOut); zoomTools.Children.Add(_zoomValue); zoomTools.Children.Add(_zoomIn); zoomTools.Children.Add(actualSize); zoomTools.Children.Add(_zoomFit);
            zoomTools.Children.Add(new TextBlock { Text = "Ctrl + 휠", Foreground = Brush("#A4A4AE"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Border previewFrame = new Border { Background = Brush("#101012"), BorderBrush = Brush("#37373D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), ClipToBounds = true };
            Grid.SetRow(previewFrame, 1); stage.Children.Add(previewFrame);
            _previewZoom.Child = _previewDecorator; _previewZoom.HorizontalAlignment = HorizontalAlignment.Center; _previewZoom.VerticalAlignment = VerticalAlignment.Center;
            _previewScroll.Content = _previewZoom; previewFrame.Child = _previewScroll;
            _previewScroll.SizeChanged += delegate { if (_fitZoom) FitPreview(); };
            _previewScroll.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                ZoomWithWheel(e.Delta, e.GetPosition(_previewScroll)); e.Handled = true;
            };
            TextBlock guide = new TextBlock { Text = "카드: 위쪽을 끌어 이동 · 모서리로 크기 조절\n항목 편집: 내용물을 끌어 이동 · 선택한 항목의 모서리로 크기 조절", Foreground = Brush("#A4A4AE"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
            Grid.SetRow(guide, 2); stage.Children.Add(guide);
            Border footer = new Border { Background = Brush("#1E1E21"), Padding = new Thickness(18, 10, 18, 10) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
            DockPanel actions = new DockPanel(); footer.Child = actions;
            Button saveButton = MakeButton("배치 저장", delegate { if (TrySave()) Close(); }); saveButton.Background = Brush("#246A55"); saveButton.IsDefault = true; DockPanel.SetDock(saveButton, Dock.Right); actions.Children.Add(saveButton);
            Button cancel = MakeButton("취소", delegate { Close(); }); cancel.IsCancel = true; DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
            _status.Foreground = Brush("#C2C2CC"); _status.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(_status);

            try
            {
                dashboard.Content = null;
                if (!_dashboardRoot.Resources.MergedDictionaries.Contains(dashboard.Resources)) { _dashboardRoot.Resources.MergedDictionaries.Add(dashboard.Resources); _addedResources = true; }
                _dashboardRoot.SetValue(Control.ForegroundProperty, dashboard.Foreground); _dashboardRoot.SetValue(Control.FontFamilyProperty, dashboard.FontFamily);
                _previewDecorator.Child = _dashboardRoot;
                _layoutView.SetEditing(true);
                Closed += delegate { RestoreDashboard(); if (!Saved) _preview(_original.Copy(), _originalCompact, null); };
                _dashboardRoot.LayoutUpdated += DashboardLayoutUpdated;
                _selected = Choices().First().Id; SetViewport(0); RefreshInspector(); Preview();
            }
            catch { RestoreDashboard(); throw; }
        }
        internal bool TrySave()
        {
            try { _save(_draft.Copy()); Saved = true; return true; }
            catch (Exception ex) { _status.Text = "저장하지 못했습니다: " + ex.Message; return false; }
        }
        internal void RestoreDashboard()
        {
            if (_restored) return; _restored = true;
            _dashboardRoot.LayoutUpdated -= DashboardLayoutUpdated;
            RemoveContentAdorner();
            foreach (LayoutCardAdorner adorner in _adorners.Values) { AdornerLayer layer = VisualTreeHelper.GetParent(adorner) as AdornerLayer; if (layer != null) layer.Remove(adorner); }
            foreach (KeyValuePair<UIElement, object> entry in _previewHitTests) RestoreValue(entry.Key, UIElement.IsHitTestVisibleProperty, entry.Value);
            foreach (KeyValuePair<Button, bool> entry in _buttonFocus) entry.Key.Focusable = entry.Value;
            _previewDecorator.Child = null; _dashboardRoot.Width = _originalWidth; _dashboardRoot.Height = _originalHeight;
            if (_addedResources) _dashboardRoot.Resources.MergedDictionaries.Remove(_dashboard.Resources);
            RestoreValue(_dashboardRoot, Control.ForegroundProperty, _originalForeground); RestoreValue(_dashboardRoot, Control.FontFamilyProperty, _originalFont);
            _dashboard.Content = _dashboardRoot;
            _layoutView.SetEditing(false);
        }
        private static void RestoreValue(DependencyObject target, DependencyProperty property, object value)
        { if (value == DependencyProperty.UnsetValue) target.ClearValue(property); else target.SetValue(property, value); }
        internal void SwitchMode(bool compact)
        { _compact = compact; _selectedItem = null; SetViewport(0); Preview(); RefreshInspector(); FocusSelectedCard(); }
        internal void MoveCard(string id, string target)
        { Mode.Move(id, target); _selected = id; RefreshInspector(); Preview(); }
        internal void ResizeCard(string id, int span, int size)
        {
            LayoutCardSettings card = Mode.Card(id); card.Span = Math.Max(1, Math.Min(3, span)); card.Size = Math.Max(0, Math.Min(2, size));
            if (card.Span > Mode.Columns) { Mode.Columns = card.Span; SetViewport(2); }
            _selected = id; RefreshInspector(); Preview();
        }

        internal void SetItemEditing(bool enabled)
        {
            _editingItems = enabled; _selectedItem = null;
            Preview(); RefreshInspector();
        }

        internal void EditItem(string id, double x, double y, double width, double height)
        {
            LayoutTile tile = SelectedTile(); if (tile == null) return;
            LayoutCardSettings card = Mode.Card(_selected);
            if (card.ItemLayouts.Count == 0) card.ItemLayouts = CardContentLayout.Capture(tile);
            var value = new LayoutItemSettings { Id = id, X = x, Y = y, Width = width, Height = height };
            if (!value.Normalize()) return;
            card.ItemLayouts.RemoveAll(item => item.Id == id); card.ItemLayouts.Add(value); _selectedItem = id;
            Preview();
        }

        private LayoutTile SelectedTile()
        { return _layoutView.Tiles(_compact).FirstOrDefault(tile => tile.Settings != null && tile.Settings.Id == _selected && tile.Card.IsVisible); }

        private void SelectItem(string id) { if (_selectedItem == id) return; _selectedItem = id; RefreshInspector(); RefreshAdorners(); }

        internal void SetItemVisible(string id, bool visible)
        {
            LayoutCardSettings card = Mode.Card(_selected);
            card.SetItemVisible(id, visible);
            string section = id.Split(':')[0]; if (section == "memory") section = "ram";
            if (visible && LayoutSettings.DefaultMode(_compact).Card(_selected).Sections.Contains(section)) card.SetSection(section, true);
            _selectedItem = id; Preview(); RefreshInspector();
        }

        private void ResetItems(bool all)
        {
            LayoutCardSettings card = Mode.Card(_selected);
            if (all) { card.ItemLayouts.Clear(); card.HiddenItems.Clear(); }
            else { card.ItemLayouts.RemoveAll(item => item.Id == _selectedItem); card.SetItemVisible(_selectedItem, true); }
            Preview(); RefreshInspector();
        }
        private IEnumerable<LayoutCardSettings> Choices()
        { return Mode.Cards.Where(card => !card.Id.StartsWith("account") || Int32.Parse(card.Id.Substring(7)) <= _accountCount); }
        private static string CardName(string id)
        { return id == "pc" ? "PC 상태" : "계정 " + id.Substring(7); }
        private void SetViewport(int preset)
        {
            _dashboardRoot.Width = preset == 1 ? (_compact ? 320 : 900) : preset == 2 ? (_compact ? 900 : 1600) : (_compact ? 460 : 1280);
            _dashboardRoot.Height = preset == 1 ? (_compact ? 480 : 620) : (_compact ? 780 : 820);
            _previewSize.Text = "미리보기  ·  " + _dashboardRoot.Width + " × " + _dashboardRoot.Height;
            if (_fitZoom) FitPreview(); else UpdatePreviewZoom();
        }
        private void ToggleMaximize()
        {
            if (_maximized)
            {
                _maximized = false; ResizeMode = ResizeMode.CanResizeWithGrip;
                WindowPlacement.SetBounds(this, _normalBounds);
            }
            else
            {
                _normalBounds = WindowPlacement.Bounds(this);
                _maximized = true; ResizeMode = ResizeMode.NoResize;
                WindowPlacement.Maximize(this);
            }
            _maximize.Content = _maximized ? "❐" : "□"; _maximize.ToolTip = _maximized ? "이전 크기로 복원" : "최대화";
        }
        internal void FitPreview()
        {
            _fitZoom = true;
            if (_previewScroll.ActualWidth > 0 && _previewScroll.ActualHeight > 0)
                _zoomScale = Math.Min(_previewScroll.ActualWidth / _dashboardRoot.Width, _previewScroll.ActualHeight / _dashboardRoot.Height);
            UpdatePreviewZoom(); _previewScroll.ScrollToHorizontalOffset(0); _previewScroll.ScrollToVerticalOffset(0);
        }
        internal void SetPreviewZoom(double scale, Point? anchor = null)
        {
            if (Double.IsNaN(scale) || Double.IsInfinity(scale)) return;
            _previewScroll.UpdateLayout(); // Apply queued scrolling before locating the next wheel event's anchor.
            Point viewportPoint = anchor ?? new Point(_previewScroll.ViewportWidth / 2, _previewScroll.ViewportHeight / 2);
            Point contentPoint = _previewScroll.TranslatePoint(viewportPoint, _dashboardRoot);
            _fitZoom = false; _zoomScale = Math.Max(0.25, Math.Min(4, scale)); UpdatePreviewZoom(); _previewScroll.UpdateLayout();
            Point moved = _dashboardRoot.TranslatePoint(contentPoint, _previewScroll);
            _previewScroll.ScrollToHorizontalOffset(_previewScroll.HorizontalOffset + moved.X - viewportPoint.X);
            _previewScroll.ScrollToVerticalOffset(_previewScroll.VerticalOffset + moved.Y - viewportPoint.Y);
        }
        internal void ZoomWithWheel(int delta, Point anchor)
        { if (delta != 0) SetPreviewZoom(_zoomScale + (delta > 0 ? 0.1 : -0.1), anchor); }
        private void UpdatePreviewZoom()
        {
            _previewScroll.HorizontalScrollBarVisibility = _fitZoom ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            _previewScroll.VerticalScrollBarVisibility = _fitZoom ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            _previewZoom.Width = _dashboardRoot.Width * _zoomScale; _previewZoom.Height = _dashboardRoot.Height * _zoomScale;
            _zoomValue.Text = Math.Round(_zoomScale * 100).ToString("0") + "%";
            _zoomOut.IsEnabled = _zoomScale > 0.25001; _zoomIn.IsEnabled = _zoomScale < 3.99999;
            _zoomFit.Background = Brush(_fitZoom ? "#255B4C" : "#292929");
        }
        private void Preview()
        {
            _preview(_draft.Copy(), _compact, _selected);
            _expandedTab.Background = Brush(_compact ? "#292929" : "#255B4C"); _widgetTab.Background = Brush(_compact ? "#255B4C" : "#292929");
            _status.Text = Choices().Any(card => card.Visible) ? "전체와 위젯의 배치는 따로 저장됩니다. 취소하면 이전 배치로 돌아갑니다." : "표시할 카드가 없습니다. 왼쪽에서 카드를 켜 주세요.";
            _dashboardRoot.UpdateLayout(); RefreshAdorners();
        }
        private void FocusSelectedCard()
        {
            LayoutTile tile = _layoutView.Tiles(_compact).FirstOrDefault(item => item.Settings.Id == _selected && item.Card.IsVisible);
            if (tile != null) tile.Card.BringIntoView(new Rect(0, 0, tile.Card.ActualWidth, Math.Min(100, tile.Card.ActualHeight)));
        }
        private void RefreshInspector()
        {
            _inspector.Children.Clear();
            _itemListSignature = null;
            Label("카드 표시", 15);
            UniformGrid cards = new UniformGrid { Columns = 2 }; _inspector.Children.Add(cards);
            foreach (LayoutCardSettings card in Choices())
            {
                DockPanel row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                CheckBox visible = new CheckBox { IsChecked = card.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), ToolTip = "카드 표시/숨김" };
                visible.Click += delegate { card.Visible = visible.IsChecked == true; _selected = card.Id; RefreshInspector(); Preview(); FocusSelectedCard(); };
                row.Children.Add(visible);
                Button select = MakeButton(CardName(card.Id), delegate { _selected = card.Id; Preview(); RefreshInspector(); FocusSelectedCard(); });
                select.Padding = new Thickness(5, 5, 5, 5);
                select.HorizontalContentAlignment = HorizontalAlignment.Left; select.Background = Brush(card.Id == _selected ? "#255B4C" : "#292929"); row.Children.Add(select); cards.Children.Add(row);
            }
            Label("열 수", 13);
            Segments(new[] { "1열", "2열", "3열" }, Mode.Columns - 1, n => { Mode.Columns = n + 1; if (n > 0) SetViewport(2); RefreshInspector(); Preview(); });
            LayoutCardSettings selected = Mode.Card(_selected);
            Label(CardName(_selected) + " 조절", 15);
            Label("폭", 12); Segments(new[] { "1칸", "2칸", "3칸" }, selected.Span - 1, n => ResizeCard(selected.Id, n + 1, selected.Size));
            Label("높이", 12); Segments(new[] { "짧게", "보통", "길게" }, selected.Size, n => ResizeCard(selected.Id, selected.Span, n));
            Button editItems = MakeButton(_editingItems ? "카드 편집으로" : "항목 편집", delegate { SetItemEditing(!_editingItems); });
            editItems.Background = Brush(_editingItems ? "#255B4C" : "#292929"); _inspector.Children.Add(editItems);
            if (_editingItems)
            {
                Label("안쪽 항목", 13);
                LayoutTile tile = SelectedTile();
                var items = tile == null ? new List<LayoutContentItem>() : tile.ContentItems();
                _itemListSignature = ItemListSignature(items);
                if (!items.Any(item => item.Id == _selectedItem)) _selectedItem = items.Count == 0 ? null : items[0].Id;
                foreach (LayoutContentItem item in items)
                {
                    string id = item.Id;
                    DockPanel row = new DockPanel();
                    string section = id.Split(':')[0]; if (section == "memory") section = "ram";
                    bool sectionVisible = !LayoutSettings.DefaultMode(_compact).Card(_selected).Sections.Contains(section) || selected.Shows(section);
                    CheckBox visible = new CheckBox { Tag = "item-visible:" + id, IsChecked = selected.ShowsItem(id) && sectionVisible,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), ToolTip = item.Label + " 표시/숨김" };
                    visible.Click += delegate { SetItemVisible(id, visible.IsChecked == true); };
                    row.Children.Add(visible);
                    Button choice = MakeButton(item.Label, delegate { SelectItem(id); }); choice.Tag = "item:" + id;
                    choice.Padding = new Thickness(7, 4, 7, 4); choice.HorizontalContentAlignment = HorizontalAlignment.Left;
                    choice.Background = Brush(id == _selectedItem ? "#255B4C" : "#292929"); row.Children.Add(choice); _inspector.Children.Add(row);
                }
                _inspector.Children.Add(new TextBlock { Text = "체크를 끄면 숨기고 다시 켜면 복원됩니다. 끌어서 이동·◢로 크기 조절 시 항목과 카드의 경계·중앙선에 맞춰집니다. Alt를 누르면 자유 조절합니다. 저장한 배치는 자동으로 바뀌지 않습니다.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B5B5BF"), FontSize = 11, Margin = new Thickness(0, 5, 0, 5) });
                _inspector.Children.Add(MakeButton("선택 항목 원래대로", delegate { ResetItems(false); }));
                _inspector.Children.Add(MakeButton("카드 안쪽 모두 원래대로", delegate { ResetItems(true); }));
            }
            string[] keys = selected.Id == "pc" ? new[] { "cpu", "gpu", "ram", "disk", "network" } : (_compact ? new[] { "short", "weekly", "credits" } : new[] { "short", "weekly", "credits", "stats", "calendar" });
            string[] names = selected.Id == "pc" ? new[] { "CPU", "GPU", "RAM", "디스크", "네트워크" } : new[] { "5시간 한도", "주간 한도", "초기화권", "사용 통계", "달력·7일" };
            UniformGrid sections = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) }; _inspector.Children.Add(sections);
            for (int n = 0; n < keys.Length; n++)
            {
                string key = keys[n]; CheckBox section = new CheckBox { Content = names[n], IsChecked = selected.Shows(key), Margin = new Thickness(0, 6, 0, 0) };
                section.Click += delegate { selected.SetSection(key, section.IsChecked == true); Preview(); }; sections.Children.Add(section);
            }
            Label("빠른 배치", 13);
            UniformGrid presets = new UniformGrid { Columns = 2 }; _inspector.Children.Add(presets);
            presets.Children.Add(MakeButton("선택 계정 중심", delegate { int number = _selected.StartsWith("account") ? Int32.Parse(_selected.Substring(7)) : 1; Mode.UseSingleAccount(number, _compact); RefreshInspector(); Preview(); }));
            presets.Children.Add(MakeButton("기본 배치", delegate { if (_compact) _draft.Widget = LayoutSettings.DefaultMode(true); else _draft.Expanded = LayoutSettings.DefaultMode(false); RefreshInspector(); Preview(); }));
            foreach (Button button in presets.Children) { button.FontSize = 12; button.Padding = new Thickness(4, 7, 4, 7); }
        }
        private void Label(string text, double size)
        { _inspector.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Brush("#DADAE0"), Margin = new Thickness(0, 6, 0, 4) }); }
        private void Segments(string[] names, int selected, Action<int> action)
        {
            UniformGrid group = new UniformGrid { Rows = 1 };
            for (int n = 0; n < names.Length; n++) { int choice = n; Button button = MakeButton(names[n], delegate { action(choice); }); button.Padding = new Thickness(4, 7, 4, 7); button.Background = Brush(n == selected ? "#255B4C" : "#292929"); group.Children.Add(button); }
            _inspector.Children.Add(group);
        }
        private void DashboardLayoutUpdated(object sender, EventArgs e) { RefreshAdorners(); }
        private void RefreshAdorners()
        {
            if (_refreshing || _restored) return; _refreshing = true;
            try
            {
                foreach (LayoutTile tile in _layoutView.Tiles(_compact))
                {
                    BlockPreviewHitTest(tile.Content);
                    LayoutCardAdorner adorner;
                    if (_adorners.TryGetValue(tile.Card, out adorner) && VisualTreeHelper.GetParent(adorner) == null) _adorners.Remove(tile.Card);
                    if (!_adorners.TryGetValue(tile.Card, out adorner))
                    {
                        AdornerLayer layer = _previewDecorator.AdornerLayer;
                        adorner = new LayoutCardAdorner(tile, _editorRoot, id => { _selected = id; RefreshInspector(); RefreshAdorners(); }, MoveCard, ResizeCard);
                        _adorners.Add(tile.Card, adorner); layer.Add(adorner);
                    }
                    adorner.Refresh(tile.Settings != null && tile.Settings.Id == _selected);
                }
                foreach (KeyValuePair<Border, LayoutCardAdorner> entry in _adorners)
                    entry.Value.Visibility = !_editingItems && entry.Key.IsVisible && entry.Key.ActualWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
                LayoutTile selectedTile = _editingItems ? SelectedTile() : null;
                if (_editingItems && _itemListSignature != ItemListSignature(selectedTile == null ? new List<LayoutContentItem>() : selectedTile.ContentItems()))
                    RefreshInspector();
                if (selectedTile != _contentTile) RemoveContentAdorner();
                if (selectedTile != null)
                {
                    if (_contentAdorner == null)
                    {
                        _contentTile = selectedTile;
                        _contentAdorner = new LayoutContentAdorner(selectedTile, _editorRoot, SelectItem, EditItem);
                        _previewDecorator.AdornerLayer.Add(_contentAdorner);
                    }
                    _contentAdorner.Refresh(_selectedItem);
                }
                foreach (Button button in Visuals<Button>(_dashboardRoot))
                {
                    if (button.Name.Contains("AccountPage")) continue;
                    BlockPreviewHitTest(button);
                    if (!_buttonFocus.ContainsKey(button)) _buttonFocus.Add(button, button.Focusable);
                    button.Focusable = false;
                }
                foreach (Border titleBar in Visuals<Border>(_dashboardRoot).Where(border => border.Name == "TitleBar" || border.Name == "CompactTitleBar"))
                    BlockPreviewHitTest(titleBar);
            }
            finally { _refreshing = false; }
        }
        private void BlockPreviewHitTest(UIElement element)
        {
            if (!_previewHitTests.ContainsKey(element)) _previewHitTests.Add(element, element.ReadLocalValue(UIElement.IsHitTestVisibleProperty));
            element.IsHitTestVisible = false;
        }
        private static string ItemListSignature(IEnumerable<LayoutContentItem> items)
        { return String.Join("|", items.Select(item => item.Id + ":" + item.Label + ":" + item.Element.Visibility)); }
        private void RemoveContentAdorner()
        {
            if (_contentAdorner != null)
            {
                _contentAdorner.ClearGuides();
                AdornerLayer layer = VisualTreeHelper.GetParent(_contentAdorner) as AdornerLayer;
                if (layer != null) layer.Remove(_contentAdorner);
            }
            _contentAdorner = null; _contentTile = null;
        }
        private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int n = 0; n < VisualTreeHelper.GetChildrenCount(parent); n++) { DependencyObject child = VisualTreeHelper.GetChild(parent, n); if (child is T) yield return (T)child; foreach (T item in Visuals<T>(child)) yield return item; }
        }
        internal static Button MakeButton(string text, Action click)
        { Button button = new Button { Content = text, Margin = new Thickness(0, 2, 5, 2) }; button.Click += delegate { click(); }; return button; }
        internal static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
    }

    internal sealed class LayoutCardAdorner : Adorner
    {
        private readonly LayoutTile _tile;
        private readonly Grid _visual = new Grid();
        private readonly Border _outline;
        private readonly Thumb _resize;
        private readonly Visual _dragSurface;
        private readonly Action<string, int, int> _resizeCard;
        private Point _dragStart, _thumbAnchor;
        private double _scaleX, _scaleY, _unit;
        private int _startSpan, _startSize;
        private Rect _lastBounds = Rect.Empty;
        internal LayoutCardAdorner(LayoutTile tile, Visual dragSurface, Action<string> select, Action<string, string> move, Action<string, int, int> resize) : base(tile.Card)
        {
            _tile = tile; _dragSurface = dragSurface; _resizeCard = resize;
            _outline = new Border { Background = Brushes.Transparent, BorderBrush = LayoutEditor.Brush("#72D4B5"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), AllowDrop = true };
            _visual.Children.Add(_outline);
            _outline.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { select(_tile.Settings.Id); e.Handled = true; };
            _outline.DragOver += delegate(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("CodexMeter.Card") ? DragDropEffects.Move : DragDropEffects.None; _outline.BorderBrush = LayoutEditor.Brush("#6CCFFF"); e.Handled = true; };
            _outline.DragLeave += delegate { _outline.BorderBrush = LayoutEditor.Brush("#72D4B5"); };
            _outline.Drop += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent("CodexMeter.Card")) move(e.Data.GetData("CodexMeter.Card") as string, _tile.Settings.Id); e.Handled = true; };
            Border drag = new Border { Height = 42, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "끌어서 카드 이동", AllowDrop = true };
            drag.DragOver += delegate(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("CodexMeter.Card") ? DragDropEffects.Move : DragDropEffects.None; _outline.BorderBrush = LayoutEditor.Brush("#6CCFFF"); e.Handled = true; };
            drag.Drop += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent("CodexMeter.Card")) move(e.Data.GetData("CodexMeter.Card") as string, _tile.Settings.Id); e.Handled = true; };
            Point start = new Point(); bool armed = false;
            drag.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { start = e.GetPosition(drag); armed = true; select(_tile.Settings.Id); e.Handled = true; };
            drag.MouseLeftButtonUp += delegate { armed = false; };
            drag.MouseMove += delegate(object sender, MouseEventArgs e) { if (!armed || e.LeftButton != MouseButtonState.Pressed) return; Point now = e.GetPosition(drag); if (Math.Abs(now.X - start.X) + Math.Abs(now.Y - start.Y) < 6) return; armed = false; DragDrop.DoDragDrop(drag, new DataObject("CodexMeter.Card", _tile.Settings.Id), DragDropEffects.Move); };
            _visual.Children.Add(drag);
            _resize = new Thumb { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, ToolTip = "끌어서 카드 크기 조절", Background = LayoutEditor.Brush("#315D4E") };
            FrameworkElementFactory grip = new FrameworkElementFactory(typeof(Border)); grip.SetValue(Border.BackgroundProperty, LayoutEditor.Brush("#315D4E")); grip.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            FrameworkElementFactory mark = new FrameworkElementFactory(typeof(TextBlock)); mark.SetValue(TextBlock.TextProperty, "◢"); mark.SetValue(TextBlock.ForegroundProperty, Brushes.White); mark.SetValue(TextBlock.FontSizeProperty, 18.0); mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); grip.AppendChild(mark);
            _resize.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = grip };
            _resize.DragStarted += delegate(object sender, DragStartedEventArgs e) {
                select(_tile.Settings.Id); _startSpan = _tile.Settings.Span; _startSize = _tile.Settings.Size;
                _unit = _tile.Card.ActualWidth / Math.Max(1, _startSpan);
                _thumbAnchor = new Point(e.HorizontalOffset, e.VerticalOffset);
                GeneralTransform transform = _resize.TransformToAncestor(_dragSurface);
                _dragStart = transform.Transform(_thumbAnchor);
                _scaleX = Math.Max(0.01, transform.Transform(new Point(_thumbAnchor.X + 1, _thumbAnchor.Y)).X - _dragStart.X);
                _scaleY = Math.Max(0.01, transform.Transform(new Point(_thumbAnchor.X, _thumbAnchor.Y + 1)).Y - _dragStart.Y);
            };
            _resize.DragDelta += delegate(object sender, DragDeltaEventArgs e) {
                // Thumb reports displacement from its initial local anchor, not the previous event.
                // Recover the pointer in a fixed surface because snapping also moves the Thumb itself.
                Point pointer = _resize.TransformToAncestor(_dragSurface).Transform(new Point(_thumbAnchor.X + e.HorizontalChange, _thumbAnchor.Y + e.VerticalChange));
                double dx = (pointer.X - _dragStart.X) / _scaleX, dy = (pointer.Y - _dragStart.Y) / _scaleY;
                int span = Math.Max(1, Math.Min(3, _startSpan + (int)Math.Round(dx / Math.Max(80, _unit))));
                int size = Math.Max(0, Math.Min(2, _startSize + (int)Math.Round(dy / 60)));
                if (span != _tile.Settings.Span || size != _tile.Settings.Size) _resizeCard(_tile.Settings.Id, span, size);
            };
            _visual.Children.Add(_resize); AddVisualChild(_visual);
        }
        internal void Refresh(bool selected)
        {
            Thickness border = new Thickness(selected ? 2 : 1);
            if (_outline.BorderThickness != border) _outline.BorderThickness = border;
            _outline.BorderBrush = LayoutEditor.Brush(selected ? "#72D4B5" : "#505058"); _resize.Opacity = selected ? 1 : 0.55;
            AdornerLayer layer = VisualTreeHelper.GetParent(this) as AdornerLayer;
            Visual parent = layer == null ? null : VisualTreeHelper.GetParent(layer) as Visual;
            if (parent != null && _tile.Card.IsVisible)
            {
                Rect bounds = _tile.Card.TransformToAncestor(parent).TransformBounds(new Rect(_tile.Card.RenderSize));
                if (bounds != _lastBounds) { _lastBounds = bounds; layer.Update(_tile.Card); }
            }
        }
        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index) { return _visual; }
        protected override Size MeasureOverride(Size size) { _visual.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size size) { _visual.Arrange(new Rect(size)); return size; }
    }
}
