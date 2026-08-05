// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-4o-mini + ключ у gateway/.env — реальна модель.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";

// [W1] промпт у SupportGW поки один; ім'я в одному місці, щоб SQL не розповзався
const string PromptName = "support-system";

// [W2] прайс за 1k токенів (in, out) — навчальні числа, пропорції реальні:
// strong дорожча за mini ~16x, вихідні дорожчі за вхідні ~4x.
// Лежить поруч із Route(): ціна моделі — вхідний параметр маршрутизації.
var prices = new Dictionary<string, (decimal In, decimal Out)>
{
    ["mock"] = (0.00015m, 0.0006m),
    ["mock-mini"] = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m, 0.01m),
};

// [W2] денний бюджет: цифра без порога — просто число; пара "витрачено/ліміт" — стан.
const decimal BudgetUsd = 5.0m;

// [W3] in-memory кеш відповідей + лічильники поруч (measure-first).
// Свідомі межі: без стелі розміру і TTL — записи живуть до рестарту; два одночасні
// miss'и на один ключ обидва підуть у модель (thundering herd) — на нашому масштабі
// лік дорожчий за хворобу. Наступний щабель — Redis (profile advanced).
var cache = new ConcurrentDictionary<string, string>();
var stats = new Stats();

// [W4 опційно] circuit breaker на КОЖНУ модель окремо: мертва mock-strong не має
// карати живу mock-mini. Стан у пам'яті процесу — тут це не дефект: кожен інстанс
// сам спостерігає власні збої (один "отруєний" не відкриває breaker усім).
var breakers = new ConcurrentDictionary<string, Breaker>();

