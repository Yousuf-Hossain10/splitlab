import type { ProblemDetails } from '../api/models';

/** An error response from the API, carrying what a person needs to see rather than a raw payload. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** Turns an error response body (RFC 7807 problem details, plain text, or nothing) into an {@link ApiError}. */
export function toApiError(status: number, body: unknown): ApiError {
  const problem = isProblem(body) ? body : undefined;
  const title = problem?.title ?? defaultTitle(status);
  const detail = problem?.detail ?? (typeof body === 'string' && body ? body : undefined);

  // 403 deliberately covers "not a member" and "no such project": say so rather than implying which it was.
  const message =
    status === 403
      ? 'You do not have access to this, or it does not exist.'
      : (detail ?? title);
  return new ApiError(status, title, message);
}

/** The text to show a person for any thrown value. */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message;
  }
  if (error instanceof TypeError) {
    return 'Could not reach the server. Check your connection and try again.';
  }
  return error instanceof Error ? error.message : 'Something went wrong.';
}

function isProblem(body: unknown): body is ProblemDetails {
  return typeof body === 'object' && body !== null;
}

function defaultTitle(status: number): string {
  switch (status) {
    case 400:
      return 'Invalid request';
    case 401:
      return 'Please sign in';
    case 404:
      return 'Not found';
    case 409:
      return 'Conflict';
    case 429:
      return 'Too many requests';
    default:
      return `Request failed (${status})`;
  }
}
