import type { NotificationSummaryResponse } from './types';
import { apiRequest } from './apiClient';

// REQ-1411 (S-216 backend/S-217 frontend): the header nav's own
// notification-badge aggregate (XGArcade.Api.Notifications.
// NotificationEndpoints — GET /notifications/summary). Polled by
// useNotificationSummary (frontend/src/lib/useNotificationSummary.ts), not
// called directly by any screen component.
export async function fetchNotificationSummary(accessToken: string): Promise<NotificationSummaryResponse> {
  return apiRequest<NotificationSummaryResponse>(accessToken, '/notifications/summary');
}
