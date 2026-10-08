using System.Text.Json.Nodes;
using System.Globalization;

namespace CodexUsageMeter.WindowsWidget;

internal static class WidgetContent
{
    internal static JsonObject Build(string? snapshotJson, string size, DateTimeOffset now)
    {
        bool small = size.Equals("Small", StringComparison.OrdinalIgnoreCase);
        bool large = size.Equals("Large", StringComparison.OrdinalIgnoreCase);
        var body = new JsonArray();
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard", ["version"] = "1.5",
            ["body"] = body,
            ["actions"] = new JsonArray
            {
                new JsonObject { ["type"]="Action.Execute", ["title"]="열기", ["verb"]="open" },
                new JsonObject { ["type"]="Action.Execute", ["title"]="새로고침", ["verb"]="refresh" }
            }
        };

        JsonObject? snapshot = null;
        string? error = null;
        if (string.IsNullOrWhiteSpace(snapshotJson)) error = "미터기를 열어 계정을 연결해 주세요.";
        else
        {
            try
            {
                snapshot = JsonNode.Parse(snapshotJson) as JsonObject;
                if (snapshot?["schemaVersion"]?.GetValue<int>() != 1 || snapshot["accounts"] is not JsonArray)
                    error = "사용량 자료를 읽을 수 없습니다. 미터기를 업데이트해 주세요.";
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            { error = "사용량 자료를 읽을 수 없습니다. 미터기에서 새로고침해 주세요."; }
        }
        if (error is not null)
        {
            var message=Text(error, "Small", true);
            message["maxLines"]=small?2:3;
            body.Add(message);
            return card;
        }

        bool running = Bool(snapshot!["running"]);
        DateTimeOffset? written = Date(snapshot["writtenAtUtc"]);
        bool snapshotStale = Stale(written, now);
        string heading = !running ? "미터기 꺼짐 · 마지막 기록" : snapshotStale ? "오래됨 · 마지막 기록" : "남은 사용량";
        if(!small) body.Add(Text(heading, "Small", false, subtle: true));

        var accounts = snapshot["accounts"]!.AsArray().OfType<JsonObject>()
            .Where(a => Number(a["number"]) is >= 1 and <= 4)
            .GroupBy(a => Number(a["number"])).Select(g => g.First()).OrderBy(a => Number(a["number"])).Take(4).ToArray();
        if (accounts.Length == 0) body.Add(Text("미터기를 열어 계정을 연결해 주세요.", "Default", true));
        JsonArray? compactColumns=null;
        foreach (var account in accounts)
        {
            int number = (int)Number(account["number"]);
            // Account numbers are stable and contain no account identity, markdown or links.
            string label = "계정 " + number.ToString(CultureInfo.InvariantCulture);
            string status = String(account["status"]);
            string statusText = status switch { "error" => "조회 실패", "unlinked" => "연결 안 됨", "waiting" => "조회 중", "ok" => "", _ => "확인 대기" };
            bool stale = status == "ok" && (snapshotStale || Stale(Date(account["observedAtUtc"]), now));
            bool resetPending=status=="ok" && new[]{"primary","secondary"}.Any(key=>account[key] is JsonObject limit && Date(limit["resetsAtUtc"]) is DateTimeOffset reset && reset<=now);
            string primary = Limit(account["primary"] as JsonObject);
            string secondary = Limit(account["secondary"] as JsonObject);
            if (small)
            {
                if(compactColumns is null || compactColumns.Count==2)
                {
                    compactColumns=new JsonArray();
                    body.Add(new JsonObject{["type"]="ColumnSet",["spacing"]="None",["columns"]=compactColumns});
                }
                string usage = !running?"꺼짐":status!="ok"?statusText:resetPending?"갱신 대기":stale?"오래됨":CompactLimits(account);
                var item=Text(label+" "+usage,"Small",false);
                item["spacing"]="None";
                compactColumns.Add(new JsonObject{["type"]="Column",["width"]="stretch",["spacing"]="Small",["items"]=new JsonArray(item)});
                continue;
            }
            var lines = new JsonArray();
            string plan = SafePlan(String(account["plan"]));
            lines.Add(Text(label + (plan.Length > 0 ? " · " + plan : "") + (statusText.Length > 0 ? " · " + statusText : "") + (stale ? " · 오래됨" : ""), "Small", false, weight: "Bolder"));
            if (status == "ok")
            {
                lines.Add(Text(resetPending?"초기화됨 · 갱신 대기":primary + "  |  " + secondary, "Small", false));
                if (large)
                {
                    string resets = ResetSummary(account, now);
                    if (resets.Length > 0) lines.Add(Text(resets, "Small", false, subtle:true));
                    if(Date(account["observedAtUtc"]) is DateTimeOffset observed)
                        lines.Add(Text("관측 "+observed.ToLocalTime().ToString("M/d HH:mm",CultureInfo.InvariantCulture),"Small",false,subtle:true));
                }
            }
            foreach(var line in lines.Skip(1))line!["spacing"]="None";
            body.Add(new JsonObject { ["type"]="Container",["spacing"]="Small",["separator"]=false,["items"]=lines });
        }
        return card;
    }