// [W4] черга підтверджень (HITL). Result == null => заявка очікує рішення.
// ЧЕСНА МЕЖА: словник у пам'яті — рестарт губить незакриті заявки, а при двох
// інстансах оператор бачить лише "свої". Доросла форма — таблиця в тій самій базі.
var approvals = new ConcurrentDictionary<string, Approval>();

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // [W4] guardrail на межі — ДО того, як текст стане частиною промпта чи ключа кешу.
    // Межа — єдине місце, яке бачить усі запити незалежно від моделей і fallback-гілок.
    var userMessage = MaskPii(body.Message);

    // [W4] вхідний детект injection (hw4 опційне): mock ловить лише канонічну
    // англійську форму (`ignore`+`instruction`), тому перефразовані атаки з трьох
    // родин L08 проходять наскрізь — детект мусить жити ТУТ, на межі. Це прохання
    // переписати правила гри: відмовляємо механічно, не питаючи модель.
    if (LooksLikeInjection(userMessage))
    {
        Interlocked.Increment(ref stats.Blocked);
        await LogRequest(dbConn, requestId, "guardrail", "none",
            (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            0, 0, 0m, 200, false, false, null, "injection");
        return Results.Json(new { request_id = requestId,
            content = "Вибачте, не можу виконати це прохання.",
            tool = (string?)null,
            latency_ms = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds });
    }

    // [W2] routing: одна точка рішення про модель.
    var model = Route(userMessage, defaultModel);

    // [W1] промпт — з реєстру, активна версія. Читаємо на КОЖЕН запит:
    // інакше activate не діє до рестарту (L02 блок 06, "Типова помилка").
    var (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);

    // [W3] ключ кешу = все, що впливає на відповідь: модель + ТЕКСТ активного промпта
    // + повідомлення. Промпт у ключі = автоматична інвалідація на promote/rollback.
    // Нормалізація мінімальна (trim + регістр): агресивніша — це вже твердження про
    // еквівалентність текстів, яке не завжди правдиве (L05 блок 03).
    // Замаскований текст іде і в модель, І в ключ кешу — інакше кеш зберігав би PII.
    var cacheKey = $"{model}|{systemPrompt}|{userMessage.Trim().ToLowerInvariant()}";

    var answer = "";
    string? toolCall = null;
    int promptTokens = 0, completionTokens = 0, status = 0; // 0 = відповіді не було
    var cacheHit = false;
    var fallbackUsed = false;

    if (cache.TryGetValue(cacheKey, out var cached))
    {
        answer = cached;
        status = 200;
        cacheHit = true;
        Interlocked.Increment(ref stats.CacheHits);
        // токени/вартість — нулі: виклику не було. Рядок у лог ІДЕ: економія видима.
    }
    else
    {
        Interlocked.Increment(ref stats.CacheMisses);

        // [W4] fallback-ланцюг. Політика з W2: у разі збою — вниз, до дешевшої.
        // На mock резерв фізично той самий mock (механіка справжня, дата-центру нема).
        // Маркер __outage робить МЕРТВОЮ лише основну (mock-dead із litellm-config),
        // а резерв лишає живим — інакше успішний fallback на mock не побачити:
        // __fail_503 їде в тексті й кладе обидва елементи ланцюга.
        string[] chain;
        if (defaultModel != "mock")
            chain = new[] { model, "azure-gpt-4o" };
        else if (userMessage.Contains("__outage"))
            chain = new[] { "mock-dead", model };
        else
            chain = new[] { model, model == "mock-strong" ? "mock-mini" : "mock" };

        var http = httpFactory.CreateClient();
        var ok = false;
        for (int i = 0; i < chain.Length && !ok; i++)
        {
            var br = breakers.GetOrAdd(chain[i], _ => new Breaker());
            var now = DateTimeOffset.UtcNow;
            if (br.OpenUntil > now)
            {
                // half-open: раз на 5с пропускаємо пробний запит. Без цього breaker —
                // самостріл: одужалий провайдер лишається заблокованим до кінця терміну.
                if (now < br.NextProbe) continue;
                br.NextProbe = now.AddSeconds(5);
            }

            if (i > 0) { Interlocked.Increment(ref stats.Fallbacks); fallbackUsed = true; }

            var res = await CallGateway(http, gateway, chain[i], systemPrompt, userMessage);

            if (res.ok)
            {
                br.Fails = 0; br.OpenUntil = default;
                ok = true;
                model = chain[i];  // у лог — модель, яка РЕАЛЬНО відповіла, не обрана роутером
                answer = res.answer; toolCall = res.tool;
                promptTokens = res.pt; completionTokens = res.ct; status = res.status;

                // [W3/W4] read-only — одразу; незворотну дію — тільки через людину.
                // Модель НІКОЛИ не тримає палець на кнопці: між проханням і дією — черга і оператор.
                if (toolCall != null)
                {
                    if (Tools.IsSideEffecting(toolCall))
                    {
                        var apprId = Guid.NewGuid().ToString("N")[..6];
                        approvals[apprId] = new Approval(toolCall, requestId, userMessage,
                                                        DateTimeOffset.UtcNow, null);
                        answer += $" (очікує підтвердження оператора, id={apprId})";
                    }
                    else
                    {
                        var toolResult = await Tools.Run(toolCall, requestId.ToString());
                        if (toolResult != null) answer += $" ({toolResult})";
                    }
                }
            }
            else
            {
                status = res.status;  // чесний статус збою: 503/429/0, не 200
                if (++br.Fails >= 3) { br.OpenUntil = now.AddSeconds(30); br.NextProbe = now.AddSeconds(5); }
            }
        }

        if (!ok)
        {
            // [W4] graceful degradation — спроєктована відмова: текст для людини,
            // статус для машин, і жоден не бреше своїй аудиторії.
            answer = "Вибачте, тимчасові проблеми на нашому боці. Спробуйте, будь ласка, трохи згодом.";
            status = (status == 200 || status == 0) ? 503 : status;
        }

        // [W3] у кеш — тільки те, у чому впевнені. ok виключає заглушку деградації
        // ("те, що народилося у fallback-гілці, у кеш не потрапляє ніколи" — L05 блок 06).
        if (ok && status == 200 && toolCall == null && !string.IsNullOrWhiteSpace(answer))
            cache[cacheKey] = answer;
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // [W2] cost: tokens * ціна. Немає моделі у прайсі -> null, а не 0:
    // нуль бреше ("безкоштовно"), null — чесне "не знаємо", яке видно запитом.
    // 6 знаків = NUMERIC(10,6) у схемі.
    decimal? costUsd = prices.TryGetValue(model, out var pr)
        ? Math.Round(promptTokens / 1000m * pr.In
                   + completionTokens / 1000m * pr.Out, 6)
        : null;

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status, cacheHit, fallbackUsed, toolCall);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі
// [W5] вітрина поверх власного лога. Жодного окремого стека метрик: p95/requests/
// error_rate — один SQL по таблиці, яку пишемо з W1. cache_hit і fallback беру
// З БАЗИ (колонки), а не з лічильників у пам'яті: інакше після ребілду плитка
// бреше нулем — а це саме ті цифри, за якими хочеться дивитися тренд (L09 блок 06).
app.MapGet("/observability", async () =>
{
    int requests = 0, p95 = 0;
    double errorRate = 0, cacheHitPct = 0;
    long fallbackEvents = 0;
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*), "
            + "COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms), 0), "
            + "COALESCE(AVG(CASE WHEN status <> '200' THEN 1.0 ELSE 0 END) * 100, 0), "
            + "COALESCE(AVG(CASE WHEN cache_hit THEN 1.0 ELSE 0 END) * 100, 0), "
            + "COALESCE(SUM(CASE WHEN fallback_used THEN 1 ELSE 0 END), 0) "
            + "FROM requests WHERE created_at::date = CURRENT_DATE", db);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            requests = (int)r.GetInt64(0);
            p95 = (int)r.GetDouble(1);
            errorRate = (double)r.GetDecimal(2);
            cacheHitPct = (double)r.GetDecimal(3);
            fallbackEvents = r.GetInt64(4);
        }
    }
    catch { /* база лягла — вітрина не валиться разом із нею */ }
    return Results.Json(new
    {
        p95_ms = p95, requests,
        cache_hit_pct = Math.Round(cacheHitPct, 1),
        error_rate_pct = Math.Round(errorRate, 1),
        fallback_events = fallbackEvents,
    });
});
// [W2] витрата за сьогодні поруч із бюджетом — плитка "Вартість сьогодні".
app.MapGet("/cost", async () =>
{
    decimal today = 0m;
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(cost_usd), 0) FROM requests WHERE created_at::date = CURRENT_DATE", db);
        today = (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
    }
    catch { /* база лягла — консоль не має валитися разом із нею */ }
    return Results.Json(new { today_usd = Math.Round(today, 4), budget_usd = BudgetUsd });
});
// [W1] реєстр промптів: список версій (консоль малює картку) + promote/rollback.
app.MapGet("/prompts", async () =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT name, version, active, created_at FROM prompts WHERE name = @n ORDER BY created_at", db);
    cmd.Parameters.AddWithValue("n", PromptName);
    var items = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        items.Add(new { name = r.GetString(0), version = r.GetString(1),
                        active = r.GetBoolean(2), created_at = r.GetDateTime(3) });
    return Results.Json(items);
});

