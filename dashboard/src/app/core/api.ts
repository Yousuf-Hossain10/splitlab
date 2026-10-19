import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import createClient from 'openapi-fetch';
import type { paths } from '../api/schema';
import { AuthStore } from './auth.store';
import { toApiError } from './problem';

/**
 * The typed API client, generated from the OpenAPI document (ADR-0008): a wrong path, parameter or field is a compile
 * error here. It signs requests with the current token and sends the person back to sign-in when the server says 401.
 */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  readonly client = createClient<paths>({ baseUrl: globalThis.location?.origin ?? '' });

  constructor() {
    this.client.use({
      onRequest: ({ request }) => {
        const token = this.auth.token();
        if (token) {
          request.headers.set('Authorization', `Bearer ${token}`);
        }
        return request;
      },
      onResponse: ({ response }) => {
        if (response.status === 401 && this.auth.isSignedIn()) {
          this.auth.signOut();
          void this.router.navigate(['/sign-in']);
        }
        return response;
      },
    });
  }

  /** Returns the response body, or throws an {@link ApiError} describing why the server refused. */
  async unwrap<T>(call: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
    const { data, error, response } = await call;
    if (!response.ok) {
      throw toApiError(response.status, error);
    }
    return data as T;
  }
}
