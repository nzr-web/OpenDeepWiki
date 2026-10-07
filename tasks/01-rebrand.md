# 01. Ребрендинг видимой части: OpenDeepWiki → NzrAiWiki

## Зачем

Форк nzr-web/OpenDeepWiki становится продуктом **NzrAiWiki** (лицензия MIT это
позволяет). Пользователь не должен видеть чужой бренд, чужого спонсора, чужие
ссылки и подсказку с учётными данными админа.

Главное ограничение: **форк продолжает подтягивать апстрим** (AIDotNet/OpenDeepWiki,
коммиты почти ежедневно). Поэтому меняется только видимое, и каждая правка в чужом
файле минимальна: одна строка вместо переписанного блока. Внутренние имена не
трогаем: namespace'ы `OpenDeepWiki.*`, `.csproj`, каталоги, `.sln`, таблицы БД,
`window.OpenDeepWiki` в `web/public/embed.js`, ключи localStorage, cookie
`deepwiki_token`, id провайдеров.

## Где работать

Задача выполняется в **отдельном worktree** `C:\Users\Nazar\Работа\OpenDeepWiki-wt01`
(ветка `nzr/01-rebrand`, ответвлена от `nzr/rebrand-ru` с этими ТЗ). В основном дереве параллельно идёт задача 02.
Если `node_modules` в `web/` нет — `npm ci` в своём worktree.

## Что сделать

1. **Константа бренда.** Создать `web/lib/brand.ts`:
   `BRAND_NAME = "NzrAiWiki"`, `BRAND_REPO_URL = "https://github.com/nzr-web/OpenDeepWiki"`.
   В `.ts`/`.tsx` имя и ссылку брать только отсюда, литералы не писать.
2. **Название во фронте** — заменить на `BRAND_NAME`:
   - `web/lib/repo-seo.ts` (`SITE_NAME`), от него питается `<title>` в `web/app/layout.tsx`;
   - `web/app/sidebar.tsx`: alt и подпись логотипа;
   - `web/components/admin/admin-sidebar.tsx`;
   - `web/app/(main)/settings/page.tsx`: фолбэк `productName`;
   - `web/lib/profile-api.ts`: оба фолбэка `productName`.
3. **Бэкенд.** `src/OpenDeepWiki/Endpoints/SystemEndpoints.cs:37`: `productName = "NzrAiWiki"`.
   Других правок в `src/` нет.
4. **Сайдбар `web/app/sidebar.tsx`:**
   - ссылку на GitHub заменить на `BRAND_REPO_URL`;
   - пункт Feishu (ссылка на feishu.cn и всплывающий QR) **скрыть, а не удалять**:
     обернуть его JSX в `{false && ( … )}` без переиндентации содержимого и поставить
     над ним комментарий `{/* NzrAiWiki: Feishu link hidden */}`. `FeishuIcon` и
     `showFeishuQr` остаются: так дифф с апстримом будет в две строки.
5. **Спонсорский баннер routin.ai.** Выключить в `web/components/with-announcement.tsx`:
   закомментировать строку `<AnnouncementBanner />` и её импорт, добавить пометку
   `// NzrAiWiki: sponsor banner disabled`. `announcement-banner.tsx` и тексты
   `common.announcement.*` в словарях не трогать.
6. **Страница входа `web/app/auth/page.tsx`.**
   - Поле там уже пустое, но в подсказках полей (строки ~207 и ~227) показываются
     `admin@routin.ai` и `Admin@123` из `defaultSeedAdmin` (строки ~15–18).
   - Подсказки взять из существующих ключей `t("authUi.emailPlaceholder")` и
     `t("authUi.passwordPlaceholder")`. Сначала проверить, что ключи есть в
     `en/auth-ui.json`. Если ключи называются иначе — использовать существующие и
     указать это в отчёте.
   - `defaultSeedAdmin` удалить.
   - Учётные данные админа не должны попадать ни в разметку, ни в бандл.
   - `DbInitializer.cs` не трогать.
7. **Ссылка «поделиться», `web/components/chat/chat-panel.tsx:154`.** В серверной ветке
   (`typeof window === "undefined"`) литерал `https://opendeepwiki.com/share/…` заменить
   на относительный `/share/${shareResult.shareId}`. Ровно одна строка.
