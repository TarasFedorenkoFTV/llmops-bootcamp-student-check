// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-4o-mini + ключ у gateway/.env — реальна модель.

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
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

// [W1] у SupportGW промпт поки один; ім'я тримаємо в одному місці, щоб SQL не розповзався
const string PromptName = "support-system";

// [W2] прайс за 1k токенів (in, out) — навчальні числа, пропорції як у реальних прайсів:
// strong дорожча за mini ~16x, вихідні токени дорожчі за вхідні ~4x.
// Лежить поруч із Route(): ціна моделі — вхідний параметр маршрутизації, а не бухдовідка.
var prices = new Dictionary<string, (decimal In, decimal Out)>
{
    ["mock"] = (0.00015m, 0.0006m),
    ["mock-mini"] = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m, 0.01m),
};

// [W2] денний бюджет. Поки константа, але віддається поруч із витратою:
// цифра без порога — просто число, пара "витрачено/ліміт" — це стан системи.
const decimal BudgetUsd = 5.0m;

// [W3] простий in-memory кеш відповідей + лічильники поруч (measure-first).
// Свідомі межі цього рішення: без стелі розміру і без TTL — записи живуть до рестарту.
// На проді це повільний витік пам'яті; наступний щабель — Redis (profile advanced) з TTL і LRU.
// Два одночасні miss'и на один ключ обидва підуть у модель — усвідомлений компроміс
// (thundering herd), лікувати локом дорожче за хворобу на нашому масштабі.
var cache = new ConcurrentDictionary<string, string>();
var stats = new Stats();

// [W4 опційно] circuit breaker на кожну модель окремо.
// Стан живе в пам'яті процесу — і тут це не дефект: кожен інстанс сам спостерігає власні
// збої, тому один "отруєний" інстанс не відкриває breaker усім. Ціна: при N інстансах
// провайдер отримає до N пробних запитів у half-open замість одного.
var breakers = new ConcurrentDictionary<string, Breaker>();

