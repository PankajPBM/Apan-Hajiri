"use client";

import { resolveApiBases } from "@/lib/api-bases";

export const USER_ACCESS_STORAGE_KEY = "pb_attendance_user_access_v1";
export const AUTH_SESSION_STORAGE_KEY = "pb_attendance_auth_session_v1";
export const AUTH_SYNC_EVENT = "pb-auth-updated";
export const DEFAULT_ADMIN_USER_ID = "default-admin";
export const DEFAULT_ADMIN_USERNAME = "admin";
export const DEFAULT_ADMIN_PASSWORD = "PB@14891277";
export const DEFAULT_ADMIN_FULL_NAME = "Admin User";

export type UserRole = "Admin" | "Manager" | "Operator";

export type FeatureKey =
  | "dashboard"
  | "departments"
  | "devices"
  | "employees"
  | "timetable"
  | "shift"
  | "assign"
  | "transactions"
  | "leaves"
  | "reports"
  | "logs"
  | "settings";

export type FeatureAccess = Record<FeatureKey, boolean>;

export type UserAccessRecord = {
  id: string;
  username: string;
  fullName: string;
  password: string;
  role: UserRole;
  isActive: boolean;
  featureAccess: FeatureAccess;
  createdAt: string;
  updatedAt: string;
};

export type AuthSession = {
  userId: string;
  username: string;
  fullName: string;
  role: UserRole;
  isActive: boolean;
  featureAccess: FeatureAccess;
  loginAt: string;
};

const featureOrder: FeatureKey[] = [
  "dashboard",
  "departments",
  "devices",
  "employees",
  "timetable",
  "shift",
  "assign",
  "transactions",
  "leaves",
  "reports",
  "logs",
  "settings",
];

const routePriority: Array<{ feature: FeatureKey; href: string }> = [
  { feature: "dashboard", href: "/" },
  { feature: "departments", href: "/setup?section=areas" },
  { feature: "devices", href: "/devices" },
  { feature: "employees", href: "/employees" },
  { feature: "timetable", href: "/timetable" },
  { feature: "shift", href: "/shift" },
  { feature: "assign", href: "/assign" },
  { feature: "transactions", href: "/attendance" },
  { feature: "leaves", href: "/leaves" },
  { feature: "reports", href: "/reports" },
  { feature: "logs", href: "/logs" },
  { feature: "settings", href: "/settings" },
];

let preferredApiBase = "";

function isDefaultAdminUsername(value: string): boolean {
  return value.trim().toLowerCase() === DEFAULT_ADMIN_USERNAME;
}

function hasWindow() {
  return typeof window !== "undefined" && typeof window.localStorage !== "undefined";
}

export function createDefaultFeatureAccess(): FeatureAccess {
  return featureOrder.reduce((acc, key) => {
    acc[key] = key === "dashboard";
    return acc;
  }, {} as FeatureAccess);
}

export function createFullFeatureAccess(): FeatureAccess {
  return featureOrder.reduce((acc, key) => {
    acc[key] = true;
    return acc;
  }, {} as FeatureAccess);
}

export function normalizeFeatureAccess(raw: unknown): FeatureAccess {
  const fallback = createDefaultFeatureAccess();
  const source = raw && typeof raw === "object" ? (raw as Record<string, unknown>) : {};
  for (const key of featureOrder) {
    if (typeof source[key] === "boolean") {
      fallback[key] = source[key] as boolean;
    }
  }
  return fallback;
}

export function normalizeUsers(raw: unknown): UserAccessRecord[] {
  const normalized = (Array.isArray(raw) ? raw : [])
    .map((item, index): UserAccessRecord | null => {
      if (!item || typeof item !== "object") {
        return null;
      }

      const source = item as Record<string, unknown>;
      const username = typeof source.username === "string" ? source.username.trim() : "";
      if (!username) {
        return null;
      }

      const role: UserRole =
        source.role === "Admin" || source.role === "Manager" || source.role === "Operator"
          ? source.role
          : "Operator";
      const nowIso = new Date().toISOString();

      return {
        id: typeof source.id === "string" && source.id.trim().length > 0 ? source.id : `user-${index + 1}`,
        username,
        fullName: typeof source.fullName === "string" && source.fullName.trim().length > 0
          ? source.fullName.trim()
          : username,
        password: typeof source.password === "string" ? source.password : "",
        role,
        isActive: typeof source.isActive === "boolean" ? source.isActive : true,
        featureAccess: normalizeFeatureAccess(source.featureAccess),
        createdAt: typeof source.createdAt === "string" ? source.createdAt : nowIso,
        updatedAt: typeof source.updatedAt === "string" ? source.updatedAt : nowIso,
      };
    })
    .filter((item): item is UserAccessRecord => item !== null);

  const deduped = new Map<string, UserAccessRecord>();
  for (const item of normalized) {
    const key = item.username.toLowerCase();
    if (!deduped.has(key)) {
      deduped.set(key, item);
    }
  }

  const users = Array.from(deduped.values());
  const adminIndex = users.findIndex((item) => isDefaultAdminUsername(item.username));
  if (adminIndex >= 0) {
    const existing = users[adminIndex];
    users[adminIndex] = {
      ...existing,
      id: DEFAULT_ADMIN_USER_ID,
      username: DEFAULT_ADMIN_USERNAME,
      fullName: DEFAULT_ADMIN_FULL_NAME,
      password: existing.password.trim().length > 0 ? existing.password : DEFAULT_ADMIN_PASSWORD,
      role: "Admin",
      isActive: true,
      featureAccess: createFullFeatureAccess(),
      updatedAt: existing.updatedAt || new Date().toISOString(),
    };
  } else {
    users.push(fallbackAdminUser());
  }

  return users.sort((a, b) => {
    const aAdmin = isDefaultAdminUsername(a.username);
    const bAdmin = isDefaultAdminUsername(b.username);
    if (aAdmin && !bAdmin) {
      return -1;
    }
    if (!aAdmin && bAdmin) {
      return 1;
    }
    return a.username.localeCompare(b.username, undefined, { sensitivity: "base" });
  });
}

