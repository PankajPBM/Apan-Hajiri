"use client";

import { useEffect, useRef, useState } from "react";
import {
  Bell,
  ChevronDown,
  Globe,
  KeyRound,
  LogOut,
  Mail,
  Menu,
  MessageCircle,
  Search,
  Sparkles,
  ShieldCheck,
} from "lucide-react";
import { usePathname, useSearchParams } from "next/navigation";
import { LicensePanel } from "@/components/license-panel";
import { useBrandingSettings } from "@/components/data/app-settings";
import { initialsFromName, type AuthSession } from "@/components/data/auth-store";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDate } from "@/components/date/date-display";
import { loadAlertFeed, type AlertItem } from "@/lib/alerts";
import { fetchAppVersion } from "@/components/data/license-client";
import packageInfo from "@/package.json";

type TopbarProps = {
  onToggleSidebar: () => void;
  onLogout: () => void;
  session: AuthSession | null;
};

const pageTitleMap: Record<string, string> = {
  "/": "Dashboard",
  "/employees": "Employees",
  "/devices": "Devices",
  "/transactions": "Transactions",
  "/attendance": "Transactions",
  "/leaves": "Leaves & Holidays",
  "/shifts": "Shift Setup",
  "/shift": "Shifts",
  "/timetable": "Time Table",
  "/assign": "Schedule Shift",
  "/setup": "Setup",
  "/departments": "Setup",
  "/reports": "Reports",
  "/logs": "System Logs",
  "/notifications": "Alerts & Notifications",
  "/settings": "Settings",
};

const DEVELOPER_NAME = "PB Tech Zone";
const DEVELOPER_PHONE = "+9779814891277";
const DEVELOPER_EMAIL = "pbtechzone77@gmail.com";
const DEVELOPER_WEBSITE = "https://prakash77.com.np/";
const WHATSAPP_URL = "https://wa.me/9779814891277";
const SOFTWARE_VERSION = process.env.NEXT_PUBLIC_APP_VERSION ?? packageInfo.version;

