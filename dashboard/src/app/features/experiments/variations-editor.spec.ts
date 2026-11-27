import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';
import { buttonByText, el, settle, text } from '../../testing';
import { VariationsEditor } from './variations-editor';
import type { VariationRow } from './variations';

function create(rows: VariationRow[]) {
  const fixture = TestBed.createComponent(VariationsEditor);
  fixture.componentRef.setInput('rows', rows);
  return fixture;
}

describe('VariationsEditor', () => {
  it('shows a row per variation and the traffic allocated', async () => {
    const fixture = create([
      { key: 'control', percent: 50 },
      { key: 'treatment', percent: 25 },
    ]);

    await settle(fixture);

    expect(el(fixture).querySelectorAll('tbody tr')).toHaveLength(2);
    expect(text(fixture)).toContain('75% allocated');
    expect(text(fixture)).toContain('25% not in the experiment');
  });

  it('adds a variation with no traffic', async () => {
    const fixture = create([{ key: 'control', percent: 100 }]);
    await settle(fixture);

    buttonByText(fixture, 'Add variation')?.click();
    await settle(fixture);

    expect(fixture.componentInstance.rows()).toEqual([
      { key: 'control', percent: 100 },
      { key: '', percent: 0 },
    ]);
  });

  it('removes a variation, but never the last one', async () => {
    const fixture = create([
      { key: 'a', percent: 50 },
      { key: 'b', percent: 50 },
    ]);
    await settle(fixture);

    el(fixture).querySelector<HTMLButtonElement>('button[aria-label="Remove variation 1"]')?.click();
    await settle(fixture);

    expect(fixture.componentInstance.rows()).toEqual([{ key: 'b', percent: 50 }]);
    expect(el(fixture).querySelector<HTMLButtonElement>('button[aria-label="Remove variation 1"]')?.disabled).toBe(true);
  });

  it('splits traffic evenly while keeping the keys', async () => {
    const fixture = create([
      { key: 'a', percent: 10 },
      { key: 'b', percent: 0 },
      { key: 'c', percent: 0 },
    ]);
    await settle(fixture);

    buttonByText(fixture, 'Split evenly')?.click();
    await settle(fixture);

    expect(fixture.componentInstance.rows()).toEqual([
      { key: 'a', percent: 33.33 },
      { key: 'b', percent: 33.33 },
      { key: 'c', percent: 33.34 },
    ]);
    expect(text(fixture)).toContain('100% allocated');
  });

  it('writes edits back to the rows', async () => {
    const fixture = create([{ key: 'a', percent: 50 }]);
    await settle(fixture);

    const input = el(fixture).querySelector<HTMLInputElement>('#vkey-0')!;
    input.value = 'renamed';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle(fixture);

    expect(fixture.componentInstance.rows()[0].key).toBe('renamed');
  });

  it('warns visibly when traffic is over-allocated', async () => {
    const fixture = create([
      { key: 'a', percent: 70 },
      { key: 'b', percent: 70 },
    ]);

    await settle(fixture);

    expect(el(fixture).querySelector('.total')?.classList.contains('over')).toBe(true);
    expect(text(fixture)).toContain('140% allocated');
  });

  it('labels every input for screen readers', async () => {
    const fixture = create([{ key: 'a', percent: 50 }]);

    await settle(fixture);

    expect(el(fixture).querySelector('label[for="vkey-0"]')?.textContent).toContain('Variation 1 key');
    expect(el(fixture).querySelector('label[for="vpct-0"]')?.textContent).toContain('traffic percent');
  });

  it('works inside a page that wraps it in a form, without Angular warning about unregistered controls', async () => {
    // The create-experiment page puts the editor inside its <form>. Its inputs belong to the editor, not to that form, so
    // they must say so explicitly; otherwise Angular logs NG01354 for every input on every render.
    @Component({
      imports: [FormsModule, VariationsEditor],
      template: `<form><app-variations-editor [(rows)]="rows" /></form>`,
    })
    class FormHost {
      readonly rows = signal<VariationRow[]>([
        { key: 'control', percent: 50 },
        { key: 'treatment', percent: 50 },
      ]);
    }

    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const fixture = TestBed.createComponent(FormHost);

    await settle(fixture);

    const angularWarnings = warn.mock.calls.map((c) => String(c[0])).filter((m) => m.includes('NG01354'));
    warn.mockRestore();
    expect(angularWarnings).toEqual([]);
    expect(el(fixture).querySelectorAll('tbody tr')).toHaveLength(2);
  });
});
