import { describe, expect, it } from "vitest";
import { defaultUiLocale, isUiLocale, resolveUiLocaleFromWikiLanguage } from "../config";

describe("i18n config (NzrAiWiki)", () => {
  it("defaults the interface to Russian", () => {
    expect(defaultUiLocale).toBe("ru");
  });

  it("recognises ru as a UI locale", () => {
    expect(isUiLocale("ru")).toBe(true);
  });

  it("maps the ru wiki language to the ru UI locale", () => {
    expect(resolveUiLocaleFromWikiLanguage("ru")).toBe("ru");
  });
});
