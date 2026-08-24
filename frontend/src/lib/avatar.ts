import { API_BASE_URL, apiRequest, throwApiError } from './apiClient';
import type { AvatarStatusResponse, SubmitAvatarResponse } from './types';

// REQ-722/S-180 (backend)/S-182 (frontend): POST /users/me/avatar is
// multipart/form-data, not JSON — apiRequest always sets
// `Content-Type: application/json` whenever `init.body` is present and
// JSON-stringifies nothing else for us, so it can't be reused here (a
// FormData body needs the browser to set its own multipart boundary
// Content-Type, never an explicit one). This mirrors apiRequest's own
// auth-header/ok-check/throwApiError convention directly instead of
// diverging from it. Server-side limits (5 MB, image/jpeg|png|webp only —
// see backend/src/XGArcade.Api/Avatars/AvatarEndpoints.cs's
// MaxImageSizeBytes/AllowedContentTypes) are the real enforcement; callers
// may pre-check client-side as a UX nicety (SettingsScreen does), but a 400
// here always carries the server's own detail text, which callers should
// surface verbatim rather than a duplicated client-side message.
export async function submitAvatar(accessToken: string, file: File): Promise<SubmitAvatarResponse> {
  const formData = new FormData();
  formData.append('file', file);

  const response = await fetch(`${API_BASE_URL}/users/me/avatar`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${accessToken}` },
    body: formData,
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as SubmitAvatarResponse;
}

// REQ-722/S-182: GET /users/me/avatar — a normal JSON GET, so this uses
// apiRequest like every other typed call site in this codebase.
export async function fetchAvatarStatus(accessToken: string): Promise<AvatarStatusResponse> {
  return apiRequest<AvatarStatusResponse>(accessToken, '/users/me/avatar');
}

// REQ-722/S-182: GET /users/me/avatar/{id}/image streams raw image bytes
// with the correct Content-Type, not JSON, and still requires the same
// bearer auth every other endpoint here does — an <img src> can't carry an
// Authorization header, so this fetches the bytes directly and hands back a
// `URL.createObjectURL` object URL for the caller to use as an <img src>
// instead. The caller owns revoking the returned URL
// (`URL.revokeObjectURL`) once it's no longer needed (e.g. on unmount or
// when the underlying imageUrl changes) — this function only ever creates
// one, it never revokes on the caller's behalf since it has no way to know
// when the caller is actually done with it.
export async function fetchAvatarImageObjectUrl(accessToken: string, imageUrl: string): Promise<string> {
  const response = await fetch(`${API_BASE_URL}${imageUrl}`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  const blob = await response.blob();
  return URL.createObjectURL(blob);
}
