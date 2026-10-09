using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageMeter.WindowsWidget;

internal static class SelfTests
{
    internal static readonly DateTimeOffset TestNow = DateTimeOffset.Parse("2026-10-09T03:00:00Z");
    internal static string SampleJson => """
        {"schemaVersion":1,"writtenAtUtc":"2026-10-09T02:59:50Z","running":true,"accounts":[
          {"number":1,"label":"계정 1","plan":"Plus","status":"ok","observedAtUtc":"2026-10-09T02:59:50Z","primary":{"remainingPercent":72.5,"resetsAtUtc":"2026-10-09T06:30:00Z","durationMinutes":300},"secondary":{"remainingPercent":41,"resetsAtUtc":"2026-10-13T12:00:00Z","durationMinutes":10080}},
          {"number":2,"label":"계정 2","plan":"Plus","status":"error","observedAtUtc":null,"primary":null,"secondary":null},
          {"number":3,"label":"계정 3","plan":"","status":"unlinked","observedAtUtc":null,"primary":null,"secondary":null},
          {"number":4,"label":"계정 4","plan":"Pro","status":"ok","observedAtUtc":"2026-10-09T02:59:50Z","primary":null,"secondary":{"remainingPercent":0,"resetsAtUtc":null,"durationMinutes":10080}}
        ]}
        """;