8. **Тексты в словарях** локалей `en, zh, ja, ko, de, es, fr, pt-BR`, только значения,
   ключи не трогать. Каталог `ru/` не трогать, даже если он появился.
   - `OpenDeepWiki`, `DeepWiki`, `KeboolaDeepWiki` в отображаемых текстах → `NzrAiWiki`:
     `auth-ui.json` (title, copyright, registerDesc), `chat.json` (имя агента),
     `home.json` (MCP-тексты, строки ~40–53), `admin.json` (~867, «DeepWiki admin URL»).
   - Не трогать: плейсхолдеры-примеры (`Example: OpenDeepWiki`, `D:\repos\OpenDeepWiki`,
     `my-deepwiki-app`) и `common.announcement.*`.
9. **JSON-конфиг MCP для копирования**, `web/components/integrations-dialog.tsx:~48`.
   Если пользователь копирует оттуда конфиг с именем сервера `deepwiki`, заменить имя
   на `nzraiwiki`. Это одна строка. URL не трогать.
10. **LICENSE.** Строку `Copyright (c) 2025 AIDotNet` оставить, под ней добавить
    `Copyright (c) 2026 nzr-web`. Больше ничего не менять.
11. **README.md.** В самый верх, над текущим содержимым, добавить блок на русском,
    3–6 строк:
    - «NzrAiWiki — форк [OpenDeepWiki](https://github.com/AIDotNet/OpenDeepWiki) (MIT)»;
    - что это: генерация вики по репозиториям с помощью LLM, MCP-доступ;
    - вход по умолчанию: `admin@routin.ai` / `Admin@123`, сменить после первого входа.

    Остальной README, `README.zh-CN.md` и `docs/` не трогать.

## Чего в этом ТЗ нет (не делать попутно)

- Переименование проектов, namespace'ов, каталогов, Docker-образов в `compose.yaml`.
- `web/components/admin/provider-icons.tsx`: routin-ai там — пресет провайдера.
- Логотип и `web/public/favicon.png` остаются чужими, их заменят отдельно.
- Видимые старые имена на бэкенде остаются на отдельную задачу:
  `Chat/Execution/AgentExecutor.cs:~347` («You are DeepWiki»),
  `Infrastructure/DbInitializer.cs:~251,268` («OpenDeepWiki Global MCP»),
  `MCP/McpGlobalTools.cs:~26`.
- Всё, что относится к задаче 02: `web/i18n/config.ts`, `request.ts`, `middleware.ts`,
  `web/i18n/messages/ru/`, `web/app/global-error.tsx`,
  `web/components/repo/markdown-renderer.tsx`.
- Попутные рефакторинги.

## Ворота (выполнить и привести вывод в отчёте)

Базовая линия снята на `2b147c7`: tsc — 0 ошибок, vitest — 4 файла / 18 тестов
зелёные. Lint на базе — 146 замечаний (80 errors, 66 warnings), снимок лежит в
`tasks/baseline-lint.txt`.

- `cd web && npx tsc --noEmit` — 0 ошибок.
- `cd web && npx vitest run` — 18/18.
- Lint без новых **ошибок и предупреждений**:
  `node ../tasks/lint-snapshot.mjs > /tmp/lint.txt && diff ../tasks/baseline-lint.txt /tmp/lint.txt`.
  Строк с `>` быть не должно. Строки `<` (исчезнувшие замечания) допустимы.
- `git grep -n -iE "deepwiki|routin|feishu" -- web ':!web/package-lock.json' ':!web/bun.lock'`.
  В отчёте каждое оставшееся вхождение отнести к одному из ожидаемых:
  - `embed.js`;
  - `provider-icons.tsx`;
  - `STORAGE_KEY` / `SPONSOR_URL` в `announcement-banner.tsx`;
  - cookie `deepwiki_token` (`web/lib/auth-api.ts`);
  - платформа `feishu` в `admin/chat-providers`;
  - ключи `sidebar.feishu`, `scanQrCode`;
  - `common.announcement.*`;
  - плейсхолдеры-примеры;
  - скрытый блок Feishu в сайдбаре.

  Всё, что не подходит ни под один пункт, — недоделка.
- `git grep -n "NzrAiWiki" -- web ':!web/i18n' ':!web/lib/brand.ts'` — пусто.
- `git grep -n "Admin@123\|admin@routin" -- web` — пусто.
- `git grep -n "<AnnouncementBanner" -- web` — только закомментированная строка.
- Для пунктов 4–7 привести дифф строк.

Коммитить не надо, правки оставить в дереве worktree.
