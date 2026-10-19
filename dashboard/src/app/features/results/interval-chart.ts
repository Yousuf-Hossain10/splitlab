import { Component, computed, input } from '@angular/core';
import type { VariationResult } from '../../api/models';
import { chartLayout } from './results.view';

/**
 * Each variation's 95% credible interval on a shared conversion-rate axis: the line is the interval, the dot is the
 * posterior mean. Overlapping intervals are the honest picture of "not clearly different". The table next to it
 * carries the exact numbers, so this is a summary and not the only source of them.
 */
@Component({
  selector: 'app-interval-chart',
  template: `
    @let l = layout();
    <svg [attr.viewBox]="'0 0 ' + l.width + ' ' + l.height" role="img" [attr.aria-label]="summary()" preserveAspectRatio="xMidYMid meet">
      <title>{{ summary() }}</title>
      @for (tick of l.ticks; track tick.x) {
        <line class="grid" [attr.x1]="tick.x" [attr.x2]="tick.x" [attr.y1]="8" [attr.y2]="l.height - 26" />
        <text class="tick" [attr.x]="tick.x" [attr.y]="l.height - 8" text-anchor="middle">{{ tick.label }}</text>
      }
      @for (row of l.rows; track row.key) {
        <text class="name" [attr.x]="l.plotLeft - 12" [attr.y]="row.y + 4" text-anchor="end">{{ row.key }}</text>
        <line class="interval" [class.control]="row.isControl" [attr.x1]="row.low" [attr.x2]="row.high" [attr.y1]="row.y" [attr.y2]="row.y" />
        <line class="cap" [class.control]="row.isControl" [attr.x1]="row.low" [attr.x2]="row.low" [attr.y1]="row.y - 6" [attr.y2]="row.y + 6" />
        <line class="cap" [class.control]="row.isControl" [attr.x1]="row.high" [attr.x2]="row.high" [attr.y1]="row.y - 6" [attr.y2]="row.y + 6" />
        <circle class="mean" [class.control]="row.isControl" [attr.cx]="row.mean" [attr.cy]="row.y" r="5.5" />
      }
    </svg>
  `,
  styles: `
    :host {
      display: block;
    }
    svg {
      width: 100%;
      height: auto;
      max-width: 760px;
    }
    .grid {
      stroke: var(--border);
      stroke-width: 1;
    }
    .tick {
      fill: var(--text-muted);
      font-size: 11px;
    }
    .name {
      fill: var(--text);
      font-size: 13px;
      font-weight: 600;
    }
    .interval,
    .cap {
      stroke: var(--accent);
      stroke-width: 3;
      stroke-linecap: round;
    }
    .cap {
      stroke-width: 2;
    }
    .mean {
      fill: var(--accent);
      stroke: var(--surface);
      stroke-width: 2;
    }
    .control {
      stroke: var(--text-muted);
    }
    circle.control {
      fill: var(--text-muted);
      stroke: var(--surface);
    }
  `,
})
export class IntervalChart {
  readonly variations = input.required<readonly VariationResult[]>();

  protected readonly layout = computed(() => chartLayout(this.variations()));
  protected readonly summary = computed(
    () => `Conversion rate with 95% credible interval. ${this.layout().rows.map((r) => r.label).join('. ')}.`,
  );
}
