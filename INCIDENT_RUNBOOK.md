# Incident Runbook · SupportGW

Формат кожного сценарію: **Симптом → Підтвердити → Дія → Переконатися**.
Команди виконувані дослівно. Якщо порти зсунуто локальним override-файлом,
задай свій порт сервісу один раз тут (стандартний — 8080):

```bash
SERVICE=http://localhost:8080
```

---

## Сценарій 1: провайдер ліг (5xx / недоступний)

**Симптом.** Скарги «бот вибачається»; на консолі росте ERROR RATE і FALLBACK.

**Підтвердити.**

```bash
curl -s $SERVICE/observability
# fallback_events росте між двома викликами, error_rate_pct > звичного фону
curl -s $SERVICE/providers
# {"providers":[{"name":"mock","status":"degraded"}]} після 3+ поспіль падінь
```

У лозі — статуси 503:

```bash
docker compose exec postgres psql -U llmops -d llmops -c \
  "SELECT status, count(*) FROM requests WHERE created_at > now() - interval '15 minutes' GROUP BY status;"
```

**Дія.** Руками — нічого: fallback і graceful degradation вже працюють,
користувач отримує ввічливу заглушку, не 500. Стежити за трендом
`fallback_events` і `error_rate_pct`.

**Переконатися (відновлення).** Контрольне питання — **нове**, якого не може
бути в кеші (кеш-хіт віддасть стару відповідь з latency_ms=0 і «зазеленить»
демо посеред інциденту):

```bash
curl -s -X POST $SERVICE/chat -H "Content-Type: application/json" \
  -d '{"message":"control question after incident 4711"}'
# нормальна відповідь (не заглушка) => провайдер піднявся
curl -s $SERVICE/providers   # знову "ok"
curl -s $SERVICE/observability
# fallback_events далі НЕ росте (лічильник кумулятивний, він не спадає ніколи —
# спад показує error_rate_pct)
```

**Гігієна після відновлення.** Якщо в період збою в кеш могли потрапити
заглушки — скинути кеш. У нашій реалізації заглушки в кеш не пишуться
(умова `ok && status==200`), тож окремого скидання не потрібно; якщо
сумніваєшся — `docker compose restart service` (кеш у пам'яті процесу).

---

## Сценарій 2: якість просіла (HTTP зелений, відповіді — ні)

**Симптом.** Скарги «бот верзе дурниці», усі HTTP-метрики зелені.

**Підтвердити.**

```bash
docker compose exec postgres psql -U llmops -d llmops -c \
  "SELECT prompt_version, count(*) FROM requests WHERE created_at > now() - interval '1 hour' GROUP BY prompt_version;"
# чи збігається сплеск скарг з активацією нової версії?
```

Локальний eval-прогін (Windows/Git Bash; `<project>` — ім'я теки репо):

```bash
MSYS_NO_PATHCONV=1 docker run --rm --network <project>_default \
  -e SERVICE_URL=http://service:8080 -e PYTHONUTF8=1 \
  -v "$(pwd -W)/evals:/e:ro" python:3.12-slim \
  python /e/run.py --dataset /e/golden.jsonl --threshold 9
# червоний? => регресія промпта підтверджена
```

**Дія.** Rollback на попередню версію однією командою:

```bash
curl -s -X POST $SERVICE/prompts/v2/activate
```

**Переконатися.** Той самий eval-прогін — зелений; скарги стихають.
Кеш чистити не треба: текст промпта — частина ключа, старі записи
стали недосяжними автоматично.

---

## Сценарій 3: вартість тече

**Симптом.** Плитка «Вартість сьогодні» вища за звичний фон / наближається до бюджету.

**Підтвердити** (розслідування трьома запитами, L04):

```bash
docker compose exec postgres psql -U llmops -d llmops -c \
  "SELECT created_at::date, SUM(cost_usd) FROM requests GROUP BY 1 ORDER BY 1;"        # коли почалося
docker compose exec postgres psql -U llmops -d llmops -c \
  "SELECT model, count(*), SUM(cost_usd) FROM requests WHERE created_at::date = CURRENT_DATE GROUP BY model;"  # роутер?
docker compose exec postgres psql -U llmops -d llmops -c \
  "SELECT prompt_version, AVG(prompt_tokens) FROM requests WHERE created_at::date = CURRENT_DATE GROUP BY prompt_version;"  # довший промпт?
```

**Дія.** За знахідкою: перекошений розподіл моделей → перевірити маркери
роутера; зрослі prompt_tokens → rollback версії промпта (сценарій 2).

**Переконатися.** Той самий зріз за годину: сума за годину повертається до фону.

---

## Rollback-критерії (пишуться ДО викочування, L11)

Відкочуємо, якщо:
- eval pass rate < 9/10 (прогін після деплою), **або**
- fallback_events зростає протягом 15 хв, **або**
- error_rate_pct > 5% протягом 15 хв.

Хто смикає: черговий, без погоджень.
Як: `curl -X POST $SERVICE/prompts/<попередня>/activate` — одна команда, секунди.

---

## Інцидент-демо (прогнано наживо, таймлайн нижче)

Команди відтворення:

```bash
# норма
curl -s -X POST $SERVICE/chat -H "Content-Type: application/json" -d '{"message":"Як скинути пароль?"}'
# інцидент: 4 запити зі збоєм провайдера
for i in 1 2 3 4; do curl -s -o /dev/null -w "%{http_code} " -X POST $SERVICE/chat \
  -H "Content-Type: application/json" -d '{"message":"__fail_503 incident"}'; done; echo
# консоль/метрики під інцидентом
curl -s $SERVICE/observability && curl -s $SERVICE/providers
# відновлення: НОВЕ питання (не з кешу)
curl -s -X POST $SERVICE/chat -H "Content-Type: application/json" -d '{"message":"control question 4711"}'
curl -s $SERVICE/providers
```

Таймлайн фактичного прогону — див. README, розділ «Інцидент-демо».