export function Topbar({ onToggleSidebar, onLogout, session }: TopbarProps) {
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const [licenseOpen, setLicenseOpen] = useState(false);
  const [profileMenuOpen, setProfileMenuOpen] = useState(false);
  const [aboutOpen, setAboutOpen] = useState(false);
  const [alertsOpen, setAlertsOpen] = useState(false);
  const [alerts, setAlerts] = useState<AlertItem[]>([]);
  const profileMenuRef = useRef<HTMLDivElement | null>(null);
  const alertsMenuRef = useRef<HTMLDivElement | null>(null);
  const branding = useBrandingSettings();
  const activeTab = searchParams.get("tab");
  const cleanPath = pathname !== "/" ? pathname.replace(/\/+$/, "") : "/";
  const title =
    cleanPath === "/shifts"
      ? activeTab === "timetable"
        ? "Time Table"
        : activeTab === "break"
        ? "Break Time"
        : activeTab === "overtime"
        ? "OT"
        : "Shifts"
      : pageTitleMap[cleanPath] ??
        Object.entries(pageTitleMap).find(([path]) => path !== "/" && cleanPath.startsWith(path))?.[1] ??
        (branding.organizationName || "Apan Hajiri");

  const unread = alerts.length;
  const initials = initialsFromName(session?.fullName || session?.username || "NA");
  const { calendarType, setCalendarType } = useCalendarPreference();
  const [appVersion, setAppVersion] = useState(SOFTWARE_VERSION);

  useEffect(() => {
    fetchAppVersion()
      .then((v) => {
        if (v) setAppVersion(v);
      })
      .catch(() => {});
  }, []);

  useEffect(() => {
    setLicenseOpen(false);
    setProfileMenuOpen(false);
    setAboutOpen(false);
    setAlertsOpen(false);
  }, [pathname]);

  useEffect(() => {
    if (!profileMenuOpen) {
      setAboutOpen(false);
      return;
    }

    const onPointerDown = (event: MouseEvent) => {
      if (!profileMenuRef.current) return;
      if (!profileMenuRef.current.contains(event.target as Node)) {
        setProfileMenuOpen(false);
      }
    };

    const onEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setProfileMenuOpen(false);
    };

    window.addEventListener("mousedown", onPointerDown);
    window.addEventListener("keydown", onEscape);
    return () => {
      window.removeEventListener("mousedown", onPointerDown);
      window.removeEventListener("keydown", onEscape);
    };
  }, [profileMenuOpen]);

  useEffect(() => {
    let disposed = false;
    const loadAlerts = async () => {
      try {
        const rows = await loadAlertFeed();
        if (!disposed) setAlerts(rows);
      } catch {
        if (!disposed) setAlerts([]);
      }
    };

    void loadAlerts();
    const handle = window.setInterval(() => {
      void loadAlerts();
    }, 30000);

    return () => {
      disposed = true;
      window.clearInterval(handle);
    };
  }, []);

  useEffect(() => {
    if (!alertsOpen) return;

    const onPointerDown = (event: MouseEvent) => {
      if (!alertsMenuRef.current) return;
      if (!alertsMenuRef.current.contains(event.target as Node)) {
        setAlertsOpen(false);
      }
    };

    const onEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setAlertsOpen(false);
    };

    window.addEventListener("mousedown", onPointerDown);
    window.addEventListener("keydown", onEscape);
    return () => {
      window.removeEventListener("mousedown", onPointerDown);
      window.removeEventListener("keydown", onEscape);
    };
  }, [alertsOpen]);

  return (
    <>
      <header className="sticky top-0 z-30 flex h-16 w-full items-center justify-between border-b border-slate-200 bg-white px-4 sm:px-6">
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={onToggleSidebar}
            className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-600 hover:bg-slate-50 hover:text-slate-900 focus:outline-none"
            aria-label="Toggle navigation sidebar"
          >
            <Menu className="h-5 w-5" />
          </button>

          <div className="flex items-center gap-2">
            <h1 className="text-base font-semibold text-slate-800">{title}</h1>
            <span className="hidden items-center gap-1 rounded-md bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700 border border-emerald-100 sm:inline-flex">
              <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
              Online
            </span>
          </div>
        </div>

        <div className="flex items-center gap-2.5">
          {/* Live Date display */}
          <div className="hidden items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-1.5 text-xs font-medium text-slate-600 md:flex">
            <span>{formatCalendarDate(new Date(), calendarType)}</span>
          </div>

          {/* Calendar Picker (BS / AD) */}
          <div className="flex items-center rounded-lg border border-slate-200 bg-slate-50 px-2.5 py-1.5 text-xs font-medium text-slate-700">
            <label className="mr-1.5 hidden text-slate-400 sm:inline">Cal:</label>
            <select
              value={calendarType}
              onChange={(e) => setCalendarType(e.target.value === "ad" ? "ad" : "bs")}
              className="bg-transparent font-semibold text-slate-800 outline-none cursor-pointer"
            >
              <option value="bs">BS</option>
              <option value="ad">AD</option>
            </select>
          </div>

          {/* Alerts / Notifications */}
          <div className="relative" ref={alertsMenuRef}>
            <button
              className="relative inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              type="button"
              onClick={() => setAlertsOpen((prev) => !prev)}
              aria-label="View notifications"
            >
              <Bell className="h-4.5 w-4.5" />
              {unread > 0 ? (
                <span className="absolute -right-1 -top-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-rose-500 px-1 text-[10px] font-bold text-white">
                  {unread}
                </span>
              ) : null}
            </button>

            {alertsOpen ? (
              <div className="absolute right-0 top-full mt-2 z-50 w-80 origin-top-right rounded-xl border border-slate-200 bg-white p-3 shadow-lg">
                <div className="mb-2.5 flex items-center justify-between border-b border-slate-100 pb-2">
                  <p className="text-xs font-semibold text-slate-800">Notifications</p>
                  <a
                    href="/notifications"
                    className="text-xs font-medium text-blue-600 hover:text-blue-700"
                    onClick={() => setAlertsOpen(false)}
                  >
                    View all
                  </a>
                </div>

                <div className="max-h-72 space-y-1.5 overflow-y-auto pr-1">
                  {alerts.length === 0 ? (
                    <div className="rounded-lg bg-slate-50 p-3 text-center text-xs text-slate-500">
                      No new notifications.
                    </div>
                  ) : (
                    alerts.slice(0, 6).map((alert) => (
                      <a
                        key={alert.id}
                        href={alert.href}
                        onClick={() => setAlertsOpen(false)}
                        className="block rounded-lg p-2 transition hover:bg-slate-50 border border-slate-100"
                      >
                        <div className="flex items-center justify-between gap-2">
                          <p className="text-xs font-medium text-slate-800">{alert.title}</p>
                          <span
                            className={`rounded px-1.5 py-0.5 text-[10px] font-semibold ${
                              alert.level === "warning"
                                ? "bg-amber-50 text-amber-700"
                                : alert.level === "success"
                                ? "bg-emerald-50 text-emerald-700"
                                : "bg-blue-50 text-blue-700"
                            }`}
                          >
                            {alert.level}
                          </span>
                        </div>
                        <p className="mt-0.5 text-[11px] text-slate-500 line-clamp-1">{alert.detail}</p>
                      </a>
                    ))
                  )}
                </div>
              </div>
            ) : null}
          </div>

          {/* User Profile Menu */}
          <div className="relative" ref={profileMenuRef}>
            <button
              type="button"
              onClick={() => setProfileMenuOpen((prev) => !prev)}
              className="flex items-center gap-2 rounded-lg border border-slate-200 bg-white p-1.5 pr-2.5 hover:bg-slate-50 transition"
            >
              <div className="flex h-7 w-7 items-center justify-center rounded-md bg-blue-600 text-xs font-bold text-white">
                {initials}
              </div>
              <div className="hidden text-left sm:block">
                <span className="block text-xs font-medium text-slate-800 truncate max-w-[100px]">
                  {session?.fullName || session?.username || "Admin"}
                </span>
              </div>
              <ChevronDown className="h-3.5 w-3.5 text-slate-400" />
            </button>

            {profileMenuOpen ? (
              <div className="absolute right-0 top-full mt-2 z-50 w-56 origin-top-right rounded-xl border border-slate-200 bg-white p-1.5 shadow-lg">
                <div className="border-b border-slate-100 px-3 py-2">
                  <p className="text-xs font-semibold text-slate-800">{session?.fullName || "Admin"}</p>
                  <p className="text-[11px] text-slate-500">{session?.role || "Administrator"}</p>
                </div>

                <div className="py-1">
                  <button
                    type="button"
                    onClick={() => {
                      setProfileMenuOpen(false);
                      setLicenseOpen(true);
                    }}
                    className="flex w-full items-center gap-2.5 rounded-lg px-3 py-2 text-xs font-medium text-slate-700 hover:bg-slate-50"
                  >
                    <KeyRound className="h-4 w-4 text-blue-600" />
                    License Management
                  </button>

                  <button
                    type="button"
                    onClick={() => setAboutOpen((prev) => !prev)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-xs font-medium text-slate-700 hover:bg-slate-50"
                  >
                    <span className="flex items-center gap-2.5">
                      <ShieldCheck className="h-4 w-4 text-slate-500" />
                      About System
                    </span>
                    <ChevronDown className={`h-3.5 w-3.5 text-slate-400 transition ${aboutOpen ? "rotate-180" : ""}`} />
                  </button>

                  {aboutOpen ? (
                    <div className="my-1 rounded-lg bg-slate-50 p-2.5 text-[11px] text-slate-600 space-y-1">
                      <p className="font-semibold text-slate-800">Apan Hajiri (अपन हाजिरी)</p>
                      <p>Version: {appVersion}</p>
                      <p>Developer: {DEVELOPER_NAME}</p>
                      <div className="pt-1.5 flex gap-2">
                        <a
                          href={WHATSAPP_URL}
                          target="_blank"
                          rel="noreferrer"
                          className="text-blue-600 hover:underline"
                        >
                          WhatsApp
                        </a>
                        <span>•</span>
                        <a
                          href={DEVELOPER_WEBSITE}
                          target="_blank"
                          rel="noreferrer"
                          className="text-blue-600 hover:underline"
                        >
                          Website
                        </a>
                      </div>
                    </div>
                  ) : null}
                </div>

                <div className="border-t border-slate-100 pt-1">
                  <button
                    type="button"
                    onClick={() => {
                      setProfileMenuOpen(false);
                      onLogout();
                    }}
                    className="flex w-full items-center gap-2.5 rounded-lg px-3 py-2 text-xs font-medium text-rose-600 hover:bg-rose-50"
                  >
                    <LogOut className="h-4 w-4" />
                    Sign Out
                  </button>
                </div>
              </div>
            ) : null}
          </div>
        </div>
      </header>

      {licenseOpen ? <LicensePanel onClose={() => setLicenseOpen(false)} /> : null}
    </>
  );
}
