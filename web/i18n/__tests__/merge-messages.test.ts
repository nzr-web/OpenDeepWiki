import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { deepMerge, type MessageTree } from "../merge-messages";

const messagesDir = join(__dirname, "..", "messages");
const load = (locale: string, file: string): MessageTree =>
  JSON.parse(readFileSync(join(messagesDir, locale, file), "utf8").replace(/^﻿/, ""));

// Первый вложенный объект, в котором не меньше двух строковых ключей.
function findNested(tree: MessageTree): { section: string; keys: [string, string] } {
  for (const [section, value] of Object.entries(tree)) {
    if (typeof value !== "object") continue;
    const keys = Object.entries(value).filter(([, v]) => typeof v === "string").map(([k]) => k);
    if (keys.length >= 2) return { section, keys: [keys[0], keys[1]] };
  }
  throw new Error("no nested section with two string keys");
}

describe("deepMerge with real dictionaries", () => {
  const en = load("en", "common.json");
  const ru = load("ru", "common.json");
  const { section, keys: [removed, kept] } = findNested(ru);

  it("fills a key missing from ru with the English value and keeps ru neighbours", () => {
    const partial = structuredClone(ru);
    delete (partial[section] as MessageTree)[removed];

    const merged = deepMerge(en, partial);
    const mergedSection = merged[section] as MessageTree;

    expect(mergedSection[removed]).toBe((en[section] as MessageTree)[removed]);
    expect(mergedSection[kept]).toBe((ru[section] as MessageTree)[kept]);
    expect(mergedSection[kept]).not.toBe((en[section] as MessageTree)[kept]);
  });

  it("returns a copy of en when the locale file is missing", () => {
    const merged = deepMerge(en, undefined);
    expect(merged).toEqual(en);
    expect(merged).not.toBe(en);
  });

  it("does not mutate its arguments", () => {
    const enBefore = structuredClone(en);
    const ruBefore = structuredClone(ru);
    deepMerge(en, ru);
    expect(en).toEqual(enBefore);
    expect(ru).toEqual(ruBefore);
  });
});
