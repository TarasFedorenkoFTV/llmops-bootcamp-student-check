"""[W6] Дешеві механічні перевірки для eval-гейта (L11 блок 07).
Ловлять клас поломок, які до моделі не доходять, — за секунди й без стека.
Автор L11 наводить власний випадок: гейт пропустив модель без ціни, і облік
вартості тихо зламався. Ці перевірки саме про такі "тихі" поломки.
"""
import io, json, re, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

problems = []
program = open("service/Program.cs", encoding="utf-8").read()
cfg = open("gateway/litellm-config.yaml", encoding="utf-8").read()

# 1. прайс покриває моделі, на які реально шле роутер
priced = set(re.findall(r'\["([\w.-]+)"\]\s*=\s*\(', program))
routed = set()
m = re.search(r'return escalation \? "([\w.-]+)" : "([\w.-]+)"', program)
if m: routed |= {m.group(1), m.group(2)}
for mm in re.findall(r'"(mock(?:-[\w]+)?)"', program):
    if mm != "mock-dead": routed.add(mm)
missing = sorted(routed - priced)
if missing: problems.append(f"модель(і) без ціни у прайсі -> cost_usd стане null: {missing}")

# 2. моделі, на які шле сервіс, існують у gateway-конфізі
declared = set(re.findall(r"model_name:\s*([\w.-]+)", cfg))
unknown = sorted(m for m in routed | {"mock-dead"} if m not in declared)
if unknown: problems.append(f"сервіс шле на модель, якої нема в litellm-config: {unknown}")

# 3. у діфі немає секретів
secret = re.compile(r"(sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----)")
for path in ["service/Program.cs", "gateway/litellm-config.yaml", "docker-compose.yml", "db/schema.sql"]:
    for i, line in enumerate(open(path, encoding="utf-8"), 1):
        if secret.search(line): problems.append(f"схоже на секрет у {path}:{i}")

# 4. golden dataset валідний і стартові кейси на місці
cases = [json.loads(x) for x in open("evals/golden.jsonl", encoding="utf-8") if x.strip()]
ids = [c["id"] for c in cases]
if len(ids) != len(set(ids)): problems.append("дублікати id у golden.jsonl")
for c in cases:
    if not ({"expect", "forbid", "expect_refusal"} & set(c)):
        problems.append(f"кейс {c['id']} без жодного очікування — завжди зелений")
lost = sorted({"faq-1","faq-2","order-1","refund-1","safety-1","caps-1"} - set(ids))
if lost: problems.append(f"видалені стартові кейси (hw5 забороняє): {lost}")

print(f"static checks: {len(cases)} eval-кейсів, {len(priced)} моделей у прайсі")
if problems:
    for p in problems: print(f"  FAIL: {p}")
    sys.exit(1)
print("static checks: ok")
