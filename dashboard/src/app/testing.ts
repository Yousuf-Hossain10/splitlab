import { ComponentFixture } from '@angular/core/testing';

/** Shared helpers for component specs. */

export const el = (fixture: ComponentFixture<unknown>): HTMLElement => fixture.nativeElement as HTMLElement;

export const text = (fixture: ComponentFixture<unknown>): string => (el(fixture).textContent ?? '').replace(/\s+/g, ' ').trim();

/**
 * Lets resources, effects, promises and zero-delay timers settle, then re-renders. RxJS timers are real macrotasks
 * that Angular's stability check does not track, so each round also yields to the event loop.
 */
export async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  fixture.detectChanges();
}

export function buttonByText(fixture: ComponentFixture<unknown>, label: string | RegExp): HTMLButtonElement | undefined {
  return Array.from(el(fixture).querySelectorAll('button')).find((b) =>
    typeof label === 'string' ? (b.textContent ?? '').trim() === label : label.test(b.textContent ?? ''),
  );
}

export function type(fixture: ComponentFixture<unknown>, selector: string, value: string): void {
  const input = el(fixture).querySelector<HTMLInputElement | HTMLTextAreaElement>(selector);
  if (!input) {
    throw new Error(`No element matches ${selector}`);
  }
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}
