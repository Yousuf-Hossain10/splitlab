import type { VariationResult } from '../../api/models';
import {
  chartLayout,
  formatLoss,
  formatPValue,
  formatProbability,
  formatRate,
  headlineFor,
  niceTicks,
  summariseSampleRatio,
  toneFor,
} from './results.view';

function variation(key: string, isControl: boolean, low: number, mean: number, high: number): VariationResult {
  return {
    key,
    isControl,
    exposed: 1000,
    converted: Math.round(mean * 1000),
    observedRate: mean,
    posteriorMean: mean,
    credibleLow: low,
    credibleHigh: high,
    probabilityToBeatControl: isControl ? null : 0.9,
    expectedLoss: 0.001,
    probabilityBest: 0.5,
  };
}

describe('formatRate', () => {
  it('shows rates as percentages', () => {
    expect(formatRate(0.1234)).toBe('12.34%');
    expect(formatRate(0.5, 0)).toBe('50%');
    expect(formatRate(0)).toBe('0.00%');
  });

  it('shows a dash rather than a number when the value is missing', () => {
    expect(formatRate(null)).toBe('—');
    expect(formatRate(undefined)).toBe('—');
    expect(formatRate(Number.NaN)).toBe('—');
  });
});

describe('formatProbability', () => {
  it('rounds ordinary probabilities to one decimal', () => {
    expect(formatProbability(0.5)).toBe('50.0%');
    expect(formatProbability(0.9731)).toBe('97.3%');
  });

  it('never claims certainty a simulation cannot give', () => {
    expect(formatProbability(1)).toBe('> 99.9%');
    expect(formatProbability(0.9999)).toBe('> 99.9%');
    expect(formatProbability(0)).toBe('< 0.1%');
    expect(formatProbability(0.0001)).toBe('< 0.1%');
  });

  it('handles missing values', () => {
    expect(formatProbability(null)).toBe('—');
  });
});

describe('formatLoss', () => {
  it('expresses expected loss in percentage points of conversion', () => {
    expect(formatLoss(0.0123)).toBe('1.23 pp');
    expect(formatLoss(0.0004)).toBe('0.040 pp');
  });

  it('collapses a vanishing loss', () => {
    expect(formatLoss(0)).toBe('≈ 0 pp');
    expect(formatLoss(1e-9)).toBe('≈ 0 pp');
  });
});

describe('formatPValue', () => {
  it('keeps tiny p-values readable', () => {
    expect(formatPValue(0.4231)).toBe('0.423');
    expect(formatPValue(0.00006334)).toBe('6.3e-5');
    expect(formatPValue(0)).toBe('0');
  });
});

describe('recommendation presentation', () => {
  it('maps each kind to a tone', () => {
    expect(toneFor('Leader')).toBe('good');
    expect(toneFor('Untrustworthy')).toBe('error');
    expect(toneFor('KeepCollecting')).toBe('warn');
    expect(toneFor('Inconclusive')).toBe('info');
  });

  it('names the leader and refuses to name a winner when untrustworthy', () => {
    expect(headlineFor('Leader', 'treatment')).toBe('Leader: treatment');
    expect(headlineFor('Untrustworthy', null)).toBe('Do not trust these results');
    expect(headlineFor('Untrustworthy', 'treatment')).not.toContain('treatment');
  });
});

describe('summariseSampleRatio', () => {
  it('flags a mismatch as a problem', () => {
    const summary = summariseSampleRatio({ chiSquare: 16, degreesOfFreedom: 1, pValue: 0.0000633, mismatch: true });

    expect(summary.healthy).toBe(false);
    expect(summary.text).toContain('does not match');
    expect(summary.text).toContain('6.3e-5');
  });

  it('reports a healthy split', () => {
    const summary = summariseSampleRatio({ chiSquare: 0.4, degreesOfFreedom: 1, pValue: 0.527, mismatch: false });

    expect(summary).toEqual({ healthy: true, text: 'Traffic split matches the configuration (p = 0.527)' });
  });

  it('says so when there is nothing to check', () => {
    expect(summariseSampleRatio(null).healthy).toBe(true);
    expect(summariseSampleRatio(undefined).text).toContain('Not enough data');
  });
});

describe('niceTicks', () => {
  it('produces round values inside the range', () => {
    const ticks = niceTicks(0.08, 0.16);

    expect(ticks.length).toBeGreaterThanOrEqual(3);
    expect(ticks.length).toBeLessThanOrEqual(6);
    expect(ticks.every((t) => t >= 0.08 - 1e-9 && t <= 0.16 + 1e-9)).toBe(true);
    // Round numbers: multiples of the step, free of floating point noise.
    expect(ticks).toEqual([0.08, 0.1, 0.12, 0.14, 0.16]);
  });

  it('copes with a degenerate range', () => {
    expect(niceTicks(0.1, 0.1)).toEqual([0.1]);
  });
});

describe('chartLayout', () => {
  const variations = [variation('control', true, 0.09, 0.1, 0.11), variation('treatment', false, 0.11, 0.13, 0.15)];

  it('places the interval and mean of every variation in order, left to right', () => {
    const { rows, plotLeft, width, plotRight } = chartLayout(variations);

    for (const row of rows) {
      expect(row.low).toBeGreaterThanOrEqual(plotLeft);
      expect(row.high).toBeLessThanOrEqual(width - plotRight);
      expect(row.low).toBeLessThan(row.mean);
      expect(row.mean).toBeLessThan(row.high);
    }
    // Treatment converts better, so it sits further right than control.
    expect(rows[1].mean).toBeGreaterThan(rows[0].mean);
  });

  it('gives each variation its own row', () => {
    const { rows } = chartLayout(variations);

    expect(new Set(rows.map((r) => r.y)).size).toBe(2);
    expect(rows[0].y).toBeLessThan(rows[1].y);
  });

  it('describes every variation in words for screen readers', () => {
    const { rows } = chartLayout(variations);

    expect(rows[0].label).toBe('control: 10.00% (95% interval 9.00% to 11.00%)');
  });

  it('never extends the axis beyond a possible conversion rate', () => {
    const edge = chartLayout([variation('a', true, 0.0, 0.001, 0.003), variation('b', false, 0.97, 0.99, 1.0)]);

    expect(edge.ticks.every((t) => t.x >= edge.plotLeft - 1e-9 && t.x <= edge.width - edge.plotRight + 1e-9)).toBe(true);
    expect(edge.ticks[0].label).toBe('0%');
  });

  it('still lays out when every interval is identical', () => {
    const layout = chartLayout([variation('a', true, 0.1, 0.1, 0.1), variation('b', false, 0.1, 0.1, 0.1)]);

    expect(layout.rows.every((r) => Number.isFinite(r.mean))).toBe(true);
  });

  it('grows taller with more variations', () => {
    const three = chartLayout([...variations, variation('c', false, 0.1, 0.12, 0.14)]);

    expect(three.height).toBeGreaterThan(chartLayout(variations).height);
  });
});
