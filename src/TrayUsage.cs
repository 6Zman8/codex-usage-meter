using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexUsageMeter
{
    internal sealed class TrayUsageSettings
    {
        internal bool Enabled = true;
        internal int AccountNumber = 1;
        internal bool Secondary = true;

        internal static TrayUsageSettings Load(RegistryKey key)
        {
            return new TrayUsageSettings {
                Enabled = Read(key, "TrayUsageEnabled", 1) != 0,
                AccountNumber = Math.Max(1, Math.Min(4, Read(key, "TrayUsageAccount", 1))),
                Secondary = Read(key, "TrayUsageSecondary", 1) != 0
            };
        }

        private static int Read(RegistryKey key, string name, int fallback)
        {
            int value;
            return key != null && Int32.TryParse(Convert.ToString(key.GetValue(name)), out value) ? value : fallback;
        }

        internal void Save(RegistryKey key)
        {
            key.SetValue("TrayUsageEnabled", Enabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("TrayUsageAccount", AccountNumber, RegistryValueKind.DWord);
            key.SetValue("TrayUsageSecondary", Secondary ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    internal sealed class TrayUsageDisplay
    {
        internal string Text;
        internal string ToolTip;

        internal static TrayUsageDisplay Build(IEnumerable<AccountState> accounts, TrayUsageSettings settings, DateTime now)
        {
            AccountState account = accounts.FirstOrDefault(value => value.Number == settings.AccountNumber);
            AccountSnapshot snapshot = account == null ? null : account.LastSnapshot;
            string status = WindowsWidgetBridge.Status(snapshot);
            RateWindow limit = snapshot == null ? null : settings.Secondary ? snapshot.Secondary : snapshot.Primary;
            string name = limit == null || String.IsNullOrWhiteSpace(limit.Name)
                ? settings.Secondary ? "장기 한도" : "단기 한도" : limit.Name;
            string notice = account == null ? "계정 없음" : status == "error" ? "조회 실패" :
                status == "unlinked" ? "연결 안 됨" : status == "waiting" ? "조회 중" :
                limit == null ? "미제공" :
                Double.IsNaN(limit.RemainingPercent) || Double.IsInfinity(limit.RemainingPercent) ? "정보 없음" :
                (limit.ResetsAt.HasValue && limit.ResetsAt.Value.ToUniversalTime() <= now) ||
                now - snapshot.RateLimitsObservedAtUtc > TimeSpan.FromMinutes(3) ||
                snapshot.RateLimitsObservedAtUtc - now > TimeSpan.FromMinutes(1) ? "갱신 대기" : null;
            string text = notice == null
                ? Math.Round(Math.Max(0, Math.Min(100, limit.RemainingPercent))).ToString("0", CultureInfo.InvariantCulture)
                : "--";
            string tooltip = "Codex · 계정 " + settings.AccountNumber + " · " + name + " · " + (notice ?? "잔여 " + text + "%");
            return new TrayUsageDisplay { Text = text, ToolTip = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip };
        }
    }

    internal sealed class TrayUsageIcon : IDisposable
    {
        private readonly Forms.NotifyIcon _notify;
        private readonly Icon _original;
        private readonly Action _open;
        private Icon _numberIcon;
        private string _text;

        internal TrayUsageIcon(Forms.NotifyIcon notify, Action open)
        {
            _notify = notify;
            _original = notify.Icon;
            _open = open;
            _notify.MouseClick += MouseClick;
        }

        private void MouseClick(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left) _open();
        }

        internal void Update(TrayUsageDisplay display, bool enabled)
        {
            if (!enabled)
            {
                RestoreIcon();
                _notify.Text = "Codex 사용량 미터기";
                return;
            }
            if (_numberIcon == null || _text != display.Text)
            {
                Icon next = Render(display.Text, Forms.SystemInformation.SmallIconSize.Width);
                Icon previous = _numberIcon;
                _notify.Icon = next;
                _numberIcon = next;
                _text = display.Text;
                if (previous != null) previous.Dispose();
            }
            _notify.Text = display.ToolTip;
        }

        internal static Icon Render(string text, int size)
        {
            using (Bitmap bitmap = new Bitmap(size, size))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
                using (FontFamily family = new FontFamily("Segoe UI"))
                using (GraphicsPath glyphs = new GraphicsPath())
                using (Matrix transform = new Matrix())
                {
                    graphics.Clear(Color.FromArgb(35, 35, 35));
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    glyphs.AddString(text, family, (int)FontStyle.Bold, size * 0.8f, PointF.Empty, format);
                    RectangleF bounds = glyphs.GetBounds();
                    // Fit actual glyph bounds; font advances can miss a hinted edge at 16 px.
                    float scale = Math.Min(1f, Math.Min((size - 2f) / bounds.Width, (size - 2f) / bounds.Height));
                    transform.Translate(-bounds.X, -bounds.Y, MatrixOrder.Append);
                    transform.Scale(scale, scale, MatrixOrder.Append);
                    transform.Translate((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2, MatrixOrder.Append);
                    glyphs.Transform(transform);
                    graphics.FillPath(Brushes.White, glyphs);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private void RestoreIcon()
        {
            _notify.Icon = _original;
            if (_numberIcon != null) _numberIcon.Dispose();
            _numberIcon = null;
            _text = null;
        }

        public void Dispose()
        {
            _notify.MouseClick -= MouseClick;
            RestoreIcon();
        }
    }
}
