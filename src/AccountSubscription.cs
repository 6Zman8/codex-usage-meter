using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class AccountSubscriptionInfo
    {
        public DateTime? Date { get; set; }
        public string Kind { get; set; }
        public string NextPlan { get; set; }
        public string Error { get; set; }
        public DateTime CheckedAt { get; set; }
        internal AccountSubscriptionInfo Copy()
        { return new AccountSubscriptionInfo { Date = Date, Kind = Kind, NextPlan = NextPlan, Error = Error, CheckedAt = CheckedAt }; }
    }

    internal static class AccountSubscription
    {
        internal const int MaximumJsonLength = 524288;

        public static string Format(AccountSubscriptionInfo value, DateTime today)
        {
            if (value == null || !value.Date.HasValue || value.Date.Value.Date < today.Date) return "구독 날짜 조회 불가";
            string label = value.Kind == "renewal" ? "구독 갱신 " : value.Kind == "end" ? "구독 종료 " : value.Kind == "period" ? "이용기간 " : value.Kind == "change" ? "플랜 변경 " : null;
            if (label == null) return "구독 날짜 조회 불가";
            int days = (value.Date.Value.Date - today.Date).Days;
            return label + value.Date.Value.ToString("M/d", CultureInfo.InvariantCulture) + (value.Kind == "period" ? "까지" : value.Kind == "change" ? " → " + PlanName(value.NextPlan) : "") +
                " · " + (days == 0 ? "오늘" : days.ToString(CultureInfo.InvariantCulture) + "일 남음");
        }

        internal static string PlanName(string plan)
        {
            switch (Plan(plan)) { case "plus": return "Plus"; case "prolite": return "Pro 100"; case "pro": return "Pro"; case "free": return "Free"; case "go": return "Go"; default: return "새 플랜"; }
        }

        public static DateTime? ReadFreshClaim(string profileRoot, string livePlan, DateTime now)
        {
            try { return ParseFreshClaim(ReadAuthJson(profileRoot), livePlan, now); }
            catch (Exception) { return null; }
        }

        internal static DateTime? ParseFreshClaim(string authJson, string livePlan, DateTime now)
        {
            try
            {
                Dictionary<string, object> tokens = Map(Get(ParseJson(authJson), "tokens"));
                Dictionary<string, object> payload = TokenPayload(Text(tokens, "id_token"));
                Dictionary<string, object> claims = Map(Get(payload, "https://api.openai.com/auth"));
                string accountId = Text(tokens, "account_id"), claimAccount = Text(claims, "chatgpt_account_id");
                if (String.IsNullOrWhiteSpace(accountId) || !String.Equals(accountId, claimAccount, StringComparison.Ordinal) ||
                    String.IsNullOrWhiteSpace(livePlan) || Plan(Text(claims, "chatgpt_plan_type")) != Plan(livePlan)) return null;
                DateTime? until = Timestamp(Get(claims, "chatgpt_subscription_active_until"));
                DateTime? checkedAt = Timestamp(Get(claims, "chatgpt_subscription_last_checked"));
                DateTime utcNow = now.ToUniversalTime();
                // The JWT exp claim is an authentication expiry, never a subscription date.
                if (!until.HasValue || !checkedAt.HasValue || until.Value <= utcNow || checkedAt.Value > utcNow ||
                    utcNow - checkedAt.Value > TimeSpan.FromHours(24)) return null;
                return until.Value.ToLocalTime();
            }
            catch (Exception) { return null; }
        }

        internal static AccountSubscriptionInfo ParseAccountResponse(string json, string accountId, string livePlan, DateTime now)
        {
            try
            {
                Dictionary<string, object> accounts = Map(Get(ParseJson(json), "accounts"));
                Dictionary<string, object> details = Map(Get(accounts, accountId));
                Dictionary<string, object> account = Map(Get(details, "account"));
                if (String.IsNullOrWhiteSpace(accountId) || !String.Equals(Text(account, "account_id"), accountId, StringComparison.Ordinal) ||
                    String.IsNullOrWhiteSpace(livePlan) || Plan(Text(account, "plan_type")) != Plan(livePlan))
                    return Unavailable("현재 계정과 플랜에 일치하는 구독 정보를 받지 못했습니다.", now);
                Dictionary<string, object> entitlement = Map(Get(details, "entitlement"));
                Dictionary<string, object> subscription = Map(Get(details, "last_active_subscription"));
                object delinquent = Get(entitlement, "is_delinquent");
                if (delinquent is bool && (bool)delinquent)
                    return Unavailable("결제 상태 확인이 필요해 구독 날짜를 확정할 수 없습니다.", now);
                DateTime? renews = Timestamp(Get(entitlement, "renews_at"));
                DateTime? cancels = Timestamp(Get(entitlement, "cancels_at"));
                DateTime? expires = Timestamp(Get(entitlement, "expires_at"));
                object willRenewValue = Get(subscription, "will_renew");
                bool? willRenew = willRenewValue is bool ? (bool?)willRenewValue : null;
                DateTime? date = null; string kind = null;
                Dictionary<string, object> change = Map(Get(entitlement, "scheduled_plan_change"));
                DateTime? changeAt = Timestamp(Get(change, "changes_at"));
                string nextPlan = Text(change, "plan_type");
                if (changeAt.HasValue && changeAt.Value > now.ToUniversalTime() && !String.IsNullOrWhiteSpace(nextPlan) && Plan(nextPlan) != Plan(livePlan))
                { date = changeAt; kind = "change"; }
                else if (willRenew == true) { date = renews; kind = "renewal"; }
                else if (willRenew == false && (renews.HasValue || (!expires.HasValue && cancels.HasValue)))
                { date = renews ?? cancels; kind = "end"; }
                else if (expires.HasValue) { date = expires; kind = "end"; }
                // With unknown renewal status, only an explicit access-period end is safe.
                if (!date.HasValue)
                { date = Timestamp(Get(subscription, "active_until")); kind = "period"; }
                if (!date.HasValue || date.Value <= now.ToUniversalTime())
                    return Unavailable("응답에 현재 유효한 구독 갱신일이나 종료일이 없습니다.", now);
                return new AccountSubscriptionInfo { Date = date.Value.ToLocalTime(), Kind = kind, NextPlan = kind == "change" ? Plan(nextPlan) : null, CheckedAt = now.ToUniversalTime() };
            }
            catch (Exception) { return Unavailable("구독 정보 응답 형식을 확인하지 못했습니다.", now); }
        }

        internal static string ReadAuthJson(string profileRoot)
        {
            if (String.IsNullOrWhiteSpace(profileRoot)) return null;
            string path = Path.Combine(profileRoot, "auth.json");
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumJsonLength) return null;
            return File.ReadAllText(path);
        }
        internal static Dictionary<string, object> ParseJson(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength) return null;
            return new JavaScriptSerializer { MaxJsonLength = MaximumJsonLength, RecursionLimit = 32 }.DeserializeObject(json) as Dictionary<string, object>;
        }
        internal static Dictionary<string, object> TokenPayload(string token)
        {
            if (String.IsNullOrWhiteSpace(token) || token.Length > MaximumJsonLength) return null;
            string[] parts = token.Split('.'); if (parts.Length != 3) return null;
            string encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            return ParseJson(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded)));
        }
        internal static DateTime? Timestamp(object value)
        {
            string text = value as string; DateTimeOffset parsed;
            return text != null && text.Length <= 64 && text.IndexOf('T') > 0 &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
                ? (DateTime?)parsed.UtcDateTime : null;
        }
        internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        internal static object Get(Dictionary<string, object> value, string key)
        { object found; return value != null && key != null && value.TryGetValue(key, out found) ? found : null; }
        internal static string Text(Dictionary<string, object> value, string key) { return Get(value, key) as string; }
        internal static string Plan(string value) { return (value ?? "").Trim().ToLowerInvariant(); }
        internal static AccountSubscriptionInfo Unavailable(string error, DateTime now)
        { return new AccountSubscriptionInfo { Error = error, CheckedAt = now.ToUniversalTime() }; }
    }

    internal sealed class AccountSubscriptionResponse
    {
        public int StatusCode { get; set; }
        public string Body { get; set; }
        public string ContentType { get; set; }
        public string Failure { get; set; }
    }

    internal sealed class AccountSubscriptionReader
    {
        private sealed class Credentials
        {
            internal string AccountId, CacheKey, Token, AuthJson;
        }
        private readonly Func<string> _readAuth;
        private readonly Func<HttpWebRequest, Task<AccountSubscriptionResponse>> _transport;
        private readonly Func<DateTime> _utcNow;
        private readonly string _profileRoot;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private AccountSubscriptionInfo _cached;
        private DateTime _cacheUntil;
        private string _cacheKey;
        private const string AccountUrl = "https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27";

        public AccountSubscriptionReader(string profileRoot)
            : this(delegate { return AccountSubscription.ReadAuthJson(profileRoot); }, SendAsync, delegate { return DateTime.UtcNow; }) { _profileRoot = profileRoot; }

        internal AccountSubscriptionReader(Func<string> readAuth, Func<HttpWebRequest, Task<AccountSubscriptionResponse>> transport, Func<DateTime> utcNow)
        {
            if (readAuth == null || transport == null || utcNow == null) throw new ArgumentNullException();
            _readAuth = readAuth; _transport = transport; _utcNow = utcNow;
        }

        public async Task<AccountSubscriptionInfo> ReadAsync(string livePlan, string expectedEmail = null)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DateTime now = _utcNow().ToUniversalTime(); string authError;
                Credentials credentials = ReadCredentials(livePlan, expectedEmail, now, out authError);
                if (credentials == null) { _cached = null; _cacheKey = null; return AccountSubscription.Unavailable(authError, now); }
                if (!String.IsNullOrWhiteSpace(_profileRoot))
                {
                    WebSubscriptionRecord web = WebSubscriptionStore.Load(_profileRoot, credentials.AccountId, null);
                    if (web != null)
                    {
                        WebSubscriptionService.QueueRefresh(_profileRoot, credentials.AccountId, expectedEmail, livePlan);
                        AccountSubscriptionInfo fresh = AccountSubscription.Plan(web.Plan) == AccountSubscription.Plan(livePlan) ? WebSubscriptionStore.Fresh(web, now) : null;
                        if (fresh != null) return fresh;
                    }
                }
                if (_cached != null && _cacheKey == credentials.CacheKey && now < _cacheUntil &&
                    (!_cached.Date.HasValue || _cached.Date.Value.ToUniversalTime() > now)) return _cached.Copy();
                AccountSubscriptionInfo result; bool allowClaimFallback = true, usedClaim = false;
                try
                {
                    // The Framework executable may otherwise capture old TLS defaults at request creation.
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(AccountUrl);
                    request.Method = "GET"; request.AllowAutoRedirect = false; request.Timeout = 6000; request.ReadWriteTimeout = 6000;
                    request.Accept = "application/json"; request.UserAgent = "CodexUsageMeter/1.0";
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + credentials.Token;
                    request.Headers["ChatGPT-Account-Id"] = credentials.AccountId;
                    AccountSubscriptionResponse response = await _transport(request).ConfigureAwait(false);
                    now = _utcNow().ToUniversalTime();
                    allowClaimFallback = response == null || response.StatusCode != 200;
                    result = response != null && response.StatusCode == 200
                        ? AccountSubscription.ParseAccountResponse(response.Body, credentials.AccountId, livePlan, now)
                        : AccountSubscription.Unavailable(FailureMessage(response), now);
                }
                catch (Exception) { result = AccountSubscription.Unavailable("구독 정보 서버에 연결하지 못했습니다. 잠시 후 자동으로 다시 확인합니다.", now); }
                string latestError;
                Credentials latest = ReadCredentials(livePlan, expectedEmail, _utcNow().ToUniversalTime(), out latestError);
                if (latest == null || latest.CacheKey != credentials.CacheKey)
                {
                    _cached = null; _cacheKey = null;
                    return AccountSubscription.Unavailable("조회 중 연결 계정이나 인증 정보가 변경되었습니다. 다음 새로고침 때 다시 확인합니다.", _utcNow());
                }
                if (!result.Date.HasValue && allowClaimFallback)
                {
                    DateTime? period = AccountSubscription.ParseFreshClaim(credentials.AuthJson, livePlan, now);
                    if (period.HasValue)
                    {
                        usedClaim = true;
                        result = new AccountSubscriptionInfo { Date = period, Kind = "period", CheckedAt = now,
                            Error = result.Error + "\n최근 24시간 이내 확인된 이용기간입니다. 자동 갱신·취소 여부는 조회하지 못했습니다." };
                    }
                }
                _cached = result.Copy(); _cacheKey = credentials.CacheKey;
                _cacheUntil = now.AddMinutes(result.Date.HasValue ? 60 : 15);
                if (result.Date.HasValue && result.Date.Value.ToUniversalTime() < _cacheUntil) _cacheUntil = result.Date.Value.ToUniversalTime();
                // A local claim must be revalidated before its 24-hour freshness limit passes.
                if (usedClaim)
                {
                    Dictionary<string, object> tokens = AccountSubscription.Map(AccountSubscription.Get(AccountSubscription.ParseJson(credentials.AuthJson), "tokens"));
                    Dictionary<string, object> claims = AccountSubscription.Map(AccountSubscription.Get(AccountSubscription.TokenPayload(AccountSubscription.Text(tokens, "id_token")), "https://api.openai.com/auth"));
                    DateTime? checkedAt = AccountSubscription.Timestamp(AccountSubscription.Get(claims, "chatgpt_subscription_last_checked"));
                    if (checkedAt.HasValue && checkedAt.Value.AddHours(24) < _cacheUntil) _cacheUntil = checkedAt.Value.AddHours(24);
                }
                return result.Copy();
            }
            finally { _gate.Release(); }
        }

        private Credentials ReadCredentials(string livePlan, string expectedEmail, DateTime now, out string error)
        {
            error = "연결된 계정 인증 정보를 읽지 못해 구독 날짜를 조회할 수 없습니다.";
            try
            {
                if (String.IsNullOrWhiteSpace(livePlan)) { error = "현재 계정의 플랜을 확인하지 못했습니다."; return null; }
                if (String.IsNullOrWhiteSpace(expectedEmail)) { error = "현재 사용량 계정의 식별 정보를 확인하지 못해 구독 날짜를 표시하지 않았습니다."; return null; }
                string raw = _readAuth();
                Dictionary<string, object> tokens = AccountSubscription.Map(AccountSubscription.Get(AccountSubscription.ParseJson(raw), "tokens"));
                string account = AccountSubscription.Text(tokens, "account_id"), token = AccountSubscription.Text(tokens, "access_token");
                Dictionary<string, object> payload = AccountSubscription.TokenPayload(token);
                Dictionary<string, object> claims = AccountSubscription.Map(AccountSubscription.Get(payload, "https://api.openai.com/auth"));
                string claimedAccount = AccountSubscription.Text(claims, "chatgpt_account_id"), user = AccountSubscription.Text(claims, "chatgpt_user_id");
                if (String.IsNullOrWhiteSpace(account) || String.IsNullOrWhiteSpace(token) || String.IsNullOrWhiteSpace(user) ||
                    !String.Equals(account, claimedAccount, StringComparison.Ordinal)) return null;
                if (!String.IsNullOrWhiteSpace(expectedEmail))
                {
                    Dictionary<string, object> profile = AccountSubscription.Map(AccountSubscription.Get(payload, "https://api.openai.com/profile"));
                    string email = AccountSubscription.Text(profile, "email");
                    if (String.IsNullOrWhiteSpace(email)) email = AccountSubscription.Text(AccountSubscription.TokenPayload(AccountSubscription.Text(tokens, "id_token")), "email");
                    if (String.IsNullOrWhiteSpace(email) || !String.Equals(email.Trim(), expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
                    { error = "현재 사용량 계정과 구독 조회 계정이 달라 날짜를 표시하지 않았습니다."; return null; }
                }
                object expiry = AccountSubscription.Get(payload, "exp"); long seconds;
                if (expiry != null && Int64.TryParse(Convert.ToString(expiry, CultureInfo.InvariantCulture), out seconds) &&
                    seconds <= (now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds)
                { error = "구독 조회용 계정 인증이 만료되었습니다. 기존 사용량 연결이 갱신되면 다시 확인합니다."; return null; }
                string key;
                using (SHA256 hash = SHA256.Create())
                    key = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(account + "\n" + user + "\n" + AccountSubscription.Plan(livePlan) + "\n" +
                        (expectedEmail ?? "").Trim().ToLowerInvariant() + "\n" + raw)));
                return new Credentials { AccountId = account, Token = token, AuthJson = raw, CacheKey = key };
            }
            catch (Exception) { return null; }
        }

        private static string FailureMessage(AccountSubscriptionResponse response)
        {
            if (response == null || response.Failure == "network") return "구독 정보 서버에 연결하지 못했습니다. 잠시 후 자동으로 다시 확인합니다.";
            if (response.Failure == "timeout") return "구독 정보 조회 시간이 초과되었습니다. 잠시 후 자동으로 다시 확인합니다.";
            if (response.Failure == "size") return "구독 정보 응답이 너무 커서 확인을 중단했습니다.";
            if (response.StatusCode == 403)
            {
                string body = response.Body ?? "";
                if (body.IndexOf("challenge-platform", StringComparison.OrdinalIgnoreCase) >= 0 || body.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    body.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "구독 정보 요청이 서버 보안 확인에서 거절되었습니다(HTTP 403). 사용량 조회와 별도의 결제 정보 경로입니다.";
                return "현재 로그인으로 구독 정보 조회가 허용되지 않았습니다(HTTP 403).";
            }
            if (response.StatusCode == 401) return "구독 정보 조회용 인증을 확인하지 못했습니다(HTTP 401).";
            if (response.StatusCode == 429) return "구독 정보 조회 요청이 일시적으로 제한되었습니다. 잠시 후 자동으로 다시 확인합니다.";
            return "구독 정보 서버가 정상 응답하지 않았습니다(HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + ").";
        }

        private static async Task<AccountSubscriptionResponse> SendAsync(HttpWebRequest request)
        {
            Task<AccountSubscriptionResponse> operation = Task.Run(delegate { return Send(request); });
            if (await Task.WhenAny(operation, Task.Delay(6000)).ConfigureAwait(false) != operation)
            { request.Abort(); return new AccountSubscriptionResponse { Failure = "timeout" }; }
            return await operation.ConfigureAwait(false);
        }
        private static AccountSubscriptionResponse Send(HttpWebRequest request)
        {
            HttpWebResponse response = null;
            try
            {
                try { response = (HttpWebResponse)request.GetResponse(); }
                catch (WebException exception)
                {
                    response = exception.Response as HttpWebResponse;
                    if (response == null) return new AccountSubscriptionResponse { Failure = exception.Status == WebExceptionStatus.Timeout ? "timeout" : "network" };
                }
                using (response)
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    char[] buffer = new char[4096]; StringBuilder body = new StringBuilder(); int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (body.Length + count > AccountSubscription.MaximumJsonLength) return new AccountSubscriptionResponse { Failure = "size" };
                        body.Append(buffer, 0, count);
                    }
                    return new AccountSubscriptionResponse { StatusCode = (int)response.StatusCode, ContentType = response.ContentType, Body = body.ToString() };
                }
            }
            catch (Exception) { if (response != null) response.Close(); return new AccountSubscriptionResponse { Failure = "network" }; }
        }
    }
}
