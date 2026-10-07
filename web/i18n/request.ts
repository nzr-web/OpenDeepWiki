import { getRequestConfig } from 'next-intl/server';
import { deepMerge, type MessageTree } from './merge-messages';
import { uiLocales, defaultUiLocale, uiLocaleNames, type UiLocale } from './config';

// Re-export for backward compatibility with existing imports
export const locales = uiLocales;
export type Locale = UiLocale;
const defaultLocale = defaultUiLocale;
export const localeNames = uiLocaleNames;

const namespaces = [
  'common',
  'theme',
  'sidebar',
  'auth',
  'auth-ui',
  'home',
  'ui',
  'recommend',
  'mindmap',
  'settings',
  'profile',
  'apps',
  'admin',
  'chat',
  'subscribe',
] as const;

// 非英文语言文件缺失时返回 undefined，对应 namespace 整体回退到英文
async function importMessages(locale: Locale, name: string): Promise<MessageTree | undefined> {
  try {
    return (await import(`./messages/${locale}/${name}.json`)).default;
  } catch {
    return undefined;
  }
}

// 动态加载所有翻译文件；非英文语言缺失的文件或键回退到英文
async function loadMessages(locale: Locale) {
  const entries = await Promise.all(
    namespaces.map(async (name) => {
      const en: MessageTree = (await import(`./messages/en/${name}.json`)).default;
      if (locale === 'en') return [name, en] as const;
      return [name, deepMerge(en, await importMessages(locale, name))] as const;
    })
  );

  const byName = Object.fromEntries(entries);
  const { 'auth-ui': authUi, ...rest } = byName;
  return { ...rest, authUi };
}

export default getRequestConfig(async ({ requestLocale }) => {
  // Take locale from request, fall back to default locale
  let locale = await requestLocale;
  
  if (!locale || !locales.includes(locale as Locale)) {
    locale = defaultLocale;
  }

  return {
    locale,
    messages: await loadMessages(locale as Locale),
  };
});