// [W1] promote і rollback — та сама дія: зробити активною задану версію.
app.MapPost("/prompts/{version}/activate", async (string version) =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();

    // спершу існування: інакше одна одруківка зняла б active з усіх (hw1 п.5)
    await using var check = new NpgsqlCommand(
        "SELECT 1 FROM prompts WHERE name = @n AND version = @v", db);
    check.Parameters.AddWithValue("n", PromptName);
    check.Parameters.AddWithValue("v", version);
    if (await check.ExecuteScalarAsync() is null)
        return Results.NotFound(new { error = "unknown prompt version", version });

    // один атомарний UPDATE: не існує моменту, коли активні дві версії або жодна
    await using var upd = new NpgsqlCommand(
        "UPDATE prompts SET active = (version = @v) WHERE name = @n", db);
    upd.Parameters.AddWithValue("n", PromptName);
    upd.Parameters.AddWithValue("v", version);
    await upd.ExecuteNonQueryAsync();
    return Results.Json(new { activated = version });
});
// [W5] статус провайдерів. Джерело істини — стан breaker'а, а не окремий ping:
// плитка НЕ МАЄ ПРАВА зеленіти посеред інциденту. Це діагностика залежностей,
// свідомо ОКРЕМО від /health (живість процесу).
app.MapGet("/providers", () =>
{
    var now = DateTimeOffset.UtcNow;
    var known = new[] { "mock-mini", "mock-strong" };
    return Results.Json(new
    {
        providers = known.Select(name =>
        {
            breakers.TryGetValue(name, out var br);
            var open = br != null && br.OpenUntil > now;
            var failing = br != null && br.Fails > 0;
            return new { name, status = open ? "down" : failing ? "degraded" : "ok", fails = br?.Fails ?? 0 };
        }).ToArray()
    });
});
// [W4] черга для консолі. Заявка — продукт для ЛЮДИНИ: без контексту рішення
// неможливе. Тому віддаємо дію + оригінальне повідомлення + request_id + час очікування.
app.MapGet("/approvals", () => Results.Json(new
{
    pending = approvals.Where(kv => kv.Value.Result == null)
        .OrderBy(kv => kv.Value.CreatedAt)
        .Select(kv => new
        {
            id = kv.Key, action = kv.Value.Action,
            request_id = kv.Value.RequestId, message = kv.Value.Message,
            waiting_sec = (int)(DateTimeOffset.UtcNow - kv.Value.CreatedAt).TotalSeconds
        }).ToArray()
}));

