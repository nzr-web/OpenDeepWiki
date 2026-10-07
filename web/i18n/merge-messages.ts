export type MessageTree = { [key: string]: string | MessageTree };

function isTree(value: unknown): value is MessageTree {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function clone(value: MessageTree): MessageTree {
  const result: MessageTree = {};
  for (const [key, item] of Object.entries(value)) {
    result[key] = isTree(item) ? clone(item) : item;
  }
  return result;
}

/**
 * Returns a new object: values from `override` laid over `base`, recursively
 * through nested objects. Neither argument is mutated.
 */
export function deepMerge(base: MessageTree, override?: MessageTree): MessageTree {
  const result = clone(base);
  if (!override) return result;

  for (const [key, value] of Object.entries(override)) {
    const current = result[key];
    if (isTree(value)) {
      result[key] = isTree(current) ? deepMerge(current, value) : clone(value);
    } else {
      result[key] = value;
    }
  }
  return result;
}
