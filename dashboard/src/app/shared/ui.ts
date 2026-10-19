import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

export interface Crumb {
  label: string;
  link?: string[];
}

/** "Projects / acme / production": where you are, with links back up. */
@Component({
  selector: 'app-breadcrumbs',
  imports: [RouterLink],
  template: `
    <nav aria-label="Breadcrumb">
      <ol>
        @for (crumb of crumbs(); track $index; let last = $last) {
          <li>
            @if (crumb.link && !last) {
              <a [routerLink]="crumb.link">{{ crumb.label }}</a>
            } @else {
              <span [attr.aria-current]="last ? 'page' : null">{{ crumb.label }}</span>
            }
          </li>
        }
      </ol>
    </nav>
  `,
  styles: `
    ol {
      display: flex;
      flex-wrap: wrap;
      gap: 0.35rem;
      list-style: none;
      margin: 0 0 0.75rem;
      padding: 0;
      font-size: 0.9rem;
      color: var(--text-muted);
    }
    li:not(:last-child)::after {
      content: '/';
      margin-left: 0.35rem;
    }
  `,
})
export class Breadcrumbs {
  readonly crumbs = input.required<Crumb[]>();
}

/** An error the person should see, announced to screen readers. */
@Component({
  selector: 'app-error',
  template: `
    @if (message(); as text) {
      <div class="banner error" role="alert">{{ text }}</div>
    }
  `,
})
export class ErrorBanner {
  readonly message = input<string | null>(null);
}

/** Colours a lifecycle status the same way everywhere. */
@Component({
  selector: 'app-status',
  template: `<span class="chip" [class]="status().toLowerCase()">{{ status() }}</span>`,
})
export class StatusChip {
  readonly status = input.required<string>();
}

/** Shown while a resource loads, or when there is nothing yet. */
@Component({
  selector: 'app-loading',
  template: `<p class="muted" role="status">{{ label() }}</p>`,
})
export class Loading {
  readonly label = input('Loading…');
}