// [W4] Виконання живе ТІЛЬКИ тут — після людського "так".
app.MapPost("/approvals/{id}/approve", async (string id) =>
{
    if (!approvals.TryGetValue(id, out var a))
        return Results.NotFound(new { error = "unknown approval", id });
    if (a.Result != null)
        return Results.NotFound(new { error = "already done", id, result = a.Result });

    // check-then-act: спершу АТОМАРНО захоплюємо заявку проміжним станом, і лише
    // переможець виконує. Два кліки / два оператори -> тікет однаково один.
    if (!approvals.TryUpdate(id, a with { Result = "__running" }, a))
        return Results.NotFound(new { error = "already claimed", id });

    var result = await Tools.Run(a.Action, a.RequestId.ToString()) ?? "виконано";
    approvals[id] = a with { Result = result, DecidedAt = DateTimeOffset.UtcNow };
    Interlocked.Increment(ref stats.Approved);
    return Results.Ok(new { id, result });
});

// [W4] reject — теж РІШЕННЯ, і теж логується. Відхилені заявки — найцінніша
// частина аудиту: список моментів, коли система хотіла зробити зайве.
app.MapPost("/approvals/{id}/reject", (string id) =>
{
    if (!approvals.TryGetValue(id, out var a))
        return Results.NotFound(new { error = "unknown approval", id });
    if (a.Result != null)
        return Results.NotFound(new { error = "already done", id, result = a.Result });
    if (!approvals.TryUpdate(id, a with { Result = "rejected", DecidedAt = DateTimeOffset.UtcNow }, a))
        return Results.NotFound(new { error = "already claimed", id });
    Interlocked.Increment(ref stats.Rejected);
    return Results.Ok(new { id, result = "rejected" });
});

app.Run("http://0.0.0.0:8080");

// [W4] один виклик моделі через gateway. Ключове: збій — ЗНАЧЕННЯ (ok=false),
// а не виняток. Тільки навколо такої функції можна будувати ланцюги і лічити переходи.
static async Task<(bool ok, string answer, string? tool, int pt, int ct, int status)>
    CallGateway(HttpClient http, string gateway, string model, string system, string user)
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
    try
    {
        var response = await http.PostAsync($"{gateway}/v1/chat/completions",
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
            tool = tools[0].GetProperty("function").GetProperty("name").GetString();
        var usage = doc.RootElement.GetProperty("usage");
        return (true, answer, tool,
                usage.GetProperty("prompt_tokens").GetInt32(),
                usage.GetProperty("completion_tokens").GetInt32(), status);
    }
    catch
    {
        // status 0 = "відповіді не було взагалі" ≠ "була, але 5xx"
        return (false, "", null, 0, 0, 0);
    }
}

// [W4] маскуємо email до відправки в модель: усе, що поїхало в модель, поїхало до
// провайдера і в наші логи. Regex — перший крок; дисципліна "маскуй до відправки"
// дає 80% користі.
static string MaskPii(string s) =>
    System.Text.RegularExpressions.Regex.Replace(s, @"[\w.\-]+@[\w.\-]+\.\w+", "[email]");

// [W4] детект прямого injection по трьох родинах з L08 блоку 09.
// Патерни ловлять типові атаки, не всі — головний захист архітектурний (HITL).
static bool LooksLikeInjection(string s)
{
    var u = s.ToLowerInvariant();
    string[] reset = { "ignore previous", "ignore all", "disregard", "forget everything",
        "forget your", "забудь попередні", "забудь усі", "ігноруй", "тепер ти", "you are now" };
    string[] exfil = { "system prompt", "your instructions", "your rules", "reveal your",
        "print your", "покажи свій", "виведи свій", "системний промпт", "свої інструк", "свої правила" };
    string[] pressure = { "i am the developer", "i'm the developer", "as an admin",
        "for debugging i need", "я розробник", "я адміністратор", "для дебагу" };
    if (reset.Any(u.Contains) && (exfil.Any(u.Contains) || u.Contains("instruction") || u.Contains("інструк"))) return true;
    if (exfil.Any(u.Contains) && new[] { "show", "print", "reveal", "repeat", "verbatim", "покажи", "виведи" }.Any(u.Contains)) return true;
    if (pressure.Any(u.Contains) && exfil.Any(u.Contains)) return true;
    if (u.Contains("ignore") && u.Contains("instruction")) return true;  // канон курсу
    return false;
}

