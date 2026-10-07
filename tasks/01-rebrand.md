# 01. Ребрендинг видимой части: OpenDeepWiki → NzrAiWiki

## Зачем

Форк nzr-web/OpenDeepWiki становится продуктом **NzrAiWiki**. Лицензия MIT позволяет,
но пользователь не должен видеть чужой бренд, чужого спонсора и чужие ссылки.

Главное ограничение: **форк продолжает подтягивать апстрим** (AIDotNet/OpenDeepWiki,
коммиты почти ежедневно). Поэтому меняется только то, что видит человек. Внутренние
имена не трогаем: namespace'ы `OpenDeepWiki.*`, `.csproj`, каталоги, `.sln`, имена
БД-таблиц, `window.OpenDeepWiki` в `web/public/embed.js`, ключи localStorage, id
провайдеров. Каждая правка в чужом файле должна быть минимальной: одна строка
вместо переписанного блока.

## Что сделать

1. **Константа бренда.** Создать `web/lib/brand.ts`:
   `BRAND_NAME = "NzrAiWiki"`, `BRAND_REPO_URL = "https://github.com/nzr-web/OpenDeepWiki"`.
   Все правки во фронте ниже берут имя и ссылку отсюда, литералами не пишут.
2. **Название во фронте** — заменить на `BRAND_NAME`:
   - `web/lib/repo-seo.ts` (`SITE_NAME`), от него питается `<title>` в `web/app/layout.tsx`;
   - `web/app/sidebar.tsx` (alt и подпись логотипа);
   - `web/components/admin/admin-sidebar.tsx`;
   - `web/app/(main)/settings/page.tsx` (фолбэк `productName`);
   - `web/lib/profile-api.ts` (оба фолбэка `productName`).
3. **Бэкенд.** `src/OpenDeepWiki/Endpoints/SystemEndpoints.cs:37`: `productName = "NzrAiWiki"`.
   Других правок в `src/` в этой задаче нет.
4. **Сайдбар (`web/app/sidebar.tsx`):**
   - ссылку на GitHub заменить на `BRAND_REPO_URL`;
   - пункт Feishu (ссылка на feishu.cn и всплывающий QR) **убрать**: удалить JSX пункта
     и ставшие неиспользуемыми `FeishuIcon`, `showFeishuQr`, импорты. `web/public/fieshu.png`
     не удалять.
5. **Спонсорский баннер routin.ai.** Найти, где рендерится
   (`web/components/with-announcement.tsx`, `announcement-banner.tsx`), и выключить
   **в одной точке**, чтобы баннер не показывался нигде. Компоненты не удалять:
   апстрим их меняет, удаление даст конфликты.
6. **Страница входа `web/app/auth/page.tsx:16`.** Убрать предзаполненный
   `admin@routin.ai`, поле должно быть пустым. Дефолтного админа в
   `DbInitializer.cs` **не трогать**: смена email на существующей базе создаст
   второго админа.
7. **Ссылка «поделиться» в `web/components/chat/chat-panel.tsx:154`.** Сейчас она ведёт
   на чужой домен `opendeepwiki.com`. Строить её от текущего origin
   (`window.location.origin`), путь `/share/{id}` оставить. Сначала проверить, что
   такой маршрут во фронте есть. Если нет — оставить как есть и написать об этом в отчёте.
8. **Тексты в словарях** `web/i18n/messages/<все существующие локали>/*.json`, только
   значения, ключи не трогать:
   - `OpenDeepWiki`, `DeepWiki`, `KeboolaDeepWiki` в отображаемых текстах → `NzrAiWiki`,
     включая `auth-ui.json` (title, copyright, registerDesc), `chat.json` (имя агента),
     `home.json` (MCP-тексты), `admin.json`;
   - `© 2026 OpenDeepWiki…` → `© 2026 NzrAiWiki…`;
   - плейсхолдеры-примеры вроде `Example: OpenDeepWiki` и `D:\repos\OpenDeepWiki`
     можно оставить, это просто пример имени репозитория.
9. **LICENSE.** Строку `Copyright (c) 2025 AIDotNet` оставить. Под ней добавить
   `Copyright (c) 2026 nzr-web`. Больше ничего не менять.
10. **README.md.** В самый верх, над текущим содержимым, добавить короткий блок на
    русском: «NzrAiWiki — форк [OpenDeepWiki](https://github.com/AIDotNet/OpenDeepWiki)
    (MIT)», 2–4 строки о том, что это. Остальной README не переписывать.
    `README.zh-CN.md` и `docs/` не трогать.

## Чего не делать

- Не переименовывать проекты, namespace'ы, каталоги и Docker-образы в `compose.yaml`.
- Не трогать `web/components/admin/provider-icons.tsx`: routin-ai там — это пресет
  провайдера, а не бренд.
- Не добавлять локаль `ru`, это задача 02. Её делают параллельно с этой: файлы
  `web/i18n/config.ts`, `web/middleware.ts`, `web/i18n/request.ts` и каталог
  `web/i18n/messages/ru/` в этой задаче **не трогать**.
- Не делать попутных рефакторингов.

## Ворота (выполнить и привести вывод в отчёте)

- `cd web && npx tsc --noEmit` — без новых ошибок. Если ошибки есть и на чистом
  `main`, сравнить списки до и после.
- `cd web && npm run lint` — без новых ошибок относительно `main`.
- `cd web && npx vitest run` — тот же результат, что на `main`.
- Поиск `git grep -n -iE "opendeepwiki|routin|feishu\.cn" -- web ':!web/i18n/messages/*/admin.json'`
  — в отчёте перечислить, что осталось, и для каждого вхождения объяснить, почему
  оставлено (внутреннее имя, пресет провайдера и т.п.).

`node_modules` в `web/` может не быть — тогда сначала `npm ci`.
Коммитить не надо, правки оставить в дереве.
