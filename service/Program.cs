// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-4o-mini + ключ у gateway/.env — реальна модель.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";

// [W2] прайс за 1k токенів (in, out) — навчальні числа, пропорції реальні:
// strong ~16x дорожча за mini, вихідні ~4x дорожчі за вхідні.
// Прайс живе поруч із Route(): ціна моделі — вхідний параметр маршрутизації.
var prices = new Dictionary<string, (decimal In, decimal Out)>
{
    ["mock-mini"]   = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m,  0.01m),
};

// [W3] простий in-memory кеш відповідей + лічильники hit/miss.
// Межі: тільки успішні (200) відповіді без tool-виклику. TTL нема — до рестарту.
var cache = new ConcurrentDictionary<string, string>();
var stats = new Stats();

// [W4] черга підтверджень (HITL): id -> (дія, результат). Result == null => очікує.
// Чесний компроміс: у пам'яті процесу — рестарт втрачає заявки; в проді — таблиця в БД.
var approvals = new ConcurrentDictionary<string, Approval>();

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // [W4] guardrails: маскуємо PII ДО відправки в модель — і в ключ кешу теж,
    // щоб ні промпт, ні лог провайдера, ні кеш не тримали сирих email
    var userMessage = Ops.MaskPii(body.Message);

    // [W2] routing: ескалація -> сильна модель, решта -> дешева.
    // Політика: повернення, скарги і термінове йдуть на strong, бо помилка
    // «переплатили за просте» дешевша за «зекономили на скарзі».
    // Fallback-порядок (політика, механізм — W4): mock-strong -> mock-mini.
    var model = Ops.Route(userMessage, defaultModel); // guardrail стоїть ДО всіх розвилок

    // [W1] промпт беремо з реєстру — активну версію, а не хардкод
    var (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);

    // [W3] кеш: ключ = модель + ТЕКСТ промпта + повідомлення. Промпт у ключі =
    // автоматична інвалідація при promote/rollback версії.
    var cacheKey = $"{model}|{systemPrompt}|{userMessage}";
    if (cache.TryGetValue(cacheKey, out var cachedAnswer))
    {
        Interlocked.Increment(ref stats.CacheHits);
        var hitLatencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        // хіт видно в лозі: рядок є, токени/гроші нульові (нуль тут чесний — виклику не було)
        await LogRequest(dbConn, requestId, model, promptVersion, hitLatencyMs, 0, 0, 0m, 200);
        return Results.Json(new { request_id = requestId, content = cachedAnswer, tool = (string?)null, latency_ms = hitLatencyMs });
    }
    Interlocked.Increment(ref stats.CacheMisses);

    // [W4] fallback-ланцюг: пробуємо по черзі. На mock другий елемент — той самий
    // mock (межа стенда); з реальним ключем — інша модель/провайдер за політикою W2.
    var http = httpFactory.CreateClient();
    var chain = defaultModel == "mock" ? new[] { model, "mock" }
                                       : new[] { model, "azure-gpt-4o" };
    var answer = "";
    string? toolCall = null;
    int promptTokens = 0, completionTokens = 0, status = 0; // 0 = відповіді не було
    var ok = false;
    for (int i = 0; i < chain.Length && !ok; i++)
    {
        if (i > 0) Interlocked.Increment(ref stats.Fallbacks); // перехід має бути видимим
        var res = await CallGateway(http, gateway, chain[i], systemPrompt, userMessage);
        if (res.ok)
        {
            ok = true;
            model = chain[i]; // у лог їде модель, яка РЕАЛЬНО відповіла
            (answer, toolCall, promptTokens, completionTokens, status) =
                (res.answer, res.tool, res.pt, res.ct, res.status);
        }
        else status = res.status;
    }

    // [W5] «успішна, але непридатна» (L01): беззмістовна відповідь — збій формату,
    // а не відповідь. Не кешуємо, не показуємо як є; окремий чесний текст і статус,
    // щоб у метриках це не змішувалось ні з успіхом, ні з падінням провайдера.
    var unusable = ok && answer.Trim().Length < 5;
    if (unusable) { ok = false; status = 502; }

    if (ok && toolCall != null)
    {
        // [W3/W4] read-only виконуємо одразу; незворотну дію — тільки через approval.
        // Модель ніколи не тримає «палець на кнопці»: injection у найгіршому разі
        // породжує заявку, а не подію.
        // Timeout: бюджет інструмента ~3с < бюджету відповіді; вичерпання — оброблена
        // гілка. Ключ ідемпотентності: request_id (закриває лише власний ретрай;
        // від повтору моделі/користувача — hash(conversation, tool, args), поза core).
        if (toolCall == "create_ticket")
        {
            var approvalId = Guid.NewGuid().ToString("N")[..6];
            approvals[approvalId] = new Approval(toolCall, null);
            answer += $" (очікує підтвердження оператора, id={approvalId})";
        }
        else
        {
            var result = RunTool(toolCall);
            if (result != null) answer += $" ({result})";
        }
    }

    // [W5] слід для /providers: повне падіння ланцюга рахуємо поспіль, успіх скидає.
    // Непридатна відповідь — не падіння провайдера, лічильник не чіпає.
    if (ok) Interlocked.Exchange(ref stats.ConsecutiveFailures, 0);
    else if (!unusable) Interlocked.Increment(ref stats.ConsecutiveFailures);

    // [W4] graceful degradation: ввічливо назовні, чесний статус усередину.
    // status==0 (відповіді не було взагалі) — теж деградація, у лог їде 503.
    if (!ok)
    {
        answer = unusable
            ? "Не вдалося сформувати змістовну відповідь. Спробуйте, будь ласка, переформулювати запит."
            : "Вибачте, тимчасові проблеми на нашому боці. Спробуйте, будь ласка, трохи згодом.";
        if (!unusable) status = (status == 200 || status == 0) ? 503 : status;
    }

    // [W3/W4] у кеш — тільки успішна відповідь без tool-виклику; заглушка
    // деградації (народжена у fallback-гілці) не кешується ніколи
    if (ok && status == 200 && toolCall == null) cache[cacheKey] = answer;

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // [W2] cost: tokens * ціна моделі. Немає в прайсі — null (чесне «не знаємо»),
    // а не нуль (брехня «було безкоштовно»).
    decimal? costUsd = prices.TryGetValue(model, out var pr)
        ? Math.Round(promptTokens / 1000m * pr.In
                   + completionTokens / 1000m * pr.Out, 6)
        : null;

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі
// [W5] агрегати консолі: requests/p95/error rate — з БД (переживають рестарт),
// cache-hit і fallback — з лічильників у пам'яті (обнуляються з кожним ребілдом)
app.MapGet("/observability", async () =>
{
    long requests = 0; double p95 = 0, errorRate = 0;
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT count(*), " +
        "COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms), 0), " +
        "COALESCE(AVG(CASE WHEN status <> '200' THEN 1.0 ELSE 0 END) * 100, 0) " +
        "FROM requests WHERE created_at::date = CURRENT_DATE", db);
    await using var rd = await cmd.ExecuteReaderAsync();
    if (await rd.ReadAsync())
    {
        requests = rd.GetInt64(0);
        p95 = rd.GetDouble(1);
        errorRate = (double)rd.GetDecimal(2);
    }
    var total = stats.CacheHits + stats.CacheMisses;
    return Results.Json(new
    {
        p95_ms = (int)p95,
        requests,
        cache_hit_pct = total == 0 ? 0 : Math.Round(stats.CacheHits * 100.0 / total, 1),
        error_rate_pct = Math.Round(errorRate, 1),
        fallback_events = stats.Fallbacks,
    });
});
// [W2] вартість сьогодні + бюджет. Бюджет — поріг дії, а не звіт; поки константа.
// Політика на 80% (записано, механізм — опційно): алерт-подія в лог, без деградації.
app.MapGet("/cost", async () =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT COALESCE(SUM(cost_usd), 0) FROM requests WHERE created_at::date = CURRENT_DATE", db);
    var today = (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
    return Results.Json(new { today_usd = Math.Round(today, 4), budget_usd = 5.0 });
});
// [W1] реєстр промптів: список версій + активація (promote і rollback — одна дія)
app.MapGet("/prompts", async () =>
{
    var list = new List<object>();
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT name, version, active FROM prompts ORDER BY created_at", db);
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
        list.Add(new { name = rd.GetString(0), version = rd.GetString(1), active = rd.GetBoolean(2) });
    return Results.Json(list);
});

