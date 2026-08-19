"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useTransition } from "react";
import type { Route } from "next";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { AppSidebar } from "@/components/app-sidebar";
import { Topbar } from "@/components/topbar";
import { CalendarProvider } from "@/components/date/calendar-context";
import {
  AUTH_SYNC_EVENT,
  clearAuthSession,
  firstAllowedRouteForSession,
  hydrateAuthSession,
  notifyAuthUpdated,
  readAuthSession,
  resolveFeatureForPath,
  saveAuthSession,
  type AuthSession,
} from "@/components/data/auth-store";
import { loadShiftConfigFromApi } from "@/components/data/shift-store";
import { clearAuthToken } from "@/lib/auth-fetch";

export function DashboardLayoutClient({ children }: Readonly<{ children: React.ReactNode }>) {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const tab = searchParams.get("tab");
  const [, startTransition] = useTransition();
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const [desktop, setDesktop] = useState(false);
  const [authReady, setAuthReady] = useState(() => typeof window !== "undefined" && !!readAuthSession());
  const [session, setSession] = useState<AuthSession | null>(() => (typeof window !== "undefined" ? readAuthSession() : null));
  const inactivityTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const refreshAuth = useCallback(() => {
    let cancelled = false;

    const run = async () => {
      const current = readAuthSession();
      if (!current) {
        if (!cancelled) {
          setSession(null);
          setAuthReady(true);
        }
        return;
      }

      try {
        const hydrated = await hydrateAuthSession(current);
        if (cancelled) {
          return;
        }

        if (!hydrated) {
          clearAuthSession();
          clearAuthToken();
          setSession(null);
          setAuthReady(true);
          return;
        }

        saveAuthSession(hydrated, true);
        setSession(hydrated);
        setAuthReady(true);
      } catch {
        if (!cancelled) {
          setSession(current);
          setAuthReady(true);
        }
      }
    };

    void run();

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const cleanup = refreshAuth();
    const onStorage = (event: StorageEvent) => {
      if (!event.key || event.key.includes("pb_attendance")) {
        refreshAuth();
      }
    };
    const onAuthUpdate = () => refreshAuth();
    window.addEventListener("storage", onStorage);
    window.addEventListener(AUTH_SYNC_EVENT, onAuthUpdate);
    return () => {
      cleanup?.();
      window.removeEventListener("storage", onStorage);
      window.removeEventListener(AUTH_SYNC_EVENT, onAuthUpdate);
    };
  }, [refreshAuth]);

  useEffect(() => {
    if (!authReady || !session) {
      return;
    }

    void loadShiftConfigFromApi().catch(() => {
      // Keep existing local cache if backend shift config is unavailable.
    });
  }, [authReady, session]);

  useEffect(() => {
    if (!authReady) {
      return;
    }
    if (!session) {
      startTransition(() => {
        router.replace("/login");
      });
      return;
    }

    if (session.role === "Admin" || session.role === "Manager") {
      return;
    }

    const normalizedPathname = pathname.length > 1 ? pathname.replace(/\/+$/, "") : pathname;
    const requiredFeature = resolveFeatureForPath(normalizedPathname, tab);
    if (!requiredFeature) {
      return;
    }

    if (!session.featureAccess[requiredFeature]) {
      const target = firstAllowedRouteForSession(session);
      const normalizedTarget = target.length > 1 ? target.replace(/\/+$/, "") : target;
      if (normalizedTarget === normalizedPathname || normalizedPathname === "/") {
        return;
      }
      startTransition(() => {
        router.replace(target as Route);
      });
    }
  }, [authReady, pathname, router, session, startTransition, tab]);

  useEffect(() => {
    const query = window.matchMedia("(min-width: 768px)");
    const update = () => setDesktop(query.matches);
    update();
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);

  useEffect(() => {
    if (!mobileOpen) {
      return;
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setMobileOpen(false);
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [mobileOpen]);

  const handleToggleSidebar = useCallback(() => {
    if (desktop) {
      setCollapsed((value) => !value);
    } else {
      setMobileOpen((value) => !value);
    }
  }, [desktop]);

  const handleNavigate = () => {
    if (!desktop) {
      setMobileOpen(false);
    }
  };

  const handleLogout = useCallback(() => {
    clearAuthSession();
    clearAuthToken();
    notifyAuthUpdated();
    startTransition(() => {
      router.replace("/login");
    });
  }, [router, startTransition]);

  useEffect(() => {
    if (!session) {
      if (inactivityTimerRef.current) {
        clearTimeout(inactivityTimerRef.current);
        inactivityTimerRef.current = null;
      }
      return;
    }

    const timeoutMs = 5 * 60 * 1000;
    const resetInactivityTimer = () => {
      if (inactivityTimerRef.current) {
        clearTimeout(inactivityTimerRef.current);
      }
      inactivityTimerRef.current = setTimeout(() => {
        handleLogout();
      }, timeoutMs);
    };

    const activityEvents: Array<keyof WindowEventMap> = [
      "mousemove",
      "mousedown",
      "keydown",
      "touchstart",
      "scroll",
      "click",
    ];

    for (const eventName of activityEvents) {
      window.addEventListener(eventName, resetInactivityTimer, { passive: true });
    }

    resetInactivityTimer();

    return () => {
      if (inactivityTimerRef.current) {
        clearTimeout(inactivityTimerRef.current);
        inactivityTimerRef.current = null;
      }
      for (const eventName of activityEvents) {
        window.removeEventListener(eventName, resetInactivityTimer);
      }
    };
  }, [handleLogout, session]);

  const canRender = useMemo(() => {
    if (!authReady || !session) {
      return false;
    }
    if (session.role === "Admin" || session.role === "Manager") {
      return true;
    }
    const normalizedPathname = pathname.length > 1 ? pathname.replace(/\/+$/, "") : pathname;
    const requiredFeature = resolveFeatureForPath(normalizedPathname, tab);
    if (!requiredFeature) {
      return true;
    }
    return Boolean(session.featureAccess[requiredFeature]);
  }, [authReady, pathname, session, tab]);

  if (!canRender) {
    return (
      <div className="dashboard-canvas grid min-h-screen place-items-center">
        <div className="rounded-lg border border-[#d9e8f7] bg-white px-4 py-2 text-sm text-[#1d446b]">
          Loading...
        </div>
      </div>
    );
  }

  return (
    <CalendarProvider>
    <div className="dashboard-canvas flex min-h-screen min-w-0">
      {!desktop && mobileOpen ? (
        <button
          type="button"
          className="fixed inset-0 z-30 bg-[#0d334d]/20 backdrop-blur-[1px] md:hidden"
          onClick={() => setMobileOpen(false)}
          aria-label="Close sidebar overlay"
        />
      ) : null}

      <AppSidebar
        collapsed={desktop ? collapsed : false}
        mobileOpen={desktop ? true : mobileOpen}
        onCloseMobile={() => setMobileOpen(false)}
        onNavigate={handleNavigate}
        session={session}
      />
      <div className="flex min-h-screen min-w-0 flex-1 flex-col">
        <Topbar onToggleSidebar={handleToggleSidebar} onLogout={handleLogout} session={session} />
        <main className="min-h-0 flex-1 overflow-visible p-4 md:p-6">
          <div className="page-chrome">{children}</div>
        </main>
      </div>
    </div>
    </CalendarProvider>
  );
}
