import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthStore } from '../../core/auth.store';
import { describeError } from '../../core/problem';
import { ErrorBanner } from '../../shared/ui';
import { SignInService } from './sign-in.service';

/** Only follow same-site paths after sign-in, so a crafted link cannot send someone elsewhere. */
export function safeReturnUrl(url: string | undefined): string {
  return url && url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/\\') ? url : '/projects';
}

@Component({
  selector: 'app-sign-in',
  imports: [FormsModule, ErrorBanner],
  template: `
    <div class="wrap">
      <h1>Sign in to Splitlab</h1>
      <app-error [message]="error()" />

      <section class="card stack" aria-labelledby="dev-heading">
        <h2 id="dev-heading">Development sign-in</h2>
        <p class="muted">
          For a local API running in Development. Enter any name; the API issues a token for it. Whoever creates a project becomes its admin.
        </p>
        <form (submit)="$event.preventDefault(); devSignIn()">
          <div class="field">
            <label for="subject">Your name</label>
            <input id="subject" type="text" autocomplete="username" [ngModel]="subject()" (ngModelChange)="subject.set($event)" name="subject" />
          </div>
          <button class="btn primary" type="submit" [disabled]="busy()">Sign in</button>
        </form>
      </section>

      <section class="card stack" aria-labelledby="token-heading">
        <h2 id="token-heading">Use a token</h2>
        <p class="muted">Paste a bearer token issued by your identity provider.</p>
        <form (submit)="$event.preventDefault(); usePasted()">
          <div class="field">
            <label for="token">Token</label>
            <textarea id="token" [ngModel]="pasted()" (ngModelChange)="pasted.set($event)" name="token" spellcheck="false" autocomplete="off"></textarea>
          </div>
          <button class="btn" type="submit">Use token</button>
        </form>
      </section>
    </div>
  `,
  styles: `
    .wrap {
      max-width: 520px;
      margin: 3rem auto;
      padding: 0 1.25rem;
    }
    section {
      margin-top: 1rem;
    }
  `,
})
export class SignInPage {
  private readonly auth = inject(AuthStore);
  private readonly service = inject(SignInService);
  private readonly router = inject(Router);

  /** Bound from the `?returnUrl=` query parameter. */
  readonly returnUrl = input<string>();

  protected readonly subject = signal('');
  protected readonly pasted = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async devSignIn(): Promise<void> {
    // The button is never disabled for an empty field: a disabled submit button silently swallows Enter (for example
    // right after autofill or voice input), whereas an explanation always helps.
    if (!this.subject().trim()) {
      this.error.set('Enter a name to sign in.');
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      this.auth.signIn(await this.service.developmentToken(this.subject().trim()));
      await this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
    } catch (e) {
      this.error.set(describeError(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected usePasted(): void {
    if (!this.pasted().trim()) {
      this.error.set('Paste a token to continue.');
      return;
    }
    this.error.set(null);
    this.auth.signIn(this.pasted());
    void this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
  }
}
