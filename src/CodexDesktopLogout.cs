using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace CodexUsageMeter
{
    internal sealed class CodexLogoutResult
    {
        public bool Success { get; private set; }
        public string Message { get; private set; }

        private CodexLogoutResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public static CodexLogoutResult Completed(string message)
        {
            return new CodexLogoutResult(true, message);
        }

        public static CodexLogoutResult Failed(string message)
        {
            return new CodexLogoutResult(false, message);
        }
    }

    internal sealed class CodexDesktopLogout
    {
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;
        private const ushort VirtualKeyControl = 0x11;
        private const ushort VirtualKeyShift = 0x10;
        private const ushort VirtualKeyP = 0x50;
        private const ushort VirtualKeyA = 0x41;
        private const ushort VirtualKeyReturn = 0x0D;
        private const ushort VirtualKeyEscape = 0x1B;
        private const int ShowWindowRestore = 9;

        public CodexLogoutResult TryLogout(TimeSpan timeout)
        {
            IntPtr window = FindCodexDesktopWindow();
            if (window == IntPtr.Zero)
            {
                return CodexLogoutResult.Failed("실행 중인 Codex 창을 찾지 못했습니다. Codex를 먼저 열어 주세요.");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            if (!ActivateCodexWindow(window))
            {
                return CodexLogoutResult.Failed("Codex 창을 앞으로 가져오지 못했습니다.");
            }

            if (!SendShortcut(VirtualKeyControl, VirtualKeyShift, VirtualKeyP))
            {
                return CodexLogoutResult.Failed("Codex 명령 메뉴를 여는 키 입력에 실패했습니다.");
            }

            AutomationElement search = WaitForCommandSearch(window, stopwatch, timeout);
            if (search == null)
            {
                SendKey(VirtualKeyEscape);
                return CodexLogoutResult.Failed("Codex 명령 메뉴가 열렸는지 확인하지 못했습니다. Codex에서 직접 로그아웃해 주세요.");
            }

            string[] terms = GetLogoutSearchTerms();
            bool exactCommandFound = false;
            foreach (string term in terms)
            {
                search = FindCommandSearch(window);
                if (search == null || !SetSearchText(search, term))
                {
                    continue;
                }

                Thread.Sleep(250);
                if (HasExactVisibleLogoutCommand(window, terms, search))
                {
                    exactCommandFound = true;
                    break;
                }
            }

            if (!exactCommandFound)
            {
                SendKey(VirtualKeyEscape);
                return CodexLogoutResult.Failed("Codex 명령 메뉴에서 로그아웃 항목을 정확히 찾지 못해 실행하지 않았습니다.");
            }

            if (!SendKey(VirtualKeyReturn))
            {
                SendKey(VirtualKeyEscape);
                return CodexLogoutResult.Failed("Codex 로그아웃 명령 실행에 실패했습니다.");
            }

            Thread.Sleep(500);
            InvokeLogoutConfirmationIfPresent(window, terms);
            Thread.Sleep(500);

            if (IsVisibleEdit(search))
            {
                SendKey(VirtualKeyEscape);
                return CodexLogoutResult.Failed("Codex 로그아웃 명령이 실행됐는지 확인하지 못했습니다.");
            }

            return CodexLogoutResult.Completed(
                "Codex의 자체 로그아웃을 실행했습니다. 열린 Codex 로그인 화면에서 사용할 계정을 선택해 주세요.");
        }

        private static IntPtr FindCodexDesktopWindow()
        {
            foreach (Process process in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    string path = process.MainModule == null ? null : process.MainModule.FileName;
                    if (!String.IsNullOrWhiteSpace(path) &&
                        path.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            return IntPtr.Zero;
        }

        private static bool ActivateCodexWindow(IntPtr window)
        {
            ShowWindow(window, ShowWindowRestore);
            BringWindowToTop(window);
            SetForegroundWindow(window);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (GetForegroundWindow() == window) return true;
                Thread.Sleep(50);
                SetForegroundWindow(window);
            }
            return GetForegroundWindow() == window;
        }

        private static AutomationElement WaitForCommandSearch(IntPtr window, Stopwatch stopwatch, TimeSpan timeout)
        {
            TimeSpan limit = timeout < TimeSpan.FromSeconds(3) ? timeout : TimeSpan.FromSeconds(3);
            while (stopwatch.Elapsed < limit)
            {
                AutomationElement search = FindCommandSearch(window);
                if (search != null) return search;
                Thread.Sleep(80);
            }
            return null;
        }

        private static AutomationElement FindCommandSearch(IntPtr window)
        {
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (IsVisibleEdit(focused)) return focused;

                AutomationElement root = AutomationElement.FromHandle(window);
                if (root == null) return null;
                AutomationElementCollection edits = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                foreach (AutomationElement edit in edits)
                {
                    if (IsVisibleEdit(edit) && edit.Current.HasKeyboardFocus) return edit;
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool IsVisibleEdit(AutomationElement element)
        {
            try
            {
                return element != null && element.Current.ControlType == ControlType.Edit &&
                    element.Current.IsEnabled && !element.Current.IsOffscreen;
            }
            catch
            {
                return false;
            }
        }

        private static bool SetSearchText(AutomationElement search, string value)
        {
            try
            {
                object pattern;
                if (search.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    ((ValuePattern)pattern).SetValue(value);
                    return true;
                }

                search.SetFocus();
                if (!SendShortcut(VirtualKeyControl, VirtualKeyA)) return false;
                return SendUnicodeText(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasExactVisibleLogoutCommand(IntPtr window, string[] terms, AutomationElement search)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                if (root == null) return false;
                AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement element in elements)
                {
                    if (Object.ReferenceEquals(element, search)) continue;
                    string name;
                    bool visible;
                    try
                    {
                        name = element.Current.Name;
                        visible = element.Current.IsEnabled && !element.Current.IsOffscreen;
                    }
                    catch
                    {
                        continue;
                    }
                    if (!visible || String.IsNullOrWhiteSpace(name)) continue;
                    foreach (string term in terms)
                    {
                        if (String.Equals(name.Trim(), term, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static void InvokeLogoutConfirmationIfPresent(IntPtr window, string[] terms)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                if (root == null) return;
                AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement button in buttons)
                {
                    string name;
                    try
                    {
                        if (!button.Current.IsEnabled || button.Current.IsOffscreen) continue;
                        name = button.Current.Name;
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (string term in terms)
                    {
                        if (!String.Equals((name ?? String.Empty).Trim(), term, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        object pattern;
                        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            return;
                        }
                        button.SetFocus();
                        SendKey(VirtualKeyReturn);
                        return;
                    }
                }
            }
            catch
            {
            }
        }

        private static string[] GetLogoutSearchTerms()
        {
            List<string> terms = new List<string>();
            if (String.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ko",
                StringComparison.OrdinalIgnoreCase))
            {
                terms.Add("로그아웃");
            }
            terms.Add("Log Out");
            terms.Add("Logout");
            if (!terms.Contains("로그아웃")) terms.Add("로그아웃");
            return terms.ToArray();
        }

        private static bool SendShortcut(params ushort[] keys)
        {
            List<KeyboardInput> inputs = new List<KeyboardInput>();
            foreach (ushort key in keys) inputs.Add(CreateKeyInput(key, false));
            for (int index = keys.Length - 1; index >= 0; index--) inputs.Add(CreateKeyInput(keys[index], true));
            return SendKeyboardInputs(inputs.ToArray());
        }

        private static bool SendKey(ushort key)
        {
            return SendKeyboardInputs(new KeyboardInput[] {
                CreateKeyInput(key, false), CreateKeyInput(key, true)
            });
        }

        private static bool SendUnicodeText(string text)
        {
            List<KeyboardInput> inputs = new List<KeyboardInput>();
            foreach (char character in text)
            {
                inputs.Add(CreateUnicodeInput(character, false));
                inputs.Add(CreateUnicodeInput(character, true));
            }
            return SendKeyboardInputs(inputs.ToArray());
        }

        private static KeyboardInput CreateKeyInput(ushort key, bool keyUp)
        {
            KeyboardInput input = new KeyboardInput();
            input.Type = InputKeyboard;
            input.Data.Keyboard = new KeyInputData {
                VirtualKey = key,
                ScanCode = 0,
                Flags = keyUp ? KeyEventKeyUp : 0,
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            };
            return input;
        }

        private static KeyboardInput CreateUnicodeInput(char character, bool keyUp)
        {
            KeyboardInput input = new KeyboardInput();
            input.Type = InputKeyboard;
            input.Data.Keyboard = new KeyInputData {
                VirtualKey = 0,
                ScanCode = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            };
            return input;
        }

        private static bool SendKeyboardInputs(KeyboardInput[] inputs)
        {
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(KeyboardInput))) == inputs.Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public uint Type;
            public InputData Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)]
            public KeyInputData Keyboard;

            [FieldOffset(0)]
            public MouseInputData Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyInputData
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInputData
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, KeyboardInput[] inputs, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