// [W4] Черга підтверджень (HITL). Result == null => заявка очікує рішення.
// ЧЕСНО про межу цього рішення: словник у пам'яті процесу. У проді неприпустимо —
// рестарт губить незакриті заявки (незворотна дія, яку хтось збирався підтвердити,
// просто зникає), а при двох інстансах оператор бачить лише "свої" заявки.
// Доросла форма — таблиця в тій самій базі, де лог: id, дія, аргументи, стан, автор, час.
var approvals = new ConcurrentDictionary<string, Approval>();

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // [W4] guardrail на межі — ДО того, як текст стане частиною промпта.
    // Межа — єдине місце, яке бачить усі запити незалежно від кількості моделей,
    // промптів і fallback-гілок: вона стоїть до розвилок і не дублюється.
    var userMessage = MaskPii(body.Message);

    // [W4] Вхідний guardrail на injection. У курсі відмову віддає mock, але його детект —
    // це рівно `ignore` + `instruction` англійською: три з чотирьох родин атак з L08 блоку 09
    // (укр. переклад, перезапис ролі, соціальний тиск) проходять наскрізь. Тому детект
    // мусить жити ТУТ — саме там, де урок і каже, що він жив би у проді.
    // Це прохання переписати правила гри — відмовляємо механічно, не питаючи модель.
    if (LooksLikeInjection(userMessage))
    {
        // policy-подія, а не збій: у лозі це має бути окрема категорія, не 5xx
        await LogRequest(dbConn, requestId, model: "guardrail", promptVersion: "none",
            latency: (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            promptTokens: 0, completionTokens: 0, cost: 0m, status: 200,
            cacheHit: false, fallbackUsed: false, tool: null, blocked: "injection");
        Interlocked.Increment(ref stats.Blocked);
        return Results.Json(new
        {
            request_id = requestId,
            content = "Вибачте, не можу виконати це прохання.",
            tool = (string?)null,
            latency_ms = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
        });
    }

    // [W2] routing: одна точка рішення про модель.
    var model = Route(userMessage, defaultModel);

    // [W1] промпт беремо з реєстру — активну версію, а не хардкод.
    // Читаємо на КОЖЕН запит: інакше activate не діє до рестарту (L02 блок 06, "Типова помилка").
    var (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);

    // [W3] ключ кешу = все, що впливає на відповідь: модель + ТЕКСТ активного промпта + повідомлення.
    // Промпт у ключі робить інвалідацію автоматичною: promote/rollback версії -> інший ключ ->
    // старі записи просто не знаходяться. Ніякого коду інвалідації писати не треба.
    // Нормалізація: тільки trim + регістр. Пунктуацію НЕ прибираю — це вже твердження
    // про еквівалентність текстів, а воно не завжди правдиве (L05 блок 03).
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
        // токени і вартість лишаються нулями — виклику моделі не було.
        // Рядок у лог усе одно піде: економія має бути видимою, а не зникати з обліку.
    }
    else
    {
        Interlocked.Increment(ref stats.CacheMisses);

        // [W4] fallback-ланцюг. Політика, оголошена на W2: у разі збою — вниз, до дешевшої.
        // На mock резервного провайдера фізично немає, тож роль резерву грає той самий mock:
        // механіка переходу справжня, "резервного дата-центру" в компоузі немає.
        //
        // Маркер __outage — мій додаток до курсу: він робить МЕРТВОЮ лише основну модель
        // (mock-dead із litellm-config), а резерв лишає живим. Без цього успішний fallback
        // на mock не перевірити взагалі: __fail_503 їде в тексті й кладе обидва елементи.
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
            // [W4 опційно] circuit breaker: окремий на КОЖНУ модель, не один глобальний.
            // Якби він був глобальним, мертва mock-strong карала б живу mock-mini.
            var br = breakers.GetOrAdd(chain[i], _ => new Breaker());
            var now = DateTimeOffset.UtcNow;
            if (br.OpenUntil > now)
            {
                // half-open: раз на 5с пропускаємо один пробний запит. БЕЗ цієї гілки
                // breaker — самостріл: одужалий провайдер лишається заблокованим до кінця
                // фіксованого терміну, тобто ти сам собі робиш outage довший за реальний.
                if (now < br.NextProbe) continue;
                br.NextProbe = now.AddSeconds(5);
            }

            // Лічильник рахує КОЖЕН перехід далі за ланцюгом — успішний він чи ні.
            // Fallback, що спрацював непомітно, — це замаскована проблема.
            if (i > 0) { Interlocked.Increment(ref stats.Fallbacks); fallbackUsed = true; }

            var res = await CallGateway(http, gateway, chain[i], systemPrompt, userMessage);

            if (res.ok)
            {
                br.Fails = 0;
                br.OpenUntil = default;
                ok = true;
                model = chain[i];  // у лог — та модель, яка РЕАЛЬНО відповіла, а не обрана роутером
                answer = res.answer;
                toolCall = res.tool;
                promptTokens = res.pt;
                completionTokens = res.ct;
                status = res.status;

                // [W3/W4] read-only виконуємо одразу; незворотну дію — тільки через людину.
                // Модель НІКОЛИ не тримає палець на кнопці: між її проханням і незворотною
                // дією стоять двоє — черга і оператор. Injection у найгіршому разі
                // породжує ЗАЯВКУ, а не подію.
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
                        // [W3] ключ ідемпотентності — request_id (генерує ініціатор, не виконавець)
                        var toolResult = await Tools.Run(toolCall, requestId.ToString());
                        if (toolResult != null) answer += $" ({toolResult})";
                    }
                }
            }
            else
            {
                status = res.status;  // чесний статус збою: 503/429/0, не 200
                if (++br.Fails >= 3)
                {
                    br.OpenUntil = now.AddSeconds(30);
                    br.NextProbe = now.AddSeconds(5);  // перший probe через 5с, не одразу
                }
            }
        }

        if (!ok)
        {
            // [W4] graceful degradation — остання сходинка драбини. Це спроєктована
            // відмова, а не "здалися": текст — для людини, статус — для машин,
            // і жоден не бреше своїй аудиторії.
            answer = "Вибачте, тимчасові проблеми на нашому боці. "
                   + "Спробуйте, будь ласка, трохи згодом.";
            status = status == 200 || status == 0 ? 503 : status;
        }

        // [W3] у кеш — тільки те, у чому впевнені:
        //   status == 200      -> не консервуємо збій провайдера (інакше він "залипає")
        //   toolCall == null   -> результат інструмента залежить від стану зовнішньої системи
        //   answer не порожній -> порожня відповідь не є відповіддю
        // Заглушка деградації сюда не потрапляє: у неї ok == false (і status 503).
        // "Те, що народилося у fallback-гілці, у кеш не потрапляє ніколи" (L05 блок 06) —
        // тому в умові стоїть ok, а не тільки status.
        if (ok && status == 200 && toolCall == null && !string.IsNullOrWhiteSpace(answer))
            cache[cacheKey] = answer;
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // [W2] cost: tokens * ціна моделі.
    // Моделі немає у прайсі -> null, а не 0: нуль бреше ("запит був безкоштовний"),
    // null — чесне "не знаємо", яке видно запитом і можна полагодити.
    // Округлення до 6 знаків = NUMERIC(10,6) у схемі, щоб не розходилось на обсягах.
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
// [W5] Вітрина поверх власного лога. Жодного окремого стека метрик: p95/requests/error_rate —
// це один SQL по таблиці, яку ми пишемо з W1.
// Два джерела свідомо: агрегати з БД (переживають рестарт, дають тренд),
// cache-hit і fallback — з лічильників у пам'яті (обнуляються з рестартом).
// Cache-hit я беру З БАЗИ, а не з пам'яті: інакше після рестарту плитка бреше нулем,
// а це саме та цифра, за якою хочеться дивитися тренд (L09 блок 06).
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
    catch { /* база лягла — вітрина не має валитися разом із нею */ }

    return Results.Json(new
    {
        p95_ms = p95,
        requests,
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
            "SELECT COALESCE(SUM(cost_usd), 0) FROM requests "
            + "WHERE created_at::date = CURRENT_DATE", db);
        today = (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
    }
    catch { /* база лягла — віддаємо 0, консоль не має валитися разом з нею */ }

    return Results.Json(new { today_usd = Math.Round(today, 4), budget_usd = BudgetUsd });
});
// [W1] реєстр промптів: список версій + promote/rollback однією операцією.
app.MapGet("/prompts", async () =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT name, version, active, created_at FROM prompts "
        + "WHERE name = @n ORDER BY created_at", db);
    cmd.Parameters.AddWithValue("n", PromptName);
    var items = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        items.Add(new
        {
            name = r.GetString(0),
            version = r.GetString(1),
            active = r.GetBoolean(2),
            created_at = r.GetDateTime(3)
        });
    }
    return Results.Json(items);
});

