import { Component, computed, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { KEY_HINT } from '../../shared/keys';
import { VariationRow, allocatedPercent, evenSplit, formatPercentValue } from './variations';

/**
 * Edits an experiment's variations: a key and a traffic share each. The first variation is the control that
 * results are compared against. Anything not allocated stays out of the experiment (users see the default).
 */
@Component({
  selector: 'app-variations-editor',
  imports: [FormsModule],
  template: `
    <fieldset [disabled]="disabled()">
      <legend>Variations</legend>
      <p class="hint">The first variation is the control. {{ keyHint }}</p>

      <div class="table-wrap">
        <table>
          <caption class="visually-hidden">Variations and their traffic share</caption>
          <thead>
            <tr>
              <th scope="col">Key</th>
              <th scope="col" class="num">Traffic %</th>
              <th scope="col"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (row of rows(); track $index; let i = $index) {
              <tr>
                <td>
                  <label class="visually-hidden" [for]="'vkey-' + i">Variation {{ i + 1 }} key</label>
                  <input [id]="'vkey-' + i" type="text" class="mono" [ngModel]="row.key" (ngModelChange)="update(i, { key: $event })" [name]="'vkey-' + i" />
                </td>
                <td class="num">
                  <label class="visually-hidden" [for]="'vpct-' + i">Variation {{ i + 1 }} traffic percent</label>
                  <input [id]="'vpct-' + i" type="number" min="0" max="100" step="0.01" [ngModel]="row.percent" (ngModelChange)="update(i, { percent: $event })" [name]="'vpct-' + i" />
                </td>
                <td>
                  <button type="button" class="btn small" (click)="remove(i)" [disabled]="rows().length <= 1" [attr.aria-label]="'Remove variation ' + (i + 1)">Remove</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      <div class="row spread footer">
        <div class="row">
          <button type="button" class="btn small" (click)="add()">Add variation</button>
          <button type="button" class="btn small" (click)="split()">Split evenly</button>
        </div>
        <p class="total" [class.over]="total() > 100" role="status">
          {{ totalLabel() }} allocated
          @if (total() < 100 && total() <= 100) {
            <span class="muted"> · {{ remainingLabel() }} not in the experiment</span>
          }
        </p>
      </div>
    </fieldset>
  `,
  styles: `
    .footer {
      margin-top: 0.75rem;
    }
    .total {
      margin: 0;
      font-weight: 600;
    }
    .total.over {
      color: var(--bad);
    }
  `,
})
export class VariationsEditor {
  readonly rows = model.required<VariationRow[]>();
  readonly disabled = model(false);

  protected readonly keyHint = KEY_HINT;
  protected readonly total = computed(() => allocatedPercent(this.rows()));
  protected readonly totalLabel = computed(() => `${formatPercentValue(this.total())}%`);
  protected readonly remainingLabel = computed(() => `${formatPercentValue(100 - this.total())}%`);

  protected update(index: number, change: Partial<VariationRow>): void {
    this.rows.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...change } : r)));
  }

  protected add(): void {
    this.rows.update((rows) => [...rows, { key: '', percent: 0 }]);
  }

  protected remove(index: number): void {
    this.rows.update((rows) => rows.filter((_, i) => i !== index));
  }

  protected split(): void {
    this.rows.update((rows) => evenSplit(rows.map((r) => r.key)));
  }
}
