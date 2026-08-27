using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Codex Usage Meter")]
[assembly: AssemblyProduct("Codex Usage Meter")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace CodexUsageMeter
{
    internal static class Program
    {
        private static Mutex _singleInstance;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && String.Equals(args[0], "--update-check-test", StringComparison.OrdinalIgnoreCase))
            {
                string resultPath = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-check-result.txt");
                try
                {
                    Version assumedCurrentVersion = null;
                    if (args.Length > 2 && !Version.TryParse(args[2], out assumedCurrentVersion))
                    {
                        throw new ArgumentException("테스트 기준 버전 형식이 올바르지 않습니다: " + args[2]);
                    }
                    UpdateCheckResult result = UpdateClient.CheckLatestAsync(assumedCurrentVersion).GetAwaiter().GetResult();
                    string stagedPath = UpdateClient.DownloadAndVerifyAsync(result.Release).GetAwaiter().GetResult();
                    try { File.Delete(stagedPath); } catch { }
                    File.WriteAllText(resultPath, "PASS current=v" + result.CurrentVersionText +
                        " latest=v" + result.LatestVersionText + " available=" + result.UpdateAvailable.ToString() +
                        " asset=" + (result.Release == null ? String.Empty : result.Release.DownloadUrl) +
                        " sha256=" + (result.Release == null ? String.Empty : result.Release.Sha256) +
                        " downloadVerified=True", Encoding.UTF8);
                    return 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(resultPath, "FAIL " + ex.ToString(), Encoding.UTF8);
                    return 1;
                }
            }

            if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                string resultPath = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-result.txt");
                return SelfTest.Run(resultPath);
            }

            bool uiSmoke = args.Length > 0 && String.Equals(args[0], "--ui-smoke", StringComparison.OrdinalIgnoreCase);
            string previewPath = uiSmoke && args.Length > 1 ? Path.GetFullPath(args[1]) : null;
            string compactPreviewPath = uiSmoke && args.Length > 2 ? Path.GetFullPath(args[2]) : null;
            string responsivePreviewPath = uiSmoke && args.Length > 3 ? Path.GetFullPath(args[3]) : null;
            string maximizedPreviewPath = uiSmoke && args.Length > 4 ? Path.GetFullPath(args[4]) : null;
            string settingsPreviewPath = uiSmoke && args.Length > 5 ? Path.GetFullPath(args[5]) : null;

            bool created = true;
            if (!uiSmoke)
            {
                _singleInstance = new Mutex(true, "Local\\CodexUsageMeter.Singleton", out created);
                if (!created)
                {
                    MessageBox.Show("Codex 사용량 미터기가 이미 실행 중입니다. 트레이 아이콘을 확인해 주세요.",
                        "Codex 사용량 미터기", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
            }

            try
            {
                Application application = new Application();
                application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                Window window = DashboardController.LoadWindow();
                DashboardController controller = new DashboardController(window);
                DispatcherTimer smokeTimer = null;
                if (uiSmoke)
                {
                    smokeTimer = new DispatcherTimer();
                    smokeTimer.Interval = TimeSpan.FromSeconds(5);
                    smokeTimer.Tick += delegate
                    {
                        smokeTimer.Stop();
                        try
                        {
                            if (!String.IsNullOrWhiteSpace(previewPath))
                            {
                                controller.ApplyDesignPreview();
                                controller.SetDisplayModeForPreview(false);
                                controller.CapturePreview(previewPath);
                                if (!String.IsNullOrWhiteSpace(compactPreviewPath))
                                {
                                    controller.SetDisplayModeForPreview(true);
                                    controller.CapturePreview(compactPreviewPath);
                                }
                                if (!String.IsNullOrWhiteSpace(responsivePreviewPath))
                                {
                                    controller.SetDisplayModeForPreview(false);
                                    controller.SetWindowSizeForPreview(1280.0, 640.0);
                                    controller.CapturePreview(responsivePreviewPath);
                                }
                                if (!String.IsNullOrWhiteSpace(maximizedPreviewPath))
                                {
                                    controller.SetDisplayModeForPreview(false);
                                    controller.SetMaximizedForPreview();
                                    controller.CapturePreview(maximizedPreviewPath);
                                    controller.RestoreFromMaximizedForPreview();
                                }
                                if (!String.IsNullOrWhiteSpace(settingsPreviewPath))
                                {
                                    controller.SetDisplayModeForPreview(false);
                                    controller.ShowSettingsForPreview();
                                    controller.CapturePreview(settingsPreviewPath);
                                    controller.HideSettingsForPreview();
                                }
                            }
                        }
                        finally
                        {
                            controller.RequestCloseForSmoke();
                        }
                    };
                    smokeTimer.Start();
                }
                application.Run(window);
                controller.Dispose();
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("앱을 시작하지 못했습니다.\n\n" + ex.Message,
                    "Codex 사용량 미터기", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            finally
            {
                if (_singleInstance != null)
                {
                    try { _singleInstance.ReleaseMutex(); } catch { }
                    _singleInstance.Dispose();
                }
            }
        }
    }

    internal sealed class AccountState
    {
        public int Number;
        public string Label;
        public CodexRpcClient Client;
        public AccountSnapshot LastSnapshot;
        public int CalendarMonthOffset;
    }

    internal sealed class AccountView
    {
        public string Label;
        public CodexRpcClient Client;
        public AccountState State;
        public Border Container;
        public Border CompactContainer;
        public TextBlock TitleText;
        public TextBlock CompactTitleText;
        public TextBlock BadgeText;
        public TextBlock CompactBadgeText;
        public TextBlock Identity;
        public Button LoginButton;
        public Button LogoutButton;
        public TextBlock PrimaryName;
        public TextBlock PrimaryValue;
        public ProgressBar PrimaryBar;
        public ProgressBar PrimaryTimeBar;
        public TextBlock PrimaryReset;
        public TextBlock PrimaryRemaining;
        public TextBlock SecondaryName;
        public TextBlock SecondaryValue;
        public ProgressBar SecondaryBar;
        public ProgressBar SecondaryTimeBar;
        public TextBlock SecondaryReset;
        public TextBlock SecondaryRemaining;
        public TextBlock ResetCreditsValue;
        public TextBlock ResetCreditsDetail;
        public TextBlock LifetimeValue;
        public TextBlock PeakValue;
        public TextBlock StreakValue;
        public TextBlock LongestTurnValue;
        public TextBlock CalendarTitle;
        public Button CalendarPreviousButton;
        public Button CalendarNextButton;
        public TextBlock WeeklyUsageValue;
        public UniformGrid WeeklyUsageGrid;
        public UniformGrid UsageGrid;
        public TextBlock UsageEmpty;
        public TextBlock Status;
        public AccountSnapshot LastSnapshot;
        public int CalendarMonthOffset;
        public int CalendarDetailLevel = -1;
        public System.Windows.Shapes.Path CompactPrimaryRing;
        public ProgressBar CompactPrimaryTimeBar;
        public TextBlock CompactPrimaryTimeValue;
        public System.Windows.Shapes.Path CompactPrimaryRecommendationRing;
        public System.Windows.Shapes.Path CompactSecondaryRing;
        public ProgressBar CompactSecondaryTimeBar;
        public TextBlock CompactSecondaryTimeValue;
        public System.Windows.Shapes.Path CompactSecondaryRecommendationRing;
        public TextBlock CompactPaceValue;
    }

    internal sealed class DashboardController : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private sealed class PerformanceDisplayItem
        {
            public string Key;
            public string Name;
            public string Value;
            public string Detail;
            public double Percent;
            public string Accent;
        }

        private sealed class PerformanceCardView
        {
            public TextBlock Name;
            public TextBlock Value;
            public TextBlock Detail;
            public ProgressBar Bar;
            public Canvas GraphCanvas;
            public System.Windows.Shapes.Polyline Graph;
        }

        private sealed class FontTarget
        {
            public DependencyObject Element;
            public double BaseSize;
        }

        private readonly Window _window;
        private readonly string _codexPath;
        private readonly AccountView _account1;
        private readonly AccountView _account2;
        private readonly List<AccountState> _accounts;
        private readonly string _accountsRoot;
        private readonly SystemMonitor _systemMonitor;
        private readonly DispatcherTimer _systemTimer;
        private readonly DispatcherTimer _accountTimer;
        private readonly Button _refreshButton;
        private readonly Button _hideButton;
        private readonly Button _topmostButton;
        private readonly Button _settingsButton;
        private readonly Button _maximizeButton;
        private readonly Button _closeButton;
        private readonly Border _titleBar;
        private readonly Border _expandedShell;
        private readonly Grid _expandedLayout;
        private readonly Grid _compactLayout;
        private readonly Button _compactModeButton;
        private readonly Button _expandedModeButton;
        private readonly Button _compactRefreshButton;
        private readonly Button _compactHideButton;
        private readonly Button _compactTopmostButton;
        private readonly Button _compactSettingsButton;
        private readonly Button _compactMaximizeButton;
        private readonly Button _compactCloseButton;
        private readonly Border _compactTitleBar;
        private readonly Border _compactShell;
        private readonly CheckBox _autostartCheckBox;
        private readonly TextBlock _footerStatus;
        private readonly UniformGrid _performanceItemsPanel;
        private readonly TextBlock _performanceCountText;
        private readonly TextBlock _systemStatus;
        private readonly TextBlock _compactAccount1Identity;
        private readonly TextBlock _compactAccount1PrimaryValue;
        private readonly TextBlock _compactAccount1SecondaryValue;
        private readonly TextBlock _compactAccount1ResetValue;
        private readonly TextBlock _compactAccount2Identity;
        private readonly TextBlock _compactAccount2PrimaryValue;
        private readonly TextBlock _compactAccount2SecondaryValue;
        private readonly TextBlock _compactAccount2ResetValue;
        private readonly TextBlock _compactCpuValue;
        private readonly TextBlock _compactGpuLabel;
        private readonly TextBlock _compactGpuValue;
        private readonly TextBlock _compactMemoryValue;
        private readonly TextBlock _compactDiskLabel;
        private readonly TextBlock _compactDiskValue;
        private readonly TextBlock _compactNetworkValue;
        private readonly System.Windows.Shapes.Path _compactCpuRing;
        private readonly System.Windows.Shapes.Path _compactGpuRing;
        private readonly System.Windows.Shapes.Path _compactMemoryRing;
        private readonly System.Windows.Shapes.Path _compactDiskRing;
        private readonly TextBlock _compactFooterStatus;
        private readonly TextBlock _compactAccountSummaryText;
        private readonly TextBlock _accountCountBadgeText;
        private readonly TextBlock _accountSummaryText;
        private readonly Grid _modalOverlay;
        private readonly TextBlock _modalTitle;
        private readonly TextBlock _modalMessage;
        private readonly Border _modalCodePanel;
        private readonly TextBlock _modalCode;
        private readonly Button _modalPrimaryButton;
        private readonly Button _modalSecondaryButton;
        private readonly Grid _settingsOverlay;
        private readonly Button _settingsCloseButton;
        private readonly Button _settingsDoneButton;
        private readonly Button _fontDecreaseButton;
        private readonly Button _fontResetButton;
        private readonly Button _fontIncreaseButton;
        private readonly TextBlock _fontScaleValue;
        private readonly Button _updateCheckButton;
        private readonly TextBlock _updateStatusText;
        private readonly TextBlock _appVersionValue;
        private readonly Button _accountCountDecreaseButton;
        private readonly Button _accountCountIncreaseButton;
        private readonly TextBlock _accountCountValue;
        private readonly Button _accountPagePreviousButton;
        private readonly Button _accountPageNextButton;
        private readonly TextBlock _accountPageText;
        private readonly Button _compactAccountPageButton;
        private readonly Forms.NotifyIcon _trayIcon;
        private readonly Dictionary<string, PerformanceCardView> _performanceCards;
        private readonly Dictionary<string, Queue<double>> _performanceHistory;
        private readonly List<FontTarget> _fontTargets;
        private readonly HashSet<DependencyObject> _fontTargetElements;
        private bool _sampling;
        private bool _refreshing;
        private bool _settingAutostart;
        private bool _updateChecking;
        private bool _disposed;
        private bool _compactMode;
        private bool _displayModeInitialized;
        private double _compactWidth = 460.0;
        private double _compactHeight = 780.0;
        private double _expandedWidth;
        private double _expandedHeight;
        private double _fontScale = 1.5;
        private string _performanceSignature;
        private int _accountCount = 2;
        private int _accountPage;
        private bool _customMaximized;
        private bool _handlingNativeMaximize;
        private Rect _restoreBounds;
        private HwndSource _windowSource;
        private Action _modalPrimaryAction;
        private Action _modalSecondaryAction;

        public DashboardController(Window window)
        {
            _window = window;
            _codexPath = CodexLocator.Find();
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _accountsRoot = Path.Combine(localData, "CodexUsageMeter", "accounts");
            _accounts = new List<AccountState>();

            _account1 = CreateAccountView(1, "계정 1", null);
            _account2 = CreateAccountView(2, "계정 2", null);

            _refreshButton = Find<Button>("RefreshButton");
            _hideButton = Find<Button>("HideButton");
            _topmostButton = Find<Button>("TopmostButton");
            _settingsButton = Find<Button>("SettingsButton");
            _maximizeButton = Find<Button>("MaximizeButton");
            _closeButton = Find<Button>("CloseButton");
            _titleBar = Find<Border>("TitleBar");
            _expandedShell = Find<Border>("ExpandedShell");
            _expandedLayout = Find<Grid>("ExpandedLayout");
            _compactLayout = Find<Grid>("CompactLayout");
            _compactModeButton = Find<Button>("CompactModeButton");
            _expandedModeButton = Find<Button>("ExpandedModeButton");
            _compactRefreshButton = Find<Button>("CompactRefreshButton");
            _compactHideButton = Find<Button>("CompactHideButton");
            _compactTopmostButton = Find<Button>("CompactTopmostButton");
            _compactSettingsButton = Find<Button>("CompactSettingsButton");
            _compactMaximizeButton = Find<Button>("CompactMaximizeButton");
            _compactCloseButton = Find<Button>("CompactCloseButton");
            _compactTitleBar = Find<Border>("CompactTitleBar");
            _compactShell = Find<Border>("CompactShell");
            _autostartCheckBox = Find<CheckBox>("AutostartCheckBox");
            _footerStatus = Find<TextBlock>("FooterStatus");
            _performanceItemsPanel = Find<UniformGrid>("PerformanceItemsPanel");
            _performanceCountText = Find<TextBlock>("PerformanceCountText");
            _systemStatus = Find<TextBlock>("SystemStatus");
            _compactAccount1Identity = Find<TextBlock>("CompactAccount1Identity");
            _compactAccount1PrimaryValue = Find<TextBlock>("CompactAccount1PrimaryValue");
            _compactAccount1SecondaryValue = Find<TextBlock>("CompactAccount1SecondaryValue");
            _compactAccount1ResetValue = Find<TextBlock>("CompactAccount1ResetValue");
            _compactAccount2Identity = Find<TextBlock>("CompactAccount2Identity");
            _compactAccount2PrimaryValue = Find<TextBlock>("CompactAccount2PrimaryValue");
            _compactAccount2SecondaryValue = Find<TextBlock>("CompactAccount2SecondaryValue");
            _compactAccount2ResetValue = Find<TextBlock>("CompactAccount2ResetValue");
            _compactCpuValue = Find<TextBlock>("CompactCpuValue");
            _compactGpuLabel = Find<TextBlock>("CompactGpuLabel");
            _compactGpuValue = Find<TextBlock>("CompactGpuValue");
            _compactMemoryValue = Find<TextBlock>("CompactMemoryValue");
            _compactDiskLabel = Find<TextBlock>("CompactDiskLabel");
            _compactDiskValue = Find<TextBlock>("CompactDiskValue");
            _compactNetworkValue = Find<TextBlock>("CompactNetworkValue");
            _compactCpuRing = Find<System.Windows.Shapes.Path>("CompactCpuRing");
            _compactGpuRing = Find<System.Windows.Shapes.Path>("CompactGpuRing");
            _compactMemoryRing = Find<System.Windows.Shapes.Path>("CompactMemoryRing");
            _compactDiskRing = Find<System.Windows.Shapes.Path>("CompactDiskRing");
            _compactFooterStatus = Find<TextBlock>("CompactFooterStatus");
            _compactAccountSummaryText = Find<TextBlock>("CompactAccountSummaryText");
            _accountCountBadgeText = Find<TextBlock>("AccountCountBadgeText");
            _accountSummaryText = Find<TextBlock>("AccountSummaryText");
            _modalOverlay = Find<Grid>("ModalOverlay");
            _modalTitle = Find<TextBlock>("ModalTitle");
            _modalMessage = Find<TextBlock>("ModalMessage");
            _modalCodePanel = Find<Border>("ModalCodePanel");
            _modalCode = Find<TextBlock>("ModalCode");
            _modalPrimaryButton = Find<Button>("ModalPrimaryButton");
            _modalSecondaryButton = Find<Button>("ModalSecondaryButton");
            _settingsOverlay = Find<Grid>("SettingsOverlay");
            _settingsCloseButton = Find<Button>("SettingsCloseButton");
            _settingsDoneButton = Find<Button>("SettingsDoneButton");
            _fontDecreaseButton = Find<Button>("FontDecreaseButton");
            _fontResetButton = Find<Button>("FontResetButton");
            _fontIncreaseButton = Find<Button>("FontIncreaseButton");
            _fontScaleValue = Find<TextBlock>("FontScaleValue");
            _updateCheckButton = Find<Button>("UpdateCheckButton");
            _updateStatusText = Find<TextBlock>("UpdateStatusText");
            _appVersionValue = Find<TextBlock>("AppVersionValue");
            _accountCountDecreaseButton = Find<Button>("AccountCountDecreaseButton");
            _accountCountIncreaseButton = Find<Button>("AccountCountIncreaseButton");
            _accountCountValue = Find<TextBlock>("AccountCountValue");
            _accountPagePreviousButton = Find<Button>("AccountPagePreviousButton");
            _accountPageNextButton = Find<Button>("AccountPageNextButton");
            _accountPageText = Find<TextBlock>("AccountPageText");
            _compactAccountPageButton = Find<Button>("CompactAccountPageButton");

            _performanceCards = new Dictionary<string, PerformanceCardView>(StringComparer.OrdinalIgnoreCase);
            _performanceHistory = new Dictionary<string, Queue<double>>(StringComparer.OrdinalIgnoreCase);
            _fontTargets = new List<FontTarget>();
            _fontTargetElements = new HashSet<DependencyObject>();

            _systemMonitor = new SystemMonitor();
            _systemTimer = new DispatcherTimer();
            _systemTimer.Interval = TimeSpan.FromSeconds(1);
            _systemTimer.Tick += SystemTimerTick;
            _accountTimer = new DispatcherTimer();
            _accountTimer.Interval = TimeSpan.FromSeconds(60);
            _accountTimer.Tick += AccountTimerTick;

            _refreshButton.Click += RefreshButtonClick;
            _hideButton.Click += HideButtonClick;
            _topmostButton.Click += TopmostButtonClick;
            _settingsButton.Click += SettingsButtonClick;
            _maximizeButton.Click += MaximizeButtonClick;
            _closeButton.Click += CloseButtonClick;
            _titleBar.MouseLeftButtonDown += TitleBarMouseLeftButtonDown;
            _compactModeButton.Click += delegate { SetDisplayMode(true); };
            _expandedModeButton.Click += delegate { SetDisplayMode(false); };
            _compactRefreshButton.Click += RefreshButtonClick;
            _compactHideButton.Click += HideButtonClick;
            _compactTopmostButton.Click += TopmostButtonClick;
            _compactSettingsButton.Click += SettingsButtonClick;
            _compactMaximizeButton.Click += MaximizeButtonClick;
            _compactCloseButton.Click += CloseButtonClick;
            _compactTitleBar.MouseLeftButtonDown += TitleBarMouseLeftButtonDown;
            _autostartCheckBox.Checked += AutostartChanged;
            _autostartCheckBox.Unchecked += AutostartChanged;
            _settingsCloseButton.Click += SettingsCloseButtonClick;
            _settingsDoneButton.Click += SettingsCloseButtonClick;
            _fontDecreaseButton.Click += FontDecreaseButtonClick;
            _fontResetButton.Click += FontResetButtonClick;
            _fontIncreaseButton.Click += FontIncreaseButtonClick;
            _updateCheckButton.Click += UpdateCheckButtonClick;
            _accountCountDecreaseButton.Click += AccountCountDecreaseButtonClick;
            _accountCountIncreaseButton.Click += AccountCountIncreaseButtonClick;
            _accountPagePreviousButton.Click += AccountPagePreviousButtonClick;
            _accountPageNextButton.Click += AccountPageNextButtonClick;
            _compactAccountPageButton.Click += CompactAccountPageButtonClick;
            _account1.LoginButton.Click += Account1LoginClick;
            _account2.LoginButton.Click += Account2LoginClick;
            _account1.LogoutButton.Click += Account1LogoutClick;
            _account2.LogoutButton.Click += Account2LogoutClick;
            _account1.CalendarPreviousButton.Click += delegate { ChangeCalendarMonth(_account1, -1); };
            _account1.CalendarNextButton.Click += delegate { ChangeCalendarMonth(_account1, 1); };
            _account2.CalendarPreviousButton.Click += delegate { ChangeCalendarMonth(_account2, -1); };
            _account2.CalendarNextButton.Click += delegate { ChangeCalendarMonth(_account2, 1); };
            _window.Loaded += WindowLoaded;
            _window.SourceInitialized += WindowSourceInitialized;
            _window.StateChanged += WindowStateChanged;
            _window.SizeChanged += WindowSizeChanged;
            _window.Closing += WindowClosing;
            _window.KeyDown += WindowKeyDown;
            _modalPrimaryButton.Click += ModalPrimaryClick;
            _modalSecondaryButton.Click += ModalSecondaryClick;

            ApplyAccountCount(UserSettings.LoadAccountCount(), false);

            _trayIcon = CreateTrayIcon();
            _trayIcon.Visible = true;
        }

        public static Window LoadWindow()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("CodexUsageMeter.Dashboard.xaml"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("내장 UI 리소스를 찾지 못했습니다.");
                }
                Window window = XamlReader.Load(stream) as Window;
                if (window == null)
                {
                    throw new InvalidOperationException("UI 리소스를 읽지 못했습니다.");
                }
                WindowChrome.SetWindowChrome(window, new WindowChrome {
                    CaptionHeight = 0.0,
                    ResizeBorderThickness = new Thickness(0.0),
                    GlassFrameThickness = new Thickness(0.0),
                    CornerRadius = new CornerRadius(0.0),
                    UseAeroCaptionButtons = false
                });
                using (Stream iconStream = assembly.GetManifestResourceStream("CodexUsageMeter.AppIcon.png"))
                {
                    if (iconStream == null)
                    {
                        throw new InvalidOperationException("내장 앱 아이콘을 찾지 못했습니다.");
                    }
                    BitmapFrame iconFrame = BitmapFrame.Create(iconStream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    iconFrame.Freeze();
                    window.Icon = iconFrame;
                }
                return window;
            }
        }

        private AccountView CreateAccountView(int number, string label, CodexRpcClient client)
        {
            string prefix = "Account" + number.ToString();
            AccountView view = new AccountView();
            view.Label = label;
            view.Client = client;
            view.Container = Find<Border>(prefix + "Card");
            view.CompactContainer = Find<Border>("Compact" + prefix + "Card");
            view.TitleText = Find<TextBlock>(prefix + "TitleText");
            view.CompactTitleText = Find<TextBlock>("Compact" + prefix + "TitleText");
            view.BadgeText = Find<TextBlock>(prefix + "BadgeText");
            view.CompactBadgeText = Find<TextBlock>("Compact" + prefix + "BadgeText");
            view.Identity = Find<TextBlock>(prefix + "Identity");
            view.LoginButton = Find<Button>(prefix + "LoginButton");
            view.LogoutButton = Find<Button>(prefix + "LogoutButton");
            view.PrimaryName = Find<TextBlock>(prefix + "PrimaryName");
            view.PrimaryValue = Find<TextBlock>(prefix + "PrimaryValue");
            view.PrimaryBar = Find<ProgressBar>(prefix + "PrimaryBar");
            view.PrimaryTimeBar = Find<ProgressBar>(prefix + "PrimaryTimeBar");
            view.PrimaryReset = Find<TextBlock>(prefix + "PrimaryReset");
            view.PrimaryRemaining = Find<TextBlock>(prefix + "PrimaryRemaining");
            view.SecondaryName = Find<TextBlock>(prefix + "SecondaryName");
            view.SecondaryValue = Find<TextBlock>(prefix + "SecondaryValue");
            view.SecondaryBar = Find<ProgressBar>(prefix + "SecondaryBar");
            view.SecondaryTimeBar = Find<ProgressBar>(prefix + "SecondaryTimeBar");
            view.SecondaryReset = Find<TextBlock>(prefix + "SecondaryReset");
            view.SecondaryRemaining = Find<TextBlock>(prefix + "SecondaryRemaining");
            view.ResetCreditsValue = Find<TextBlock>(prefix + "ResetCreditsValue");
            view.ResetCreditsDetail = Find<TextBlock>(prefix + "ResetCreditsDetail");
            view.LifetimeValue = Find<TextBlock>(prefix + "LifetimeValue");
            view.PeakValue = Find<TextBlock>(prefix + "PeakValue");
            view.StreakValue = Find<TextBlock>(prefix + "StreakValue");
            view.LongestTurnValue = Find<TextBlock>(prefix + "LongestTurnValue");
            view.CalendarTitle = Find<TextBlock>(prefix + "CalendarTitle");
            view.CalendarPreviousButton = Find<Button>(prefix + "CalendarPreviousButton");
            view.CalendarNextButton = Find<Button>(prefix + "CalendarNextButton");
            view.WeeklyUsageValue = Find<TextBlock>(prefix + "WeeklyUsageValue");
            view.WeeklyUsageGrid = Find<UniformGrid>(prefix + "WeeklyUsageGrid");
            view.UsageGrid = Find<UniformGrid>(prefix + "UsageGrid");
            view.UsageEmpty = Find<TextBlock>(prefix + "UsageEmpty");
            view.Status = Find<TextBlock>(prefix + "Status");
            string compactPrefix = "Compact" + prefix;
            view.CompactPrimaryRing = Find<System.Windows.Shapes.Path>(compactPrefix + "PrimaryRing");
            view.CompactPrimaryTimeBar = Find<ProgressBar>(compactPrefix + "PrimaryTimeBar");
            view.CompactPrimaryTimeValue = Find<TextBlock>(compactPrefix + "PrimaryTimeValue");
            view.CompactPrimaryRecommendationRing = Find<System.Windows.Shapes.Path>(compactPrefix + "PrimaryRecommendationRing");
            view.CompactSecondaryRing = Find<System.Windows.Shapes.Path>(compactPrefix + "SecondaryRing");
            view.CompactSecondaryTimeBar = Find<ProgressBar>(compactPrefix + "SecondaryTimeBar");
            view.CompactSecondaryTimeValue = Find<TextBlock>(compactPrefix + "SecondaryTimeValue");
            view.CompactSecondaryRecommendationRing = Find<System.Windows.Shapes.Path>(compactPrefix + "SecondaryRecommendationRing");
            view.CompactPaceValue = Find<TextBlock>(compactPrefix + "PaceValue");
            return view;
        }

        private T Find<T>(string name) where T : FrameworkElement
        {
            T element = _window.FindName(name) as T;
            if (element == null)
            {
                throw new InvalidOperationException("UI 요소를 찾지 못했습니다: " + name);
            }
            return element;
        }

        private Forms.NotifyIcon CreateTrayIcon()
        {
            Forms.NotifyIcon icon = new Forms.NotifyIcon();
            using (Stream iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUsageMeter.AppIcon.ico"))
            {
                if (iconStream == null)
                {
                    throw new InvalidOperationException("내장 트레이 아이콘을 찾지 못했습니다.");
                }
                icon.Icon = new System.Drawing.Icon(iconStream);
            }
            icon.Text = "Codex 사용량 미터기";
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            Forms.ToolStripMenuItem openItem = new Forms.ToolStripMenuItem("열기");
            Forms.ToolStripMenuItem exitItem = new Forms.ToolStripMenuItem("종료");
            openItem.Click += delegate { _window.Dispatcher.BeginInvoke(new Action(ShowWindow)); };
            exitItem.Click += delegate { _window.Dispatcher.BeginInvoke(new Action(ExitApplication)); };
            menu.Items.Add(openItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { _window.Dispatcher.BeginInvoke(new Action(ShowWindow)); };
            return icon;
        }

        private async void WindowLoaded(object sender, RoutedEventArgs e)
        {
            CaptureFontTargets(_window.Content as DependencyObject);
            ApplyFontScale(UserSettings.LoadFontScale(), false);
            _settingAutostart = true;
            try
            {
                bool autostartEnabled = AutoStartManager.IsEnabled();
                _autostartCheckBox.IsChecked = autostartEnabled;
                if (autostartEnabled)
                {
                    AutoStartManager.SetEnabled(true);
                }
            }
            finally
            {
                _settingAutostart = false;
            }
            ApplyTopmostState(UserSettings.LoadTopmost(), false);
            SetDisplayMode(true);
            _systemTimer.Start();
            _accountTimer.Start();
            await RefreshSystemAsync();
            await RefreshAccountsAsync();
        }

        private void SetDisplayMode(bool compact)
        {
            bool restoreMaximized = _customMaximized;
            if (restoreMaximized)
            {
                RestoreCustomMaximize();
                _window.UpdateLayout();
            }

            if (_displayModeInitialized)
            {
                if (_compactMode)
                {
                    _compactWidth = _window.ActualWidth > 0.0 ? _window.ActualWidth : _window.Width;
                    _compactHeight = _window.ActualHeight > 0.0 ? _window.ActualHeight : _window.Height;
                }
                else
                {
                    _expandedWidth = _window.ActualWidth > 0.0 ? _window.ActualWidth : _window.Width;
                    _expandedHeight = _window.ActualHeight > 0.0 ? _window.ActualHeight : _window.Height;
                }
            }

            _compactMode = compact;
            _displayModeInitialized = true;
            _compactLayout.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            _expandedLayout.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

            Rect workArea = SystemParameters.WorkArea;
            if (compact)
            {
                _window.MinWidth = 0.0;
                _window.MinHeight = 0.0;
                _window.Width = Math.Min(Math.Max(1.0, _compactWidth), Math.Max(1.0, workArea.Width - 10.0));
                _window.Height = Math.Min(Math.Max(1.0, _compactHeight), Math.Max(1.0, workArea.Height - 10.0));
                _window.Left = Math.Max(workArea.Left, workArea.Right - _window.Width - 14.0);
                _window.Top = workArea.Top + 14.0;
            }
            else
            {
                double availableWidth = Math.Max(640.0, workArea.Width - 24.0);
                double availableHeight = Math.Max(440.0, workArea.Height - 24.0);
                double scale = Math.Min(1.0, Math.Min(availableWidth / 1280.0, availableHeight / 820.0));
                double defaultWidth = Math.Round(1280.0 * scale);
                double defaultHeight = Math.Round(820.0 * scale);
                _window.MinWidth = 0.0;
                _window.MinHeight = 0.0;
                _window.Width = Math.Max(1.0, _expandedWidth > 0.0 ? _expandedWidth : defaultWidth);
                _window.Height = Math.Max(1.0, _expandedHeight > 0.0 ? _expandedHeight : defaultHeight);
                _window.Left = workArea.Left + Math.Max(0.0, (workArea.Width - _window.Width) / 2.0);
                _window.Top = workArea.Top + Math.Max(0.0, (workArea.Height - _window.Height) / 2.0);
            }
            _window.UpdateLayout();
            if (restoreMaximized)
            {
                ApplyCustomMaximize();
            }
        }

        private void WindowSourceInitialized(object sender, EventArgs e)
        {
            _windowSource = PresentationSource.FromVisual(_window) as HwndSource;
            if (_windowSource != null)
            {
                _windowSource.AddHook(WindowMessageHook);
            }
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmNcHitTest = 0x0084;
            const int WmSizing = 0x0214;
            if (_window.WindowState != WindowState.Normal || _customMaximized)
            {
                return IntPtr.Zero;
            }

            if (message == WmSizing)
            {
                if ((Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) && lParam != IntPtr.Zero)
                {
                    NativeRect rectangle = (NativeRect)Marshal.PtrToStructure(lParam, typeof(NativeRect));
                    double ratio = _compactMode ? 460.0 / 780.0 : 1280.0 / 820.0;
                    ConstrainResizeToAspect(ref rectangle, wParam.ToInt32(), ratio);
                    Marshal.StructureToPtr(rectangle, lParam, false);
                    handled = true;
                    return new IntPtr(1);
                }
                return IntPtr.Zero;
            }

            if (message != WmNcHitTest)
            {
                return IntPtr.Zero;
            }

            long packed = lParam.ToInt64();
            Point screenPoint = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
            Point point = _window.PointFromScreen(screenPoint);
            int hit = GetResizeHitTest(point.X, point.Y, _window.ActualWidth, _window.ActualHeight, 12.0, 30.0);
            if (hit != 1)
            {
                handled = true;
                return new IntPtr(hit);
            }
            return IntPtr.Zero;
        }

        internal static void ConstrainResizeToAspect(ref NativeRect rectangle, int sizingEdge, double ratio)
        {
            int width = Math.Max(1, rectangle.Right - rectangle.Left);
            int height = Math.Max(1, rectangle.Bottom - rectangle.Top);
            bool fromLeft = sizingEdge == 1 || sizingEdge == 4 || sizingEdge == 7;
            bool fromTop = sizingEdge == 3 || sizingEdge == 4 || sizingEdge == 5;

            if (sizingEdge == 1 || sizingEdge == 2)
            {
                int desiredHeight = Math.Max(1, (int)Math.Round(width / ratio));
                rectangle.Bottom = rectangle.Top + desiredHeight;
                return;
            }
            if (sizingEdge == 3 || sizingEdge == 6)
            {
                int desiredWidth = Math.Max(1, (int)Math.Round(height * ratio));
                rectangle.Right = rectangle.Left + desiredWidth;
                return;
            }

            int heightFromWidth = Math.Max(1, (int)Math.Round(width / ratio));
            int widthFromHeight = Math.Max(1, (int)Math.Round(height * ratio));
            if (Math.Abs(heightFromWidth - height) <= Math.Abs(widthFromHeight - width))
            {
                if (fromTop)
                {
                    rectangle.Top = rectangle.Bottom - heightFromWidth;
                }
                else
                {
                    rectangle.Bottom = rectangle.Top + heightFromWidth;
                }
            }
            else
            {
                if (fromLeft)
                {
                    rectangle.Left = rectangle.Right - widthFromHeight;
                }
                else
                {
                    rectangle.Right = rectangle.Left + widthFromHeight;
                }
            }
        }

        internal static int GetResizeHitTest(double x, double y, double width, double height,
            double edgeSize, double cornerSize)
        {
            bool cornerLeft = x >= 0.0 && x <= cornerSize;
            bool cornerRight = x <= width && x >= width - cornerSize;
            bool cornerTop = y >= 0.0 && y <= cornerSize;
            bool cornerBottom = y <= height && y >= height - cornerSize;
            if (cornerLeft && cornerTop) return 13;
            if (cornerRight && cornerTop) return 14;
            if (cornerLeft && cornerBottom) return 16;
            if (cornerRight && cornerBottom) return 17;

            bool left = x >= 0.0 && x <= edgeSize;
            bool right = x <= width && x >= width - edgeSize;
            bool top = y >= 0.0 && y <= edgeSize;
            bool bottom = y <= height && y >= height - edgeSize;
            if (left) return 10;
            if (right) return 11;
            if (top) return 12;
            if (bottom) return 15;
            return 1;
        }

        public void SetDisplayModeForPreview(bool compact)
        {
            SetDisplayMode(compact);
        }

        public void SetWindowSizeForPreview(double width, double height)
        {
            _window.Width = width;
            _window.Height = height;
            _window.UpdateLayout();
        }

        public void SetMaximizedForPreview()
        {
            if (!_customMaximized)
            {
                _maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            _window.UpdateLayout();
            if (!_customMaximized || Convert.ToString(_maximizeButton.Content) != "❐" || !IsInsideCurrentWorkArea())
            {
                throw new InvalidOperationException("최대화 버튼 동작을 확인하지 못했습니다.");
            }
        }

        public void RestoreFromMaximizedForPreview()
        {
            if (_customMaximized)
            {
                _compactMaximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            _window.UpdateLayout();
            if (_customMaximized || _window.WindowState != WindowState.Normal || Convert.ToString(_compactMaximizeButton.Content) != "□")
            {
                throw new InvalidOperationException("최대화 복원 버튼 동작을 확인하지 못했습니다.");
            }
        }

        public void ShowSettingsForPreview()
        {
            ApplyFontScale(1.5, false);
            _appVersionValue.Text = "v" + UpdateClient.CurrentVersionText;
            _updateStatusText.Text = "수동 확인 대기 중";
            _settingsOverlay.Visibility = Visibility.Visible;
            _window.UpdateLayout();
        }

        public void HideSettingsForPreview()
        {
            _settingsOverlay.Visibility = Visibility.Collapsed;
            ApplyFontScale(1.5, false);
            _window.UpdateLayout();
        }

        private void WindowClosing(object sender, CancelEventArgs e)
        {
            _systemTimer.Stop();
            _accountTimer.Stop();
            _trayIcon.Visible = false;
        }

        private async void SystemTimerTick(object sender, EventArgs e)
        {
            UpdateCountdowns();
            await RefreshSystemAsync();
        }

        private void HideButtonClick(object sender, RoutedEventArgs e)
        {
            _window.Hide();
        }

        private void MaximizeButtonClick(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            if (_customMaximized) RestoreCustomMaximize();
            else ApplyCustomMaximize();
        }

        private void WindowStateChanged(object sender, EventArgs e)
        {
            if (_window.WindowState == WindowState.Maximized && !_handlingNativeMaximize)
            {
                _handlingNativeMaximize = true;
                _window.WindowState = WindowState.Normal;
                _handlingNativeMaximize = false;
                ApplyCustomMaximize();
                return;
            }
            UpdateMaximizedChrome();
        }

        private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_compactMode) return;
            RefreshCalendarDetailIfNeeded(_account1);
            RefreshCalendarDetailIfNeeded(_account2);
        }

        private static void RefreshCalendarDetailIfNeeded(AccountView view)
        {
            if (view == null || view.State == null || view.LastSnapshot == null) return;
            int expected = GetCalendarDetailLevel(view.UsageGrid.ActualHeight);
            if (expected != view.CalendarDetailLevel)
            {
                RenderUsageGrid(view, view.LastSnapshot.DailyUsage);
            }
        }

        private void ApplyCustomMaximize()
        {
            if (_customMaximized) return;
            _window.WindowState = WindowState.Normal;
            _restoreBounds = new Rect(_window.Left, _window.Top,
                Math.Max(1.0, _window.ActualWidth > 0.0 ? _window.ActualWidth : _window.Width),
                Math.Max(1.0, _window.ActualHeight > 0.0 ? _window.ActualHeight : _window.Height));
            Rect workArea = GetCurrentWorkArea();
            _customMaximized = true;
            _window.Left = workArea.Left;
            _window.Top = workArea.Top;
            _window.Width = workArea.Width;
            _window.Height = workArea.Height;
            UpdateMaximizedChrome();
            _window.UpdateLayout();
        }

        private void RestoreCustomMaximize()
        {
            if (!_customMaximized) return;
            _customMaximized = false;
            if (_restoreBounds.Width > 0.0 && _restoreBounds.Height > 0.0)
            {
                _window.Left = _restoreBounds.Left;
                _window.Top = _restoreBounds.Top;
                _window.Width = _restoreBounds.Width;
                _window.Height = _restoreBounds.Height;
            }
            UpdateMaximizedChrome();
            _window.UpdateLayout();
        }

        private void UpdateMaximizedChrome()
        {
            bool maximized = _customMaximized;
            string glyph = maximized ? "❐" : "□";
            string tooltip = maximized ? "이전 크기로 복원" : "최대화";
            _maximizeButton.Content = glyph;
            _compactMaximizeButton.Content = glyph;
            _maximizeButton.ToolTip = tooltip;
            _compactMaximizeButton.ToolTip = tooltip;
            _expandedShell.Margin = new Thickness(0.0);
            _compactShell.Margin = new Thickness(0.0);
            _expandedShell.CornerRadius = new CornerRadius(0.0);
            _compactShell.CornerRadius = new CornerRadius(0.0);
        }

        private Rect GetCurrentWorkArea()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(_window).Handle;
                Forms.Screen screen = Forms.Screen.FromHandle(handle);
                System.Drawing.Rectangle pixels = screen.WorkingArea;
                if (_windowSource != null && _windowSource.CompositionTarget != null)
                {
                    Matrix fromDevice = _windowSource.CompositionTarget.TransformFromDevice;
                    Point topLeft = fromDevice.Transform(new Point(pixels.Left, pixels.Top));
                    Point bottomRight = fromDevice.Transform(new Point(pixels.Right, pixels.Bottom));
                    return new Rect(topLeft, bottomRight);
                }
            }
            catch { }
            return SystemParameters.WorkArea;
        }

        private bool IsInsideCurrentWorkArea()
        {
            Rect workArea = GetCurrentWorkArea();
            return Math.Abs(_window.Left - workArea.Left) < 2.0 && Math.Abs(_window.Top - workArea.Top) < 2.0 &&
                _window.ActualWidth <= workArea.Width + 2.0 && _window.ActualHeight <= workArea.Height + 2.0;
        }

        private void CloseButtonClick(object sender, RoutedEventArgs e)
        {
            _window.Close();
        }

        private void TitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed && _window.WindowState == WindowState.Normal && !_customMaximized)
            {
                try { _window.DragMove(); } catch { }
            }
        }

        private void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _settingsOverlay.Visibility == Visibility.Visible)
            {
                _settingsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _modalOverlay.Visibility == Visibility.Visible)
            {
                HideModal();
                e.Handled = true;
            }
        }

        private async void AccountTimerTick(object sender, EventArgs e)
        {
            await RefreshAccountsAsync();
        }

        private async void RefreshButtonClick(object sender, RoutedEventArgs e)
        {
            await RefreshAccountsAsync();
        }

        private async void Account1LoginClick(object sender, RoutedEventArgs e)
        {
            await BeginLoginAsync(_account1);
        }

        private async void Account2LoginClick(object sender, RoutedEventArgs e)
        {
            await BeginLoginAsync(_account2);
        }

        private void Account1LogoutClick(object sender, RoutedEventArgs e)
        {
            ConfirmLogout(_account1);
        }

        private void Account2LogoutClick(object sender, RoutedEventArgs e)
        {
            ConfirmLogout(_account2);
        }

        private void AccountClientChanged(object sender, EventArgs e)
        {
            _window.Dispatcher.BeginInvoke(new Action(RefreshAccountsFromNotification));
        }

        private async void RefreshAccountsFromNotification()
        {
            await Task.Delay(500);
            await RefreshAccountsAsync();
        }

        private void TopmostButtonClick(object sender, RoutedEventArgs e)
        {
            ApplyTopmostState(!_window.Topmost, true);
        }

        private void ApplyTopmostState(bool enabled, bool persist)
        {
            _window.Topmost = enabled;
            Brush background = BrushFromHex(enabled ? "#1F6F5C" : "#232323");
            Brush border = BrushFromHex(enabled ? "#2F9E7E" : "#424242");
            _topmostButton.Background = background;
            _compactTopmostButton.Background = background;
            _topmostButton.BorderBrush = border;
            _compactTopmostButton.BorderBrush = border;
            _topmostButton.ToolTip = enabled ? "항상 위 끄기" : "항상 위 켜기";
            _compactTopmostButton.ToolTip = enabled ? "항상 위 끄기" : "항상 위 켜기";
            if (persist)
            {
                try
                {
                    UserSettings.SaveTopmost(enabled);
                }
                catch (Exception ex)
                {
                    SetFooterText("항상 위 설정 저장 실패: " + ex.Message);
                }
            }
        }

        private void AutostartChanged(object sender, RoutedEventArgs e)
        {
            if (_settingAutostart)
            {
                return;
            }

            try
            {
                AutoStartManager.SetEnabled(_autostartCheckBox.IsChecked == true);
                SetFooterText(_autostartCheckBox.IsChecked == true
                    ? "Windows 자동 시작을 켰습니다."
                    : "Windows 자동 시작을 껐습니다.");
            }
            catch (Exception ex)
            {
                _settingAutostart = true;
                _autostartCheckBox.IsChecked = AutoStartManager.IsEnabled();
                _settingAutostart = false;
                ShowModal("자동 시작 설정 실패", ex.Message, null, "확인", null, null, null);
            }
        }

        private void SettingsButtonClick(object sender, RoutedEventArgs e)
        {
            _settingsOverlay.Visibility = Visibility.Visible;
            _fontScaleValue.Text = Math.Round(_fontScale * 100.0).ToString("0") + "%";
            _appVersionValue.Text = "v" + UpdateClient.CurrentVersionText;
            if (!_updateChecking)
            {
                _updateStatusText.Text = "수동 확인 대기 중";
            }
        }

        private void SettingsCloseButtonClick(object sender, RoutedEventArgs e)
        {
            _settingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void FontDecreaseButtonClick(object sender, RoutedEventArgs e)
        {
            ApplyFontScale(_fontScale - 0.1, true);
        }

        private void FontResetButtonClick(object sender, RoutedEventArgs e)
        {
            ApplyFontScale(1.5, true);
        }

        private void FontIncreaseButtonClick(object sender, RoutedEventArgs e)
        {
            ApplyFontScale(_fontScale + 0.1, true);
        }

        private async void UpdateCheckButtonClick(object sender, RoutedEventArgs e)
        {
            if (_updateChecking) return;
            _updateChecking = true;
            _updateCheckButton.IsEnabled = false;
            _updateCheckButton.Content = "확인 중…";
            _updateStatusText.Text = "GitHub 정식 릴리스 확인 중…";
            try
            {
                UpdateCheckResult result = await UpdateClient.CheckLatestAsync();
                if (!result.UpdateAvailable)
                {
                    _updateStatusText.Text = "최신 버전 v" + result.CurrentVersionText;
                    ShowModal("업데이트 확인", "현재 v" + result.CurrentVersionText + "이 최신 버전입니다.",
                        null, "확인", null, null, null);
                    return;
                }

                _updateStatusText.Text = "새 버전 v" + result.LatestVersionText + " 사용 가능";
                string notes = CompactReleaseNotes(result.Release == null ? null : result.Release.Notes);
                string message = "현재 v" + result.CurrentVersionText + " → 최신 v" + result.LatestVersionText +
                    "\n\n다운로드 후 프로그램을 종료하고 새 버전으로 다시 시작합니다." +
                    (String.IsNullOrWhiteSpace(notes) ? String.Empty : "\n\n" + notes);
                ShowModal("새 버전 사용 가능", message, null, "다운로드 및 재시작", "나중에",
                    delegate { DownloadAndInstallUpdateAsync(result.Release); }, null);
            }
            catch (Exception ex)
            {
                _updateStatusText.Text = "확인 실패";
                ShowModal("업데이트 확인 실패", ex.Message, null, "확인", null, null, null);
            }
            finally
            {
                _updateChecking = false;
                _updateCheckButton.IsEnabled = true;
                _updateCheckButton.Content = "업데이트 확인";
            }
        }

        private async void DownloadAndInstallUpdateAsync(UpdateReleaseInfo release)
        {
            if (release == null)
            {
                ShowModal("업데이트 실패", "새 버전 정보를 찾지 못했습니다.", null, "확인", null, null, null);
                return;
            }
            _updateChecking = true;
            _updateCheckButton.IsEnabled = false;
            _updateCheckButton.Content = "다운로드 중…";
            _updateStatusText.Text = "v" + release.VersionText + " 다운로드 및 검증 중…";
            try
            {
                string stagedPath = await UpdateClient.DownloadAndVerifyAsync(release);
                _updateStatusText.Text = "검증 완료 · 재시작 중…";
                UpdateClient.StartUpdater(stagedPath, release);
                ExitApplication();
            }
            catch (Exception ex)
            {
                _updateStatusText.Text = "업데이트 실패";
                ShowModal("업데이트 실패", ex.Message, null, "확인", null, null, null);
                _updateChecking = false;
                _updateCheckButton.IsEnabled = true;
                _updateCheckButton.Content = "업데이트 확인";
            }
        }

        private static string CompactReleaseNotes(string notes)
        {
            if (String.IsNullOrWhiteSpace(notes)) return String.Empty;
            string compact = notes.Replace("\r", String.Empty).Trim();
            if (compact.Length > 420) compact = compact.Substring(0, 420).TrimEnd() + "…";
            return compact;
        }

        private async void AccountCountDecreaseButtonClick(object sender, RoutedEventArgs e)
        {
            ApplyAccountCount(_accountCount - 1, true);
            await RefreshAccountsAsync();
        }

        private async void AccountCountIncreaseButtonClick(object sender, RoutedEventArgs e)
        {
            ApplyAccountCount(_accountCount + 1, true);
            await RefreshAccountsAsync();
        }

        private void AccountPagePreviousButtonClick(object sender, RoutedEventArgs e)
        {
            SetAccountPage(_accountPage - 1);
        }

        private void AccountPageNextButtonClick(object sender, RoutedEventArgs e)
        {
            SetAccountPage(_accountPage + 1);
        }

        private void CompactAccountPageButtonClick(object sender, RoutedEventArgs e)
        {
            int pageCount = Math.Max(1, (_accountCount + 1) / 2);
            SetAccountPage((_accountPage + 1) % pageCount);
        }

        private void ApplyAccountCount(int requestedCount, bool persist)
        {
            int count = Math.Max(1, Math.Min(4, requestedCount));
            while (_accounts.Count < count)
            {
                int number = _accounts.Count + 1;
                AccountState state = new AccountState();
                state.Number = number;
                state.Label = "계정 " + number.ToString();
                state.Client = new CodexRpcClient(_codexPath, Path.Combine(_accountsRoot, "account-" + number.ToString()));
                state.Client.AccountChanged += AccountClientChanged;
                _accounts.Add(state);
            }
            while (_accounts.Count > count)
            {
                AccountState state = _accounts[_accounts.Count - 1];
                state.Client.AccountChanged -= AccountClientChanged;
                state.Client.Dispose();
                _accounts.RemoveAt(_accounts.Count - 1);
            }
            _accountCount = count;
            int pageCount = Math.Max(1, (_accountCount + 1) / 2);
            _accountPage = Math.Max(0, Math.Min(pageCount - 1, _accountPage));
            BindAccountPage();
            _accountCountValue.Text = _accountCount.ToString() + "개";
            _compactAccountSummaryText.Text = "계정 " + _accountCount.ToString() + "개 · PC 상태";
            _accountCountBadgeText.Text = _accountCount.ToString() + " ACC";
            _accountSummaryText.Text = "계정 " + _accountCount.ToString() + "개 한도 · 사용 기록 · 시스템 텔레메트리";
            _accountCountDecreaseButton.IsEnabled = _accountCount > 1;
            _accountCountIncreaseButton.IsEnabled = _accountCount < 4;
            if (persist)
            {
                try { UserSettings.SaveAccountCount(_accountCount); }
                catch (Exception ex) { SetFooterText("계정 칸 수 저장 실패: " + ex.Message); }
            }
        }

        private void SetAccountPage(int page)
        {
            SaveVisibleCalendarOffsets();
            int pageCount = Math.Max(1, (_accountCount + 1) / 2);
            _accountPage = Math.Max(0, Math.Min(pageCount - 1, page));
            BindAccountPage();
        }

        private void SaveVisibleCalendarOffsets()
        {
            if (_account1.State != null) _account1.State.CalendarMonthOffset = _account1.CalendarMonthOffset;
            if (_account2.State != null) _account2.State.CalendarMonthOffset = _account2.CalendarMonthOffset;
        }

        private void BindAccountPage()
        {
            BindAccountView(_account1, _accountPage * 2);
            BindAccountView(_account2, _accountPage * 2 + 1);
            int pageCount = Math.Max(1, (_accountCount + 1) / 2);
            int start = _accountPage * 2 + 1;
            int end = Math.Min(_accountCount, start + 1);
            string pageText = "계정 " + start.ToString() + (end > start ? "–" + end.ToString() : String.Empty) + " / " + _accountCount.ToString();
            _accountPageText.Text = pageText;
            _compactAccountPageButton.Content = pageText.Replace("계정 ", String.Empty);
            _accountPagePreviousButton.IsEnabled = _accountPage > 0;
            _accountPageNextButton.IsEnabled = _accountPage < pageCount - 1;
            _compactAccountPageButton.IsEnabled = pageCount > 1;
        }

        private void BindAccountView(AccountView view, int stateIndex)
        {
            AccountState state = stateIndex >= 0 && stateIndex < _accounts.Count ? _accounts[stateIndex] : null;
            view.State = state;
            view.Client = state == null ? null : state.Client;
            view.Container.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
            view.CompactContainer.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
            if (state == null)
            {
                view.LastSnapshot = null;
                return;
            }
            view.Label = state.Label;
            view.TitleText.Text = state.Label;
            view.CompactTitleText.Text = state.Label;
            view.BadgeText.Text = state.Number.ToString();
            view.CompactBadgeText.Text = state.Number.ToString();
            view.CalendarMonthOffset = state.CalendarMonthOffset;
            AccountSnapshot snapshot = state.LastSnapshot;
            if (snapshot == null)
            {
                snapshot = new AccountSnapshot { ResetCredits = new List<ResetCreditInfo>(), DailyUsage = new List<DailyUsageBucket>() };
            }
            UpdateAccount(view, snapshot);
        }

        private void ApplyFontScale(double scale, bool persist)
        {
            _fontScale = Math.Round(Math.Max(1.0, Math.Min(2.0, scale)) * 10.0) / 10.0;
            foreach (FontTarget target in _fontTargets)
            {
                TextBlock text = target.Element as TextBlock;
                if (text != null)
                {
                    text.FontSize = target.BaseSize * _fontScale;
                    continue;
                }
                Control control = target.Element as Control;
                if (control != null)
                {
                    control.FontSize = target.BaseSize * _fontScale;
                }
            }
            _fontScaleValue.Text = Math.Round(_fontScale * 100.0).ToString("0") + "%";
            _fontDecreaseButton.IsEnabled = _fontScale > 1.0;
            _fontIncreaseButton.IsEnabled = _fontScale < 2.0;
            _window.UpdateLayout();
            if (persist)
            {
                try { UserSettings.SaveFontScale(_fontScale); }
                catch (Exception ex) { SetFooterText("글자 크기 설정 저장 실패: " + ex.Message); }
            }
        }

        private void CaptureFontTargets(DependencyObject root)
        {
            if (root == null)
            {
                return;
            }
            if (!_fontTargetElements.Contains(root))
            {
                TextBlock text = root as TextBlock;
                Control control = root as Control;
                if (text != null)
                {
                    _fontTargetElements.Add(root);
                    _fontTargets.Add(new FontTarget { Element = root, BaseSize = text.FontSize });
                }
                else if (control != null && !Object.ReferenceEquals(control.Style, _window.Resources["IconButton"] as Style))
                {
                    _fontTargetElements.Add(root);
                    _fontTargets.Add(new FontTarget { Element = root, BaseSize = control.FontSize });
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject dependencyChild = child as DependencyObject;
                if (dependencyChild != null)
                {
                    CaptureFontTargets(dependencyChild);
                }
            }
        }

        private async Task RefreshAccountsAsync()
        {
            if (_refreshing)
            {
                return;
            }
            _refreshing = true;
            _refreshButton.IsEnabled = false;
            _compactRefreshButton.IsEnabled = false;
            _accountCountDecreaseButton.IsEnabled = false;
            _accountCountIncreaseButton.IsEnabled = false;
            SetFooterText("계정 사용량을 확인하는 중…");

            try
            {
                AccountState[] states = _accounts.ToArray();
                Task<AccountSnapshot>[] refreshes = states.Select(delegate(AccountState state)
                {
                    return state.Client.RefreshAsync();
                }).ToArray();
                AccountSnapshot[] snapshots = await Task.WhenAll(refreshes);
                for (int index = 0; index < states.Length; index++)
                {
                    states[index].LastSnapshot = snapshots[index];
                }
                BindAccountPage();
                UpdateFooter();
            }
            catch (Exception ex)
            {
                SetFooterText("계정 갱신 실패: " + ex.Message);
            }
            finally
            {
                _refreshing = false;
                _refreshButton.IsEnabled = true;
                _compactRefreshButton.IsEnabled = true;
                _accountCountDecreaseButton.IsEnabled = _accountCount > 1;
                _accountCountIncreaseButton.IsEnabled = _accountCount < 4;
            }
        }

        private async Task RefreshSystemAsync()
        {
            if (_sampling)
            {
                return;
            }
            _sampling = true;
            try
            {
                SystemSnapshot snapshot = await _systemMonitor.SampleAsync();
                List<PerformanceDisplayItem> items = BuildPerformanceItems(snapshot);
                UpdatePerformanceCards(items);
                _performanceCountText.Text = "CPU · RAM · GPU " + snapshot.Gpus.Count.ToString() +
                    " · 디스크 " + snapshot.Disks.Count.ToString() + " · 네트워크 " + snapshot.Networks.Count.ToString();
                GpuSnapshot busiestGpu = snapshot.Gpus
                    .OrderByDescending(delegate(GpuSnapshot gpu) { return gpu.Percent; })
                    .ThenBy(delegate(GpuSnapshot gpu) { return gpu.Index; })
                    .FirstOrDefault();
                DiskSnapshot busiestDisk = snapshot.Disks
                    .OrderByDescending(delegate(DiskSnapshot disk) { return disk.Percent; })
                    .ThenByDescending(delegate(DiskSnapshot disk) { return disk.ReadBytesPerSecond + disk.WriteBytesPerSecond; })
                    .ThenBy(delegate(DiskSnapshot disk) { return disk.Index; })
                    .FirstOrDefault();
                _compactCpuValue.Text = Percent(snapshot.CpuPercent);
                _compactGpuLabel.Text = busiestGpu == null ? "GPU" : "GPU " + busiestGpu.Index.ToString();
                _compactGpuLabel.ToolTip = busiestGpu == null ? null : busiestGpu.Name;
                _compactGpuValue.Text = busiestGpu == null ? "N/A" : Percent(busiestGpu.Percent);
                _compactGpuValue.ToolTip = _compactGpuLabel.ToolTip;
                _compactMemoryValue.Text = Percent(snapshot.MemoryPercent);
                _compactDiskLabel.Text = busiestDisk == null ? "디스크" : "디스크 " + busiestDisk.Index.ToString();
                _compactDiskLabel.ToolTip = busiestDisk == null ? null : busiestDisk.Name + " · " + busiestDisk.Detail;
                _compactDiskValue.Text = busiestDisk == null ? "N/A" : Percent(busiestDisk.Percent);
                _compactDiskValue.ToolTip = busiestDisk == null ? null :
                    "읽기 " + FormatRate(busiestDisk.ReadBytesPerSecond) + " · 쓰기 " + FormatRate(busiestDisk.WriteBytesPerSecond);
                _compactCpuRing.Data = CreateArcGeometry(snapshot.CpuPercent, 35.0, new Point(48.0, 48.0));
                _compactGpuRing.Data = CreateArcGeometry(busiestGpu == null ? 0.0 : busiestGpu.Percent, 35.0, new Point(48.0, 48.0));
                _compactMemoryRing.Data = CreateArcGeometry(snapshot.MemoryPercent, 35.0, new Point(48.0, 48.0));
                _compactDiskRing.Data = CreateArcGeometry(busiestDisk == null ? 0.0 : busiestDisk.Percent, 35.0, new Point(48.0, 48.0));
                _compactNetworkValue.Text = "NET " + snapshot.Networks.Count.ToString() + " · ↓ " + FormatRate(snapshot.NetworkReceiveBytesPerSecond) +
                    "   ↑ " + FormatRate(snapshot.NetworkSendBytesPerSecond);
                if (!String.IsNullOrWhiteSpace(snapshot.Warning))
                {
                    _systemStatus.Text = snapshot.Warning;
                }
                else
                {
                    _systemStatus.Text = "감지된 성능 항목 " + items.Count.ToString() + "개를 개별 측정 중";
                }
            }
            catch (Exception ex)
            {
                _systemStatus.Text = "PC 자원 갱신 실패: " + ex.Message;
            }
            finally
            {
                _sampling = false;
            }
        }

        private static List<PerformanceDisplayItem> BuildPerformanceItems(SystemSnapshot snapshot)
        {
            List<PerformanceDisplayItem> items = new List<PerformanceDisplayItem>();
            items.Add(new PerformanceDisplayItem {
                Key = "cpu", Name = "CPU", Value = Percent(snapshot.CpuPercent), Detail = "전체 프로세서",
                Percent = snapshot.CpuPercent, Accent = "#38BDF8"
            });
            items.Add(new PerformanceDisplayItem {
                Key = "memory", Name = "RAM", Value = Percent(snapshot.MemoryPercent),
                Detail = snapshot.MemoryUsedGb.ToString("0.0") + " / " + snapshot.MemoryTotalGb.ToString("0.0") + " GB",
                Percent = snapshot.MemoryPercent, Accent = "#FBBF24"
            });
            foreach (GpuSnapshot gpu in snapshot.Gpus)
            {
                items.Add(new PerformanceDisplayItem {
                    Key = gpu.Key, Name = "GPU " + gpu.Index.ToString(), Value = Percent(gpu.Percent),
                    Detail = gpu.Name, Percent = gpu.Percent, Accent = "#A78BFA"
                });
            }
            foreach (DiskSnapshot disk in snapshot.Disks)
            {
                items.Add(new PerformanceDisplayItem {
                    Key = disk.Key, Name = disk.Name, Value = Percent(disk.Percent),
                    Detail = disk.Detail + " · R " + FormatRate(disk.ReadBytesPerSecond) + " / W " + FormatRate(disk.WriteBytesPerSecond),
                    Percent = disk.Percent, Accent = "#F472B6"
                });
            }
            foreach (NetworkSnapshot network in snapshot.Networks)
            {
                string detail = network.Connected
                    ? "↓ " + FormatRate(network.ReceiveBytesPerSecond) + "  ↑ " + FormatRate(network.SendBytesPerSecond) +
                      (network.LinkSpeedBitsPerSecond > 0 ? " · " + FormatLinkSpeed(network.LinkSpeedBitsPerSecond) : String.Empty)
                    : "연결 끊김";
                if (!String.IsNullOrWhiteSpace(network.Detail)) detail += " · " + network.Detail;
                items.Add(new PerformanceDisplayItem {
                    Key = network.Key, Name = network.Name, Value = network.Connected ? Percent(network.Percent) : "OFF",
                    Detail = detail, Percent = network.Percent, Accent = "#34D399"
                });
            }
            return items;
        }

        private void UpdatePerformanceCards(List<PerformanceDisplayItem> items)
        {
            foreach (PerformanceDisplayItem item in items)
            {
                Queue<double> history;
                if (!_performanceHistory.TryGetValue(item.Key, out history))
                {
                    history = new Queue<double>();
                    _performanceHistory[item.Key] = history;
                }
                history.Enqueue(Math.Max(0.0, Math.Min(100.0, item.Percent)));
                while (history.Count > 60) history.Dequeue();
            }
            StringBuilder signature = new StringBuilder();
            foreach (PerformanceDisplayItem item in items) signature.Append(item.Key).Append('|');
            string currentSignature = signature.ToString();
            if (!String.Equals(currentSignature, _performanceSignature, StringComparison.Ordinal))
            {
                _performanceSignature = currentSignature;
                _performanceItemsPanel.Children.Clear();
                _performanceCards.Clear();
                _performanceItemsPanel.Columns = items.Count >= 6 ? 2 : 1;
                foreach (PerformanceDisplayItem item in items)
                {
                    PerformanceCardView view;
                    Border card = CreatePerformanceCard(item, out view);
                    _performanceCards[item.Key] = view;
                    _performanceItemsPanel.Children.Add(card);
                    CaptureFontTargets(card);
                }
                ApplyFontScale(_fontScale, false);
            }

            foreach (PerformanceDisplayItem item in items)
            {
                PerformanceCardView view;
                if (!_performanceCards.TryGetValue(item.Key, out view)) continue;
                view.Name.Text = item.Name;
                view.Value.Text = item.Value;
                view.Detail.Text = item.Detail;
                view.Bar.Value = item.Percent;
                Queue<double> history;
                if (_performanceHistory.TryGetValue(item.Key, out history))
                {
                    RenderPerformanceGraph(view, history);
                }
            }
        }

        private Border CreatePerformanceCard(PerformanceDisplayItem item, out PerformanceCardView view)
        {
            Brush accent = BrushFromHex(item.Accent);
            Border card = new Border();
            card.Style = _window.Resources["SubCard"] as Style;
            card.Padding = new Thickness(9.0, 7.0, 9.0, 7.0);
            card.Margin = new Thickness(2.5, 2.5, 2.5, 2.5);

            StackPanel body = new StackPanel();
            body.VerticalAlignment = VerticalAlignment.Center;
            Grid heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            Border accentLine = new Border { Width = 4.0, Height = 14.0, CornerRadius = new CornerRadius(2.0), Background = accent, Margin = new Thickness(0.0, 0.0, 7.0, 0.0) };
            TextBlock name = new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            namePanel.Children.Add(accentLine);
            namePanel.Children.Add(name);
            TextBlock value = new TextBlock { Text = item.Value, FontWeight = FontWeights.Bold, Margin = new Thickness(8.0, 0.0, 0.0, 0.0) };
            Grid.SetColumn(value, 1);
            heading.Children.Add(namePanel);
            heading.Children.Add(value);
            TextBlock detail = new TextBlock { Text = item.Detail, Foreground = BrushFromHex("#A1A1AA"), FontSize = 9.0, Margin = new Thickness(11.0, 3.0, 0.0, 0.0), TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas graphCanvas = new Canvas { Height = 16.0, Margin = new Thickness(0.0, 4.0, 0.0, 0.0), Background = BrushFromHex("#1D2524") };
            System.Windows.Shapes.Polyline graph = new System.Windows.Shapes.Polyline { Stroke = accent, StrokeThickness = 1.5 };
            graphCanvas.Children.Add(graph);
            ProgressBar bar = new ProgressBar { Style = _window.Resources["QuotaBar"] as Style, Foreground = accent, Height = 5.0, Margin = new Thickness(0.0, 5.0, 0.0, 0.0), Value = item.Percent };
            body.Children.Add(heading);
            body.Children.Add(detail);
            body.Children.Add(graphCanvas);
            body.Children.Add(bar);
            card.Child = body;
            view = new PerformanceCardView { Name = name, Value = value, Detail = detail, Bar = bar, GraphCanvas = graphCanvas, Graph = graph };
            return card;
        }

        private void RenderPerformanceGraph(PerformanceCardView view, Queue<double> history)
        {
            view.Graph.Points.Clear();
            double panelWidth = Math.Max(90.0, _performanceItemsPanel.ActualWidth / Math.Max(1, _performanceItemsPanel.Columns) - 24.0);
            double height = Math.Max(10.0, view.GraphCanvas.Height);
            double step = history.Count <= 1 ? 0.0 : panelWidth / (history.Count - 1.0);
            int index = 0;
            foreach (double sample in history)
            {
                view.Graph.Points.Add(new Point(index * step, height - height * sample / 100.0));
                index++;
            }
        }

        private static string FormatLinkSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond >= 1000000000L) return (bitsPerSecond / 1000000000.0).ToString("0.#") + " Gbps";
            if (bitsPerSecond >= 1000000L) return (bitsPerSecond / 1000000.0).ToString("0") + " Mbps";
            return (bitsPerSecond / 1000.0).ToString("0") + " Kbps";
        }

        private static Brush BrushFromHex(string value)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }

        private async Task BeginLoginAsync(AccountView view)
        {
            if (view == null || view.Client == null) return;
            view.LoginButton.IsEnabled = false;
            view.Status.Text = "로그인 코드를 만드는 중…";
            view.Status.Visibility = Visibility.Visible;
            try
            {
                DeviceLoginInfo login = await view.Client.StartDeviceLoginAsync();
                bool copied = TrySetClipboardText(login.UserCode);
                bool browserOpened = false;
                try
                {
                    ProcessStartInfo browser = new ProcessStartInfo();
                    browser.FileName = login.VerificationUrl;
                    browser.UseShellExecute = true;
                    Process.Start(browser);
                    browserOpened = true;
                }
                catch
                {
                    browserOpened = false;
                }
                view.Status.Text = browserOpened
                    ? "브라우저에서 인증을 마치면 자동으로 갱신됩니다."
                    : "아래 주소와 코드를 사용해 브라우저에서 인증해 주세요.";
                string copiedCode = login.UserCode;
                string loginMessage = browserOpened
                    ? copied
                        ? "인증 코드를 복사하고 OpenAI 인증 페이지를 열었습니다. 페이지에서 계정을 선택한 뒤 코드를 붙여 넣어 주세요."
                        : "OpenAI 인증 페이지를 열었습니다. 클립보드가 사용 중이라 자동 복사는 건너뛰었으니 아래 코드를 직접 입력해 주세요."
                    : "브라우저를 자동으로 열지 못했습니다. 아래 주소를 브라우저에서 연 다음 표시된 코드를 입력해 주세요.\n\n" + login.VerificationUrl;
                ShowModal(view.Label + " 연결",
                    loginMessage,
                    copiedCode, "확인", "코드 복사", null,
                    delegate { CopyLoginCode(view, copiedCode); });
            }
            catch (Exception ex)
            {
                view.Status.Text = "로그인 시작 실패: " + ex.Message;
            }
            finally
            {
                view.LoginButton.IsEnabled = true;
            }
        }

        private static bool TrySetClipboardText(string text)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch
                {
                    if (attempt < 5)
                    {
                        Thread.Sleep(70);
                    }
                }
            }
            return false;
        }

        private void CopyLoginCode(AccountView view, string code)
        {
            if (TrySetClipboardText(code))
            {
                view.Status.Text = "인증 코드를 클립보드에 복사했습니다.";
                return;
            }
            view.Status.Text = "클립보드가 사용 중입니다. 코드를 직접 입력해 주세요.";
            ShowModal("코드 복사 실패",
                "다른 프로그램이 Windows 클립보드를 사용 중입니다. 잠시 후 다시 시도하거나 아래 코드를 직접 입력해 주세요.",
                code, "확인", null, null, null);
        }

        private void ConfirmLogout(AccountView view)
        {
            ShowModal(view.Label + " 연결 해제",
                "이 미터기 전용 프로필에서만 로그아웃합니다. 현재 Codex 데스크톱 앱의 로그인에는 영향을 주지 않습니다.",
                null, "연결 해제", "취소", delegate { LogoutConfirmed(view); }, null);
        }

        private async void LogoutConfirmed(AccountView view)
        {
            view.LogoutButton.IsEnabled = false;
            try
            {
                await view.Client.LogoutAsync();
                await RefreshAccountsAsync();
            }
            catch (Exception ex)
            {
                view.Status.Text = "로그아웃 실패: " + ex.Message;
            }
            finally
            {
                view.LogoutButton.IsEnabled = true;
            }
        }

        private void ShowModal(string title, string message, string code, string primaryText,
            string secondaryText, Action primaryAction, Action secondaryAction)
        {
            _modalTitle.Text = title;
            _modalMessage.Text = message;
            _modalCode.Text = code ?? String.Empty;
            _modalCodePanel.Visibility = String.IsNullOrWhiteSpace(code) ? Visibility.Collapsed : Visibility.Visible;
            _modalPrimaryButton.Content = String.IsNullOrWhiteSpace(primaryText) ? "확인" : primaryText;
            _modalSecondaryButton.Content = secondaryText ?? String.Empty;
            _modalSecondaryButton.Visibility = String.IsNullOrWhiteSpace(secondaryText) ? Visibility.Collapsed : Visibility.Visible;
            _modalPrimaryAction = primaryAction;
            _modalSecondaryAction = secondaryAction;
            _modalOverlay.Visibility = Visibility.Visible;
            _modalPrimaryButton.Focus();
        }

        private void HideModal()
        {
            _modalOverlay.Visibility = Visibility.Collapsed;
            _modalPrimaryAction = null;
            _modalSecondaryAction = null;
        }

        private void ModalPrimaryClick(object sender, RoutedEventArgs e)
        {
            Action action = _modalPrimaryAction;
            HideModal();
            if (action != null)
            {
                action();
            }
        }

        private void ModalSecondaryClick(object sender, RoutedEventArgs e)
        {
            Action action = _modalSecondaryAction;
            HideModal();
            if (action != null)
            {
                action();
            }
        }

        private void UpdateAccount(AccountView view, AccountSnapshot snapshot)
        {
            view.LastSnapshot = snapshot;
            if (!snapshot.IsAuthenticated)
            {
                view.Identity.Text = "연결되지 않음";
                view.LoginButton.Visibility = Visibility.Visible;
                view.LogoutButton.Visibility = Visibility.Collapsed;
                ClearWindow(view.PrimaryName, view.PrimaryValue, view.PrimaryBar, view.PrimaryTimeBar, view.PrimaryReset, view.PrimaryRemaining, "단기 한도", false);
                ClearWindow(view.SecondaryName, view.SecondaryValue, view.SecondaryBar, view.SecondaryTimeBar, view.SecondaryReset, view.SecondaryRemaining, "장기 한도", true);
                ClearAccountExtras(view);
                view.Status.Text = String.IsNullOrWhiteSpace(snapshot.Error)
                    ? "연결 버튼으로 이 칸 전용 계정을 등록하세요."
                    : snapshot.Error;
                view.Status.Visibility = Visibility.Visible;
                UpdateCompactAccount(view, snapshot);
                return;
            }

            string identity = String.IsNullOrWhiteSpace(snapshot.Email) ? "ChatGPT 계정" : snapshot.Email;
            if (!String.IsNullOrWhiteSpace(snapshot.PlanType))
            {
                identity += "  ·  " + PlanName(snapshot.PlanType);
            }
            view.Identity.Text = identity;
            view.LoginButton.Visibility = Visibility.Collapsed;
            view.LogoutButton.Visibility = Visibility.Visible;
            UpdateWindow(view.PrimaryName, view.PrimaryValue, view.PrimaryBar, view.PrimaryTimeBar, view.PrimaryReset, view.PrimaryRemaining, snapshot.Primary, "단기 한도", false);
            UpdateWindow(view.SecondaryName, view.SecondaryValue, view.SecondaryBar, view.SecondaryTimeBar, view.SecondaryReset, view.SecondaryRemaining, snapshot.Secondary, "장기 한도", true);
            UpdateResetCredits(view, snapshot);
            UpdateUsage(view, snapshot);
            if (!String.IsNullOrWhiteSpace(snapshot.Error))
            {
                view.Status.Text = snapshot.Error;
                view.Status.Visibility = Visibility.Visible;
            }
            else if (!String.IsNullOrWhiteSpace(snapshot.UsageError))
            {
                view.Status.Text = snapshot.UpdatedAt.ToString("HH:mm:ss") + " · 사용 기록 미제공";
                view.Status.Visibility = Visibility.Visible;
            }
            else
            {
                view.Status.Text = snapshot.UpdatedAt.ToString("HH:mm:ss") + " · 모든 데이터 갱신";
                view.Status.Visibility = Visibility.Collapsed;
            }
            UpdateCompactAccount(view, snapshot);
        }

        private static void ClearAccountExtras(AccountView view)
        {
            view.ResetCreditsValue.Text = "초기화권 --";
            view.ResetCreditsDetail.Text = "연결 후 보유 여부와 만료일을 표시합니다.";
            view.LifetimeValue.Text = "--";
            view.PeakValue.Text = "--";
            view.StreakValue.Text = "--";
            view.LongestTurnValue.Text = "최장 작업 --";
            RenderUsageGrid(view, new List<DailyUsageBucket>());
            RenderWeeklyUsage(view, new List<DailyUsageBucket>());
            view.UsageEmpty.Text = "계정 연결 후 이번 달 사용량이 표시됩니다.";
        }

        private void UpdateCompactAccount(AccountView view, AccountSnapshot snapshot)
        {
            bool first = Object.ReferenceEquals(view, _account1);
            TextBlock identity = first ? _compactAccount1Identity : _compactAccount2Identity;
            TextBlock primaryValue = first ? _compactAccount1PrimaryValue : _compactAccount2PrimaryValue;
            TextBlock secondaryValue = first ? _compactAccount1SecondaryValue : _compactAccount2SecondaryValue;
            TextBlock resetValue = first ? _compactAccount1ResetValue : _compactAccount2ResetValue;

            if (snapshot == null || !snapshot.IsAuthenticated)
            {
                identity.Text = "연결 필요 · 전체 보기에서 연결";
                primaryValue.Text = "--";
                secondaryValue.Text = "--";
                view.CompactPrimaryRing.Data = Geometry.Empty;
                view.CompactPrimaryTimeBar.Value = 0.0;
                view.CompactPrimaryTimeValue.Text = "--";
                view.CompactPrimaryRecommendationRing.Data = Geometry.Empty;
                view.CompactSecondaryRing.Data = Geometry.Empty;
                view.CompactSecondaryTimeBar.Value = 0.0;
                view.CompactSecondaryTimeValue.Text = "--";
                view.CompactSecondaryRecommendationRing.Data = Geometry.Empty;
                resetValue.Text = "초기화권 --";
                return;
            }

            string accountName = String.IsNullOrWhiteSpace(snapshot.Email) ? "ChatGPT 계정" : snapshot.Email;
            if (!String.IsNullOrWhiteSpace(snapshot.PlanType))
            {
                accountName += " · " + PlanName(snapshot.PlanType);
            }
            identity.Text = accountName;
            UpdateCompactWindow(primaryValue, view.CompactPrimaryRing, view.CompactPrimaryTimeBar,
                view.CompactPrimaryTimeValue, view.CompactPrimaryRecommendationRing,
                snapshot.Primary, 35.0, new Point(48.0, 48.0), false);
            UpdateCompactWindow(secondaryValue, view.CompactSecondaryRing, view.CompactSecondaryTimeBar,
                view.CompactSecondaryTimeValue, view.CompactSecondaryRecommendationRing,
                snapshot.Secondary, 46.0, new Point(60.0, 60.0), true);

            if (!snapshot.ResetCreditCount.HasValue)
            {
                resetValue.Text = "초기화권 정보 없음";
            }
            else if (snapshot.ResetCreditCount.Value <= 0)
            {
                resetValue.Text = "초기화권 없음";
            }
            else
            {
                DateTime? nearestExpiry = null;
                if (snapshot.ResetCredits != null)
                {
                    foreach (ResetCreditInfo credit in snapshot.ResetCredits)
                    {
                        if (credit.ExpiresAt.HasValue &&
                            (!nearestExpiry.HasValue || credit.ExpiresAt.Value < nearestExpiry.Value))
                        {
                            nearestExpiry = credit.ExpiresAt.Value;
                        }
                    }
                }
                resetValue.Text = "초기화권 " + snapshot.ResetCreditCount.Value.ToString() + "개" +
                    (nearestExpiry.HasValue ? " · " + FormatRemaining(nearestExpiry.Value) : String.Empty);
            }
        }

        private static void UpdateCompactWindow(TextBlock value, System.Windows.Shapes.Path usageRing,
            ProgressBar timeBar, TextBlock timeValue, System.Windows.Shapes.Path recommendationRing,
            RateWindow window, double usageRadius, Point center, bool weekly)
        {
            Brush brush = GetLimitBrush(weekly);
            value.Foreground = BrushFromHex("#F4F4F5");
            usageRing.Stroke = brush;
            if (window == null)
            {
                value.Text = "--";
                usageRing.Data = Geometry.Empty;
                timeBar.Value = 0.0;
                timeValue.Text = "--";
                recommendationRing.Data = Geometry.Empty;
                return;
            }
            value.Text = Math.Round(window.RemainingPercent).ToString("0") + "%";
            double elapsed = GetElapsedPercent(window);
            double recommendedRemaining = 100.0 - elapsed;
            Geometry recommendation = CreateArcGeometry(recommendedRemaining, usageRadius, center);
            recommendationRing.Data = recommendation;
            usageRing.Data = CreateArcGeometry(window.RemainingPercent, usageRadius, center);
            if (window.ResetsAt.HasValue && window.DurationMinutes > 0)
            {
                timeBar.Value = 100.0 - elapsed;
                timeValue.Text = FormatCompactRemaining(window.ResetsAt.Value);
            }
            else
            {
                timeBar.Value = 0.0;
                timeValue.Text = "정보 없음";
            }
        }

        private static void UpdateUsage(AccountView view, AccountSnapshot snapshot)
        {
            UsageSummary usage = snapshot.Usage;
            view.LifetimeValue.Text = usage == null ? "--" : FormatTokenCount(usage.LifetimeTokens);
            view.PeakValue.Text = usage == null ? "--" : FormatTokenCount(usage.PeakDailyTokens);
            view.StreakValue.Text = usage == null || !usage.CurrentStreakDays.HasValue
                ? "--"
                : usage.CurrentStreakDays.Value.ToString() + "일" +
                  (usage.LongestStreakDays.HasValue ? " / " + usage.LongestStreakDays.Value.ToString() + "일" : String.Empty);
            view.LongestTurnValue.Text = usage == null || !usage.LongestRunningTurnSeconds.HasValue
                ? "최장 작업 --"
                : "최장 작업 " + FormatDuration(usage.LongestRunningTurnSeconds.Value);
            RenderUsageGrid(view, snapshot.DailyUsage);
            RenderWeeklyUsage(view, snapshot.DailyUsage);
        }

        private static void RenderUsageGrid(AccountView view, List<DailyUsageBucket> buckets)
        {
            view.UsageGrid.Children.Clear();
            view.CalendarDetailLevel = GetCalendarDetailLevel(view.UsageGrid.ActualHeight);
            DateTime currentMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            DateTime monthStart = currentMonth.AddMonths(Math.Max(-1, Math.Min(1, view.CalendarMonthOffset)));
            DateTime nextMonth = monthStart.AddMonths(1);
            int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            int leadingCells = (int)monthStart.DayOfWeek;
            Dictionary<DateTime, long> byDate = new Dictionary<DateTime, long>();
            if (buckets != null)
            {
                foreach (DailyUsageBucket bucket in buckets)
                {
                    DateTime date = bucket.Date.Date;
                    if (date >= monthStart && date < nextMonth)
                    {
                        byDate[date] = bucket.Tokens;
                    }
                }
            }
            long monthTotal = byDate.Values.Sum();
            long maximum = byDate.Count == 0 ? 1L : Math.Max(1L, byDate.Values.Max());
            view.CalendarTitle.Text = monthStart.ToString("yyyy년 M월") + " · " + FormatTokenCount(monthTotal);
            view.CalendarPreviousButton.IsEnabled = view.CalendarMonthOffset > -1;
            view.CalendarNextButton.IsEnabled = view.CalendarMonthOffset < 1;
            view.UsageEmpty.Visibility = byDate.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            view.UsageEmpty.Text = monthStart.ToString("M월") + " 사용 기록이 없습니다.";

            for (int slot = 0; slot < 42; slot++)
            {
                int dayNumber = slot - leadingCells + 1;
                if (dayNumber < 1 || dayNumber > daysInMonth)
                {
                    Border blank = new Border();
                    blank.Margin = new Thickness(2);
                    blank.CornerRadius = new CornerRadius(5);
                    blank.Background = new SolidColorBrush(Color.FromRgb(25, 25, 25));
                    blank.Opacity = 0.42;
                    view.UsageGrid.Children.Add(blank);
                    continue;
                }

                DateTime date = monthStart.AddDays(dayNumber - 1);
                long tokens;
                byDate.TryGetValue(date, out tokens);
                double intensity = Math.Sqrt(Math.Max(0.0, Math.Min(1.0, tokens / (double)maximum)));
                byte red = (byte)Math.Round(36.0 + 4.0 * intensity);
                byte green = (byte)Math.Round(36.0 + 77.0 * intensity);
                byte blue = (byte)Math.Round(36.0 + 55.0 * intensity);

                Border cell = new Border();
                cell.Margin = new Thickness(2);
                cell.CornerRadius = new CornerRadius(5);
                cell.Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
                cell.BorderThickness = date == DateTime.Today ? new Thickness(1) : new Thickness(0);
                cell.BorderBrush = new SolidColorBrush(Color.FromRgb(114, 212, 181));
                cell.Opacity = date > DateTime.Today ? 0.46 : 1.0;
                cell.ToolTip = date.ToString("M월 d일") + " · " + FormatTokenCount(tokens) + " 토큰";

                StackPanel content = new StackPanel();
                content.HorizontalAlignment = HorizontalAlignment.Center;
                content.VerticalAlignment = VerticalAlignment.Center;
                TextBlock day = new TextBlock();
                day.Text = date.Day.ToString();
                day.FontSize = 9;
                day.Foreground = date.DayOfWeek == DayOfWeek.Sunday
                    ? new SolidColorBrush(Color.FromRgb(214, 154, 154))
                    : date.DayOfWeek == DayOfWeek.Saturday
                        ? new SolidColorBrush(Color.FromRgb(140, 180, 216))
                        : new SolidColorBrush(Color.FromRgb(183, 183, 190));
                day.HorizontalAlignment = HorizontalAlignment.Center;
                TextBlock amount = new TextBlock();
                amount.Text = tokens == 0L || view.CalendarDetailLevel <= 0
                    ? "·"
                    : FormatTokenCount(tokens);
                amount.FontSize = 8;
                amount.FontWeight = FontWeights.SemiBold;
                amount.Foreground = new SolidColorBrush(Color.FromRgb(239, 253, 250));
                amount.HorizontalAlignment = HorizontalAlignment.Center;
                content.Children.Add(day);
                content.Children.Add(amount);
                cell.Child = content;
                view.UsageGrid.Children.Add(cell);
            }
        }

        private static int GetCalendarDetailLevel(double height)
        {
            if (height >= 210.0) return 2;
            if (height >= 130.0) return 1;
            return 0;
        }

        private static void RenderWeeklyUsage(AccountView view, List<DailyUsageBucket> buckets)
        {
            view.WeeklyUsageGrid.Children.Clear();
            Dictionary<DateTime, long> byDate = new Dictionary<DateTime, long>();
            if (buckets != null)
            {
                foreach (DailyUsageBucket bucket in buckets)
                {
                    byDate[bucket.Date.Date] = Math.Max(0L, bucket.Tokens);
                }
            }
            DateTime start = DateTime.Today.AddDays(-6);
            long weeklyTotal = 0L;
            long maximum = 1L;
            for (int day = 0; day < 7; day++)
            {
                long tokens;
                byDate.TryGetValue(start.AddDays(day), out tokens);
                weeklyTotal += tokens;
                maximum = Math.Max(maximum, tokens);
            }
            view.WeeklyUsageValue.Text = "최근 7일 · " + FormatTokenCount(weeklyTotal);

            for (int day = 0; day < 7; day++)
            {
                DateTime date = start.AddDays(day);
                long tokens;
                byDate.TryGetValue(date, out tokens);
                Grid slot = new Grid { Margin = new Thickness(1.5, 0.0, 1.5, 0.0), ToolTip = date.ToString("MM'/'dd") + " · " + FormatTokenCount(tokens) };
                slot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26.0) });
                slot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid barArea = new Grid { Margin = new Thickness(0.0, 0.0, 0.0, 2.0) };
                Border track = new Border { Background = BrushFromHex("#303030"), CornerRadius = new CornerRadius(3.0), VerticalAlignment = VerticalAlignment.Stretch };
                Border fill = new Border {
                    Background = BrushFromHex("#2F9E7E"), CornerRadius = new CornerRadius(3.0), VerticalAlignment = VerticalAlignment.Bottom,
                    Height = tokens <= 0 ? 2.0 : 4.0 + 19.0 * tokens / (double)maximum
                };
                TextBlock dateLabel = new TextBlock {
                    Text = day == 6 ? "오늘" : date.ToString("dd"),
                    FontSize = 8.0,
                    FontWeight = day == 6 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = day == 6 ? BrushFromHex("#8DE0C4") : BrushFromHex("#A1A1AA"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                barArea.Children.Add(track);
                barArea.Children.Add(fill);
                slot.Children.Add(barArea);
                Grid.SetRow(dateLabel, 1);
                slot.Children.Add(dateLabel);
                view.WeeklyUsageGrid.Children.Add(slot);
            }
        }

        private static void ChangeCalendarMonth(AccountView view, int delta)
        {
            view.CalendarMonthOffset = Math.Max(-1, Math.Min(1, view.CalendarMonthOffset + delta));
            if (view.State != null)
            {
                view.State.CalendarMonthOffset = view.CalendarMonthOffset;
            }
            RenderUsageGrid(view, view.LastSnapshot == null ? null : view.LastSnapshot.DailyUsage);
        }

        private static void UpdateResetCredits(AccountView view, AccountSnapshot snapshot)
        {
            if (!snapshot.ResetCreditCount.HasValue)
            {
                view.ResetCreditsValue.Text = "초기화권 정보 없음";
                view.ResetCreditsValue.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170));
                view.ResetCreditsDetail.Text = "현재 계정에서 상세 정보를 제공하지 않습니다.";
                return;
            }

            int count = snapshot.ResetCreditCount.Value;
            view.ResetCreditsValue.Text = count > 0 ? "초기화권 " + count.ToString() + "개 보유" : "초기화권 없음";
            view.ResetCreditsValue.Foreground = count > 0
                ? new SolidColorBrush(Color.FromRgb(141, 224, 196))
                : new SolidColorBrush(Color.FromRgb(161, 161, 170));

            DateTime? nearestExpiry = null;
            if (snapshot.ResetCredits != null)
            {
                foreach (ResetCreditInfo credit in snapshot.ResetCredits)
                {
                    if (credit.ExpiresAt.HasValue &&
                        (!nearestExpiry.HasValue || credit.ExpiresAt.Value < nearestExpiry.Value))
                    {
                        nearestExpiry = credit.ExpiresAt;
                    }
                }
            }
            view.ResetCreditsDetail.Text = count == 0
                ? "사용 가능한 리셋권이 없습니다."
                : nearestExpiry.HasValue
                    ? "최근 만료 " + nearestExpiry.Value.ToString("MM'/'dd HH:mm") + " · " + FormatRemaining(nearestExpiry.Value)
                    : "보유 개수만 제공됨 · 만료 상세 없음";
        }

        private void UpdateFooter()
        {
            string duplicatedEmail = _accounts
                .Where(delegate(AccountState state)
                {
                    return state.LastSnapshot != null && state.LastSnapshot.IsAuthenticated &&
                        !String.IsNullOrWhiteSpace(state.LastSnapshot.Email);
                })
                .GroupBy(delegate(AccountState state) { return state.LastSnapshot.Email; }, StringComparer.OrdinalIgnoreCase)
                .Where(delegate(IGrouping<string, AccountState> group) { return group.Count() > 1; })
                .Select(delegate(IGrouping<string, AccountState> group) { return group.Key; })
                .FirstOrDefault();
            if (!String.IsNullOrWhiteSpace(duplicatedEmail))
            {
                SetFooterText("주의: 여러 칸에 같은 계정이 연결되어 있습니다.");
            }
            else
            {
                SetFooterText("계정 사용량 " + DateTime.Now.ToString("HH:mm:ss") + " 갱신");
            }
        }

        private void SetFooterText(string text)
        {
            _footerStatus.Text = text;
            _compactFooterStatus.Text = text;
        }

        private static void UpdateWindow(TextBlock name, TextBlock value, ProgressBar bar, ProgressBar timeBar,
            TextBlock reset, TextBlock remaining, RateWindow window, string fallbackName, bool weekly)
        {
            if (window == null)
            {
                ClearWindow(name, value, bar, timeBar, reset, remaining, fallbackName, weekly);
                return;
            }
            name.Text = window.Name;
            value.Text = Math.Round(window.RemainingPercent).ToString("0") + "%";
            bar.Value = window.RemainingPercent;
            Brush limitBrush = GetLimitBrush(weekly);
            value.Foreground = limitBrush;
            bar.Foreground = limitBrush;
            if (window.ResetsAt.HasValue && window.DurationMinutes > 0)
            {
                timeBar.Value = 100.0 - GetElapsedPercent(window);
                reset.Text = "초기화 " + FormatReset(window.ResetsAt.Value);
                remaining.Text = FormatRemaining(window.ResetsAt.Value);
            }
            else
            {
                timeBar.Value = 0.0;
                reset.Text = "초기화 정보 없음";
                remaining.Text = "--";
            }
        }

        private static Brush GetLimitBrush(bool weekly)
        {
            return weekly ? BrushFromHex("#A78BFA") : BrushFromHex("#72D4B5");
        }

        private static void ClearWindow(TextBlock name, TextBlock value, ProgressBar bar, ProgressBar timeBar,
            TextBlock reset, TextBlock remaining, string fallbackName, bool weekly)
        {
            name.Text = fallbackName;
            value.Text = "--";
            bar.Value = 0;
            Brush limitBrush = GetLimitBrush(weekly);
            value.Foreground = limitBrush;
            bar.Foreground = limitBrush;
            timeBar.Value = 0.0;
            reset.Text = "초기화 --";
            remaining.Text = "--";
        }

        private static double GetElapsedPercent(RateWindow window)
        {
            if (window == null || !window.ResetsAt.HasValue || window.DurationMinutes <= 0) return 0.0;
            DateTime start = window.ResetsAt.Value.AddMinutes(-window.DurationMinutes);
            double elapsedMinutes = (DateTime.Now - start).TotalMinutes;
            return Math.Max(0.0, Math.Min(100.0, 100.0 * elapsedMinutes / window.DurationMinutes));
        }

        private static Geometry CreateArcGeometry(double percent, double radius, Point center)
        {
            double clamped = Math.Max(0.0, Math.Min(100.0, percent));
            if (clamped <= 0.01) return Geometry.Empty;
            if (clamped >= 99.99) return new EllipseGeometry(center, radius, radius);
            double startAngle = -90.0;
            double endAngle = startAngle + clamped * 3.6;
            Point start = new Point(center.X + radius * Math.Cos(startAngle * Math.PI / 180.0),
                center.Y + radius * Math.Sin(startAngle * Math.PI / 180.0));
            Point end = new Point(center.X + radius * Math.Cos(endAngle * Math.PI / 180.0),
                center.Y + radius * Math.Sin(endAngle * Math.PI / 180.0));
            PathFigure figure = new PathFigure();
            figure.StartPoint = start;
            figure.IsClosed = false;
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0.0, clamped > 50.0,
                SweepDirection.Clockwise, true));
            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static string FormatReset(DateTime value)
        {
            return value.ToString("MM'/'dd HH:mm");
        }

        private void UpdateCountdowns()
        {
            UpdateAccountCountdown(_account1);
            UpdateAccountCountdown(_account2);
        }

        private void UpdateAccountCountdown(AccountView view)
        {
            AccountSnapshot snapshot = view.LastSnapshot;
            if (snapshot == null || !snapshot.IsAuthenticated)
            {
                return;
            }
            if (snapshot.Primary != null && snapshot.Primary.ResetsAt.HasValue)
            {
                view.PrimaryTimeBar.Value = 100.0 - GetElapsedPercent(snapshot.Primary);
                view.PrimaryReset.Text = "초기화 " + FormatReset(snapshot.Primary.ResetsAt.Value);
                view.PrimaryRemaining.Text = FormatRemaining(snapshot.Primary.ResetsAt.Value);
            }
            if (snapshot.Secondary != null && snapshot.Secondary.ResetsAt.HasValue)
            {
                view.SecondaryTimeBar.Value = 100.0 - GetElapsedPercent(snapshot.Secondary);
                view.SecondaryReset.Text = "초기화 " + FormatReset(snapshot.Secondary.ResetsAt.Value);
                view.SecondaryRemaining.Text = FormatRemaining(snapshot.Secondary.ResetsAt.Value);
            }
            UpdateResetCredits(view, snapshot);
            UpdateCompactAccount(view, snapshot);
        }

        private static string FormatRemaining(DateTime target)
        {
            TimeSpan remaining = target - DateTime.Now;
            if (remaining.TotalSeconds <= 0.0)
            {
                return "초기화 확인 중";
            }
            if (remaining.TotalDays >= 1.0)
            {
                return ((int)remaining.TotalDays).ToString() + "일 " + remaining.Hours.ToString() + "시간 남음";
            }
            if (remaining.TotalHours >= 1.0)
            {
                return ((int)remaining.TotalHours).ToString() + "시간 " + remaining.Minutes.ToString() + "분 남음";
            }
            if (remaining.TotalMinutes >= 1.0)
            {
                return Math.Max(1, remaining.Minutes).ToString() + "분 남음";
            }
            return Math.Max(1, remaining.Seconds).ToString() + "초 남음";
        }

        private static string FormatCompactRemaining(DateTime target)
        {
            TimeSpan remaining = target - DateTime.Now;
            if (remaining.TotalSeconds <= 0.0)
            {
                return "확인 중";
            }
            if (remaining.TotalDays >= 1.0)
            {
                return ((int)remaining.TotalDays).ToString() + "일 " + remaining.Hours.ToString() + "시간";
            }
            if (remaining.TotalHours >= 1.0)
            {
                return ((int)remaining.TotalHours).ToString() + "시간 " + remaining.Minutes.ToString() + "분";
            }
            if (remaining.TotalMinutes >= 1.0)
            {
                return Math.Max(1, remaining.Minutes).ToString() + "분";
            }
            return Math.Max(1, remaining.Seconds).ToString() + "초";
        }

        private static string FormatTokenCount(long? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatTokenCount(value.Value);
        }

        private static string FormatTokenCount(long value)
        {
            if (value >= 1000000000L) return (value / 1000000000.0).ToString("0.0") + "B";
            if (value >= 1000000L) return (value / 1000000.0).ToString("0.0") + "M";
            if (value >= 1000L) return (value / 1000.0).ToString("0.#") + "K";
            return value.ToString("N0");
        }

        private static string FormatDuration(long totalSeconds)
        {
            if (totalSeconds >= 3600L) return (totalSeconds / 3600L).ToString() + "시간 " + ((totalSeconds % 3600L) / 60L).ToString() + "분";
            if (totalSeconds >= 60L) return (totalSeconds / 60L).ToString() + "분 " + (totalSeconds % 60L).ToString() + "초";
            return totalSeconds.ToString() + "초";
        }

        private static string Percent(double value)
        {
            return Math.Round(value).ToString("0") + "%";
        }

        private static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1073741824.0)
            {
                return (bytesPerSecond / 1073741824.0).ToString("0.0") + " GB/s";
            }
            if (bytesPerSecond >= 1048576.0)
            {
                return (bytesPerSecond / 1048576.0).ToString("0.0") + " MB/s";
            }
            if (bytesPerSecond >= 1024.0)
            {
                return (bytesPerSecond / 1024.0).ToString("0") + " KB/s";
            }
            return Math.Max(0.0, bytesPerSecond).ToString("0") + " B/s";
        }

        private static string PlanName(string plan)
        {
            if (String.IsNullOrWhiteSpace(plan))
            {
                return String.Empty;
            }
            if (plan.Length == 1)
            {
                return plan.ToUpperInvariant();
            }
            return Char.ToUpperInvariant(plan[0]) + plan.Substring(1);
        }

        private void ShowWindow()
        {
            if (!_window.IsVisible)
            {
                _window.Show();
            }
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }
            _window.Activate();
        }

        private void ExitApplication()
        {
            _systemTimer.Stop();
            _accountTimer.Stop();
            _trayIcon.Visible = false;
            _window.Close();
            Application.Current.Shutdown();
        }

        public void RequestCloseForSmoke()
        {
            _window.Close();
        }

        public void CapturePreview(string path)
        {
            _window.UpdateLayout();
            int width = Math.Max(1, (int)Math.Ceiling(_window.ActualWidth));
            int height = Math.Max(1, (int)Math.Ceiling(_window.ActualHeight));
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
            bitmap.Render(_window);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }
        }

        public void ApplyDesignPreview()
        {
            ApplyAccountCount(4, false);
            SetAccountPage(1);
            if (_account1.State == null || _account1.State.Number != 3 || _account2.State == null || _account2.State.Number != 4 ||
                _account1.BadgeText.Text != "3" || _account2.CompactBadgeText.Text != "4")
            {
                throw new InvalidOperationException("추가 계정 페이지 전환에 실패했습니다.");
            }
            ApplyAccountCount(2, false);
            SetAccountPage(0);
            ApplyTopmostState(false, false);
            if (_window.Topmost || Convert.ToString(_topmostButton.ToolTip) != "항상 위 켜기")
            {
                throw new InvalidOperationException("위젯 항상 위 토글 동기화에 실패했습니다.");
            }
            ApplyTopmostState(true, false);
            if (!_window.Topmost || Convert.ToString(_compactTopmostButton.ToolTip) != "항상 위 끄기")
            {
                throw new InvalidOperationException("전체 화면 항상 위 토글 동기화에 실패했습니다.");
            }
            ApplyTopmostState(false, false);
            ApplyFontScale(1.5, false);
            _accounts[0].LastSnapshot = CreatePreviewSnapshot("account.one@example.com", "plus", 72.0, 44.0, 2, 0);
            _accounts[1].LastSnapshot = CreatePreviewSnapshot("account.two@example.com", "pro", 31.0, 81.0, 1, 3);
            BindAccountPage();
            SystemSnapshot previewSystem = new SystemSnapshot();
            previewSystem.CpuPercent = 28.0;
            previewSystem.MemoryPercent = 63.0;
            previewSystem.MemoryUsedGb = 20.2;
            previewSystem.MemoryTotalGb = 32.0;
            previewSystem.GpuAvailable = true;
            previewSystem.GpuPercent = 41.0;
            previewSystem.Gpus.Add(new GpuSnapshot { Index = 0, Key = "gpu:preview0", Name = "Intel Arc Graphics", Percent = 41.0 });
            previewSystem.Gpus.Add(new GpuSnapshot { Index = 1, Key = "gpu:preview1", Name = "NVIDIA GeForce", Percent = 17.0 });
            previewSystem.DiskAvailable = true;
            previewSystem.DiskPercent = 34.0;
            previewSystem.Disks.Add(new DiskSnapshot { Index = 0, Key = "disk:0", Name = "디스크 0 (C:)", Detail = "SSD · 512 GB · System NVMe", Percent = 12.0, ReadBytesPerSecond = 48000000.0, WriteBytesPerSecond = 6200000.0 });
            previewSystem.Disks.Add(new DiskSnapshot { Index = 1, Key = "disk:1", Name = "디스크 1 (D:)", Detail = "SSD · 2.0 TB · Data NVMe", Percent = 34.0, ReadBytesPerSecond = 8400000.0, WriteBytesPerSecond = 1200000.0 });
            previewSystem.Networks.Add(new NetworkSnapshot { Key = "network:wifi", Name = "Wi-Fi", Detail = "Intel Wi-Fi 7", Connected = true, LinkSpeedBitsPerSecond = 2400000000L, Percent = 4.0, ReceiveBytesPerSecond = 11300000.0, SendBytesPerSecond = 684000.0 });
            previewSystem.Networks.Add(new NetworkSnapshot { Key = "network:ethernet", Name = "Ethernet", Detail = "2.5GbE Controller", Connected = false, LinkSpeedBitsPerSecond = 2500000000L, Percent = 0.0 });
            for (int sample = 0; sample < 24; sample++)
            {
                previewSystem.CpuPercent = 21.0 + (sample * 17 % 38);
                previewSystem.MemoryPercent = 55.0 + (sample % 6) * 1.6;
                previewSystem.Gpus[0].Percent = 28.0 + (sample * 11 % 42);
                previewSystem.Gpus[1].Percent = 10.0 + (sample * 7 % 24);
                previewSystem.Disks[0].Percent = 5.0 + (sample * 19 % 43);
                previewSystem.Disks[1].Percent = 18.0 + (sample * 13 % 37);
                previewSystem.Networks[0].Percent = 2.0 + (sample * 5 % 15);
                UpdatePerformanceCards(BuildPerformanceItems(previewSystem));
            }
            previewSystem.CpuPercent = 28.0;
            previewSystem.MemoryPercent = 63.0;
            previewSystem.Gpus[0].Percent = 41.0;
            previewSystem.Gpus[1].Percent = 17.0;
            previewSystem.Disks[0].Percent = 12.0;
            previewSystem.Disks[1].Percent = 34.0;
            previewSystem.Networks[0].Percent = 4.0;
            List<PerformanceDisplayItem> previewItems = BuildPerformanceItems(previewSystem);
            UpdatePerformanceCards(previewItems);
            _performanceCountText.Text = "CPU · RAM · GPU 2 · 디스크 2 · 네트워크 2";
            _systemStatus.Text = "감지된 성능 항목 " + previewItems.Count.ToString() + "개를 개별 측정 중";
            _compactCpuValue.Text = "28%";
            _compactGpuLabel.Text = "GPU 0";
            _compactGpuLabel.ToolTip = previewSystem.Gpus[0].Name;
            _compactGpuValue.Text = "41%";
            _compactGpuValue.ToolTip = _compactGpuLabel.ToolTip;
            _compactMemoryValue.Text = "63%";
            _compactDiskLabel.Text = "디스크 1";
            _compactDiskLabel.ToolTip = previewSystem.Disks[1].Name + " · " + previewSystem.Disks[1].Detail;
            _compactDiskValue.Text = "34%";
            _compactDiskValue.ToolTip = "읽기 " + FormatRate(previewSystem.Disks[1].ReadBytesPerSecond) +
                " · 쓰기 " + FormatRate(previewSystem.Disks[1].WriteBytesPerSecond);
            _compactCpuRing.Data = CreateArcGeometry(28.0, 35.0, new Point(48.0, 48.0));
            _compactGpuRing.Data = CreateArcGeometry(41.0, 35.0, new Point(48.0, 48.0));
            _compactMemoryRing.Data = CreateArcGeometry(63.0, 35.0, new Point(48.0, 48.0));
            _compactDiskRing.Data = CreateArcGeometry(34.0, 35.0, new Point(48.0, 48.0));
            _compactNetworkValue.Text = "NET 2 · ↓ 11.3 MB/s   ↑ 684 KB/s";
            UpdateFooter();
        }

        private static AccountSnapshot CreatePreviewSnapshot(string email, string plan, double primaryRemaining,
            double secondaryRemaining, int resetCount, int dateOffset)
        {
            AccountSnapshot snapshot = new AccountSnapshot();
            snapshot.IsAuthenticated = true;
            snapshot.Email = email;
            snapshot.PlanType = plan;
            snapshot.UpdatedAt = DateTime.Now;
            snapshot.Primary = new RateWindow {
                Name = "5시간 한도", RemainingPercent = primaryRemaining, UsedPercent = 100.0 - primaryRemaining,
                DurationMinutes = 300, ResetsAt = DateTime.Now.AddHours(2).AddMinutes(34)
            };
            snapshot.Secondary = new RateWindow {
                Name = "주간 한도", RemainingPercent = secondaryRemaining, UsedPercent = 100.0 - secondaryRemaining,
                DurationMinutes = 10080, ResetsAt = DateTime.Now.AddDays(3).AddHours(7)
            };
            snapshot.ResetCreditCount = resetCount;
            snapshot.ResetCredits = new List<ResetCreditInfo>();
            if (resetCount > 0)
            {
                snapshot.ResetCredits.Add(new ResetCreditInfo { Status = "available", Title = "Rate-limit reset", ExpiresAt = DateTime.Now.AddDays(9 - dateOffset) });
            }
            snapshot.Usage = new UsageSummary {
                LifetimeTokens = 2483500L + dateOffset * 140000L,
                PeakDailyTokens = 186400L + dateOffset * 8000L,
                CurrentStreakDays = 8L - dateOffset,
                LongestStreakDays = 19L,
                LongestRunningTurnSeconds = 742L + dateOffset * 35L
            };
            snapshot.DailyUsage = new List<DailyUsageBucket>();
            for (int day = 1; day <= DateTime.Today.Day; day++)
            {
                long tokens = (long)(day * 4100 + ((day + dateOffset) % 4) * 13800);
                if ((day + dateOffset) % 6 == 0) tokens = 0L;
                snapshot.DailyUsage.Add(new DailyUsageBucket {
                    Date = new DateTime(DateTime.Today.Year, DateTime.Today.Month, day), Tokens = tokens
                });
            }
            return snapshot;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _systemTimer.Stop();
            _accountTimer.Stop();
            foreach (AccountState state in _accounts)
            {
                state.Client.AccountChanged -= AccountClientChanged;
                state.Client.Dispose();
            }
            if (_windowSource != null)
            {
                try { _windowSource.RemoveHook(WindowMessageHook); } catch { }
                _windowSource = null;
            }
            _trayIcon.Visible = false;
            System.Drawing.Icon drawingIcon = _trayIcon.Icon;
            _trayIcon.Dispose();
            if (drawingIcon != null)
            {
                drawingIcon.Dispose();
            }
        }
    }

    internal static class UserSettings
    {
        private const string SettingsKey = "Software\\CodexUsageMeter";
        private const string TopmostValue = "Topmost";
        private const string FontScaleValue = "FontScalePercent";
        private const string AccountCountValue = "AccountCount";

        public static bool LoadTopmost()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
            {
                if (key == null)
                {
                    return false;
                }
                object raw = key.GetValue(TopmostValue);
                if (raw is int)
                {
                    return (int)raw != 0;
                }
                bool parsed;
                return Boolean.TryParse(Convert.ToString(raw), out parsed) && parsed;
            }
        }

        public static void SaveTopmost(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("사용자 설정 레지스트리를 열지 못했습니다.");
                }
                key.SetValue(TopmostValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        public static double LoadFontScale()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
            {
                if (key == null) return 1.5;
                int percent;
                if (!Int32.TryParse(Convert.ToString(key.GetValue(FontScaleValue)), out percent)) return 1.5;
                return Math.Max(1.0, Math.Min(2.0, percent / 100.0));
            }
        }

        public static void SaveFontScale(double scale)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (key == null) throw new InvalidOperationException("사용자 설정 레지스트리를 열지 못했습니다.");
                int percent = (int)Math.Round(Math.Max(1.0, Math.Min(2.0, scale)) * 100.0);
                key.SetValue(FontScaleValue, percent, RegistryValueKind.DWord);
            }
        }

        public static int LoadAccountCount()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
            {
                if (key == null) return 2;
                int count;
                if (!Int32.TryParse(Convert.ToString(key.GetValue(AccountCountValue)), out count)) return 2;
                return Math.Max(1, Math.Min(4, count));
            }
        }

        public static void SaveAccountCount(int count)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (key == null) throw new InvalidOperationException("사용자 설정 레지스트리를 열지 못했습니다.");
                key.SetValue(AccountCountValue, Math.Max(1, Math.Min(4, count)), RegistryValueKind.DWord);
            }
        }
    }

    internal static class AutoStartManager
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string ValueName = "CodexUsageMeter";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                if (key == null)
                {
                    return false;
                }
                string value = key.GetValue(ValueName) as string;
                return !String.IsNullOrWhiteSpace(value);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Windows 시작 프로그램 레지스트리를 열지 못했습니다.");
                }
                if (enabled)
                {
                    string executable = Assembly.GetExecutingAssembly().Location;
                    key.SetValue(ValueName, "\"" + executable + "\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    internal static class SelfTest
    {
        public static int Run(string resultPath)
        {
            List<string> lines = new List<string>();
            string temporaryProfile = Path.Combine(Path.GetTempPath(),
                "codex-meter-self-test-" + Guid.NewGuid().ToString("N"));
            CodexRpcClient client = null;
            try
            {
                string codex = CodexLocator.Find();
                lines.Add("PASS codex: " + codex);

                Window window = DashboardController.LoadWindow();
                window.Measure(new Size(1040, 720));
                window.Arrange(new Rect(0, 0, 1040, 720));
                if (window.Icon == null)
                {
                    throw new InvalidOperationException("내장 앱 아이콘이 적용되지 않았습니다.");
                }
                string[] requiredNames = new string[] {
                    "ExpandedLayout", "CompactLayout", "ExpandedShell", "CompactShell", "AccountCountBadgeText", "AccountSummaryText", "CompactModeButton", "ExpandedModeButton",
                    "RefreshButton", "TopmostButton", "SettingsButton", "HideButton", "MaximizeButton", "CloseButton", "TitleBar", "AutostartCheckBox", "FooterStatus",
                    "CompactRefreshButton", "CompactTopmostButton", "CompactSettingsButton", "CompactHideButton", "CompactMaximizeButton", "CompactCloseButton", "CompactTitleBar", "CompactFooterStatus",
                    "Account1Identity", "Account1LoginButton", "Account1LogoutButton",
                    "Account1Card", "Account1TitleText", "Account1BadgeText", "Account1PrimaryName", "Account1PrimaryValue", "Account1PrimaryBar", "Account1PrimaryTimeBar", "Account1PrimaryReset", "Account1PrimaryRemaining",
                    "Account1SecondaryName", "Account1SecondaryValue", "Account1SecondaryBar", "Account1SecondaryTimeBar", "Account1SecondaryReset", "Account1SecondaryRemaining",
                    "Account1ResetCreditsValue", "Account1ResetCreditsDetail", "Account1LifetimeValue", "Account1PeakValue",
                    "Account1StreakValue", "Account1LongestTurnValue", "Account1CalendarTitle", "Account1CalendarPreviousButton", "Account1CalendarNextButton", "Account1WeeklyUsageValue", "Account1WeeklyUsageGrid", "Account1WeekdayHeader", "Account1UsageGrid", "Account1UsageEmpty", "Account1Status",
                    "Account2Identity", "Account2LoginButton", "Account2LogoutButton",
                    "Account2Card", "Account2TitleText", "Account2BadgeText", "Account2PrimaryName", "Account2PrimaryValue", "Account2PrimaryBar", "Account2PrimaryTimeBar", "Account2PrimaryReset", "Account2PrimaryRemaining",
                    "Account2SecondaryName", "Account2SecondaryValue", "Account2SecondaryBar", "Account2SecondaryTimeBar", "Account2SecondaryReset", "Account2SecondaryRemaining",
                    "Account2ResetCreditsValue", "Account2ResetCreditsDetail", "Account2LifetimeValue", "Account2PeakValue",
                    "Account2StreakValue", "Account2LongestTurnValue", "Account2CalendarTitle", "Account2CalendarPreviousButton", "Account2CalendarNextButton", "Account2WeeklyUsageValue", "Account2WeeklyUsageGrid", "Account2WeekdayHeader", "Account2UsageGrid", "Account2UsageEmpty", "Account2Status",
                    "PerformanceItemsPanel", "PerformanceCountText", "SystemStatus",
                    "CompactAccountSummaryText", "CompactAccountPageButton", "CompactAccount1Card", "CompactAccount1TitleText", "CompactAccount1BadgeText", "CompactAccount1Identity", "CompactAccount1PrimaryValue",
                    "CompactAccount1PrimaryTrack", "CompactAccount1PrimaryRing", "CompactAccount1PrimaryTimeBar", "CompactAccount1PrimaryTimeValue", "CompactAccount1PrimaryRecommendationRing", "CompactAccount1SecondaryTrack", "CompactAccount1SecondaryValue", "CompactAccount1SecondaryRing", "CompactAccount1SecondaryTimeBar", "CompactAccount1SecondaryTimeValue", "CompactAccount1SecondaryRecommendationRing", "CompactAccount1PaceValue", "CompactAccount1ResetValue",
                    "CompactAccount2Card", "CompactAccount2TitleText", "CompactAccount2BadgeText", "CompactAccount2Identity", "CompactAccount2PrimaryValue",
                    "CompactAccount2PrimaryTrack", "CompactAccount2PrimaryRing", "CompactAccount2PrimaryTimeBar", "CompactAccount2PrimaryTimeValue", "CompactAccount2PrimaryRecommendationRing", "CompactAccount2SecondaryTrack", "CompactAccount2SecondaryValue", "CompactAccount2SecondaryRing", "CompactAccount2SecondaryTimeBar", "CompactAccount2SecondaryTimeValue", "CompactAccount2SecondaryRecommendationRing", "CompactAccount2PaceValue", "CompactAccount2ResetValue",
                    "CompactCpuValue", "CompactCpuTrack", "CompactCpuRing", "CompactGpuLabel", "CompactGpuValue", "CompactGpuTrack", "CompactGpuRing", "CompactMemoryValue", "CompactMemoryTrack", "CompactMemoryRing", "CompactDiskLabel", "CompactDiskValue", "CompactDiskTrack", "CompactDiskRing", "CompactNetworkValue",
                    "ModalOverlay", "ModalTitle", "ModalMessage", "ModalCodePanel", "ModalCode", "ModalPrimaryButton", "ModalSecondaryButton",
                    "SettingsOverlay", "SettingsCloseButton", "SettingsDoneButton", "AccountCountDecreaseButton", "AccountCountValue", "AccountCountIncreaseButton", "FontDecreaseButton", "FontResetButton", "FontIncreaseButton", "FontScaleValue", "AppVersionValue", "UpdateStatusText", "UpdateCheckButton",
                    "AccountPagePreviousButton", "AccountPageText", "AccountPageNextButton"
                };
                foreach (string requiredName in requiredNames)
                {
                    if (window.FindName(requiredName) == null)
                    {
                        throw new InvalidOperationException("UI 요소를 찾지 못했습니다: " + requiredName);
                    }
                }
                UniformGrid account1WeekdayHeader = window.FindName("Account1WeekdayHeader") as UniformGrid;
                UniformGrid account2WeekdayHeader = window.FindName("Account2WeekdayHeader") as UniformGrid;
                string account1WeekdayOrder = String.Concat(account1WeekdayHeader.Children.OfType<TextBlock>().Select(label => label.Text));
                string account2WeekdayOrder = String.Concat(account2WeekdayHeader.Children.OfType<TextBlock>().Select(label => label.Text));
                if (account1WeekdayOrder != "일월화수목금토" || account2WeekdayOrder != "일월화수목금토")
                {
                    throw new InvalidOperationException("달력 요일이 일요일부터 토요일 순서가 아닙니다.");
                }
                WindowChrome chrome = WindowChrome.GetWindowChrome(window);
                Border expandedShell = window.FindName("ExpandedShell") as Border;
                Border compactShell = window.FindName("CompactShell") as Border;
                if (chrome == null || chrome.CaptionHeight != 0.0 ||
                    chrome.ResizeBorderThickness.Left != 0.0 || chrome.ResizeBorderThickness.Top != 0.0 ||
                    chrome.ResizeBorderThickness.Right != 0.0 || chrome.ResizeBorderThickness.Bottom != 0.0 ||
                    chrome.GlassFrameThickness.Left != 0.0 || chrome.GlassFrameThickness.Top != 0.0 ||
                    chrome.GlassFrameThickness.Right != 0.0 || chrome.GlassFrameThickness.Bottom != 0.0 ||
                    expandedShell == null || compactShell == null ||
                    expandedShell.BorderThickness.Left != 0.0 || compactShell.BorderThickness.Left != 0.0 ||
                    expandedShell.CornerRadius.TopLeft != 0.0 || compactShell.CornerRadius.TopLeft != 0.0)
                {
                    throw new InvalidOperationException("창 콘텐츠가 네이티브 프레임 없이 전체 영역을 채우지 않습니다.");
                }
                System.Windows.Shapes.Ellipse primaryTrack = window.FindName("CompactAccount1PrimaryTrack") as System.Windows.Shapes.Ellipse;
                System.Windows.Shapes.Path primaryRing = window.FindName("CompactAccount1PrimaryRing") as System.Windows.Shapes.Path;
                System.Windows.Shapes.Path primaryRecommendation = window.FindName("CompactAccount1PrimaryRecommendationRing") as System.Windows.Shapes.Path;
                System.Windows.Shapes.Ellipse weeklyTrack = window.FindName("CompactAccount1SecondaryTrack") as System.Windows.Shapes.Ellipse;
                System.Windows.Shapes.Path weeklyRing = window.FindName("CompactAccount1SecondaryRing") as System.Windows.Shapes.Path;
                System.Windows.Shapes.Path weeklyRecommendation = window.FindName("CompactAccount1SecondaryRecommendationRing") as System.Windows.Shapes.Path;
                System.Windows.Shapes.Ellipse cpuTrack = window.FindName("CompactCpuTrack") as System.Windows.Shapes.Ellipse;
                System.Windows.Shapes.Path cpuRing = window.FindName("CompactCpuRing") as System.Windows.Shapes.Path;
                ProgressBar compactPrimaryTimeBar = window.FindName("CompactAccount1PrimaryTimeBar") as ProgressBar;
                ProgressBar compactWeeklyTimeBar = window.FindName("CompactAccount1SecondaryTimeBar") as ProgressBar;
                if (primaryTrack.StrokeThickness != primaryRing.StrokeThickness || primaryRing.StrokeThickness != primaryRecommendation.StrokeThickness ||
                    primaryTrack.Width != 70.0 || weeklyTrack.StrokeThickness != weeklyRing.StrokeThickness ||
                    weeklyRing.StrokeThickness != weeklyRecommendation.StrokeThickness || weeklyTrack.Width != 92.0 ||
                    cpuTrack.StrokeThickness != cpuRing.StrokeThickness || cpuTrack.Width != 70.0)
                {
                    throw new InvalidOperationException("원형 게이지의 빈 트랙과 채움 호 크기가 일치하지 않습니다.");
                }
                if (compactPrimaryTimeBar == null || compactWeeklyTimeBar == null ||
                    compactPrimaryTimeBar.Parent is Canvas || compactWeeklyTimeBar.Parent is Canvas ||
                    compactPrimaryTimeBar.Maximum != 100.0 || compactWeeklyTimeBar.Maximum != 100.0)
                {
                    throw new InvalidOperationException("위젯 갱신 시간 게이지가 사용량 원형 게이지와 분리되지 않았습니다.");
                }
                if (window.ResizeMode != ResizeMode.CanResize || window.AllowsTransparency || window.MinWidth != 0.0 || window.MinHeight != 0.0 ||
                    DashboardController.GetResizeHitTest(25.0, 25.0, 800.0, 600.0, 12.0, 30.0) != 13 ||
                    DashboardController.GetResizeHitTest(775.0, 575.0, 800.0, 600.0, 12.0, 30.0) != 17 ||
                    DashboardController.GetResizeHitTest(6.0, 300.0, 800.0, 600.0, 12.0, 30.0) != 10 ||
                    DashboardController.GetResizeHitTest(20.0, 300.0, 800.0, 600.0, 12.0, 30.0) != 1 ||
                    DashboardController.GetResizeHitTest(400.0, 300.0, 800.0, 600.0, 12.0, 30.0) != 1)
                {
                    throw new InvalidOperationException("창 크기 조절 히트 테스트가 올바르지 않습니다.");
                }
                Button defaultTopmost = window.FindName("TopmostButton") as Button;
                Button defaultCompactTopmost = window.FindName("CompactTopmostButton") as Button;
                if (defaultTopmost == null || defaultCompactTopmost == null || window.Topmost)
                {
                    throw new InvalidOperationException("항상 위 기본값이 꺼짐 상태가 아닙니다.");
                }
                Button maximizeButton = window.FindName("MaximizeButton") as Button;
                Button compactMaximizeButton = window.FindName("CompactMaximizeButton") as Button;
                if (maximizeButton == null || compactMaximizeButton == null ||
                    Convert.ToString(maximizeButton.Content) != "□" || Convert.ToString(compactMaximizeButton.Content) != "□")
                {
                    throw new InvalidOperationException("최대화 버튼의 기본 상태가 올바르지 않습니다.");
                }
                DashboardController.NativeRect aspectRectangle = new DashboardController.NativeRect {
                    Left = 0, Top = 0, Right = 1200, Bottom = 700
                };
                DashboardController.ConstrainResizeToAspect(ref aspectRectangle, 8, 1280.0 / 820.0);
                double constrainedRatio = (aspectRectangle.Right - aspectRectangle.Left) /
                    (double)(aspectRectangle.Bottom - aspectRectangle.Top);
                if (Math.Abs(constrainedRatio - (1280.0 / 820.0)) > 0.01)
                {
                    throw new InvalidOperationException("Shift 비율 고정 계산이 올바르지 않습니다.");
                }
                window.Close();
                lines.Add("PASS ui: dynamic performance panel, settings overlay, no app minimum size, top-bar always-on-top, maximize/restore, and app icon enabled");

                UpdateClient.RunUpdaterSelfTest();
                lines.Add("PASS updater: embedded helper replacement, SHA-256 verification, and rollback path enabled; current v" + UpdateClient.CurrentVersionText);

                SystemMonitor monitor = new SystemMonitor();
                monitor.SampleAsync().GetAwaiter().GetResult();
                Thread.Sleep(1100);
                SystemSnapshot system = monitor.SampleAsync().GetAwaiter().GetResult();
                lines.Add("PASS sensors: CPU 1, RAM 1, GPU " + system.Gpus.Count.ToString() +
                    ", physical disks " + system.Disks.Count.ToString() + ", network adapters " + system.Networks.Count.ToString());

                Directory.CreateDirectory(temporaryProfile);
                client = new CodexRpcClient(codex, temporaryProfile);
                client.ProbeAsync().GetAwaiter().GetResult();
                AccountSnapshot account = client.RefreshAsync().GetAwaiter().GetResult();
                lines.Add("PASS app-server: initialized; isolated profile authenticated=" + account.IsAuthenticated.ToString());
                lines.Add("PASS self-test completed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                WriteResult(resultPath, lines);
                return 0;
            }
            catch (Exception ex)
            {
                lines.Add("FAIL " + ex.GetType().Name + ": " + ex.Message);
                try { WriteResult(resultPath, lines); } catch { }
                return 1;
            }
            finally
            {
                if (client != null)
                {
                    client.Dispose();
                }
                SafeDeleteTemporaryProfile(temporaryProfile);
            }
        }

        private static void WriteResult(string resultPath, List<string> lines)
        {
            string directory = Path.GetDirectoryName(resultPath);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllLines(resultPath, lines.ToArray(), new UTF8Encoding(false));
        }

        private static void SafeDeleteTemporaryProfile(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string leaf = Path.GetFileName(fullPath);
                if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    leaf.StartsWith("codex-meter-self-test-", StringComparison.Ordinal) && Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                }
            }
            catch
            {
            }
        }
    }
}
