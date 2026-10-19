import { VariationRow, allocatedPercent, evenSplit, formatPercentValue, fromVariations, toVariations, validateVariations } from './variations';

const two: VariationRow[] = [
  { key: 'control', percent: 50 },
  { key: 'treatment', percent: 50 },
];

describe('converting between percent and basis points', () => {
  it('round-trips', () => {
    const variations = toVariations(two);

    expect(variations).toEqual([
      { key: 'control', trafficBasisPoints: 5000 },
      { key: 'treatment', trafficBasisPoints: 5000 },
    ]);
    expect(fromVariations(variations)).toEqual(two);
  });

  it('survives floating point rounding', () => {
    // 33.33 * 100 is 3332.9999999999995 in floating point.
    expect(toVariations([{ key: 'a', percent: 33.33 }])[0].trafficBasisPoints).toBe(3333);
    expect(toVariations([{ key: 'a', percent: 0.07 }])[0].trafficBasisPoints).toBe(7);
  });

  it('trims keys and treats a blank percent as zero', () => {
    expect(toVariations([{ key: '  a  ', percent: null }])).toEqual([{ key: 'a', trafficBasisPoints: 0 }]);
  });
});

describe('allocatedPercent', () => {
  it('adds without floating point noise', () => {
    expect(allocatedPercent([{ key: 'a', percent: 33.33 }, { key: 'b', percent: 33.33 }, { key: 'c', percent: 33.34 }])).toBe(100);
    expect(allocatedPercent([{ key: 'a', percent: 0.1 }, { key: 'b', percent: 0.2 }])).toBe(0.3);
  });
});

describe('evenSplit', () => {
  it('splits two ways', () => {
    expect(evenSplit(['a', 'b']).map((r) => r.percent)).toEqual([50, 50]);
  });

  it('gives the remainder to the last variation so the total is exactly 100%', () => {
    const rows = evenSplit(['a', 'b', 'c']);

    expect(rows.map((r) => r.percent)).toEqual([33.33, 33.33, 33.34]);
    expect(allocatedPercent(rows)).toBe(100);
  });

  it('keeps the keys', () => {
    expect(evenSplit(['x', 'y']).map((r) => r.key)).toEqual(['x', 'y']);
  });
});

describe('validateVariations', () => {
  it('accepts a sound experiment, including one that leaves traffic out', () => {
    expect(validateVariations(two, { minimum: 2 })).toBeNull();
    expect(validateVariations([{ key: 'a', percent: 10 }, { key: 'b', percent: 10 }], { minimum: 2 })).toBeNull();
  });

  it('requires the minimum number of variations', () => {
    expect(validateVariations([{ key: 'only', percent: 100 }], { minimum: 2 })).toContain('at least 2');
    expect(validateVariations([], { minimum: 1 })).toContain('at least 1 variation');
  });

  it('rejects invalid keys', () => {
    expect(validateVariations([{ key: 'Has Space', percent: 50 }], { minimum: 1 })).toContain('valid key');
    expect(validateVariations([{ key: '', percent: 50 }], { minimum: 1 })).toContain('Variation 1');
  });

  it('rejects duplicate keys', () => {
    expect(validateVariations([{ key: 'a', percent: 10 }, { key: 'a', percent: 10 }], { minimum: 1 })).toContain('more than once');
  });

  it('rejects shares outside 0 to 100', () => {
    expect(validateVariations([{ key: 'a', percent: -1 }], { minimum: 1 })).toContain('between 0 and 100');
    expect(validateVariations([{ key: 'a', percent: 101 }], { minimum: 1 })).toContain('between 0 and 100');
    expect(validateVariations([{ key: 'a', percent: null }], { minimum: 1 })).toContain('between 0 and 100');
    expect(validateVariations([{ key: 'a', percent: Number.NaN }], { minimum: 1 })).toContain('between 0 and 100');
  });

  it('rejects more precision than the API can store', () => {
    expect(validateVariations([{ key: 'a', percent: 12.345 }], { minimum: 1 })).toContain('two decimal places');
  });

  it('rejects over-allocation and says by how much', () => {
    expect(validateVariations([{ key: 'a', percent: 60 }, { key: 'b', percent: 60 }], { minimum: 1 })).toBe(
      'Traffic adds up to 120%, which is more than 100%.',
    );
  });
});

describe('formatPercentValue', () => {
  it('drops needless decimals', () => {
    expect(formatPercentValue(50)).toBe('50');
    expect(formatPercentValue(33.33)).toBe('33.33');
    expect(formatPercentValue(12.5)).toBe('12.5');
  });
});
