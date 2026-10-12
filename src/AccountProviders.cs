using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CodexUsageMeter
{
    [Flags]
    internal enum AccountCapabilities { None = 0, CodexSwitch = 1, ChatGptSubscription = 2, CodexExtras = 4 }

    internal sealed class AccountProvider
    {
        internal readonly string Id, DisplayName, Symbol, CredentialFile;
        internal readonly AccountCapabilities Capabilities;
        internal readonly Func<string, int, string> ProfilePath;
        internal readonly Func<string, IUsageAccount> Create;

        internal AccountProvider(string id, string name, string symbol, AccountCapabilities capabilities,
            Func<string, int, string> path, Func<string, IUsageAccount> create, string credentialFile = null)
        { Id = id; DisplayName = name; Symbol = symbol; Capabilities = capabilities; ProfilePath = path; Create = create; CredentialFile = credentialFile; }

        internal bool Supports(AccountCapabilities capability) { return (Capabilities & capability) == capability; }
        internal string CardTitle(string label) { return Symbol + " " + DisplayName + " · " + label; }
    }

    internal sealed class AccountLoginPrompt
    {
        internal string VerificationUrl, UserCode, Message;
        // External CLI login returns immediately; only this task waits for the person.
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
        Timeout, Canceled, IdentityUnavailable, IdentityMismatch, RenewalBlocked, Storage, Network, InvalidResponse, UnknownProvider
    }

    internal sealed class AccountProviderException : Exception
    {
        internal readonly AccountErrorKind Kind;
        internal AccountProviderException(AccountErrorKind kind, string message) : base(message) { Kind = kind; }
    }

    internal static class AccountProviders
    {
        internal static readonly AccountProvider Codex = new AccountProvider("codex", "GPT/Codex", "◎",
            AccountCapabilities.CodexSwitch | AccountCapabilities.ChatGptSubscription | AccountCapabilities.CodexExtras,
            (root, number) => Path.Combine(root, "account-" + number),
            path => new CodexUsageAccount(new CodexRpcClient(new Func<string>(CodexLocator.Find), path)), "auth.json");
        internal static readonly AccountProvider Claude = new AccountProvider("claude", "Claude", "✳",
            AccountCapabilities.None, (root, number) => Path.Combine(root, "claude", "account-" + number),
            path => new ClaudeUsageAccount(path), ".credentials.json");
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

        internal static async Task<AccountSnapshot> RefreshSafelyAsync(IUsageAccount account)
        {
            try { return await account.RefreshAsync(); }
            catch (Exception error)
            {
                AccountProviderException known = error as AccountProviderException;
                return new AccountSnapshot { ProviderId = account.Provider.Id, UpdatedAt = DateTime.Now,
                    ErrorKind = known == null ? AccountErrorKind.Network : known.Kind,
                    Error = known == null ? "이 계정의 사용량을 갱신하지 못했습니다. 연결 상태를 확인해 주세요." : known.Message };
            }
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
