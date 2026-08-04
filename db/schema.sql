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
    -- [W3] мій додаток до схеми: hit кешу — це рядок лога з нульовими токенами,
    -- і без окремого прапорця його не відрізнити від справжнього безкоштовного виклику.
    -- Потрібно для cache_hit_pct у /observability (W5): лічильники в пам'яті гинуть при рестарті.
    cache_hit       BOOLEAN NOT NULL DEFAULT false,
    -- [W4] чи спрацював fallback — для плитки FALLBACK у консолі (W5)
    fallback_used   BOOLEAN NOT NULL DEFAULT false,
    -- [W3] перший крок аудиту дій (L06 блок 09): який інструмент викликано.
    -- Питання "чому створився цей тікет" мусить закриватися одним SELECT.
    tool            TEXT,
    -- [W4] policy log: що заблокував guardrail і чому. Спрацювання guardrail — це подія
    -- для аудиту ("чому бот відмовив клієнту"), а не 5xx.
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
-- created_at рознесений явно, бо GetActivePrompt робить ORDER BY created_at DESC:
-- у межах одного INSERT now() однаковий для всіх рядків, і порядок був би невизначений.
INSERT INTO prompts (name, version, body, active, created_at) VALUES
    ('support-system', 'v1', 'You are an assistant.', true, now() - interval '2 minutes'),
    ('support-system', 'v2', 'You are a support assistant. Be concise and helpful.', false, now() - interval '1 minute')
ON CONFLICT (name, version) DO NOTHING;