// [W2] Політика маршрутизації одним реченням (L03 блок 05 просить саме словами):
// повернення, скарги і термінове йдуть на сильну модель, решта — на дешеву,
// бо помилка "переплатили за просте" дешевша за "зекономили на скарзі".
// Fallback-порядок (політика вже, механізм на W4): mock-strong -> mock-mini.
static string Route(string message, string def)
{
    if (def != "mock") return def;  // реальний ключ: беремо задану модель
    var u = message.ToLowerInvariant();
    bool escalation = u.Contains("поверн") || u.Contains("терміново")
                   || u.Contains("refund") || u.Contains("скарг");
    return escalation ? "mock-strong" : "mock-mini";
}

// [W1] активна версія промпта. Порожній реєстр / мертва база -> дефолт БЕЗ "support"
// і версія "none": поломка реєстру мусить бути видимою (fail-visible), не замаскованою.
static async Task<(string Version, string Body)> GetActivePrompt(string conn)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT version, body FROM prompts WHERE name = 'support-system' AND active = true "
            + "ORDER BY created_at DESC LIMIT 1", db);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync()) return (r.GetString(0), r.GetString(1));
    }
    catch { /* база недоступна — падаємо у видимий дефолт */ }
    return ("none", "You are an assistant.");
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status,
    bool cacheHit = false, bool fallbackUsed = false, string? tool = null, string? blocked = null)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status, cache_hit, fallback_used, tool, blocked) "
            + "VALUES (@id, @model, @pv, @lat, @pt, @ct, @cost, @status, @hit, @fb, @tool, @blocked)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("pv", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
        cmd.Parameters.AddWithValue("hit", cacheHit);
        cmd.Parameters.AddWithValue("fb", fallbackUsed);
        cmd.Parameters.AddWithValue("tool", (object?)tool ?? DBNull.Value);
        cmd.Parameters.AddWithValue("blocked", (object?)blocked ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* не валимо запит через лог */ }
}

record ChatIn(string Message);

// [W4] заявка на підтвердження. Result == null => очікує рішення.
record Approval(string Action, Guid RequestId, string Message,
                DateTimeOffset CreatedAt, string? Result,
                DateTimeOffset? DecidedAt = null);

// [W4] стан circuit breaker'а однієї моделі.
class Breaker
{
    public int Fails;
    public DateTimeOffset OpenUntil;
    public DateTimeOffset NextProbe;
}

// [W3] лічильники: клас, а не int-и — Interlocked потребує посилання на поле.
// І окремий static class для стану інструментів: у файлі з top-level statements
// поля після них оголошувати не можна (CS0106) — §9a гайду про це досі мовчить.
class Stats
{
    public long CacheHits;
    public long CacheMisses;
    public long Fallbacks;
    public long Approved;
    public long Rejected;
    public long Blocked;
}

// [W3] реєстр інструментів + обв'язка з L06: таймаут, ідемпотентність, оброблена гілка.
static class Tools
{
    // Бюджет часу згори вниз (L06): чат обіцяє ~10с, моделі ~8с, інструменту — 3с.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    // Реєстр оброблених ключів. Сховище зі строком життя, а не вічна пам'ять;
    // TTL тут свідомо немає — навчальний контур.
    static readonly ConcurrentDictionary<string, string> Done = new();

    // Категорія видна З КОНТРАКТУ: один інструмент — одна дія, жодного поля action.
    public static bool IsSideEffecting(string name) => name switch
    {
        "create_ticket" => true,
        _ => false,
    };

    public static async Task<string?> Run(string name, string idempotencyKey)
    {
        var key = $"{idempotencyKey}|{name}";
        if (Done.TryGetValue(key, out var known)) return known;

        string? result;
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            result = await Execute(name, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Вичерпаний ліміт — ОБРОБЛЕНА гілка. І це "відповіді не було",
            // а не "дії не було": зовнішня система могла встигнути виконати.
            return IsSideEffecting(name)
                ? "не вдалося підтвердити виконання, перевіряємо"
                : "не вдалося перевірити статус, спробуйте пізніше";
        }

        if (result != null) Done[key] = result;
        return result;
    }

    // Невідомий інструмент -> null: прохання про неіснуюче безпечно стає
    // відповіддю без результату, а не винятком.
    static Task<string?> Execute(string name, CancellationToken ct) => name switch
    {
        "lookup_order" => Task.FromResult<string?>("статус: оплачено, доставку призначено"),
        "create_ticket" => Task.FromResult<string?>("тікет #T-" + Guid.NewGuid().ToString("N")[..4]),
        _ => Task.FromResult<string?>(null),
    };
}
