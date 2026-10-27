using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CodexUsageMeter
{
    // UsageExtras: reset credits, statistics and calendar sections. SubscriptionDate: the subscription line.
    // ChromeSubscription: the subscription date read from the person's own Chrome through the Tampermonkey bridge.
    // AppSwitch: the card's 전환/다시 연결 makes the provider's own app (Codex / Claude Code) use this slot's account.
    // Every registered provider has the same capabilities; only an unknown stored provider has none.
    [Flags]
    internal enum AccountCapabilities { None = 0, AppSwitch = 1, ChromeSubscription = 2, UsageExtras = 4, SubscriptionDate = 8 }

    internal sealed class AccountProvider
    {
        internal readonly string Id, DisplayName, Symbol, CredentialFile;
        // AppName: the app the 전환 button signs in. WebName/BillingUrl: the site whose billing page Chrome opens.
        internal readonly string AppName, WebName, BillingUrl;
        internal readonly AccountCapabilities Capabilities;
        internal readonly Func<string, int, string> ProfilePath;
        internal readonly Func<string, IUsageAccount> Create;

        internal AccountProvider(string id, string name, string symbol, AccountCapabilities capabilities,
            Func<string, int, string> path, Func<string, IUsageAccount> create, string credentialFile = null,
            string appName = null, string webName = null, string billingUrl = null)
        {
            Id = id; DisplayName = name; Symbol = symbol; Capabilities = capabilities; ProfilePath = path; Create = create; CredentialFile = credentialFile;
            AppName = appName ?? name; WebName = webName; BillingUrl = billingUrl;
        }

        internal bool Supports(AccountCapabilities capability) { return (Capabilities & capability) == capability; }
        internal string CardTitle(string label) { return Symbol + " " + DisplayName + " · " + label; }
    }

    internal sealed class AccountLoginPrompt
    {
        internal string VerificationUrl, UserCode, Message;
        // Browser login returns immediately; only this task waits for the person.
        internal Task Completion;
        internal Action Cancel;
    }

    internal interface IUsageAccount : IDisposable
    {
        AccountProvider Provider { get; }
        string ProfileRoot { get; }
        event EventHandler AccountChanged;
        Task<AccountSnapshot> RefreshAsync();
        Task<AccountLoginPrompt> StartLoginAsync();
        Task LogoutAsync();
    }

    internal enum AccountErrorKind
    {
        None, InstallNeeded, LoginRequired, UnsupportedAuthentication, ScopeDenied, RateLimited,
        Timeout, Canceled, IdentityUnavailable, IdentityMismatch, RenewalBlocked, Storage, Network, InvalidResponse, UnknownProvider,
        QuotaUnavailable
    }

    internal sealed class AccountProviderException : Exception
    {
        internal readonly AccountErrorKind Kind;
        internal AccountProviderException(AccountErrorKind kind, string message) : base(message) { Kind = kind; }
    }

    internal static class AccountProviders
    {
        private const AccountCapabilities AllCapabilities = AccountCapabilities.AppSwitch | AccountCapabilities.ChromeSubscription |
            AccountCapabilities.UsageExtras | AccountCapabilities.SubscriptionDate;
        internal static readonly AccountProvider Codex = new AccountProvider("codex", "GPT/Codex", "◎",
            AllCapabilities, (root, number) => Path.Combine(root, "account-" + number),
            path => new CodexUsageAccount(new CodexRpcClient(new Func<string>(CodexLocator.Find), path)), "auth.json",
            "Codex", "ChatGPT", "https://chatgpt.com/settings/billing");
        internal static readonly AccountProvider Claude = new AccountProvider("claude", "Claude", "✳",
            AllCapabilities, (root, number) => Path.Combine(root, "claude", "account-" + number),
            path => new ClaudeUsageAccount(path), ".credentials.json",
            "Claude Code", "claude.ai", "https://claude.ai/settings/billing");
        internal static readonly AccountProvider[] Registered = { Codex, Claude };

        internal static AccountProvider Resolve(string id)
        {
            // Only a missing value migrates to Codex. An unknown/empty stored ID stays unsupported.
            if (id == null) return Codex;
            return Registered.FirstOrDefault(value => value.Id == id) ??
                new AccountProvider(id, "지원하지 않는 공급자", "?", AccountCapabilities.None, null, null);
        }

        internal static IUsageAccount Create(string id, string accountsRoot, int number)
        {
            AccountProvider provider = Resolve(id);
            return provider.Create == null ? new UnsupportedUsageAccount(provider) :
                provider.Create(provider.ProfilePath(accountsRoot, number));
        }

        internal static string LoadSlot(RegistryKey key, int number)
        {
            object value = key == null ? null : key.GetValue("AccountProvider" + number);
            return value == null ? Codex.Id : Convert.ToString(value);
        }

        internal static void SaveSlot(RegistryKey key, int number, string id)
        {
            if (!Registered.Any(provider => provider.Id == id)) throw new ArgumentException("지원하지 않는 공급자입니다.");
            key.SetValue("AccountProvider" + number, id, RegistryValueKind.String);
        }

        internal static AccountProvider For(AccountSnapshot snapshot)
        { return Resolve(snapshot == null ? null : snapshot.ProviderId); }

        internal static bool HasRetainedCredentials(IUsageAccount account)
        { return account.Provider.CredentialFile != null && File.Exists(Path.Combine(account.ProfileRoot, account.Provider.CredentialFile)); }

        internal static string DuplicateKey(AccountSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsAuthenticated) return null;
            if (For(snapshot).Id == Codex.Id)
                return String.IsNullOrEmpty(snapshot.RemoteAccountId) ? null : "codex\n" + snapshot.RemoteAccountId;
            return snapshot.IdentityVerified ? UsageHistoryStore.ProviderAccountKey(snapshot.ProviderId,
                snapshot.RemoteAccountId, snapshot.RemoteOrganizationId) : null;
        }

        internal static Task<AccountSnapshot> RefreshSafelyAsync(IUsageAccount account)
        { return RefreshSafelyAsync(account, milliseconds => Task.Delay(milliseconds)); }

        internal static async Task<AccountSnapshot> RefreshSafelyAsync(IUsageAccount account, Func<int, Task> retryDelay)
        {
            for (int attempt = 0; ; attempt++)
            {
                AccountSnapshot snapshot;
                bool retry;
                try
                {
                    snapshot = await account.RefreshAsync();
                    retry = IsTransientRefresh(snapshot);
                }
                catch (Exception error)
                {
                    AccountProviderException known = error as AccountProviderException;
                    AccountErrorKind kind = known != null ? known.Kind :
                        error is OperationCanceledException || error is ObjectDisposedException ? AccountErrorKind.Canceled :
                        error is TimeoutException ? AccountErrorKind.Timeout :
                        error is IOException || error is UnauthorizedAccessException ? AccountErrorKind.Storage : AccountErrorKind.Network;
                    snapshot = new AccountSnapshot { ProviderId = account.Provider.Id, UpdatedAt = DateTime.Now,
                        ErrorKind = kind,
                        Error = known == null ? "이 계정의 사용량을 갱신하지 못했습니다. 연결 상태를 확인해 주세요." : known.Message };
                    // Do not turn arbitrary exceptions (including storage/cancellation) into retryable network failures.
                    retry = known != null ? IsTransientRefreshKind(known.Kind) :
                        error is TimeoutException;
                }
                // Claude refresh may renew/write OAuth credentials. Only Codex's read-only refresh is repeated.
                if (account.Provider.Id != Codex.Id || attempt != 0 || !retry ||
                    (!snapshot.IsAuthenticated && !HasRetainedCredentials(account)))
                    return snapshot;
                // Only this account's read is repeated; never reconnect, rotate credentials or invoke login/logout here.
                await retryDelay(5000);
            }
        }

        private static bool IsTransientRefreshKind(AccountErrorKind kind)
        { return kind == AccountErrorKind.Network || kind == AccountErrorKind.Timeout; }

        private static bool IsTransientRefresh(AccountSnapshot snapshot)
        {
            if (snapshot == null || snapshot.RetryAfterUtc.HasValue) return false;
            // QuotaUnavailable is emitted only after a successful quota response parses without a usable window.
            if (IsTransientRefreshKind(snapshot.ErrorKind) || snapshot.ErrorKind == AccountErrorKind.QuotaUnavailable) return true;
            if (snapshot.ErrorKind != AccountErrorKind.None) return false;
            return !snapshot.IsAuthenticated && String.IsNullOrWhiteSpace(snapshot.Error);
        }
    }

    internal sealed class CodexUsageAccount : IUsageAccount
    {
        internal readonly CodexRpcClient Rpc;
        internal CodexUsageAccount(CodexRpcClient rpc) { Rpc = rpc; }
        public AccountProvider Provider { get { return AccountProviders.Codex; } }
        public string ProfileRoot { get { return Rpc.ProfileRoot; } }
        public event EventHandler AccountChanged { add { Rpc.AccountChanged += value; } remove { Rpc.AccountChanged -= value; } }
        public Task<AccountSnapshot> RefreshAsync() { return Rpc.RefreshAsync(); }
        public async Task<AccountLoginPrompt> StartLoginAsync()
        {
            DeviceLoginInfo login = await Rpc.StartDeviceLoginAsync();
            return new AccountLoginPrompt { VerificationUrl = login.VerificationUrl, UserCode = login.UserCode };
        }
        public Task LogoutAsync() { return Rpc.LogoutAsync(); }
        public void Dispose() { Rpc.Dispose(); }
    }

    internal sealed class UnsupportedUsageAccount : IUsageAccount
    {
        private readonly AccountProvider _provider;
        internal UnsupportedUsageAccount(AccountProvider provider) { _provider = provider; }
        public AccountProvider Provider { get { return _provider; } }
        public string ProfileRoot { get { return null; } }
        public event EventHandler AccountChanged { add { } remove { } }
        public Task<AccountSnapshot> RefreshAsync()
        {
            return Task.FromResult(new AccountSnapshot { ProviderId = Provider.Id, ErrorKind = AccountErrorKind.UnknownProvider,
                Error = "저장된 공급자를 지원하지 않습니다. 연결에서 공급자를 선택해 주세요. 기존 인증은 보존됩니다." });
        }
        public Task<AccountLoginPrompt> StartLoginAsync() { throw new AccountProviderException(AccountErrorKind.UnknownProvider, "공급자를 먼저 선택해 주세요."); }
        public Task LogoutAsync() { throw new AccountProviderException(AccountErrorKind.UnknownProvider, "알 수 없는 공급자의 인증은 변경하지 않았습니다."); }
        public void Dispose() { }
    }
}
