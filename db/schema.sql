-- Базова схема. Студент розширює під власні потреби.

CREATE TABLE IF NOT EXISTS requests (
    request_id      UUID PRIMARY KEY,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    model           TEXT NOT NULL,
    provider        TEXT,
    prompt_version  TEXT,
    latency_ms      INTEGER,
    prompt_tokens   INTEGER,
    completion_tokens INTEGER,
    cost_usd        NUMERIC(10, 6),
    status          TEXT,
    -- [W3+] мої розширення (гайд: "студент розширює під власні потреби"):
    -- hit кешу інакше не відрізнити від безкоштовного виклику, а лічильники
    -- в пам'яті гинуть на кожному ребілді — цифри для W5 мусять жити в лозі.
    cache_hit       BOOLEAN NOT NULL DEFAULT false,
    fallback_used   BOOLEAN NOT NULL DEFAULT false,
    -- [W3] перший крок аудиту дій (L06): "чому створився цей тікет" = один SELECT
    tool            TEXT,
    -- [W4] policy log: що заблокував guardrail і чому
    blocked         TEXT
);

CREATE TABLE IF NOT EXISTS prompts (
    name        TEXT NOT NULL,
    version     TEXT NOT NULL,
    body        TEXT NOT NULL,
    active      BOOLEAN NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (name, version)
);

CREATE INDEX IF NOT EXISTS idx_requests_created_at ON requests (created_at);
CREATE INDEX IF NOT EXISTS idx_requests_model ON requests (model);

-- [W1/hw1 п.2] seed реєстру промптів.
-- v1 навмисно "погана": без маркера "support" mock відповідає "не знаю" —
-- на цьому відтворюється регресія (L02 блок 08).
-- created_at рознесений явно: GetActivePrompt робить ORDER BY created_at DESC,
-- а now() у межах однієї транзакції однаковий для всіх рядків INSERT.
INSERT INTO prompts (name, version, body, active, created_at) VALUES
    ('support-system', 'v1', 'You are an assistant.', false, now() - interval '2 minutes'),
    ('support-system', 'v2', 'You are a support assistant. Be concise and helpful.', true, now() - interval '1 minute')
ON CONFLICT (name, version) DO NOTHING;
