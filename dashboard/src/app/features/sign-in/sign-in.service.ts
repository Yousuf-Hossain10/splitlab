import { Injectable } from '@angular/core';
import { toApiError } from '../../core/problem';

interface DevTokenResponse {
  token: string;
}

@Injectable({ providedIn: 'root' })
export class SignInService {
  /**
   * Asks the API's development issuer for a token. That endpoint exists only when the API runs in Development without an
   * external authority, so it is deliberately absent from the published OpenAPI document and called directly here.
   */
  async developmentToken(subject: string): Promise<string> {
    const response = await fetch('/dev/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subject }),
    });

    if (response.status === 404) {
      throw toApiError(404, {
        title: 'No development issuer',
        detail: 'This server has no development sign-in. Paste a token from your identity provider instead.',
      });
    }
    if (!response.ok) {
      throw toApiError(response.status, await response.json().catch(() => undefined));
    }
    return ((await response.json()) as DevTokenResponse).token;
  }
}