app.MapPost("/prompts/{version}/activate", async (string version) =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();

    // невідома версія — чесний 404, інакше одруківка деактивувала б усе
    await using var check = new NpgsqlCommand(
        "SELECT 1 FROM prompts WHERE name = 'support-system' AND version = @v", db);
    check.Parameters.AddWithValue("v", version);
    if (await check.ExecuteScalarAsync() is null)
        return Results.NotFound(new { error = $"unknown version '{version}'" });

    // атомарний promote/rollback: жодного моменту з двома активними або нульома
    await using var upd = new NpgsqlCommand(
        "UPDATE prompts SET active = (version = @v) WHERE name = 'support-system'", db);
    upd.Parameters.AddWithValue("v", version);
    await upd.ExecuteNonQueryAsync();
    return Results.Ok(new { active = version });
});
// [W5] статус провайдерів. Core-варіант без circuit breaker: темніємо до
// "degraded" після 3+ поспіль збоїв fallback-ланцюга (лічильник скидається успіхом)
app.MapGet("/providers", () => Results.Json(new
{
    providers = new[]
    {
        new { name = "mock", status = stats.ConsecutiveFailures >= 3 ? "degraded" : "ok" }
    }
}));
// [W4] черга HITL: незакриті заявки + виконані з результатом (спостережуваний слід)
app.MapGet("/approvals", () => Results.Json(new
{
    pending = approvals.Where(kv => kv.Value.Result == null)
                       .Select(kv => new { id = kv.Key, action = kv.Value.Action })
                       .ToArray(),
    done = approvals.Where(kv => kv.Value.Result != null)
                    .Select(kv => new { id = kv.Key, action = kv.Value.Action, result = kv.Value.Result })
                    .ToArray()
}));