    private static JsonObject Text(string value,string size,bool wrap,bool subtle=false,string weight="Default") => new()
    {
        ["type"]="TextBlock",["text"]=value,["size"]=size,["weight"]=weight,["wrap"]=wrap,
        ["maxLines"]=wrap?3:1,["spacing"]="Small",["isSubtle"]=subtle
    };
    private static string CompactLimits(JsonObject account)
    {
        string Percent(JsonNode? node,string defaultLabel)
        {
            double value=node is JsonObject limit?Number(limit["remainingPercent"],double.NaN):double.NaN;
            string label=node is JsonObject data?Duration(Number(data["durationMinutes"])).Replace("시간","h").Replace("주간","주"):defaultLabel;
            return label+(double.IsFinite(value) && value>=0 && value<=100?Math.Round(value,0,MidpointRounding.AwayFromZero).ToString("0",CultureInfo.InvariantCulture)+"%":"—");
        }
        return Percent(account["primary"],"5h")+" "+Percent(account["secondary"],"주");
    }
    private static string Limit(JsonObject? limit)
    {
        if(limit is null) return "미제공";
        double value=Number(limit["remainingPercent"],double.NaN);
        string label=Duration(Number(limit["durationMinutes"]));
        if(!double.IsFinite(value) || value<0 || value>100) return label+" 미제공";
        return label+" "+Math.Round(value,0,MidpointRounding.AwayFromZero).ToString("0",CultureInfo.InvariantCulture)+"%";
    }
    private static string Duration(double minutes) => minutes switch { 300=>"5시간",10080=>"주간", >0 when minutes%1440==0=>((int)minutes/1440)+"일", >0 when minutes%60==0=>((int)minutes/60)+"시간", >0=>((int)minutes)+"분", _=>"한도" };
    private static string ResetSummary(JsonObject account, DateTimeOffset now)
    {
        var parts=new List<string>();
        foreach(var key in new[]{"primary","secondary"})
        {
            if(account[key] is not JsonObject limit || Date(limit["resetsAtUtc"]) is not DateTimeOffset reset)continue;
            string remaining = reset<=now?"갱신 대기":reset-now<TimeSpan.FromHours(24)?$"{Math.Ceiling((reset-now).TotalMinutes):0}분 후":$"{Math.Ceiling((reset-now).TotalDays):0}일 후";
            parts.Add(Duration(Number(limit["durationMinutes"]))+" "+remaining);
        }
        return parts.Count==0?"초기화 시각 미제공":"초기화 · "+string.Join(" / ",parts);
    }
    private static bool Stale(DateTimeOffset? value,DateTimeOffset now) => !value.HasValue || now-value.Value>TimeSpan.FromMinutes(3) || value.Value-now>TimeSpan.FromMinutes(1);
    private static DateTimeOffset? Date(JsonNode? node) => DateTimeOffset.TryParse(String(node),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)?date:null;
    private static string String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)?text:"";
    private static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static double Number(JsonNode? node,double fallback=0) => node is JsonValue value && value.TryGetValue<double>(out var number)?number:fallback;
    private static string SafePlan(string plan) => plan.ToLowerInvariant() switch {"free"=>"Free","plus"=>"Plus","pro"=>"Pro","prolite"=>"Pro Lite","business"=>"Business","team"=>"Team","enterprise"=>"Enterprise","edu"=>"Edu",_=>""};
}
