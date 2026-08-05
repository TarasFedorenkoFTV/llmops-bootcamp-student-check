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
