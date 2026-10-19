import type { RecommendationKind, SampleRatio, VariationResult } from '../../api/models';

const EM_DASH = '—';

/** 0.1234 → "12.34%". Missing values show a dash rather than a misleading zero. */
export function formatRate(value: number | null | undefined, digits = 2): string {
  return value === null || value === undefined || !Number.isFinite(value) ? EM_DASH : `${(value * 100).toFixed(digits)}%`;
}

/**
 * A probability for people. Monte Carlo estimates are never exactly 0 or 1, so the extremes are shown as bounds
 * ("> 99.9%") instead of claiming certainty the method cannot give.
 */
export function formatProbability(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return EM_DASH;
  }
  if (value >= 0.9995) {
    return '> 99.9%';
  }
  if (value <= 0.0005) {
    return '< 0.1%';
  }
  return `${(value * 100).toFixed(1)}%`;
}

/** Expected loss is in absolute conversion-rate units; people read it as percentage points of conversion. */
export function formatLoss(value: number): string {
  if (!Number.isFinite(value)) {
    return EM_DASH;
  }
  const points = value * 100;
  return points < 0.0005 ? '≈ 0 pp' : `${points.toFixed(points < 0.1 ? 3 : 2)} pp`;
}

export function formatCount(value: number): string {
  return value.toLocaleString('en-US');
}

export function formatPValue(p: number): string {
  if (p === 0) {
    return '0';
  }
  return p < 0.001 ? p.toExponential(1) : p.toFixed(3);
}

export type Tone = 'good' | 'warn' | 'info' | 'error';

export function toneFor(kind: RecommendationKind): Tone {
  switch (kind) {
    case 'Leader':
      return 'good';
    case 'Untrustworthy':
      return 'error';
    case 'KeepCollecting':
      return 'warn';
    default:
      return 'info';
  }
}

export function headlineFor(kind: RecommendationKind, variation: string | null | undefined): string {
  switch (kind) {
    case 'Leader':
      return `Leader: ${variation ?? ''}`.trim();
    case 'Untrustworthy':
      return 'Do not trust these results';
    case 'KeepCollecting':
      return 'Keep collecting data';
    default:
      return 'No clear winner yet';
  }
}

export interface SampleRatioSummary {
  healthy: boolean;
  text: string;
}

export function summariseSampleRatio(sr: SampleRatio | null | undefined): SampleRatioSummary {
  if (!sr) {
    return { healthy: true, text: 'Not enough data to check the traffic split' };
  }
  return sr.mismatch
    ? { healthy: false, text: `Traffic split does not match the configuration (p = ${formatPValue(sr.pValue)})` }
    : { healthy: true, text: `Traffic split matches the configuration (p = ${formatPValue(sr.pValue)})` };
}

// ---- interval chart layout ----

export interface ChartRow {
  key: string;
  isControl: boolean;
  y: number;
  low: number;
  mean: number;
  high: number;
  label: string;
}

export interface ChartTick {
  x: number;
  label: string;
}

export interface ChartLayout {
  width: number;
  height: number;
  plotLeft: number;
  plotRight: number;
  rows: ChartRow[];
  ticks: ChartTick[];
}

const ROW_HEIGHT = 46;
const TOP = 12;
const AXIS_HEIGHT = 30;

/**
 * Places each variation's credible interval on a shared rate axis. The domain pads the data a little and is
 * clamped to [0, 1], since a conversion rate cannot leave that range.
 */
export function chartLayout(variations: readonly VariationResult[], width = 640, plotLeft = 120, plotRight = 24): ChartLayout {
  const lows = variations.map((v) => v.credibleLow);
  const highs = variations.map((v) => v.credibleHigh);
  const rawMin = Math.min(...lows);
  const rawMax = Math.max(...highs);
  const pad = Math.max((rawMax - rawMin) * 0.08, 0.0005);
  const min = Math.max(0, rawMin - pad);
  const max = Math.min(1, rawMax + pad);

  const span = max - min || 1;
  const scale = (value: number): number => plotLeft + ((value - min) / span) * (width - plotLeft - plotRight);

  return {
    width,
    height: TOP + variations.length * ROW_HEIGHT + AXIS_HEIGHT,
    plotLeft,
    plotRight,
    rows: variations.map((v, i) => ({
      key: v.key,
      isControl: v.isControl,
      y: TOP + i * ROW_HEIGHT + ROW_HEIGHT / 2,
      low: scale(v.credibleLow),
      mean: scale(v.posteriorMean),
      high: scale(v.credibleHigh),
      label: `${v.key}: ${formatRate(v.posteriorMean)} (95% interval ${formatRate(v.credibleLow)} to ${formatRate(v.credibleHigh)})`,
    })),
    ticks: niceTicks(min, max).map((t) => ({ x: scale(t), label: formatRate(t, tickDigits(max - min)) })),
  };
}

function tickDigits(span: number): number {
  return span < 0.01 ? 2 : span < 0.1 ? 1 : 0;
}

/** Four to six round tick values inside [min, max]. */
export function niceTicks(min: number, max: number): number[] {
  const span = max - min;
  if (!(span > 0)) {
    return [min];
  }
  const rough = span / 4;
  const magnitude = 10 ** Math.floor(Math.log10(rough));
  const step = [1, 2, 5, 10].map((m) => m * magnitude).find((s) => s >= rough) ?? rough;
  const ticks: number[] = [];
  for (let t = Math.ceil(min / step) * step; t <= max + step * 1e-9; t += step) {
    ticks.push(Math.round(t / step) * step);
  }
  return ticks;
}
