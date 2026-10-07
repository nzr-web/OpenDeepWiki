// Снимок замечаний eslint в виде "файл | уровень | правило", отсортированный.
// Запуск из web/: node ../tasks/lint-snapshot.mjs > out.txt
// Сравнение с базой: diff ../tasks/baseline-lint.txt out.txt — новые строки с "+" и есть новые замечания.
import { execSync } from "node:child_process";

let raw;
try {
  raw = execSync("npx eslint . -f json", { encoding: "utf8", maxBuffer: 64 * 1024 * 1024, stdio: ["ignore", "pipe", "ignore"] });
} catch (e) {
  raw = e.stdout; // eslint выходит с кодом 1, если есть ошибки
}

const lines = [];
for (const file of JSON.parse(raw)) {
  const rel = file.filePath.split("\\").join("/").replace(/^.*?\/web\//, "web/");
  for (const m of file.messages) {
    lines.push(`${rel} | ${m.severity === 2 ? "error" : "warn"} | ${m.ruleId ?? "parse"}`);
  }
}
lines.sort();
process.stdout.write(lines.join("\n") + "\n");
