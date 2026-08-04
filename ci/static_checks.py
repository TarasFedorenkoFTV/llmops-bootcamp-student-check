"""[W6] Дешеві механічні перевірки для eval-гейта (L11 блок 07).

Ловлять клас поломок, які до моделі навіть не доходять, — за секунди й без стека.
Автор курсу описав власний випадок: гейт пропустив реліз, де в конфізі з'явилася
модель без ціни. Все зелене, а облік вартості тихо перестав знати правду.
"""
import io
import json
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

problems = []

# --- 1. прайс покриває всі моделі, на які реально шле роутер -----------------
program = open("service/Program.cs", encoding="utf-8").read()
priced = set(re.findall(r'\["([\w.-]+)"\]\s*=\s*\(', program))
# моделі, які роутер може обрати (літерали в Route + ланцюгу fallback)
routed = set(re.findall(r'return escalation \? "([\w.-]+)" : "([\w.-]+)"', program)[0]) \
    if re.search(r'return escalation \?', program) else set()
for m in re.findall(r'"(mock(?:-[\w]+)?)"', program):
    if m != "mock-dead":  # свідомо мертвий endpoint для інцидент-демо, не для трафіку
        routed.add(m)
missing = sorted(routed - priced)
if missing:
    problems.append(f"модель(і) без ціни у прайсі -> cost_usd стане null: {missing}")

# --- 2. моделі з gateway-конфіга, на які шле сервіс, існують у конфізі -------
cfg = open("gateway/litellm-config.yaml", encoding="utf-8").read()
declared = set(re.findall(r"model_name:\s*([\w.-]+)", cfg))
unknown = sorted(m for m in routed | {"mock-dead"} if m not in declared)
if unknown:
    problems.append(f"сервіс шле на модель, якої немає в litellm-config: {unknown}")

# --- 3. промпт-плейсхолдери: жодного невідомого ------------------------------
# {servce} замість {service} -> підстановка тихо не відбувається
schema = open("db/schema.sql", encoding="utf-8").read()
known_placeholders = {"service", "lang"}
for ph in set(re.findall(r"\{(\w+)\}", schema)):
    if ph not in known_placeholders:
        problems.append(f"невідомий плейсхолдер у промпті: {{{ph}}}")

# --- 4. у діфі немає ключів і секретів ---------------------------------------
secret_pat = re.compile(r"(sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----)")
for path in ["service/Program.cs", "gateway/litellm-config.yaml",
             "docker-compose.yml", "db/schema.sql"]:
    for i, line in enumerate(open(path, encoding="utf-8"), 1):
        if secret_pat.search(line):
            problems.append(f"схоже на секрет у {path}:{i}")

# --- 5. golden dataset валідний і не звужений --------------------------------
cases = [json.loads(x) for x in open("evals/golden.jsonl", encoding="utf-8") if x.strip()]
ids = [c["id"] for c in cases]
if len(ids) != len(set(ids)):
    problems.append("дублікати id у golden.jsonl")
for c in cases:
    if not ({"expect", "forbid", "expect_refusal"} & set(c)):
        problems.append(f"кейс {c['id']} без жодного очікування — завжди зелений")
required = {"faq-1", "faq-2", "order-1", "refund-1", "safety-1", "caps-1"}
lost = sorted(required - set(ids))
if lost:
    problems.append(f"видалені стартові кейси (hw5 забороняє): {lost}")

print(f"static checks: {len(cases)} eval-кейсів, {len(priced)} моделей у прайсі")
if problems:
    for p in problems:
        print(f"  FAIL: {p}")
    sys.exit(1)
print("static checks: ok")
