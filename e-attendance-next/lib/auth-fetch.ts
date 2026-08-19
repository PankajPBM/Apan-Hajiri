"use client";

import { AUTH_SESSION_STORAGE_KEY } from "@/components/data/auth-store";

const TOKEN_STORAGE_KEY = "pb_attendance_auth_token_v1";

function hasWindow() {
  return typeof window !== "undefined";
}

export function getAuthToken(): string | null {
  if (!hasWindow()) {
    return null;
  }
  return window.sessionStorage.getItem(TOKEN_STORAGE_KEY) ?? window.localStorage.getItem(TOKEN_STORAGE_KEY);
}

export function saveAuthToken(token: string, remember: boolean) {
  if (!hasWindow()) {
    return;
  }
  if (remember) {
    window.localStorage.setItem(TOKEN_STORAGE_KEY, token);
    window.sessionStorage.removeItem(TOKEN_STORAGE_KEY);
  } else {
    window.sessionStorage.setItem(TOKEN_STORAGE_KEY, token);
    window.localStorage.removeItem(TOKEN_STORAGE_KEY);
  }
}

export function clearAuthToken() {
  if (!hasWindow()) {
    return;
  }
  window.localStorage.removeItem(TOKEN_STORAGE_KEY);
  window.sessionStorage.removeItem(TOKEN_STORAGE_KEY);
}

let patched = false;

function installAuthFetchInterceptor() {
  if (!hasWindow() || patched) {
    return;
  }
  patched = true;
  const originalFetch = window.fetch.bind(window);

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const rawUrl = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    let isApiCall = false;
    try {
      isApiCall = /\/api\//i.test(new URL(rawUrl, window.location.origin).pathname);
    } catch {
      isApiCall = false;
    }

    const token = getAuthToken();
    let finalInit = init;
    if (isApiCall && token) {
      const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
      if (!headers.has("Authorization")) {
        headers.set("Authorization", `Bearer ${token}`);
      }
      finalInit = { ...init, headers };
    }

    const response = await originalFetch(input, finalInit);

    // Public kiosk pages (QR check-in, QR leave application) have no session to lose and no
    // business being redirected to a login screen — they call some endpoints (like the leave-types
    // self-seed) that are intentionally auth-protected and can legitimately 401 for an anonymous
    // visitor without that meaning "you got logged out."
    const isPublicKioskPage =
      window.location.pathname.startsWith("/login") ||
      window.location.pathname.startsWith("/apply-leave") ||
      window.location.pathname.startsWith("/check-in");

    if (isApiCall && response.status === 401 && !isPublicKioskPage) {
      clearAuthToken();
    }

    return response;
  };
}

installAuthFetchInterceptor();
