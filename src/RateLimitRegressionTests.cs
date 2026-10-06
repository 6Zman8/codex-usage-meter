using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageMeter
{
    internal static class RateLimitRegressionTests
    {
        private const string Weekly = "{\"usedPercent\":2,\"windowDurationMins\":10080,\"resetsAt\":1791543761}";
        private const string Short = "{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":1790964000}";

        public static void Run(Action<string> report, string previewDirectory = null)
        {
            int failures = 0;
            Check(report, ref failures, "weekly-only quota goes to weekly display", delegate {
                AccountSnapshot s = Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"planType\":\"prolite\",\"primary\":" + Weekly + ",\"secondary\":null}}");
                Require(s.Primary == null && s.Secondary != null && s.Secondary.RemainingPercent == 98 &&
                    s.Secondary.DurationMinutes == 10080 && s.Secondary.ResetsAt.HasValue, "weekly quota was put in the short-term slot");
            });
            Check(report, ref failures, "live plan supersedes cached Plus after upgrade", delegate {
                AccountSnapshot s = Parse("{\"rateLimits\":{\"planType\":\"prolite\",\"primary\":" + Weekly + "}}", "plus");
                Require(s.PlanType == "prolite", "cached plan hides the upgraded server plan");
            });
            Check(report, ref failures, "Plus retains both windows and cached plan if absent from response", delegate {
                AccountSnapshot s = Parse("{\"rateLimits\":{\"primary\":" + Short + ",\"secondary\":" + Weekly + "}}", "plus");
                Require(s.PlanType == "plus" && s.Primary.RemainingPercent == 75 && s.Secondary.RemainingPercent == 98,
                    "two-window response changed");
            });
            Check(report, ref failures, "Codex multi-bucket data wins over legacy view", delegate {
                AccountSnapshot s = Parse("{\"rateLimits\":{\"planType\":\"plus\",\"primary\":" + Short + "},\"rateLimitsByLimitId\":{\"codex\":{\"planType\":\"prolite\",\"primary\":" + Weekly + "}}}");
                Require(s.Primary == null && s.Secondary != null && s.Secondary.RemainingPercent == 98 && s.PlanType == "prolite",
                    "legacy view overrides the current Codex bucket");
            });
            Check(report, ref failures, "unrelated bucket cannot masquerade as Codex", delegate {
                AccountSnapshot s = Parse("{\"rateLimitsByLimitId\":{\"other\":{\"primary\":" + Short + "}}}");
                Require(s.Primary == null && s.Secondary == null && !String.IsNullOrEmpty(s.Error), "unrelated quota was selected");
            });
            Check(report, ref failures, "missing usage is unknown, not 100 percent remaining", delegate {
                AccountSnapshot s = Parse("{\"rateLimits\":{\"primary\":{\"windowDurationMins\":300},\"secondary\":" + Weekly + "}}");
                Require(s.Primary == null && s.Secondary.RemainingPercent == 98, "missing usedPercent became unused quota");
            });
            Check(report, ref failures, "reversed windows follow duration", delegate {
                AccountSnapshot s = Parse("{\"rateLimits\":{\"primary\":" + Weekly + ",\"secondary\":" + Short + "}}");
                Require(s.Primary.RemainingPercent == 75 && s.Secondary.RemainingPercent == 98, "response position overrides duration");
            });
            Check(report, ref failures, "Pro Lite has a readable display name", delegate {
                string name = (string)typeof(DashboardController).GetMethod("PlanName", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { "prolite" });
                Require(name == "Pro Lite", "plan name was not formatted");
            });
            Check(report, ref failures, "real compact and expanded views show weekly-only and Plus quotas", delegate {
                TestViews(previewDirectory);
            });
            if (failures > 0) throw new InvalidOperationException(failures + " rate-limit regression(s) failed");
        }

        private static void TestViews(string previewDirectory)
        {
            Window window = DashboardController.LoadWindow();
            DashboardController controller = (DashboardController)FormatterServices.GetUninitializedObject(typeof(DashboardController));
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(DashboardController).GetField("_window", flags).SetValue(controller, window);
            MethodInfo createView = typeof(DashboardController).GetMethod("CreateAccountView", flags);
            AccountView first = (AccountView)createView.Invoke(controller, new object[] { 1, "계정 1", null });
            AccountView second = (AccountView)createView.Invoke(controller, new object[] { 2, "계정 2", null });
            typeof(DashboardController).GetField("_account1", flags).SetValue(controller, first);
            typeof(DashboardController).GetField("_account2", flags).SetValue(controller, second);
            foreach (int number in new int[] { 1, 2 })
                foreach (string suffix in new string[] { "Identity", "PrimaryValue", "SecondaryValue", "ResetValue" })
                    typeof(DashboardController).GetField("_compactAccount" + number + suffix, flags)
                        .SetValue(controller, window.FindName("CompactAccount" + number + suffix));
            MethodInfo update = typeof(DashboardController).GetMethod("UpdateAccount", flags);
            AccountSnapshot lite = Parse("{\"rateLimits\":{\"planType\":\"prolite\",\"primary\":" + Weekly + "}}", "plus");
            AccountSnapshot plus = Parse("{\"rateLimits\":{\"planType\":\"plus\",\"primary\":" + Short + ",\"secondary\":" + Weekly + "}}");
            lite.Email = "Pro Lite 계정";
            plus.Email = "Plus 계정";
            update.Invoke(controller, new object[] { first, lite });
            update.Invoke(controller, new object[] { second, plus });
            Require(first.SecondaryValue.Text == "98%" && first.PrimaryValue.Text == "미제공" && first.Identity.Text.Contains("Pro Lite"),
                "expanded Pro Lite display is wrong");
            Require(((TextBlock)window.FindName("CompactAccount1SecondaryValue")).Text == "98%" &&
                ((TextBlock)window.FindName("CompactAccount1PrimaryValue")).Text == "없음" &&
                ((TextBlock)window.FindName("CompactAccount1PrimaryName")).Text == "5시간" &&
                first.CompactPrimaryTimeValue.Text == "미제공" && !first.CompactSecondaryRing.Data.IsEmpty(), "compact Pro Lite display is wrong");
            Require(second.PrimaryValue.Text == "75%" && second.SecondaryValue.Text == "98%" &&
                ((TextBlock)window.FindName("CompactAccount2PrimaryName")).Text == "5시간", "Plus display regressed");
            if (!String.IsNullOrWhiteSpace(previewDirectory))
            {
                Directory.CreateDirectory(previewDirectory);
                Grid root = (Grid)window.Content;
                window.Content = null;
                root.Resources.MergedDictionaries.Add(window.Resources);
                root.SetValue(Control.FontFamilyProperty, window.FontFamily);
                root.SetValue(Control.ForegroundProperty, window.Foreground);
                HwndSourceParameters parameters = new HwndSourceParameters("CodexUsageMeter.RateLimitTest");
                parameters.WindowStyle = unchecked((int)0x80000000);
                parameters.ExtendedWindowStyle = 0x08000080;
                parameters.Width = 1280;
                parameters.Height = 820;
                using (HwndSource surface = new HwndSource(parameters))
                {
                    surface.RootVisual = root;
                    foreach (bool compact in new bool[] { false, true })
                    {
                        ((Grid)window.FindName("ExpandedLayout")).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                        ((Grid)window.FindName("CompactLayout")).Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                        int width = compact ? 460 : 1280;
                        int height = compact ? 780 : 820;
                        root.Measure(new Size(width, height));
                        root.Arrange(new Rect(0, 0, width, height));
                        root.UpdateLayout();
                        TextBlock shown = (TextBlock)window.FindName(compact ? "CompactAccount1SecondaryValue" : "Account1SecondaryValue");
                        Require(shown.IsVisible && shown.ActualWidth > 0 && shown.ActualHeight > 0, "quota preview is not visible");
                        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        PngBitmapEncoder encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (FileStream output = File.Create(Path.Combine(previewDirectory, compact ? "compact.png" : "expanded.png")))
                            encoder.Save(output);
                    }
                }
            }
            // A later unavailable response must clear old quota values, not invent zero usage.
            AccountSnapshot unavailable = Parse("{\"rateLimits\":{\"planType\":\"prolite\",\"primary\":null,\"secondary\":null}}");
            update.Invoke(controller, new object[] { first, unavailable });
            Require(first.PrimaryValue.Text == "--" && first.SecondaryValue.Text == "--" && first.Status.Visibility == Visibility.Visible &&
                ((TextBlock)window.FindName("CompactAccount1PrimaryValue")).Text == "--", "unavailable response left stale values");
            window.Close();
        }

        private static AccountSnapshot Parse(string json, string cachedPlan = null)
        {
            AccountSnapshot snapshot = new AccountSnapshot { IsAuthenticated = true, PlanType = cachedPlan,
                ResetCredits = new List<ResetCreditInfo>() };
            using (CodexRpcClient client = new CodexRpcClient("unused", "unused"))
                typeof(CodexRpcClient).GetMethod("ParseRateLimits", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(client, new object[] { new JavaScriptSerializer().DeserializeObject(json), snapshot });
            return snapshot;
        }

        private static void Check(Action<string> report, ref int failures, string name, Action test)
        {
            try { test(); report("PASS " + name); }
            catch (Exception ex) { failures++; report("FAIL " + name + ": " + ex.GetBaseException().Message); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
