import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Route and query parameters arrive as component inputs (project, key, metric, returnUrl).
    provideRouter(routes, withComponentInputBinding()),
  ],
};
