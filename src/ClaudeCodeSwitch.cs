using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    // The Claude card's 전환/다시 연결: Claude Code uses the slot's subscription sign-in, like the Codex card switches Codex.
    // Only Claude Code's own stores change: <home>\.claude\.credentials.json (claudeAiOauth; other entries such as
    // MCP sign-ins are kept) and, when it already has one, the oauthAccount block of <home>\.claude.json.
    // Claude Desktop, Mixdog and every other app keep their own sign-ins.
    internal sealed class ClaudeCodeSwitcher
    {
        private const int MaximumBytes = 32 * 1024 * 1024;
        private readonly string _credentialsPath, _configPath, _claudeFolder;
        private readonly IAccountSwitchJournal _journal;
        private int _switching;

        internal ClaudeCodeSwitcher(string userProfile, IAccountSwitchJournal journal)
        {
            if (String.IsNullOrWhiteSpace(userProfile)) throw new ArgumentNullException("userProfile");
            if (journal == null) throw new ArgumentNullException("journal");
            _claudeFolder = Path.Combine(userProfile, ".claude");
            _credentialsPath = Path.Combine(_claudeFolder, ".credentials.json");
            _configPath = Path.Combine(userProfile, ".claude.json");
            _journal = journal;
        }

        internal string CredentialsPath { get { return _credentialsPath; } }
        internal string ConfigPath { get { return _configPath; } }
        // Claude Code creates these on its first run.
        internal bool IsInstalled { get { return Directory.Exists(_claudeFolder) || File.Exists(_configPath); } }

        private sealed class Slot
        {
            internal int Number;
            internal string Root, Raw, Account, Organization, Email;
            internal Dictionary<string, object> OAuth;
        }

        // The slot whose verified owner Claude Code currently uses, or null.
        internal int? DetectActiveAccountNumber(IEnumerable<KeyValuePair<int, string>> slots)
        {
            try
            {
                Dictionary<string, object> current = OAuthOf(ReadDocument(_credentialsPath));
                if (current == null) return null;
                Dictionary<string, object> account = JsonValue.AsObject(JsonValue.Get(ReadDocument(_configPath), "oauthAccount"));
                foreach (Slot slot in slots.Select(pair => ReadSlot(pair.Key, pair.Value)).Where(slot => slot != null))
                {
                    if (account != null && JsonValue.Get(account, "accountUuid") is string)
                    {
                        if (SameId(JsonValue.Get(account, "accountUuid") as string, slot.Account) &&
                            SameId(JsonValue.Get(account, "organizationUuid") as string, slot.Organization)) return slot.Number;
                    }
                    else if (Token(current, "refreshToken") != null && Token(current, "refreshToken") == Token(slot.OAuth, "refreshToken")) return slot.Number;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
            return null;
        }

        // Before a switch, a newer sign-in Claude Code made for the active slot's own account is kept in that slot (like Codex).
        internal void SynchronizeCurrentCredentials(IEnumerable<KeyValuePair<int, string>> slots)
        {
            List<KeyValuePair<int, string>> list = slots.ToList();
            int? active = DetectActiveAccountNumber(list);
            if (!active.HasValue) return;
            Slot slot = ReadSlot(active.Value, list.First(pair => pair.Key == active.Value).Value);
            Dictionary<string, object> current = OAuthOf(ReadDocument(_credentialsPath));
            if (slot == null || !IsSubscriptionSignIn(current) || Token(current, "accessToken") == Token(slot.OAuth, "accessToken") ||
                Expiry(current) <= Expiry(slot.OAuth)) return;
            // No binding is written: the slot reads the profile of the new credential once and verifies its owner itself.
            string raw = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "claudeAiOauth", current } });
            ClaudeUsageAccount.AtomicWrite(Path.Combine(slot.Root, ".credentials.json"), raw);
            _journal.Write("claude-code-sync", "account=" + slot.Number);
        }

        // Claude sign-ins renew with single-use refresh tokens, and after a switch the slot and Claude Code share one.
        // Before each poll Claude Code's newer renewal is taken into the slot; the returned state lets AfterPoll hand a
        // renewal the meter made back to Claude Code, so neither side is left with a used refresh token.
        internal sealed class PollShare { internal int Number; internal string Refresh; }

        internal PollShare BeforePoll(IEnumerable<KeyValuePair<int, string>> slots)
        {
            try
            {
                List<KeyValuePair<int, string>> list = slots.ToList();
                SynchronizeCurrentCredentials(list);
                int? active = DetectActiveAccountNumber(list);
                string refresh = Token(OAuthOf(ReadDocument(_credentialsPath)), "refreshToken");
                return active.HasValue && refresh != null ? new PollShare { Number = active.Value, Refresh = refresh } : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        internal void AfterPoll(PollShare share, IEnumerable<KeyValuePair<int, string>> slots)
        {
            if (share == null || Interlocked.CompareExchange(ref _switching, 1, 0) != 0) return;
            try
            {
                KeyValuePair<int, string> entry = slots.FirstOrDefault(pair => pair.Key == share.Number);
                if (entry.Value == null) return;
                Slot slot = ReadSlot(entry.Key, entry.Value);
                byte[] original = File.Exists(_credentialsPath) ? ReadBytes(_credentialsPath) : null;
                Dictionary<string, object> document = original == null ? null : Parse(original);
                Dictionary<string, object> current = OAuthOf(document);
                // Only when Claude Code still holds exactly the token the slot had before its renewal.
                if (slot == null || current == null || Token(current, "refreshToken") != share.Refresh ||
                    Token(slot.OAuth, "refreshToken") == share.Refresh || Expiry(slot.OAuth) <= Expiry(current)) return;
                document["claudeAiOauth"] = slot.OAuth;
                AtomicWrite(_credentialsPath, Encoding.UTF8.GetBytes(Serializer().Serialize(document)));
                _journal.Write("claude-code-follow", "account=" + slot.Number);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
            finally { Interlocked.Exchange(ref _switching, 0); }
        }

        internal AccountSwitchResult SwitchTo(int targetNumber, IEnumerable<KeyValuePair<int, string>> slots)
        {
            List<KeyValuePair<int, string>> list = slots.ToList();
            if (!list.Any(pair => pair.Key == targetNumber)) return AccountSwitchResult.Failed(false, "Claude에 연결된 계정 칸만 전환할 수 있습니다.");
            if (!IsInstalled)
                return AccountSwitchResult.Failed(false, "Claude Code를 찾지 못해 계정을 변경하지 않았습니다. Claude Code를 설치하고 한 번 실행한 뒤 다시 시도해 주세요.");
            if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0) return AccountSwitchResult.Failed(false, "다른 계정 전환이 진행 중입니다.");
            byte[] originalCredentials = null, originalConfig = null;
            bool credentialsApplied = false, configApplied = false;
            try
            {
                _journal.Write("claude-switch-start", "target-account=" + targetNumber);
                int? before = DetectActiveAccountNumber(list);
                SynchronizeCurrentCredentials(list);
                Slot target = ReadSlot(targetNumber, list.First(pair => pair.Key == targetNumber).Value);
                if (target == null) throw new InvalidDataException("등록된 계정의 인증정보를 확인하지 못했습니다. 이 칸을 다시 연결해 주세요.");
                _journal.Write("claude-target-validated", "target-account=" + targetNumber);

                if (File.Exists(_credentialsPath)) originalCredentials = ReadBytes(_credentialsPath);
                Dictionary<string, object> document = originalCredentials == null ? new Dictionary<string, object>() : Parse(originalCredentials);
                if (document == null) throw new InvalidDataException("Claude Code 인증정보 파일 형식을 확인하지 못해 변경하지 않았습니다.");
                document["claudeAiOauth"] = target.OAuth;
                credentialsApplied = true;
                AtomicWrite(_credentialsPath, Encoding.UTF8.GetBytes(Serializer().Serialize(document)));

                if (File.Exists(_configPath))
                {
                    originalConfig = ReadBytes(_configPath);
                    Dictionary<string, object> config = Parse(originalConfig);
                    Dictionary<string, object> account = JsonValue.AsObject(JsonValue.Get(config, "oauthAccount"));
                    // The block describes the signed-in account; another account's names/roles are not carried over.
                    if (account != null && !(SameId(JsonValue.Get(account, "accountUuid") as string, target.Account) &&
                        SameId(JsonValue.Get(account, "organizationUuid") as string, target.Organization)))
                    {
                        config["oauthAccount"] = new Dictionary<string, object> { { "accountUuid", target.Account },
                            { "emailAddress", target.Email }, { "organizationUuid", target.Organization } };
                        configApplied = true;
                        AtomicWrite(_configPath, Encoding.UTF8.GetBytes(Serializer().Serialize(config)));
                    }
                }
                _journal.Write("claude-auth-applied", "target-account=" + targetNumber);
                if (Token(OAuthOf(ReadDocument(_credentialsPath)), "accessToken") != Token(target.OAuth, "accessToken"))
                    throw new InvalidOperationException("적용된 계정 인증을 확인하지 못했습니다.");
                _journal.Write("claude-switch-complete", "target-account=" + targetNumber);
                bool already = before.HasValue && before.Value == targetNumber;
                return AccountSwitchResult.Completed(already, (already ? "Claude Code에 이 계정의 최신 로그인을 다시 적용했습니다. " : "선택한 계정으로 Claude Code 로그인을 바꿨습니다. ") +
                    "새로 여는 Claude Code부터 이 계정을 사용합니다. 이미 실행 중인 Claude Code는 다시 시작해 주세요. 미터기는 계속 실행 중입니다.");
            }
            catch (Exception ex)
            {
                bool restored = true;
                try
                {
                    if (configApplied && originalConfig != null) AtomicWrite(_configPath, originalConfig);
                    if (credentialsApplied)
                    {
                        if (originalCredentials != null) AtomicWrite(_credentialsPath, originalCredentials);
                        else if (File.Exists(_credentialsPath)) File.Delete(_credentialsPath);
                    }
                }
                catch { restored = false; }
                _journal.Write("claude-switch-failed", "target-account=" + targetNumber + " applied=" + credentialsApplied +
                    " restored=" + restored + " error-type=" + ex.GetType().Name);
                string message = SafeFailureMessage(ex);
                if (credentialsApplied)
                    message = restored ? "계정 전환에 실패하여 기존 Claude Code 계정으로 복원했습니다. " + message
                        : "계정 전환과 기존 계정 복원에 실패했습니다. Claude Code 로그인 상태를 확인해 주세요. " + message;
                return AccountSwitchResult.Failed(credentialsApplied && restored, message);
            }
            finally { Interlocked.Exchange(ref _switching, 0); }
        }

        // A slot qualifies only with a subscription sign-in whose exact text is bound to its verified owner.
        private static Slot ReadSlot(int number, string root)
        {
            string path = Path.Combine(root, ".credentials.json");
            if (!File.Exists(path)) return null;
            string raw = File.ReadAllText(path, Encoding.UTF8);
            Dictionary<string, object> oauth;
            try { oauth = OAuthOf(Parse(Encoding.UTF8.GetBytes(raw))); }
            catch (InvalidDataException) { return null; }
            string[] owner = ClaudeUsageAccount.BoundOwner(root, raw);
            if (owner == null || !IsSubscriptionSignIn(oauth)) return null;
            return new Slot { Number = number, Root = root, Raw = raw, OAuth = oauth, Account = owner[0], Organization = owner[1], Email = owner[2] };
        }

        private static bool IsSubscriptionSignIn(Dictionary<string, object> oauth)
        {
            return oauth != null && Token(oauth, "accessToken") != null && Token(oauth, "refreshToken") != null &&
                new[] { "pro", "max", "team", "enterprise" }.Contains(JsonValue.Get(oauth, "subscriptionType") as string) &&
                JsonValue.AsItems(JsonValue.Get(oauth, "scopes")).Any(scope => scope as string == "user:profile");
        }

        private static Dictionary<string, object> OAuthOf(Dictionary<string, object> document)
        { return JsonValue.AsObject(JsonValue.Get(document, "claudeAiOauth")); }
        private static string Token(Dictionary<string, object> oauth, string key)
        { string value = JsonValue.Get(oauth, key) as string; return String.IsNullOrWhiteSpace(value) ? null : value; }
        private static double Expiry(Dictionary<string, object> oauth)
        {
            object value = JsonValue.Get(oauth, "expiresAt");
            return value is int || value is long || value is double || value is decimal ? Convert.ToDouble(value) : 0;
        }
        private static bool SameId(string left, string right)
        { Guid a, b; return Guid.TryParse(left, out a) && Guid.TryParse(right, out b) && a == b; }

        private static Dictionary<string, object> ReadDocument(string path)
        { return File.Exists(path) ? Parse(ReadBytes(path)) : null; }
        private static byte[] ReadBytes(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Claude Code 설정 파일 크기가 예상 범위를 벗어났습니다.");
            return bytes;
        }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 256 }; }
        private static Dictionary<string, object> Parse(byte[] bytes)
        {
            try { return Serializer().DeserializeObject(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) as Dictionary<string, object>; }
            catch (ArgumentException) { throw new InvalidDataException("Claude Code 설정 파일 형식을 확인하지 못해 변경하지 않았습니다."); }
            catch (InvalidOperationException) { throw new InvalidDataException("Claude Code 설정 파일 형식을 확인하지 못해 변경하지 않았습니다."); }
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    stream.Write(bytes, 0, bytes.Length);
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static string SafeFailureMessage(Exception exception)
        {
            if (exception is FileNotFoundException) return "등록된 계정의 인증정보를 찾지 못했습니다.";
            if (exception is UnauthorizedAccessException) return "인증정보 파일을 읽거나 쓸 권한이 없습니다.";
            if (exception is InvalidDataException) return exception.Message;
            if (exception is IOException) return "인증정보 파일을 안전하게 교체하지 못했습니다.";
            return String.IsNullOrWhiteSpace(exception.Message) ? "알 수 없는 오류가 발생했습니다." : exception.Message;
        }
    }
}