// promote і rollback — це та сама дія: зробити активною задану версію (L02 блок 07).
app.MapPost("/prompts/{version}/activate", async (string version) =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();

    // спершу переконуємось, що версія існує: інакше одна одруківка зняла б active з усіх
    await using var check = new NpgsqlCommand(
        "SELECT 1 FROM prompts WHERE name = @n AND version = @v", db);
    check.Parameters.AddWithValue("n", PromptName);
    check.Parameters.AddWithValue("v", version);
    if (await check.ExecuteScalarAsync() is null)
        return Results.NotFound(new { error = "unknown prompt version", version });

    // один атомарний UPDATE по всіх версіях: немає моменту, коли активні дві або жодна
    await using var upd = new NpgsqlCommand(
        "UPDATE prompts SET active = (version = @v) WHERE name = @n", db);
    upd.Parameters.AddWithValue("n", PromptName);
    upd.Parameters.AddWithValue("v", version);
    await upd.ExecuteNonQueryAsync();

    return Results.Json(new { activated = version });
});
// [W5] Статус провайдерів. Плитка НЕ МАЄ ПРАВА зеленіти посеред інциденту, тому
// джерело істини тут — стан circuit breaker'а, а не окремий ping.
// Це діагностика залежностей, і вона свідомо ОКРЕМО від /health: health-check, який
// тягне зовнішні перевірки, оголошує сервіс мертвим щоразу, коли чхнула чужа система,
// і оркестратор рестартить цілком здоровий процес. Живість — про процес.
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
            return new
            {
                name,
                status = open ? "down" : failing ? "degraded" : "ok",
                fails = br?.Fails ?? 0,
            };
        }).ToArray()
    });
});
// [W4] черга підтверджень для консолі. Заявка — це продукт для ЛЮДИНИ: без контексту
// рішення неможливе, і черга виродиться в HITL-театр. Тому віддаємо не тільки дію,
// а й оригінальне повідомлення, request_id для сліду в лозі і час очікування:
// заявка, що висить добу, — це користувач, який добу чекає, тобто тихий інцидент.
app.MapGet("/approvals", () => Results.Json(new
{
    pending = approvals
        .Where(kv => kv.Value.Result == null)
        .OrderBy(kv => kv.Value.CreatedAt)
        .Select(kv => new
        {
            id = kv.Key,
            action = kv.Value.Action,
            request_id = kv.Value.RequestId,
            message = kv.Value.Message,
            waiting_sec = (int)(DateTimeOffset.UtcNow - kv.Value.CreatedAt).TotalSeconds
        })
        .ToArray()
}));