    internal static int Run(string? outputPath)
    {
        var checks = new List<object>();
        int failed = 0;
        void Check(string name, Action test)
        {
            try { test(); checks.Add(new { name, passed = true }); }
            catch (Exception error) { failed++; checks.Add(new { name, passed = false, error = error.Message }); }
        }
        void Require(bool condition, string failure) { if (!condition) throw new Exception(failure); }
        string Render(string json, string size = "Medium") => WidgetContent.Build(json, size, TestNow).ToJsonString();
        string Text(string json, string size = "Medium") => string.Join("\n", CollectTexts(WidgetContent.Build(json, size, TestNow)));

        Check("known usage is rounded and not replaced by zero", () => Require(Text(SampleJson).Contains("73%"), "72.5 percent must be displayed as 73%"));
        Check("server lowercase plan names have canonical labels", () => { Require(Text(SampleJson.Replace("Plus", "plus").Replace("Pro", "prolite")).Contains("Plus") && Text(SampleJson.Replace("Pro", "prolite")).Contains("Pro Lite"), "Real server plan names were omitted"); });
        Check("small card has two compact rows for four accounts", () => { var body=WidgetContent.Build(SampleJson,"Small",TestNow)["body"]!.AsArray();Require(body.Count==2 && body.All(x=>x?["type"]?.GetValue<string>()=="ColumnSet"),"Small card exceeds compact two-row layout"); });
        Check("small stale and offline states stay explicit", () => { Require(Text(SampleJson.Replace("02:59:50Z", "02:56:59Z"),"Small").Contains("오래됨"),"Small stale flag missing");Require(Text(SampleJson.Replace("\"running\":true", "\"running\":false"),"Small").Contains("꺼짐"),"Small offline flag missing"); });
        Check("reset boundary waits for a new observation in every size", () => { foreach(string size in new[]{"Small","Medium","Large"}){var text=Text(SampleJson.Replace("06:30:00Z","03:00:00Z"),size);Require(text.Contains("갱신 대기") && !text.Contains("73%"),"Expired limit shown current in "+size);} });
        Check("large card timestamp is the actual observation", () => { var sample=SampleJson.Replace("02:59:50Z","02:58:00Z").Replace("\"writtenAtUtc\":\"2026-10-09T02:58:00Z\"","\"writtenAtUtc\":\"2026-10-09T03:00:00Z\"");string expected=DateTimeOffset.Parse("2026-10-09T02:58:00Z").ToLocalTime().ToString("M/d HH:mm",System.Globalization.CultureInfo.InvariantCulture);Require(Text(sample,"Large").Contains("관측 "+expected),"Snapshot write time was used as an observation"); });
        Check("failed child exit is visible but a resident process is successful",()=>{Require(WidgetFiles.LaunchNotice(1) is not null && WidgetFiles.LaunchNotice(0) is null && WidgetFiles.LaunchNotice(null) is null,"Child outcome was not classified correctly");});
        Check("actual zero remains a known zero", () => Require(Text(SampleJson).Contains("0%"), "A measured zero must be present"));
        Check("failure and unlinked accounts remain distinct", () => { var s = Text(SampleJson); Require(s.Contains("조회 실패") && s.Contains("연결 안 됨"), s); });
        Check("single limit labels missing primary as unavailable", () => Require(Text(SampleJson, "Large").Contains("미제공"), "Missing primary must not look like 0%"));
        Check("missing snapshot has setup guidance and no numeric usage", () => { var s=Text(""); Require(s.Contains("미터기") && !s.Contains("0%"), s); });
        Check("corrupt snapshot is visibly unavailable", () => { var s=Text("{ broken"); Require(s.Contains("읽을 수 없") && !s.Contains("0%"), s); });
        Check("unsupported schema is rejected", () => Require(!Text(SampleJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")).Contains("73%"), "Unsupported schema leaked data"));
        Check("observations older than three minutes are marked", () => Require(Text(SampleJson.Replace("02:59:50Z", "02:56:59Z")).Contains("오래됨"), "Stale observation not marked"));
        Check("exact three minute boundary is current", () => Require(!Text(SampleJson.Replace("02:59:50Z", "02:57:00Z")).Contains("오래됨"), "Boundary incorrectly stale"));
        Check("offline meter is visible even with fresh data", () => Require(Text(SampleJson.Replace("\"running\":true", "\"running\":false")).Contains("꺼짐"), "Offline state missing"));
        Check("unknown observation time is not current", () => Require(Text(SampleJson.Replace("\"observedAtUtc\":\"2026-10-09T02:59:50Z\"", "\"observedAtUtc\":null")).Contains("오래됨"), "Unknown timestamp marked current"));
        Check("invalid percentages are not clamped into measured zero", () => { var s=Text(SampleJson.Replace("72.5", "-7").Replace("\"remainingPercent\":0", "\"remainingPercent\":107")); Require(!s.Contains("73%") && s.Contains("미제공"), s); });
        Check("error does not show stale percent as a fresh reading", () => { var s=Text(SampleJson.Replace("\"status\":\"ok\"", "\"status\":\"error\"")); Require(!s.Contains("73%") && s.Contains("조회 실패"), s); });
        Check("small medium large cards contain all four account labels", () => { foreach (string size in new[] { "Small", "Medium", "Large" }) { var s=Text(SampleJson,size); for(int n=1;n<=4;n++) Require(s.Contains("계정 "+n), size+" missing account "+n); } });
        Check("actions are executable verbs with no external url", () => { var card=WidgetContent.Build(SampleJson,"Medium",TestNow); var actions=card["actions"]!.AsArray(); Require(actions.Count==2,"Expected open and refresh"); Require(actions.Any(a=>a!["verb"]?.GetValue<string>()=="open") && actions.Any(a=>a!["verb"]?.GetValue<string>()=="refresh"),"Missing actions"); Require(actions.All(a=>a!["type"]?.GetValue<string>()=="Action.Execute"),"Wrong action type"); Require(!Render(SampleJson).Contains("Action.OpenUrl"),"External action leaked"); });
        Check("card JSON is valid adaptive card 1.5", () => { foreach (string size in new[]{"Small","Medium","Large"}) { var c=WidgetContent.Build(SampleJson,size,TestNow); Require(c["type"]?.GetValue<string>()=="AdaptiveCard" && c["version"]?.GetValue<string>()=="1.5","Invalid card schema"); JsonDocument.Parse(c.ToJsonString()); } });
        Check("account label control characters and markdown cannot inject card content", () => { var s=Render(SampleJson.Replace("계정 1", "[evil](https://invalid.example)")); Require(!s.Contains("https://invalid.example"),"Untrusted label must not become markdown link"); });
        Check("unexpected account numbers and duplicates do not exceed four rows", () => { var doc=JsonNode.Parse(SampleJson)!;var accounts=doc["accounts"]!.AsArray();accounts.Add(accounts[0]!.DeepClone());var extra=accounts[0]!.DeepClone();extra["number"]=5;extra["label"]="계정 5";accounts.Add(extra);var s=Text(doc.ToJsonString());Require(!s.Contains("계정 5") && s.Split("계정 1").Length==2,"Unexpected or duplicate account was displayed"); });
        Check("open action retains the exact executable path without a shell", () => { var start=WidgetFiles.CreateStartInfo(@"C:\fixture with spaces\a&b\CodexUsageMeter.exe","open");Require(start.FileName==@"C:\fixture with spaces\a&b\CodexUsageMeter.exe" && start.Arguments=="" && !start.UseShellExecute,"Open command was changed or interpreted by shell"); });
        Check("rendered compact dashboard replaces text rows in every size and pages", () => {
            var sample=JsonNode.Parse(SampleJson)!;
            var views=new JsonObject();
            foreach(string size in new[]{"Small","Medium","Large"})
            {
                var pages=new JsonArray();
                for(int page=0;page<2;page++) pages.Add(new JsonObject {
                    ["image"]="data:image/png;base64,"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(size+page)),
                    ["alt"]="계정 "+(page==0?"1 · 계정 2":"3 · 계정 4")
                });
                views[size]=pages;
            }
            sample["widgetPages"]=views;
            foreach(string size in new[]{"Small","Medium","Large"})
            {
                var card=WidgetContent.Build(sample.ToJsonString(),size,TestNow,1);
                var image=card["body"]!.AsArray().OfType<JsonObject>().Single(item=>item["type"]?.GetValue<string>()=="Image");
                Require(image["url"]!.GetValue<string>()==views[size]![1]!["image"]!.GetValue<string>() &&
                    image["altText"]!.GetValue<string>().Contains("계정 4"),"Wrong dashboard size/page");
                Require(image["selectAction"]?["verb"]?.GetValue<string>()=="open" &&
                    card["actions"]!.AsArray().Any(action=>action?["verb"]?.GetValue<string>()=="page"),"Missing open/page action");
                Require(!CollectTexts(card).Any(text=>text.Contains("73%")),"Legacy text rows duplicate the dashboard");
            }
            Require(string.Join(" ",CollectTexts(WidgetContent.Build(sample.ToJsonString().Replace("\"running\":true","\"running\":false"),"Small",TestNow))).Contains("꺼짐"),"Image hides offline state");
            Require(string.Join(" ",CollectTexts(WidgetContent.Build(sample.ToJsonString().Replace("02:59:50Z","02:56:59Z"),"Medium",TestNow))).Contains("오래됨"),"Image hides stale state");
            Require(string.Join(" ",CollectTexts(WidgetContent.Build(sample.ToJsonString().Replace("06:30:00Z","03:00:00Z"),"Large",TestNow))).Contains("갱신 대기"),"Image hides reset state");
            views["Medium"]![0]!["image"]="https://invalid.example/private.png";
            var invalid=WidgetContent.Build(sample.ToJsonString(),"Medium",TestNow);
            Require(!invalid.ToJsonString().Contains("invalid.example") && CollectTexts(invalid).Any(text=>text.Contains("읽을 수 없")),"Widget loaded an external image");
        });
        Check("refresh action only supplies the background request argument", () => Require(WidgetFiles.CreateStartInfo(@"C:\fixture\CodexUsageMeter.exe","refresh").Arguments=="--widget-refresh","Refresh must not open a normal app window"));
        Check("unknown actions and non-executable path contents are refused", () => { foreach(var value in new[]{"relative.exe",@"C:\fixture\meter.cmd","C:\\fixture\\meter.exe\nC:\\bad.exe"}){bool rejected=false;try{WidgetFiles.CreateStartInfo(value,"open");}catch(ArgumentException){rejected=true;}Require(rejected,"Invalid executable accepted");}bool badVerb=false;try{WidgetFiles.CreateStartInfo(@"C:\fixture\meter.exe","run-shell");}catch(ArgumentException){badVerb=true;}Require(badVerb,"Unknown verb accepted"); });

        var result=new { passed=failed==0, total=checks.Count, failed, checks };
        string report=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
        if (outputPath is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);File.WriteAllText(outputPath,report); }
        Console.WriteLine($"Widget content tests: {checks.Count-failed}/{checks.Count} passed");
        if(failed>0) Console.WriteLine(report);
        return failed==0?0:1;
    }

    internal static IEnumerable<string> CollectTexts(JsonNode? node)
    {
        if(node is JsonObject obj) { if(obj["text"] is JsonValue text && text.TryGetValue<string>(out var value))yield return value; foreach(var pair in obj) foreach(var part in CollectTexts(pair.Value))yield return part; }
        else if(node is JsonArray array) foreach(var item in array) foreach(var part in CollectTexts(item))yield return part;
    }
}
