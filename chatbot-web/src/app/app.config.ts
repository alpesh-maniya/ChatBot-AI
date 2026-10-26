import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch } from '@angular/common/http';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // withFetch() puts HttpClient on the same fetch backend the SSE client uses, so
    // both paths share one set of browser semantics for credentials and aborts.
    provideHttpClient(withFetch()),
  ],
};