// [W4] Виконання живе ТІЛЬКИ тут — після людського "так".
app.MapPost("/approvals/{id}/approve", async (string id) =>
{
    if (!approvals.TryGetValue(id, out var a))
        return Results.NotFound(new { error = "unknown approval", id });
    if (a.Result != null)
        return Results.NotFound(new { error = "already done", id, result = a.Result });

    // check-then-act: між перевіркою і виконанням є зазор, у який влазить другий
    // одночасний approve. Тому спершу АТОМАРНО захоплюємо заявку проміжним станом,
    // і лише переможець виконує дію. Два кліки / два оператори -> тікет однаково один.
    if (!approvals.TryUpdate(id, a with { Result = "__running" }, a))
        return Results.NotFound(new { error = "already claimed", id });

    var result = await Tools.Run(a.Action, a.RequestId.ToString()) ?? "виконано";
    approvals[id] = a with { Result = result, DecidedAt = DateTimeOffset.UtcNow };
    Interlocked.Increment(ref stats.Approved);
    return Results.Ok(new { id, result });
});

// [W4] reject — теж РІШЕННЯ, і теж логується. Відхилені заявки — найцінніша частина
// аудиту: це список моментів, коли система хотіла зробити те, чого робити не слід.
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

// [W4] guardrail: маскуємо email до відправки в модель.
// Все, що потрапило в промпт, потрапило до провайдера і в наші логи — і лишиться там.
// Моделі для відповіді на "скиньте пароль" справжній email не потрібен.
// Regex — перший крок; у проді сюди доростають телефони, картки, імена й PII-детектори,
// але 80% користі дає сама дисципліна "маскуй до відправки".
// [W4] Детект прямого injection по трьох родинах з L08 блоку 09.
// Чесно про межі: патерни ловлять типові атаки, а не всі — injection це гонка озброєнь.
// Головний захист усе одно архітектурний (HITL на незворотне), а це — перший бар'єр
// і місце, куди ставити нові кейси після кожного red-team.
static bool LooksLikeInjection(string s)
{
    var u = s.ToLowerInvariant();

    // 1. перезапис ролі / скидання інструкцій
    string[] reset = {
        "ignore previous", "ignore all previous", "ignore your instruction",
        "disregard", "forget everything", "forget your instruction",
        "забудь попередні", "забудь усі", "забудь інструк", "ігноруй",
        "тепер ти", "you are now",
    };
    // 2. витяг інструкцій (розвідка перед атакою)
    string[] exfil = {
        "system prompt", "your instructions", "your rules", "reveal your",
        "print your", "repeat your", "покажи свій", "виведи свій",
        "системний промпт", "свої інструкц", "свої правила",
    };
    // 3. соціальний тиск — атака не на модель, а на її "ввічливість"
    string[] pressure = {
        "i am the developer", "i'm the developer", "as an admin", "for debugging i need",
        "я розробник", "я адміністратор", "для дебагу",
    };

    if (reset.Any(u.Contains) && (exfil.Any(u.Contains) || u.Contains("instruction") || u.Contains("інструк")))
        return true;
    if (exfil.Any(u.Contains) && (u.Contains("show") || u.Contains("print") || u.Contains("reveal")
                                  || u.Contains("repeat") || u.Contains("verbatim")
                                  || u.Contains("покажи") || u.Contains("виведи")))
        return true;
    if (pressure.Any(u.Contains) && exfil.Any(u.Contains))
        return true;
    // канонічний кейс курсу
    if (u.Contains("ignore") && u.Contains("instruction")) return true;
    return false;
}

static string MaskPii(string s) =>
    Regex.Replace(s, @"[\w.\-]+@[\w.\-]+\.\w+", "[email]");

// [W2] Політика маршрутизації одним реченням (L03 блок 05 просить це саме словами):
// повернення, скарги і термінове йдуть на сильну модель, решта — на дешеву,
// бо помилка "переплатили за просте" дешевша за "зекономили на скарзі".
// Fallback-порядок (оголошуємо тут, механізм — W4): mock-strong -> mock-mini.
// Тобто при збої сильної моделі відповідаємо дешевшою, а не не відповідаємо взагалі.
static string Route(string message, string def)
{
    if (def != "mock") return def;  // реальний ключ: беремо задану модель
    var u = message.ToLowerInvariant();
    bool escalation = u.Contains("поверн") || u.Contains("терміново")
                   || u.Contains("refund") || u.Contains("скарг");
    return escalation ? "mock-strong" : "mock-mini";
}

