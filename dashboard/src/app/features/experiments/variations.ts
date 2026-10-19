import type { Variation } from '../../api/models';
import { isValidKey } from '../../shared/keys';

/** One line of the variations editor. People think in percent; the API speaks basis points (1 = 0.01%). */
export interface VariationRow {
  key: string;
  percent: number | null;
}

const BASIS_POINTS_PER_PERCENT = 100;
const FULL = 100;

export function toVariations(rows: VariationRow[]): Variation[] {
  return rows.map((r) => ({ key: r.key.trim(), trafficBasisPoints: Math.round((r.percent ?? 0) * BASIS_POINTS_PER_PERCENT) }));
}

export function fromVariations(variations: readonly Variation[]): VariationRow[] {
  return variations.map((v) => ({ key: v.key, percent: v.trafficBasisPoints / BASIS_POINTS_PER_PERCENT }));
}

/** Total traffic allocated across all variations, in percent, free of floating point noise. */
export function allocatedPercent(rows: VariationRow[]): number {
  const basisPoints = toVariations(rows).reduce((sum, v) => sum + v.trafficBasisPoints, 0);
  return basisPoints / BASIS_POINTS_PER_PERCENT;
}

/** Splits all traffic as evenly as whole basis points allow, giving any remainder to the last variation. */
export function evenSplit(keys: string[]): VariationRow[] {
  const total = FULL * BASIS_POINTS_PER_PERCENT;
  const share = Math.floor(total / keys.length);
  return keys.map((key, i) => ({
    key,
    percent: (i === keys.length - 1 ? total - share * (keys.length - 1) : share) / BASIS_POINTS_PER_PERCENT,
  }));
}

/** The first thing wrong with the rows, in words a person can act on, or null if they are fine. */
export function validateVariations(rows: VariationRow[], options: { minimum: number }): string | null {
  if (rows.length < options.minimum) {
    return `Add at least ${options.minimum} variation${options.minimum === 1 ? '' : 's'}.`;
  }

  const seen = new Set<string>();
  for (const [index, row] of rows.entries()) {
    const key = row.key.trim();
    if (!isValidKey(key)) {
      return `Variation ${index + 1} needs a valid key (lowercase letters, digits, '-' or '_').`;
    }
    if (seen.has(key)) {
      return `The key "${key}" is used more than once.`;
    }
    seen.add(key);

    const percent = row.percent;
    if (percent === null || !Number.isFinite(percent) || percent < 0 || percent > FULL) {
      return `"${key}" needs a traffic share between 0 and 100%.`;
    }
    if (Math.abs(percent * BASIS_POINTS_PER_PERCENT - Math.round(percent * BASIS_POINTS_PER_PERCENT)) > 1e-6) {
      return `"${key}" can have at most two decimal places.`;
    }
  }

  const total = allocatedPercent(rows);
  return total > FULL ? `Traffic adds up to ${formatPercentValue(total)}%, which is more than 100%.` : null;
}

export function formatPercentValue(percent: number): string {
  return Number.isInteger(percent) ? String(percent) : percent.toFixed(2).replace(/0+$/, '').replace(/\.$/, '');
}
