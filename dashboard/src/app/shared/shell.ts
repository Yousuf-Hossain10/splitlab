import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthStore } from '../core/auth.store';

/** The frame around every signed-in page: brand, who you are, sign out, and the page itself. */
@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterOutlet],
  template: `
    <a class="skip" href="#main">Skip to content</a>
    <header class="bar">
      <div class="inner">
        <a class="brand" routerLink="/projects" aria-label="Splitlab, all projects">
          <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path d="M4 12h6M14 6h6M14 18h6M10 12l4-6M10 12l4 6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
          </svg>
          Splitlab
        </a>
        <div class="who">
          @if (auth.subject(); as subject) {
            <span class="muted">Signed in as <strong>{{ subject }}</strong></span>
          }
          <button type="button" class="btn small" (click)="signOut()">Sign out</button>
        </div>
      </div>
    </header>
    <main id="main" tabindex="-1">
      <router-outlet />
    </main>
  `,
  styles: `
    .skip {
      position: absolute;
      left: -999px;
      top: 0;
      background: var(--surface);
      padding: 0.5rem 1rem;
      z-index: 10;
      &:focus {
        left: 0.5rem;
        top: 0.5rem;
      }
    }
    .bar {
      background: var(--surface);
      border-bottom: 1px solid var(--border);
    }
    .inner,
    main {
      max-width: 1100px;
      margin: 0 auto;
      padding: 0 1.25rem;
    }
    .inner {
      display: flex;
      justify-content: space-between;
      align-items: center;
      min-height: 3.4rem;
      gap: 1rem;
      flex-wrap: wrap;
    }
    main {
      padding-top: 1.5rem;
      padding-bottom: 3rem;
      outline: none;
    }
    .brand {
      display: inline-flex;
      align-items: center;
      gap: 0.45rem;
      font-weight: 800;
      font-size: 1.1rem;
      text-decoration: none;
      color: var(--text);
    }
    .who {
      display: flex;
      align-items: center;
      gap: 0.9rem;
    }
  `,
})
export class Shell {
  protected readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  protected signOut(): void {
    this.auth.signOut();
    void this.router.navigate(['/sign-in']);
  }
}
