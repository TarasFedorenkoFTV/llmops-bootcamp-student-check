# Incident runbook — SupportGW

Формат: **симптом → підтвердити → дія → переконатися**. Читач цього документа —
я сам під адреналіном, тому команди повні й готові до копіювання, назви плиток точні.

> **У мене порти перемаплені** (на хості зайняті 8080 і 5432): сервіс — `localhost:18080`,
> Postgres — `localhost:15432`. У «канонічному» стеку курсу підставляй 8080 / 5432.

---

## 1. Провайдер лежить

| Такт | Що саме |
|---|---|
| **Симптом** | скарги «бот вибачається»; у чаті — «Вибачте, тимчасові проблеми на нашому боці» |
| **Підтвердити** | `curl -s localhost:18080/observability` → `error_rate_pct` і `fallback_events` ростуть; `curl -s localhost:18080/providers` → статус `down`/`degraded`; у лозі статуси 503/429 |
| **Дія** | **руками нічого.** fallback і graceful degradation вже працюють; circuit breaker прибирає очікування таймаутів. Стежити за `fallback_events`. Комунікація: користувач бачить ввічливу заглушку, не 500 |
| **Переконатися** | звичайне питання відповідає нормально; `fallback_events` перестав рости; `/providers` → `ok` |

```bash
curl -s localhost:18080/observability
curl -s localhost:18080/providers
docker compose exec -T postgres psql -U llmops -d llmops -c "SELECT created_at::time(0), model, status, fallback_used FROM requests WHERE status <> '200' ORDER BY created_at DESC LIMIT 10"
```

> **Пастка, на яку я наступив (курс про неї не попереджає).** «Переконатися» через
> запитання, яке вже лежить у кеші, **не працює**: cache-hit не йде в gateway, тому
> circuit breaker не отримує пробного запиту і `/providers` показує `down` навіть після
> того, як провайдер ожив. Перевіряй **унікальним** текстом:
> ```bash
> curl -s -X POST localhost:18080/chat -H "Content-Type: application/json" \
>   -d '{"message":"health probe 2026-08-04-1743"}'
> curl -s localhost:18080/providers
> ```

## 2. Якість просіла

| Такт | Що саме |
|---|---|
| **Симптом** | скарги «бот верзе дурниці» при зелених HTTP-метриках; `error_rate_pct` у нормі |
| **Підтвердити** | `prompt_version` у свіжих рядках лога — чи збігається сплеск скарг з активацією нової версії; локальний прогін evals — червоний? |
| **Дія** | rollback на попередню версію — **одна операція**: `POST /prompts/{стара}/activate`. Кеш чиститься сам: версія промпта — частина ключа |
| **Переконатися** | прогін evals зелений; скарги стихають |

```bash
curl -s localhost:18080/prompts
docker compose exec -T postgres psql -U llmops -d llmops -c "SELECT created_at::time(0), prompt_version, count(*) FROM requests GROUP BY 1,2 ORDER BY 1 DESC LIMIT 10"
curl -s -X POST localhost:18080/prompts/v2/activate
MSYS_NO_PATHCONV=1 docker run --rm --network llmops-student_default \
  -e SERVICE_URL=http://service:8080 -e PYTHONUTF8=1 \
  -v "C:/Work/llmops-student/evals:/e:ro" python:3.12-slim \
  python /e/run.py --dataset /e/golden.jsonl --threshold 12
```

## 3. Вартість тече

| Такт | Що саме |
|---|---|
| **Симптом** | плитка «Вартість сьогодні» наближається до бюджету; або рахунок росте без росту трафіку |
| **Підтвердити** | три зрізи (L04 блок 06): по днях → коли почалося; по моделях → чи не поїхав роутер у strong; по `prompt_version` із середніми `prompt_tokens` → чи не подовшав промпт |
| **Дія** | якщо винен роутер — правка `Route()` (реліз через гейт); якщо промпт — rollback версії; якщо `cost_usd IS NULL` — у прайсі немає моделі з конфіга (це ловить `ci/static_checks.py`) |
| **Переконатися** | той самий зріз показує спад; `SELECT count(*) FROM requests WHERE cost_usd IS NULL AND created_at::date = CURRENT_DATE` → 0 |

```bash
docker compose exec -T postgres psql -U llmops -d llmops -c "SELECT created_at::date, count(*), SUM(cost_usd) FROM requests GROUP BY 1 ORDER BY 1"
docker compose exec -T postgres psql -U llmops -d llmops -c "SELECT model, count(*), SUM(cost_usd) FROM requests GROUP BY 1 ORDER BY 3 DESC NULLS LAST"
docker compose exec -T postgres psql -U llmops -d llmops -c "SELECT prompt_version, count(*), round(avg(prompt_tokens)) FROM requests GROUP BY 1"
```

## 4. Черга approvals протухла

| Такт | Що саме |
|---|---|
| **Симптом** | користувач написав «поверніть гроші» і чекає; заявка висить |
| **Підтвердити** | `curl -s localhost:18080/approvals` → `waiting_sec` великий |
| **Дія** | оператор ухвалює рішення: `approve` або `reject`. Обидва — рішення, обидва лічаться |
| **Переконатися** | `pending` порожній або `waiting_sec` малий |

> **Відомий дефект мого контуру** (курс визнає його як компроміс, L08): черга живе
> в пам'яті процесу. Рестарт сервісу **губить незакриті заявки**. Тому перед
> `docker compose up --build -d service` завжди перевіряй `/approvals` — і не
> рестартуй з непорожньою чергою. Доросла форма — таблиця в базі; це перше, що
> я зробив би, несучи цей механізм у реальну систему.

---

## Rollback-критерії (записані ДО викочування, L11 блок 09)

```
Відкочуємо, якщо:
  eval pass rate < 12/13                      (прогін після деплою)
  або fallback_events зростає                 протягом 15 хв
  або error_rate_pct > 5%                     протягом 15 хв
  або today_usd > 80% budget_usd              до 18:00
Хто смикає: черговий, без погоджень.
Як (промпт):  POST /prompts/{попередня}/activate     — секунди, без деплою
Як (код):     revert PR + merge (гейт перевірить)    — хвилини
```

## Постмортем: два питання, без яких це хроніка, а не розбір

1. **Що зробимо, щоб не повторилося?** Відповідь мусить бути механізмом або кейсом
   у `golden.jsonl`, а не «будемо уважнішими».
2. **Як ми дізналися про інцидент?** Якщо відповідь «зі скарг» — у спостережуваності
   діра: інцидент такого класу мусить спершу з'явитися на консолі.

Розбираємо механізми, а не людей: не «хто активував погану версію», а «чому гейт її
пропустив і якого кейса бракувало в датасеті».