export function readStoredUsers(): UserAccessRecord[] {
  return [];
}

export function fallbackAdminUser(): UserAccessRecord {
  const nowIso = new Date().toISOString();
  return {
    id: DEFAULT_ADMIN_USER_ID,
    username: DEFAULT_ADMIN_USERNAME,
    fullName: DEFAULT_ADMIN_FULL_NAME,
    password: DEFAULT_ADMIN_PASSWORD,
    role: "Admin",
    isActive: true,
    featureAccess: createFullFeatureAccess(),
    createdAt: nowIso,
    updatedAt: nowIso,
  };
}

export function loginCandidates(): UserAccessRecord[] {
  return [fallbackAdminUser()];
}

export function readAuthSession(): AuthSession | null {
  if (!hasWindow()) {
    return null;
  }

  const parse = (raw: string | null): AuthSession | null => {
    if (!raw) {
      return null;
    }
    try {
      const parsed = JSON.parse(raw) as Partial<AuthSession>;
      if (!parsed || typeof parsed !== "object") {
        return null;
      }
      if (typeof parsed.username !== "string" || parsed.username.trim().length === 0) {
        return null;
      }
      return {
        userId: typeof parsed.userId === "string" ? parsed.userId : "",
        username: parsed.username.trim(),
        fullName: typeof parsed.fullName === "string" && parsed.fullName.trim().length > 0
          ? parsed.fullName.trim()
          : parsed.username.trim(),
        role:
          parsed.role === "Admin" || parsed.role === "Manager" || parsed.role === "Operator"
            ? parsed.role
            : "Operator",
        isActive: parsed.isActive !== false,
        featureAccess: normalizeFeatureAccess(parsed.featureAccess),
        loginAt: typeof parsed.loginAt === "string" ? parsed.loginAt : new Date().toISOString(),
      };
    } catch {
      return null;
    }
  };

  return parse(window.sessionStorage.getItem(AUTH_SESSION_STORAGE_KEY))
    ?? parse(window.localStorage.getItem(AUTH_SESSION_STORAGE_KEY));
}

export function saveAuthSession(session: AuthSession, remember: boolean) {
  if (!hasWindow()) {
    return;
  }

  const payload = JSON.stringify(session);
  if (remember) {
    window.localStorage.setItem(AUTH_SESSION_STORAGE_KEY, payload);
    window.sessionStorage.removeItem(AUTH_SESSION_STORAGE_KEY);
    return;
  }

  window.sessionStorage.setItem(AUTH_SESSION_STORAGE_KEY, payload);
  window.localStorage.removeItem(AUTH_SESSION_STORAGE_KEY);
}

export function clearAuthSession() {
  if (!hasWindow()) {
    return;
  }
  window.localStorage.removeItem(AUTH_SESSION_STORAGE_KEY);
  window.sessionStorage.removeItem(AUTH_SESSION_STORAGE_KEY);
}

export function notifyAuthUpdated() {
  if (!hasWindow()) {
    return;
  }
  window.dispatchEvent(new Event(AUTH_SYNC_EVENT));
}

type AuthApiError = Error & { status?: number };

function createAuthApiError(message: string, status?: number): AuthApiError {
  const error = new Error(message) as AuthApiError;
  if (typeof status === "number") {
    error.status = status;
  }
  return error;
}

