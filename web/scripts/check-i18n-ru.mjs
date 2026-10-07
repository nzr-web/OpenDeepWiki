// NzrAiWiki: сверка русской локали с английской.
// Находит отсутствующие файлы и ключи, лишние ключи, расхождение переменных
// и дубликаты ключей. Код выхода 1, если что-то нашлось.
// Запуск: npm run i18n:check-ru
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "i18n", "messages");
const enDir = join(root, "en");
const ruDir = join(root, "ru");

const read = (p) => readFileSync(p, "utf8").replace(/^﻿/, "");

function flatten(obj, prefix = "", out = new Map()) {
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value && typeof value === "object") flatten(value, path, out);
    else out.set(path, String(value));
  }
  return out;
}

// Имена переменных верхнего уровня: {name} и {count, plural, ...} -> name, count.
// {{name}} (синтаксис i18next, встречается в апстримном en) считается той же переменной.
function variables(input) {
  const text = input.replace(/\{\{\s*([A-Za-z_]\w*)\s*\}\}/g, "{$1}");
  const vars = new Set();
  let depth = 0;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === "{") {
      if (depth === 0) {
        const m = /^\{\s*([A-Za-z_][\w]*)\s*[,}]/.exec(text.slice(i));
        if (m) vars.add(m[1]);
      }
      depth++;
    } else if (ch === "}") {
      depth = Math.max(0, depth - 1);
    }
  }
  return vars;
}

// Дубликаты ключей на одном уровне вложенности по сырому тексту.
function duplicateKeys(raw) {
  const dups = [];
  const stack = [new Set()];
  const re = /"((?:[^"\\]|\\.)*)"\s*:|[{}]/g;
  let m;
  while ((m = re.exec(raw))) {
    if (m[0] === "{") stack.push(new Set());
    else if (m[0] === "}") stack.pop();
    else {
      const level = stack[stack.length - 1];
      if (level.has(m[1])) dups.push(m[1]);
      level.add(m[1]);
    }
  }
  return dups;
}

const problems = [];
for (const file of readdirSync(enDir).filter((f) => f.endsWith(".json")).sort()) {
  const ruPath = join(ruDir, file);
  if (!existsSync(ruPath)) {
    problems.push(`${file}: нет файла в ru`);
    continue;
  }
  const ruRaw = read(ruPath);
  const en = flatten(JSON.parse(read(join(enDir, file))));
  const ru = flatten(JSON.parse(ruRaw));

  for (const key of en.keys()) if (!ru.has(key)) problems.push(`${file}: нет ключа ${key}`);
  for (const key of ru.keys()) if (!en.has(key)) problems.push(`${file}: лишний ключ ${key}`);
  for (const [key, enValue] of en) {
    if (!ru.has(key)) continue;
    const a = [...variables(enValue)].sort().join(",");
    const b = [...variables(ru.get(key))].sort().join(",");
    if (a !== b) problems.push(`${file}: ${key} — переменные en {${a}} ≠ ru {${b}}`);
  }
  for (const key of duplicateKeys(ruRaw)) problems.push(`${file}: дубликат ключа ${key}`);
}

if (problems.length) {
  console.log(problems.join("\n"));
  console.log(`\nНайдено: ${problems.length}`);
  process.exit(1);
}
console.log("ru совпадает с en: ключи, переменные, дубликатов нет");
