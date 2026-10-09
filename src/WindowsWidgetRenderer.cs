using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageMeter
{
    // Render the actual compact dashboard, not a second implementation of its gauges/layout.
    // This window has no controller, timers, tray icon, account clients or visible HWND.
    internal sealed class WindowsWidgetRenderer : IDisposable
    {
        internal readonly Window Window = DashboardController.LoadWindow();
        internal readonly DashboardLayoutView Layout;
        private readonly Grid _root;
        private readonly AccountView[] _views = new AccountView[2];
        private readonly List<DashboardController.FontTarget> _fonts = new List<DashboardController.FontTarget>();
        private readonly HwndSource _surface;
        internal static readonly string[] Sizes = { "Small", "Medium", "Large" };
        internal static readonly int[] Heights = { 100, 194, 350 };

        internal WindowsWidgetRenderer()
        {
            Layout = new DashboardLayoutView(Window);
            _root = (Grid)Window.Content;
            _root.Resources.MergedDictionaries.Add(Window.Resources);
            _root.SetValue(Control.FontFamilyProperty, Window.FontFamily);
            _root.SetValue(Control.ForegroundProperty, Window.Foreground);
            _root.SetValue(Control.FontSizeProperty, Window.FontSize);
            foreach (FrameworkElement child in _root.Children)
                child.Visibility = child == Find<Grid>("CompactLayout") ? Visibility.Visible : Visibility.Collapsed;
            Grid shell = (Grid)Find<Border>("CompactShell").Child;
            foreach (FrameworkElement child in shell.Children)
                if (Grid.GetRow(child) != 1) child.Visibility = Visibility.Collapsed;
            shell.RowDefinitions[0].Height = shell.RowDefinitions[2].Height = new GridLength(0);
            ((FrameworkElement)shell.Children.Cast<FrameworkElement>().Single(child => Grid.GetRow(child) == 1)).Margin = new Thickness(0);
            for (int slot = 1; slot <= 2; slot++)
            {
                string prefix = "CompactAccount" + slot;
                _views[slot - 1] = new AccountView {
                    SubscriptionValue = Find<TextBlock>("Account" + slot + "SubscriptionValue"),
                    CompactSubscriptionValue = Find<TextBlock>(prefix + "SubscriptionValue"),
                    CompactPrimaryRing = Find<System.Windows.Shapes.Path>(prefix + "PrimaryRing"),
                    CompactPrimaryRecommendationRing = Find<System.Windows.Shapes.Path>(prefix + "PrimaryRecommendationRing"),
                    CompactPrimaryTimeBar = Find<ProgressBar>(prefix + "PrimaryTimeBar"),
                    CompactPrimaryTimeValue = Find<TextBlock>(prefix + "PrimaryTimeValue"),
                    CompactSecondaryRing = Find<System.Windows.Shapes.Path>(prefix + "SecondaryRing"),
                    CompactSecondaryRecommendationRing = Find<System.Windows.Shapes.Path>(prefix + "SecondaryRecommendationRing"),
                    CompactSecondaryTimeBar = Find<ProgressBar>(prefix + "SecondaryTimeBar"),
                    CompactSecondaryTimeValue = Find<TextBlock>(prefix + "SecondaryTimeValue")
                };
                // These are not interactive inside a Windows-hosted image. The card opens the meter.
                Find<Button>(prefix + "HistoryButton").Visibility = Visibility.Collapsed;
                Find<Button>(prefix + "CodexLoginButton").Visibility = Visibility.Collapsed;
            }
            DashboardController.CollectFontTargets(_root, Window, _fonts, new HashSet<DependencyObject>());
            Window.Content = null;
            _surface = new HwndSource(new HwndSourceParameters("CodexUsageMeter.WidgetRender") {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080, Width = 300, Height = 350
            });
            _surface.RootVisual = _root;
        }

        internal Dictionary<string, List<Dictionary<string, string>>> Render(IEnumerable<AccountState> accounts, LayoutSettings settings, SystemSnapshot system, double fontScale, DateTime now)
        {
            AccountState[] available = accounts.ToArray();
            string[] selected = settings.Widget.Cards.Where(card => card.Visible &&
                (card.Id == "pc" || available.Any(account => card.Id == "account" + account.Number))).Select(card => card.Id).ToArray();
            var result = new Dictionary<string, List<Dictionary<string, string>>>();
            DashboardController.ScaleFontTargets(_fonts, fontScale);
            if (system != null) DashboardController.RenderCompactSystem(Window, system);
            for (int size = 0; size < Sizes.Length; size++)
            {
                var pages = new List<Dictionary<string, string>>();
                int offset = 0;
                do
                {
                    // Keep saved order; page instead of squeezing three complete cards into Small.
                    var cards = new List<string>();
                    int accountCount = 0;
                    while (offset < selected.Length && cards.Count < (size == 2 ? 2 : 1))
                    {
                        string id = selected[offset];
                        if (id != "pc" && accountCount == 2) break;
                        cards.Add(id); offset++;
                        if (id != "pc") accountCount++;
                    }
                    LayoutSettings pageLayout = settings.Copy();
                    foreach (LayoutCardSettings card in pageLayout.Widget.Cards) card.Visible = cards.Contains(card.Id);
                    AccountState[] pageAccounts = cards.Where(id => id != "pc")
                        .Select(id => available.Single(account => id == "account" + account.Number)).ToArray();
                    for (int slot = 0; slot < 2; slot++)
                    {
                        _views[slot].State = slot < pageAccounts.Length ? pageAccounts[slot] : null;
                        if (_views[slot].State != null) RenderAccount(slot + 1, _views[slot].State, now);
                    }
                    Layout.Apply(pageLayout, _views, fontScale);
                    int height = Heights[size];
                    _root.Measure(new Size(300, height)); _root.Arrange(new Rect(0, 0, 300, height)); _root.UpdateLayout();
                    RenderTargetBitmap bitmap = new RenderTargetBitmap(600, height * 2, 192, 192, PixelFormats.Pbgra32);
                    bitmap.Render(_root);
                    PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (MemoryStream stream = new MemoryStream())
                    {
                        encoder.Save(stream);
                        pages.Add(new Dictionary<string, string> {
                            { "image", "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()) },
                            { "alt", String.Join(" · ", VisibleText(_root)) }
                        });
                    }
                } while (offset < selected.Length);
                result.Add(Sizes[size], pages);
            }
            return result;
        }

        private void RenderAccount(int slot, AccountState state, DateTime now)
        {
            AccountSnapshot original = state.LastSnapshot;
            string status = WindowsWidgetBridge.Status(original);
            bool expired = status == "ok" && new[] { original.Primary, original.Secondary }
                .Any(limit => limit != null && limit.ResetsAt.HasValue && limit.ResetsAt.Value.ToUniversalTime() <= now);
            bool stale = status == "ok" && (now - original.RateLimitsObservedAtUtc > TimeSpan.FromMinutes(3) ||
                original.RateLimitsObservedAtUtc - now > TimeSpan.FromMinutes(1));
            // Only display fields enter the isolated surface; identity and raw errors never do.
            var display = new AccountSnapshot {
                IsAuthenticated = status == "ok", Primary = status == "ok" && !expired ? original.Primary : null,
                Secondary = status == "ok" && !expired ? original.Secondary : null,
                ResetCreditCount = status == "ok" ? original.ResetCreditCount : null,
                ResetCredits = status == "ok" ? original.ResetCredits : null,
                Subscription = status == "ok" ? original.Subscription : null
            };
            DashboardController.RenderCompactAccount(Window, _views[slot - 1], display, slot);
            string prefix = "CompactAccount" + slot;
            Find<TextBlock>(prefix + "TitleText").Text = "계정 " + state.Number;
            Find<TextBlock>(prefix + "BadgeText").Text = state.Number.ToString();
            string plan = original == null ? "" : (original.PlanType ?? "").ToLowerInvariant();
            string[] known = { "free", "plus", "pro", "prolite", "business", "team", "enterprise", "edu" };
            plan = !known.Contains(plan) ? "" : plan == "prolite" ? "Pro Lite" : Char.ToUpperInvariant(plan[0]) + plan.Substring(1);
            string notice = expired ? "초기화됨 · 갱신 대기" : stale ? "오래됨 · 마지막 기록" :
                status == "error" ? "조회 실패" : status == "unlinked" ? "연결 안 됨" : status == "waiting" ? "조회 중" : "";
            Find<TextBlock>(prefix + "Identity").Text = String.Join(" · ", new[] { plan, notice }.Where(text => text.Length > 0));
        }

        private static IEnumerable<string> VisibleText(DependencyObject root)
        {
            TextBlock text = root as TextBlock;
            if (text != null && text.IsVisible && !String.IsNullOrEmpty(text.Text)) yield return text.Text;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject element = child as DependencyObject;
                if (element != null) foreach (string value in VisibleText(element)) yield return value;
            }
        }
        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        public void Dispose() { _surface.Dispose(); }
    }
}
