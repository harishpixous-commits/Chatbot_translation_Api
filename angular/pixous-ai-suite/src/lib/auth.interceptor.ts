import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Attach the host app's auth token to AiSuite backend calls.
 * The host provides the token getter — this library never handles credentials.
 *
 * Usage (host app):
 *   provideHttpClient(withInterceptors([aiSuiteAuthInterceptor(() => tokenService.getToken())]))
 */
export function aiSuiteAuthInterceptor(getToken: () => string | null): HttpInterceptorFn {
  return (req, next) => {
    if (!req.url.includes('/api/ai/')) {
      return next(req);
    }
    const token = getToken();
    if (!token) return next(req);
    return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
  };
}
