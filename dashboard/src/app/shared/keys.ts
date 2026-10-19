/** Keys appear in URLs and the SDK datafile: lowercase letters, digits, '_' and '-', starting with a letter or digit, up to 64 characters. */
export const KEY_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;

export const KEY_HINT = "Lowercase letters, digits, '-' and '_', starting with a letter or digit (up to 64 characters).";

export function isValidKey(value: string): boolean {
  return KEY_PATTERN.test(value);
}

/** Suggests a key from a display name, e.g. "Checkout Flow v2" becomes "checkout-flow-v2". */
export function slugify(name: string): string {
  return name
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 64)
    .replace(/-+$/g, '');
}