async function authRequest<T>(path: string, init?: RequestInit): Promise<T> {
  let lastNetworkError: AuthApiError = createAuthApiError("Failed to reach the server.");
  let lastHttpError: AuthApiError | null = null;

  for (const base of resolveApiBases({ preferredBase: preferredApiBase, includeCurrentOrigin: true })) {
    try {
      const response = await fetch(`${base}${path}`, {
        ...init,
        headers: {
          "Content-Type": "application/json",
          ...(init?.headers ?? {}),
        },
        cache: "no-store",
      });

      if (response.ok) {
        preferredApiBase = base;
        return (await response.json()) as T;
      }

      let message = `Request failed (${response.status})`;
      try {
        const payload = await response.json();
        if (payload && typeof payload === "object" && typeof (payload as { message?: unknown }).message === "string") {
          message = (payload as { message: string }).message;
        }
      } catch {
        const text = await response.text();
        if (text.trim()) {
          message = text.trim();
        }
      }

      lastHttpError = createAuthApiError(message, response.status);
    } catch (error) {
      lastNetworkError = createAuthApiError(error instanceof Error ? error.message : "Failed to reach the server.");
    }
  }

  throw lastHttpError ?? lastNetworkError;
}

export async function loadUsersFromApi(): Promise<UserAccessRecord[]> {
  const users = await authRequest<UserAccessRecord[]>("/api/auth/users");
  return normalizeUsers(users).map((user) => ({
    ...user,
    password: "",
  }));
}

export async function saveUsersToApi(users: UserAccessRecord[]): Promise<UserAccessRecord[]> {
  const payload = users.map((user) => ({
    id: user.id,
    username: user.username.trim(),
    fullName: user.fullName.trim() || user.username.trim(),
    password: user.password,
    role: user.role,
    isActive: user.isActive,
    featureAccess: normalizeFeatureAccess(user.featureAccess),
  }));
  const saved = await authRequest<UserAccessRecord[]>("/api/auth/users", {
    method: "PUT",
    body: JSON.stringify(payload),
  });
  return normalizeUsers(saved).map((user) => ({
    ...user,
    password: "",
  }));
}

export async function loginUser(username: string, password: string): Promise<UserAccessRecord & { token?: string }> {
  const user = await authRequest<UserAccessRecord & { token?: string }>("/api/auth/login", {
    method: "POST",
    body: JSON.stringify({
      username: username.trim(),
      password,
    }),
  });
  return { ...(normalizeUsers([user])[0] ?? fallbackAdminUser()), token: user.token };
}

export async function hydrateAuthSession(session: AuthSession): Promise<AuthSession | null> {
  const query = new URLSearchParams();
  if (session.userId) {
    query.set("userId", session.userId);
  }
  if (session.username) {
    query.set("username", session.username);
  }

  try {
    const user = await authRequest<UserAccessRecord>(`/api/auth/session?${query.toString()}`);
    if (!user.isActive) {
      return null;
    }
    return toAuthSession(normalizeUsers([user])[0] ?? user, session.loginAt);
  } catch (error) {
    const apiError = error as AuthApiError;
    if (apiError.status === 404 || apiError.status === 401 || apiError.status === 403) {
      return null;
    }
    throw error;
  }
}

export function toAuthSession(user: UserAccessRecord, loginAt?: string): AuthSession {
  return {
    userId: user.id,
    username: user.username,
    fullName: user.fullName,
    role: user.role,
    isActive: user.isActive,
    featureAccess: normalizeFeatureAccess(user.featureAccess),
    loginAt: loginAt ?? new Date().toISOString(),
  };
}

export function resolveFeatureForPath(pathname: string, tab: string | null): FeatureKey | null {
  if (pathname === "/" || pathname.startsWith("/dashboard")) {
    return "dashboard";
  }
  if (pathname.startsWith("/devices")) {
    return "devices";
  }
  if (pathname.startsWith("/employees")) {
    return "employees";
  }
  if (pathname.startsWith("/timetable")) {
    return "timetable";
  }
  if (pathname.startsWith("/shift")) {
    return "shift";
  }
  if (pathname.startsWith("/assign")) {
    return "assign";
  }
  if (pathname.startsWith("/access-control")) {
    return "assign";
  }
  if (pathname.startsWith("/shifts")) {
    return tab === "timetable" ? "timetable" : "shift";
  }
  if (pathname.startsWith("/attendance")) {
    return "transactions";
  }
  if (pathname.startsWith("/leaves")) {
    return "leaves";
  }
  if (pathname.startsWith("/setup") || pathname.startsWith("/departments")) {
    return "departments";
  }
  if (pathname.startsWith("/reports")) {
    return "reports";
  }
  if (pathname.startsWith("/logs") || pathname.startsWith("/notifications")) {
    return "logs";
  }
  if (pathname.startsWith("/settings")) {
    return "settings";
  }
  return null;
}

export function firstAllowedRoute(access: FeatureAccess): string {
  for (const entry of routePriority) {
    if (access[entry.feature]) {
      return entry.href;
    }
  }
  return "/login";
}

export function firstAllowedRouteForSession(session: AuthSession): string {
  return firstAllowedRoute(session.featureAccess);
}

export function initialsFromName(name: string): string {
  const tokens = (name || "").trim().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) {
    return "NA";
  }
  if (tokens.length === 1) {
    return tokens[0].slice(0, 2).toUpperCase();
  }
  return `${tokens[0][0] ?? ""}${tokens[1][0] ?? ""}`.toUpperCase();
}
