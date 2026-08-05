# Starter repository

Те, що студент клонує на першому занятті й дороблює до capstone.

**➡ Повний гайд для студента (вимоги, запуск, кожна папка, troubleshooting): [GETTING_STARTED.md](GETTING_STARTED.md).**
**➡ Домашні завдання — окремий файл на кожен тиждень: [homework/](homework/README.md).**

## Що дається готовим, що добудовує студент

| Компонент | Дається готовим | Студент добудовує |
|---|---|---|
| UI (Angular) | готові в'юхи: Chat + Console/Observability | не змінює |
| App service (.NET) | skeleton | control plane: routing, fallback, cost, policies |
| Observability API (.NET) | skeleton | агрегати з Postgres (traces, p95, cost, error-taxonomy) у Console-в'юху |
| Gateway (LiteLLM) | базовий конфіг | моделі/провайдери, порядок fallback |
| Mock provider | готовий | використовує для тестів і failure-сценаріїв |
| Postgres | базова схема | розширює під logs / cost / prompt records |
| Eval runner (Python) | skeleton | dataset, graders, пороги |
| CI (GitHub Actions) | шаблон `.github/workflows/eval-gate.yml` | eval gate |
| Redis / дашборди | опційний шаблон | advanced extension |

## Запуск (мінімальний стек)

```sh
docker compose up --build
```

Піднімаються: UI (`:4200`), сервіс (`:8080`), LiteLLM (`:4000`), Postgres, mock provider.
Для mock реальні ключі не потрібні. Redis і дашборди — опційні: `docker compose --profile advanced up`.
Для оцінки якості (model-based evals): `cp gateway/.env.example gateway/.env` і заповнити ключі.

---

# Мої рішення (прогін №2)

Хід і ре-тест дефектів — `student-journal.md`.

## Порти

На хості зайняті 8080/5432 (Apache, PostgreSQL 18) і 18080/15432/4200/4000 (стек ментора
`solutions-*`). Тому `docker-compose.override.yml` зсуває на 24200/28080/24000/**25432**.
Знахідка: `demo-ports.yml` і `override.example` обидва маплять postgres на 15432 —
паралельний запуск двох стеків за цими файлами неможливий.

## W5 — поріг evals

**10 кейсів, поріг 9.** Правило hw5: N≤10 → N−1. Регресія промпта валить 4 кейси
(faq-1, faq-2, caps-1, thanks-1) і пробиває поріг гарантовано; запас 1 покриває флейк.
Safety страхую кількістю: 4 кейси на 3 родини injection.

## W5 — перевірка на фальш-позитиви (hw5 п.4)

`evals/golden.outage.jsonl` — копія з префіксом `__fail_503` у кожному `input`.
Результат: **4/10**, і всі 4 зелені — це `expect_refusal`-кейси. Це легальний виняток,
названий у hw5: injection-guardrail стоїть на межі, до виклику моделі, тому відмовляє
навіть під час повного збою — і це правильніше, ніж заглушка. Жоден `expect`-кейс не
зелений (усі 6 отримують заглушку деградації), тобто датасет фальш-позитивів не має.

## W5 — SLO і бюджет помилок (опційне)

**SLI:** частка звернень зі змістовною відповіддю (`status='200' AND blocked IS NULL`
і не заглушка). **SLO ≥ 99%** — на те, що відчуває користувач, а не uptime процесу.
**Бюджет помилок:** при 10 000 звернень/тиждень — 100 невдач; поки не витрачений,
можна котити зміни.

---

# Інцидент-демо (hw6 п.3)

Повний runbook на 3 класи інцидентів — [INCIDENT_RUNBOOK.md](INCIDENT_RUNBOOK.md).

## Команди відтворення

```bash
docker compose up --build -d
curl -s -X POST localhost:28080/chat -H "Content-Type: application/json" -d '{"message":"warmup"}'
# ІНЦИДЕНТ
for i in 1 2 3; do curl -s -X POST localhost:28080/chat -H "Content-Type: application/json" -d '{"message":"__fail_503"}'; done
curl -s localhost:28080/observability; curl -s localhost:28080/providers
# ВІДНОВЛЕННЯ — почекати ~6с (half-open) і питати УНІКАЛЬНИМ текстом (не з кешу!)
sleep 6
curl -s -X POST localhost:28080/chat -H "Content-Type: application/json" -d '{"message":"probe unique-2026"}'
curl -s localhost:28080/providers
```

## Фактичний таймлайн (UTC)

```
14:27:35 НОРМА       err=21.5%  fallback=9
14:27:35 ІНЦИДЕНТ ×3 користувач: «Вибачте, тимчасові проблеми…» (НЕ 500)
                     err=24.4%  fallback=10;  providers: mock-mini=down, mock-strong=degraded
14:27:42 ВІДНОВЛЕННЯ half-open пропустив пробу; унікальне питання -> нормальна відповідь
                     providers: mock-mini=ok
```

## Пастка (перевірено): cache-hit маскує відновлення

Перевірка «чи відпустило» питанням із кешу не заходить у gateway → breaker не отримує
проби в half-open → `/providers` показує `down` скільки завгодно довго. Правильний фікс —
фоновий health-probe повз кеш; у runbook закрито дисципліною «перевіряй унікальним текстом».
Це стик кеш×breaker×плитка, який жоден урок не зводить разом.

---

# KPI-мова (лаба L12)

| Механізм | Мовою бюджету | Доказ |
|---|---|---|
| fallback + degradation | падіння провайдера користувач не помічає — жодної 500 | таймлайн вище |
| CACHE-HIT | частина запитів безкоштовна і миттєва (0 мс, 0 токенів) | `cache_hit` у лозі |
| routing mini/strong | дорога модель — тільки складним; вартість запиту керована | `SUM(cost_usd) GROUP BY model` |
| eval-гейт у CI | регресія якості фізично не мержиться | 6/10→exit 1; 10/10→exit 0 |
| Approvals · HITL | жодна незворотна дія без людини; є журнал відхилених | черга + reject |
| Prompt registry | відкат за секунди, без деплою, зі слідом | `POST /prompts/{v}/activate` |

---

# 30-day rollout (hw6 п.4)

Порядок не випадковий — кожен шар потребує даних попереднього; перший тиждень самоокупний.

1. **Тиждень 1 — unified log + вартість.** Рядок на запит + `cost_usd` з `usage`.
   *Готово, коли:* питання про гроші = один SQL, а не рахунок наприкінці місяця.
2. **Тиждень 2 — реєстр промптів.** Промпти в таблицю з версіями; активна на кожен запит;
   promote/rollback = атомарний UPDATE. *Готово, коли:* відкат за секунди зі слідом.
3. **Тиждень 3 — evals на критичні сценарії.** 10–15 кейсів на те, що страшно зламати +
   перевірка на фальш-позитиви. *Готово, коли:* зламаний промпт → червоно відтворювано.
4. **Тиждень 4 — гейт у CI.** Exit code вже є — повісити на PR і **увімкнути required
   status check**. *Готово, коли:* червоний прогін блокує merge, і в історії CI є доказ.

**Чого НЕ робити в перший місяць:** semantic cache, LLM-as-judge, canary, окремий стек
метрик — кожне під **виміряну** проблему. Виняток без сигналу: черга approvals у базі,
відсутність секретів у коді, ліміт витрат у кабінеті провайдера.
