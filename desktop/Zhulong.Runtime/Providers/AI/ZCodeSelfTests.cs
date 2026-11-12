using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

internal static class ZCodeSelfTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var fixture = Path.Combine(directory, "zcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var sourcePath = Path.Combine(fixture, "source.db");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Pooling = false }.ToString());
        source.Open();
        using var cmd = source.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT);
            INSERT INTO session VALUES ('s','p');
            CREATE TABLE model_usage (id TEXT PRIMARY KEY,session_id TEXT,model_id TEXT,status TEXT,completed_at INTEGER,
                input_tokens INTEGER,output_tokens INTEGER,cache_read_input_tokens INTEGER,cache_creation_input_tokens INTEGER,reasoning_tokens INTEGER);
            INSERT INTO model_usage VALUES
                ('old','s','GLM-5.3','completed',$now,20,2,0,0,0),
                ('else','s','Codex','completed',$now,200,2,0,0,0),
                ('active','s','GLM-5.3-Flash','running',NULL,0,0,0,0,0),
                ('latest','s','GLM-5.3','completed',$now,100,10,50,20,5),
                ('bad','s','GLM-5.3','completed',$now,10,1,5,6,0);
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        Check(File.Exists(sourcePath + "-wal"), "ZCode 夹具须包含未合并的 WAL");

        using var store = new UsageStore(Path.Combine(fixture, "accountless.db"));
        var scanner = new ZCodeScanner(sourcePath);
        var first = scanner.ScanBatch(store, 2, CancellationToken.None);
        Check(first.EventsAdded == 1 && first.HasMore, "ZCode 应先读取最近的 WAL 并跳过不一致的计数");
        using (var query = store.Command("SELECT input_tokens,cached_input_tokens,cache_write_tokens,output_tokens,reasoning_tokens,total_tokens,parent_session_id FROM usage_records"))
        using (var read = query.ExecuteReader())
            Check(read.Read() && read.GetInt64(0) == 100 && read.GetInt64(1) == 50 && read.GetInt64(2) == 20 &&
                read.GetInt64(3) == 10 && read.GetInt64(4) == 5 && read.GetInt64(5) == 110 && read.GetString(6) == "p",
                "缓存与推理是子集，ZCode 会话须保留父子关联");

        cmd.CommandText = "INSERT INTO model_usage VALUES ('new','s','GLM-5.3','completed',$now,7,1,0,0,0);";
        cmd.ExecuteNonQuery();
        scanner = new ZCodeScanner(sourcePath);
        Check(scanner.ScanBatch(store, 2, CancellationToken.None).EventsAdded == 1, "历史回填期间也应优先统计新增请求");
        cmd.CommandText = "UPDATE model_usage SET status='completed', completed_at=$now, input_tokens=30, output_tokens=3 WHERE id='active';";
        cmd.ExecuteNonQuery();
        Check(scanner.ScanBatch(store, 2, CancellationToken.None).EventsAdded == 2, "须补读原地转为完成的请求并恢复历史回填");
        Check(scanner.ScanBatch(store, 2, CancellationToken.None).EventsAdded == 0 && Scalar("SELECT SUM(total_tokens) FROM usage_records") == 173,
            "重复扫描不得叠加且非 GLM 不计入本来源");

        cmd.CommandText = "INSERT INTO model_usage VALUES ('retry','s','GLM-5.3','completed',$now,9,1,0,0,0),('error','s','GLM-5.3','error',$now,10,1,0,0,0);";
        cmd.ExecuteNonQuery();
        using (var trigger = store.Command("CREATE TRIGGER zcode_retry BEFORE INSERT ON usage_records BEGIN SELECT RAISE(ABORT,'fixture'); END"))
            trigger.ExecuteNonQuery();
        var rolledBack = false;
        try { scanner.ScanBatch(store, 100, CancellationToken.None); }
        catch (SqliteException) { rolledBack = true; }
        Check(rolledBack && Scalar("SELECT COUNT(*) FROM usage_records") == 4, "写入失败必须回滚记录与游标");
        using (var drop = store.Command("DROP TRIGGER zcode_retry")) drop.ExecuteNonQuery();
        Check(scanner.ScanBatch(store, 100, CancellationToken.None).EventsAdded == 1 && Scalar("SELECT SUM(total_tokens) FROM usage_records") == 183,
            "重试须补齐失败批次，失败请求没有确认的用量不进入账本");
        cmd.CommandText = "DELETE FROM model_usage WHERE id='error';";
        cmd.ExecuteNonQuery();
        Check(scanner.ScanBatch(store, 100, CancellationToken.None).EventsAdded == 0 && Scalar("SELECT COUNT(*) FROM usage_records") == 5,
            "来源重写尾部时须重新识别记录且不重复入账");

        var reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
        var quotaJson = $$$"""
            {"code":200,"success":true,"data":{"subscriptionStatus":"glm_coding_plan_pro","limits":[
                {"type":"CREDIT_LIMIT","unit":6,"number":1,"usage":60000,"currentValue":7200,"nextResetTime":{{{reset}}}},
                {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":44},
                {"type":"CREDIT_LIMIT","unit":3,"number":5,"usage":12000,"remaining":9000,"nextResetTime":{{{reset}}}},
                {"type":"TIME_LIMIT","unit":5,"number":1,"usage":400,"currentValue":8},
                {"type":"CREDIT_LIMIT","unit":9,"number":1,"percentage":7}
            ]}}
            """;
        using var document = JsonDocument.Parse(quotaJson);
        var quota = ZCodeAccountClient.ParseReading(document.RootElement);
        Check(quota is { State: "ok", Identity: null, PlanType: "glm_coding_plan_pro", AccountTokens: null } && quota.Windows.Count == 3,
            "套餐快照只采用明确窗口，且不伪造账号身份或账户 token 总数");
        Check(quota.Windows[0] is { Name: "limit5h", UsedPercent: 25, TotalUnits: 12000, UsedUnits: 3000, Unit: "credits" } &&
            quota.Windows[0].ResetsAt?.ToUnixTimeMilliseconds() == reset, "5小时积分窗口应优先于过渡期旧 token 窗口并保留源重置时间");
        Check(quota.Windows[1] is { Name: "limit7d", UsedPercent: 12, WindowMinutes: 10080 } &&
            quota.Windows[2] is { Name: "monthMcp", Unit: "requests", ResetsAt: null }, "周积分与月度 MCP 次数须分开，缺失重置仍未知");

        using var fake = new FakeHttp(quotaJson);
        using var http = new HttpClient(fake, disposeHandler: false);
        var domestic = await ZCodeAccountClient.ReadAsync(http, "fixture-cn", true, CancellationToken.None).ConfigureAwait(false);
        Check(domestic.State == "ok" && fake.Requests[^1] == ("open.bigmodel.cn", "fixture-cn"), "国内只读接口须用固定域和官方原始凭据头");
        var international = await ZCodeAccountClient.ReadAsync(http, "fixture-global", false, CancellationToken.None).ConfigureAwait(false);
        Check(international.State == "ok" && fake.Requests[^1] == ("api.z.ai", "fixture-global"), "国际套餐须使用独立凭据与固定域");
        fake.Status = HttpStatusCode.Forbidden;
        Check((await ZCodeAccountClient.ReadAsync(http, "fixture-global", false, CancellationToken.None).ConfigureAwait(false)).State == "auth_rejected",
            "认证失败应显式返回状态");
        fake.Status = HttpStatusCode.TooManyRequests;
        Check((await ZCodeAccountClient.ReadAsync(http, "fixture-global", false, CancellationToken.None).ConfigureAwait(false)).State == "rate_limited",
            "限流响应应显式返回状态");
        return assertions;

        long Scalar(string sql)
        {
            using var command = store.Command(sql);
            return (long)command.ExecuteScalar()!;
        }
    }

    sealed class FakeHttp(string body) : HttpMessageHandler
    {
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal readonly List<(string Host, string Auth)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/api/monitor/usage/quota/limit")
                throw new InvalidOperationException("GLM 查询不得发起有副作用的请求");
            Requests.Add((request.RequestUri.Host, request.Headers.GetValues("Authorization").Single()));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body) });
        }
    }
}