// [W1] активна версія промпта з реєстру.
// Реєстр порожній або база лежить -> дефолт БЕЗ слова "support" і версія "none":
// поломка реєстру мусить бути видимою (fail-visible), а не замаскованою нормальними відповідями.
static async Task<(string Version, string Body)> GetActivePrompt(string conn)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT version, body FROM prompts WHERE name = @n AND active = true "
            + "ORDER BY created_at DESC LIMIT 1", db);
        cmd.Parameters.AddWithValue("n", PromptName);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync()) return (r.GetString(0), r.GetString(1));
    }
    catch { /* база недоступна — падаємо у видимий дефолт нижче */ }
    return ("none", "You are an assistant.");
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status, bool cacheHit = false,
    bool fallbackUsed = false, string? tool = null, string? blocked = null)
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

// [W4] Один виклик моделі через gateway. Ключове рішення: збій — це ЗНАЧЕННЯ
// (ok=false), а не виняток, що летить крізь стек. Тільки навколо такої функції
// можна будувати ланцюги, лічити переходи і ухвалювати рішення.
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
        {
            tool = tools[0].GetProperty("function").GetProperty("name").GetString();
        }

        var usage = doc.RootElement.GetProperty("usage");
        return (true, answer, tool,
                usage.GetProperty("prompt_tokens").GetInt32(),
                usage.GetProperty("completion_tokens").GetInt32(), status);
    }
    catch
    {
        // status 0 = "відповіді не було взагалі"; це не те саме, що "була, але 5xx"
        return (false, "", null, 0, 0, 0);
    }
}

record ChatIn(string Message);

// [W4] заявка на підтвердження. Result == null => очікує рішення.
record Approval(string Action, Guid RequestId, string Message,
                DateTimeOffset CreatedAt, string? Result,
                DateTimeOffset? DecidedAt = null);

// [W3] Реєстр інструментів + обв'язка, яку вимагає L06: таймаут, ідемпотентність,
// оброблена гілка вичерпання часу.
//
// Чому окремий static class, а не локальні функції поруч з іншими: у файлі з top-level
// statements після них можна оголошувати лише типи й локальні функції, але НЕ поля,
// а реєстр оброблених ключів і константа таймауту — це стан. Курс цього не згадує.
static class Tools
{
    // Бюджет часу згори вниз (L06 блок 05): чат обіцяє "кілька секунд" -> загальний
    // бюджет відповіді ~10с, з них моделі ~8с, тому інструменту дістається 3с.
    // Ліміт інструмента МУСИТЬ бути помітно меншим за загальний, інакше конфлікт
    // бюджетів віддано на розсуд таймінгів чужого сервісу.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    // Реєстр оброблених ключів ідемпотентності. Це сховище зі строком життя, а не вічна
    // пам'ять: досить вікна, за межами якого повтор уже не прилетить (години, не місяці).
    // TTL тут немає свідомо — навчальний контур; на проді сюди їде те саме, що в кеш L05.
    static readonly ConcurrentDictionary<string, string> Done = new();

    // Категорія видна З КОНТРАКТУ, а не з'ясовується всередині виконавця.
    // Один інструмент — одна дія; жодного "універсального" з полем action.
    public static bool IsSideEffecting(string name) => name switch
    {
        "create_ticket" => true,
        _ => false,
    };

    public static async Task<string?> Run(string name, string idempotencyKey)
    {
        // повтор із тим самим ключем не виконує дію вдруге, а віддає збережений результат
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
            // Вичерпаний ліміт — ОБРОБЛЕНА гілка, не аварія. І це "відповіді не було",
            // а не "дії не було": зовнішня система могла встигнути виконати операцію і
            // не встигнути відповісти. Саме тому ключ вище обов'язковий.
            return IsSideEffecting(name)
                ? "не вдалося підтвердити виконання, перевіряємо"
                : "не вдалося перевірити статус, спробуйте пізніше";
        }

        if (result != null) Done[key] = result;
        return result;
    }

    // Власне "зовнішні системи". Невідомий інструмент -> null: прохання про те, чого в
    // реєстрі немає, безпечно стає відповіддю без результату, а не винятком.
    static Task<string?> Execute(string name, CancellationToken ct) => name switch
    {
        "lookup_order" => Task.FromResult<string?>("статус: оплачено, доставку призначено"),
        "create_ticket" => Task.FromResult<string?>("тікет #T-" + Guid.NewGuid().ToString("N")[..4]),
        _ => Task.FromResult<string?>(null),
    };
}

// [W3] лічильники кешу. Клас, а не два int-и, бо Interlocked потребує посилання на поле.
class Stats
{
    public long CacheHits;
    public long CacheMisses;
    public long Fallbacks;
    public long Approved;
    public long Rejected;
    public long Blocked;
}

// [W4] стан circuit breaker'а однієї моделі
class Breaker
{
    public int Fails;
    public DateTimeOffset OpenUntil;
    public DateTimeOffset NextProbe;
}