app.MapPost("/approvals/{id}/approve", (string id) =>
{
    // атомарне захоплення заявки (check-then-act закритий): другий одночасний
    // approve не пройде TryUpdate — тікет один незалежно від таймінгів
    if (approvals.TryGetValue(id, out var a) && a.Result == null
        && approvals.TryUpdate(id, a with { Result = "__executing" }, a))
    {
        var result = RunTool(a.Action) ?? "виконано";
        approvals[id] = a with { Result = result };
        return Results.Ok(new { id, result });
    }
    // «вже виконано» і «ніколи не існувало» навмисно нерозрізненні для клієнта
    return Results.NotFound(new { error = "not found or already done" });
});

app.Run("http://0.0.0.0:8080");

// [W4] один виклик моделі через gateway; збій — значення, а не виняток.
// ok=false при статусі >= 400; status=0 — «відповіді не було взагалі».
static async Task<(bool ok, string answer, string? tool, int pt, int ct, int status)>
    CallGateway(HttpClient http, string gateway, string model, string system, string user)
{
    try
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });
        var response = await http.PostAsync(
            $"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var status = (int)response.StatusCode;
        if (status >= 400) return (false, "", null, 0, 0, status);

        var rawJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rawJson);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var answer = message.GetProperty("content").GetString() ?? "";

        string? tool = null;
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            tool = tools[0].GetProperty("function").GetProperty("name").GetString();
        }

        var usage = doc.RootElement.GetProperty("usage");
        var pt = usage.GetProperty("prompt_tokens").GetInt32();
        var ct = usage.GetProperty("completion_tokens").GetInt32();
        return (true, answer, tool, pt, ct, status);
    }
    catch { return (false, "", null, 0, 0, 0); } // мережа / gateway лежить
}

// [W3] мінімальний реєстр інструментів. lookup_order — read-only; create_ticket —
// незворотна дія, поки виконується автономно (чесна «дірка», закриється HITL на W4:
// незворотне -> заявка в /approvals -> виконання після підтвердження людиною).
static string? RunTool(string name) => name switch
{
    "lookup_order"  => "статус: оплачено, доставку призначено",
    "create_ticket" => "тікет #T-" + Guid.NewGuid().ToString("N")[..4],
    _ => null,
};

// [W1] активна версія промпта з реєстру. Порожній реєстр / мертва база — дефолт
// БЕЗ маркера "support": відповіді деградують помітно (fail-visible, не fail-pretty).
static async Task<(string Version, string Body)> GetActivePrompt(string conn)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT version, body FROM prompts WHERE active = true ORDER BY created_at DESC LIMIT 1", db);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (await rd.ReadAsync())
            return (rd.GetString(0), rd.GetString(1));
    }
    catch { /* впадемо на дефолт нижче */ }
    return ("none", "You are an assistant.");
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status) "
            + "VALUES (@id, @model, @pv, @lat, @pt, @ct, @cost, @status)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("pv", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* не валимо запит через лог */ }
}

record ChatIn(string Message);

// [W4] заявка HITL: Result == null — очікує рішення людини
record Approval(string Action, string? Result);

// Чисті функції control plane — винесені в public static class, щоб їх бачив
// юніт-тест (hw4 вимагає тест на MaskPii; Route — «найдешевші тести курсу» з L03)
public static class Ops
{
    // [W2] одна точка рішення про модель. З реальним ключем (MODEL != mock)
    // роутер чесно вироджується: другої реальної моделі в конфізі немає.
    public static string Route(string message, string def)
    {
        if (def != "mock") return def;
        var u = message.ToLowerInvariant();
        bool escalation = u.Contains("поверн") || u.Contains("терміново")
                       || u.Contains("refund") || u.Contains("скарг");
        return escalation ? "mock-strong" : "mock-mini";
    }

    // [W4] guardrail на межі: маскування email до відправки в модель.
    // Regex — перший крок; у проді — телефони/картки/PII-детектори.
    public static string MaskPii(string s) =>
        Regex.Replace(s, @"[\w.\-]+@[\w.\-]+", "[email]");
}

// [W3] лічильники кешу; поля публічні для Interlocked
class Stats
{
    public int CacheHits;
    public int CacheMisses;
    public int Fallbacks;           // [W4] кожен перехід далі за ланцюгом
    public int ConsecutiveFailures; // [W5] поспіль повних падінь ланцюга — для /providers
}
