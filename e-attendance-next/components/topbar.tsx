"use client";

import { useEffect, useRef, useState } from "react";
import { Bell, ChevronDown, Globe, KeyRound, LogOut, Mail, Menu, MessageCircle, Search, Sparkles, X } from "lucide-react";
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
  "/leaves": "Leaves and Holiday",
  "/shifts": "Shift Setup",
  "/shift": "Shifts",
  "/timetable": "Time Table",
  "/assign": "Schedule Shift",
  "/setup": "Setup",
  "/departments": "Setup",
  "/reports": "Reports",
  "/logs": "Logs",
  "/notifications": "Logs",
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
  const title = cleanPath === "/shifts"
    ? activeTab === "timetable"
      ? "Time Table"
      : activeTab === "break"
        ? "Break Time"
        : activeTab === "overtime"
          ? "OT"
          : "Shifts"
    : pageTitleMap[cleanPath] ??
      Object.entries(pageTitleMap).find(([path]) => path !== "/" && cleanPath.startsWith(path))?.[1] ??
      (branding.organizationName || "अपन हाजिरी");
  const unread = alerts.length;
  const initials = initialsFromName(session?.fullName || session?.username || "NA");
  const { calendarType, setCalendarType } = useCalendarPreference();
  const [appVersion, setAppVersion] = useState(SOFTWARE_VERSION);

  useEffect(() => {
    fetchAppVersion().then((v) => {
      if (v) setAppVersion(v);
    }).catch(() => {});
  }, []);

  useEffect(() => {
    if (!profileMenuOpen) {
      setAboutOpen(false);
      return;
    }

    const onPointerDown = (event: MouseEvent) => {
      if (!profileMenuRef.current) {
        return;
      }
      if (!profileMenuRef.current.contains(event.target as Node)) {
        setProfileMenuOpen(false);
      }
    };

    const onEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setProfileMenuOpen(false);
      }
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
        if (!disposed) {
          setAlerts(rows);
        }
      } catch {
        if (!disposed) {
          setAlerts([]);
        }
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
    if (!alertsOpen) {
      return;
    }

    const onPointerDown = (event: MouseEvent) => {
      if (!alertsMenuRef.current) {
        return;
      }
      if (!alertsMenuRef.current.contains(event.target as Node)) {
        setAlertsOpen(false);
      }
    };

    const onEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setAlertsOpen(false);
      }
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
      <header className="sticky top-0 z-30 border-b border-[#d9e8f7]/70 bg-[radial-gradient(circle_at_top_left,rgba(56,189,248,0.18),transparent_30%),linear-gradient(120deg,rgba(241,246,252,0.9),rgba(234,244,255,0.94))] px-4 py-3 backdrop-blur-xl">
        <div className="relative flex min-h-[4.25rem] flex-col gap-3 overflow-visible rounded-[24px] border border-[#d9e8f7] bg-[linear-gradient(135deg,rgba(255,255,255,0.97),rgba(243,249,255,0.98))] px-3 py-3 shadow-[0_22px_32px_-24px_rgba(20,47,70,0.82)] md:flex-row md:items-center md:justify-between md:gap-4 md:px-4">
          <span className="pointer-events-none absolute -left-10 top-0 h-24 w-24 rounded-full bg-cyan-300/25 blur-2xl" />
          <span className="pointer-events-none absolute right-10 top-1 h-20 w-28 rounded-full bg-blue-300/25 blur-2xl" />
          <span className="pointer-events-none absolute inset-x-12 bottom-0 h-px bg-[linear-gradient(90deg,transparent,rgba(59,130,246,0.35),transparent)]" />

          <div className="relative flex min-w-0 flex-1 items-center gap-3">
            <button
              type="button"
              onClick={onToggleSidebar}
              className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl border border-[#d3e2f2] bg-[linear-gradient(145deg,#ffffff,#eef6ff)] text-[#1d446b] shadow-[0_14px_24px_-20px_rgba(37,99,235,0.9)] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd] hover:bg-[linear-gradient(145deg,#ffffff,#e0f2fe)] hover:shadow-[0_18px_26px_-18px_rgba(37,99,235,0.8)]"
            >
              <Menu className="h-4.5 w-4.5" />
            </button>

            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <span className="inline-flex items-center gap-1 rounded-full border border-[#cfe4f9] bg-[linear-gradient(135deg,#f8fdff,#eef7ff)] px-2.5 py-1 text-[10px] font-semibold uppercase tracking-[0.18em] text-[#5a7b98] shadow-[0_10px_18px_-18px_rgba(37,99,235,0.65)]">
                  <Sparkles className="h-3 w-3 text-[#2563eb]" />
                  {branding.systemTitle}
                </span>
                <span className="inline-flex items-center gap-1 rounded-full bg-[#0f172a] px-2.5 py-1 text-[10px] font-semibold uppercase tracking-[0.18em] text-white shadow-[0_12px_18px_-16px_rgba(15,23,42,0.9)]">
                  <span className="h-2 w-2 rounded-full bg-emerald-400 shadow-[0_0_0_6px_rgba(52,211,153,0.12)]" />
                  Live Workspace
                </span>
              </div>
              <div className="mt-2 flex min-w-0 flex-wrap items-center gap-2">
                <div className="truncate text-base font-semibold text-[#0d334d] md:text-[1.02rem]">{title}</div>
              </div>
            </div>
          </div>

          <div className="relative z-10 flex flex-wrap items-center justify-end gap-2 md:flex-nowrap md:gap-3">
            <button className="hidden h-10 w-10 items-center justify-center rounded-2xl border border-[#d3e2f2] bg-[linear-gradient(145deg,#ffffff,#eef6ff)] text-[#1d446b] shadow-[0_12px_22px_-18px_rgba(37,99,235,0.8)] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd] hover:bg-[linear-gradient(145deg,#ffffff,#dff4ff)] hover:shadow-[0_18px_28px_-18px_rgba(37,99,235,0.82)] sm:inline-flex" type="button">
              <Search className="h-4 w-4" />
            </button>
            <div className="relative" ref={alertsMenuRef}>
              <button
                className="relative inline-flex h-10 w-10 items-center justify-center rounded-2xl border border-[#d3e2f2] bg-[linear-gradient(145deg,#ffffff,#eef6ff)] text-[#1d446b] shadow-[0_12px_22px_-18px_rgba(37,99,235,0.8)] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd] hover:bg-[linear-gradient(145deg,#ffffff,#dff4ff)] hover:shadow-[0_18px_28px_-18px_rgba(37,99,235,0.82)]"
                type="button"
                onClick={() => setAlertsOpen((prev) => !prev)}
              >
                <Bell className={`h-4 w-4 ${unread > 0 ? "animate-pulse" : ""}`} />
                {unread > 0 ? (
                  <span className="absolute -right-1 -top-1 rounded-full bg-[linear-gradient(135deg,#ef4444,#dc2626)] px-1.5 text-[10px] font-semibold text-white shadow-[0_10px_18px_-14px_rgba(220,38,38,0.95)]">{unread}</span>
                ) : null}
              </button>

              <div
                className={`absolute right-0 top-[calc(100%+0.7rem)] z-[80] w-[min(24rem,calc(100vw-1.5rem))] origin-top-right rounded-2xl border border-[#cde0f5] bg-white p-3 shadow-[0_26px_38px_-24px_rgba(18,54,88,0.85)] transition-all duration-200 ${
                  alertsOpen ? "translate-y-0 scale-100 opacity-100" : "pointer-events-none -translate-y-1 scale-95 opacity-0"
                }`}
              >
                <div className="mb-3 flex items-center justify-between gap-2">
                  <div>
                    <p className="text-sm font-semibold text-[#123a60]">Notifications & Alerts</p>
                    <p className="text-[11px] text-[#5b7a96]">Manual punch, requests, device alerts, and system updates.</p>
                  </div>
                  <a
                    href="/notifications"
                    className="rounded-full border border-[#cfe0f2] bg-[#f5f9ff] px-2.5 py-1 text-[11px] font-semibold text-[#1e4c78]"
                    onClick={() => setAlertsOpen(false)}
                  >
                    View all
                  </a>
                </div>

                <div className="max-h-[22rem] space-y-2 overflow-y-auto pr-1">
                  {alerts.length === 0 ? (
                    <div className="rounded-xl border border-[#d9e8f7] bg-[linear-gradient(120deg,#f8fbff,#eef6ff)] px-3 py-3 text-sm text-[#5c7a97]">
                      No active alerts right now.
                    </div>
                  ) : (
                    alerts.slice(0, 8).map((alert) => (
                      <a
                        key={alert.id}
                        href={alert.href}
                        onClick={() => setAlertsOpen(false)}
                        className="block rounded-xl border border-[#d9e8f7] bg-[linear-gradient(120deg,#f8fbff,#eef6ff)] px-3 py-3 transition hover:-translate-y-0.5 hover:border-[#93c5fd]"
                      >
                        <div className="flex items-start justify-between gap-2">
                          <p className="text-sm font-semibold text-[#123a60]">{alert.title}</p>
                          <span
                            className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase ${
                              alert.level === "warning"
                                ? "bg-amber-100 text-amber-800"
                                : alert.level === "success"
                                  ? "bg-emerald-100 text-emerald-800"
                                  : "bg-sky-100 text-sky-800"
                            }`}
                          >
                            {alert.level}
                          </span>
                        </div>
                        <p className="mt-1 text-xs text-[#5b7a96]">{alert.detail}</p>
                      </a>
                    ))
                  )}
                </div>
              </div>
            </div>
            <div className="hidden rounded-2xl border border-[#d3e2f2] bg-[linear-gradient(135deg,#ffffff,#f3faff)] px-3 py-2 text-xs font-medium text-[#53738d] shadow-[0_14px_22px_-20px_rgba(37,99,235,0.78)] sm:flex sm:items-center sm:gap-2">
              <span className="h-2.5 w-2.5 rounded-full bg-emerald-400 shadow-[0_0_0_6px_rgba(52,211,153,0.12)]" />
              <span className="whitespace-nowrap">{formatCalendarDate(new Date(), calendarType)}</span>
            </div>
            <div
              title="Display calendar only — device communication always uses AD (Gregorian) dates regardless of this setting"
              className="rounded-2xl border border-[#d3e2f2] bg-[linear-gradient(135deg,#ffffff,#f3faff)] px-2.5 py-1.5 text-xs text-[#53738d] shadow-[0_14px_22px_-20px_rgba(37,99,235,0.78)] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd]">
              <label className="mr-1 hidden sm:inline text-[#6b87a1]">Calendar</label>
              <select
                value={calendarType}
                onChange={(event) => setCalendarType(event.target.value === "ad" ? "ad" : "bs")}
                className="bg-transparent text-xs font-semibold text-[#1d446b] outline-none"
              >
                <option value="bs">BS</option>
                <option value="ad">AD</option>
              </select>
            </div>
            <div className="relative" ref={profileMenuRef}>
              <button
                type="button"
                onClick={() => setProfileMenuOpen((prev) => !prev)}
                className="group inline-flex h-11 max-w-full items-center gap-2 rounded-full border border-[#c6ddf3] bg-[linear-gradient(145deg,#ffffff,#eef7ff)] pl-1 pr-3 shadow-[0_14px_24px_-20px_rgba(34,72,108,0.82)] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd] hover:shadow-[0_20px_28px_-18px_rgba(37,99,235,0.78)]"
              >
                <span className="grid h-9 w-9 place-items-center rounded-full bg-[linear-gradient(135deg,#1e3a8a,#2563eb,#0ea5e9)] text-xs font-bold text-white shadow-[0_12px_20px_-12px_rgba(37,99,235,0.85)] transition duration-300 group-hover:scale-105">
                  {initials}
                </span>
                <span className="hidden min-w-0 text-left sm:block">
                  <span className="block max-w-[9rem] truncate text-xs font-semibold text-[#123a60]">{session?.fullName || session?.username || "User"}</span>
                  <span className="block text-[10px] uppercase tracking-[0.18em] text-[#7b97ae]">{session?.role || "User"}</span>
                </span>
                <ChevronDown className={`h-3.5 w-3.5 transition ${profileMenuOpen ? "rotate-180 text-[#1d446b]" : "text-[#6a85a0]"}`} />
              </button>

              <div
                className={`absolute right-0 top-[calc(100%+0.7rem)] z-[80] w-[min(18rem,calc(100vw-1.5rem))] origin-top-right rounded-2xl border border-[#cde0f5] bg-white p-3 shadow-[0_26px_38px_-24px_rgba(18,54,88,0.85)] transition-all duration-200 ${
                  profileMenuOpen ? "translate-y-0 scale-100 opacity-100" : "pointer-events-none -translate-y-1 scale-95 opacity-0"
                }`}
              >
                <div className="mb-3 rounded-xl border border-[#d6e6f6] bg-[linear-gradient(120deg,#ecfeff,#eff6ff,#eef2ff)] px-3 py-2">
                  <p className="text-sm font-semibold text-[#123a60]">{session?.fullName || "User"}</p>
                  <p className="text-xs text-[#5b7a96]">{session?.role || "Role"}</p>
                </div>

                <button
                  type="button"
                  onClick={() => {
                    setProfileMenuOpen(false);
                    setLicenseOpen(true);
                  }}
                  className="group flex w-full items-center gap-3 rounded-xl border border-[#c4d7f3] bg-[linear-gradient(120deg,#dbeafe,#e0f2fe,#ede9fe)] px-3 py-2 text-left transition duration-200 hover:-translate-y-0.5 hover:shadow-[0_12px_20px_-14px_rgba(54,111,171,0.95)]"
                >
                  <span className="inline-flex h-8 w-8 items-center justify-center rounded-lg bg-[linear-gradient(145deg,#2563eb,#0ea5e9)] text-white shadow-[0_10px_16px_-12px_rgba(37,99,235,0.9)]">
                    <KeyRound className="h-4 w-4" />
                  </span>
                  <span className="min-w-0">
                    <span className="block text-sm font-semibold text-[#103a61]">License Center</span>
                    <span className="block text-[11px] text-[#5c7a97]">Activation, trial, and renewal</span>
                  </span>
                  <Sparkles className="ml-auto h-4 w-4 text-[#315f95] transition-transform duration-200 group-hover:rotate-12" />
                </button>

                <button
                  type="button"
                  onClick={() => setAboutOpen((prev) => !prev)}
                  className="mt-2 flex w-full items-center justify-between rounded-xl border border-[#c4d7f3] bg-[linear-gradient(120deg,#eef6ff,#f7fbff)] px-3 py-2 text-left text-[#103a61] transition duration-200 hover:-translate-y-0.5 hover:shadow-[0_12px_20px_-14px_rgba(54,111,171,0.95)]"
                >
                  <span className="text-sm font-semibold">About Software</span>
                  <ChevronDown className={`h-4 w-4 transition ${aboutOpen ? "rotate-180" : ""}`} />
                </button>

                {aboutOpen ? (
                  <div className="mt-2 rounded-xl border border-[#d9e8f7] bg-[linear-gradient(120deg,#f8fbff,#eef6ff)] px-3 py-3">
                    <div className="flex items-center gap-2 border-b border-[#d2e4f7] pb-2 mb-2">
                      <img src="/apan_logo.png" alt="अपन हाजिरी" className="h-6 w-6 object-contain" />
                      <div>
                        <p className="text-xs font-bold text-[#103a61] font-nepali leading-tight">अपन हाजिरी</p>
                        <p className="text-[10px] text-[#5c7a97]">Apan Hajiri</p>
                      </div>
                    </div>
                    <p className="mt-1 text-[11px] text-[#5c7a97]">Developer: {DEVELOPER_NAME}</p>
                    <p className="text-[11px] text-[#5c7a97]">Version: {appVersion}</p>
                    <p className="mt-1 text-[11px] text-[#5c7a97]">{DEVELOPER_PHONE}</p>
                    <p className="text-[11px] text-[#5c7a97] break-all">{DEVELOPER_EMAIL}</p>
                    <p className="text-[11px] text-[#5c7a97] break-all">{DEVELOPER_WEBSITE}</p>

                    <div className="mt-2 grid grid-cols-3 gap-2">
                      <a
                        href={WHATSAPP_URL}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center justify-center gap-1 rounded-lg border border-[#b9d3ea] bg-white px-2 py-1.5 text-[11px] font-medium text-[#1d446b] transition duration-200 hover:-translate-y-0.5 hover:bg-[#ecf6ff]"
                      >
                        <MessageCircle className="h-3.5 w-3.5 text-[#0b8f3a]" />
                        WhatsApp
                      </a>
                      <a
                        href={DEVELOPER_WEBSITE}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center justify-center gap-1 rounded-lg border border-[#b9d3ea] bg-white px-2 py-1.5 text-[11px] font-medium text-[#1d446b] transition duration-200 hover:-translate-y-0.5 hover:bg-[#ecf6ff]"
                      >
                        <Globe className="h-3.5 w-3.5" />
                        Website
                      </a>
                      <a
                        href={`mailto:${DEVELOPER_EMAIL}`}
                        className="inline-flex items-center justify-center gap-1 rounded-lg border border-[#b9d3ea] bg-white px-2 py-1.5 text-[11px] font-medium text-[#1d446b] transition duration-200 hover:-translate-y-0.5 hover:bg-[#ecf6ff]"
                      >
                        <Mail className="h-3.5 w-3.5" />
                        Mail
                      </a>
                    </div>
                  </div>
                ) : null}

                <button
                  type="button"
                  onClick={() => {
                    setProfileMenuOpen(false);
                    onLogout();
                  }}
                  className="mt-2 flex w-full items-center gap-3 rounded-xl border border-[#ecd0d0] bg-[#fff5f5] px-3 py-2 text-left text-[#9f3232] transition duration-200 hover:-translate-y-0.5 hover:bg-[#ffecec]"
                >
                  <span className="inline-flex h-8 w-8 items-center justify-center rounded-lg bg-[#fff] text-[#9f3232]">
                    <LogOut className="h-4 w-4" />
                  </span>
                  <span>
                    <span className="block text-sm font-semibold">Logout</span>
                    <span className="block text-[11px] text-[#ad6161]">End current session</span>
                  </span>
                </button>
              </div>
            </div>
          </div>
        </div>
      </header>

      {licenseOpen ? (
        <div
          className="fixed inset-0 z-50 grid place-items-center bg-[radial-gradient(circle_at_18%_16%,rgba(56,189,248,0.24),transparent_42%),radial-gradient(circle_at_84%_12%,rgba(59,130,246,0.24),transparent_40%),rgba(6,20,35,0.62)] p-4 backdrop-blur-[3px]"
          onClick={() => setLicenseOpen(false)}
        >
          <div
            className="license-gradient-frame w-full max-w-4xl rounded-[28px] bg-[linear-gradient(120deg,#38bdf8,#60a5fa,#a78bfa,#f472b6,#22d3ee)] p-[1px] shadow-[0_35px_55px_-34px_rgba(8,22,44,0.92)]"
            onClick={(event) => event.stopPropagation()}
          >
            <div className="max-h-[90vh] overflow-y-auto rounded-[27px] border border-white/40 bg-white/95 p-5 backdrop-blur">
              <div className="mb-4 flex items-start justify-between gap-2 border-b border-[#d8e8f7] pb-3">
                <div className="rounded-2xl border border-[#d6e7f8] bg-[linear-gradient(120deg,#ecfeff,#eff6ff,#f5f3ff)] px-4 py-3">
                  <h3 className="inline-flex items-center gap-2 text-base font-semibold text-[#0d334d]">
                    <Sparkles className="h-4 w-4 text-[#2563eb]" />
                    License & Trial Center
                  </h3>
                  <p className="mt-1 text-xs text-[#53738d]">Activation, expiry, trial window, and deactivation controls.</p>
                </div>
                <button
                  type="button"
                  onClick={() => setLicenseOpen(false)}
                  className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-[#d3e2f2] bg-white text-[#1d446b] transition duration-200 hover:-translate-y-0.5 hover:rotate-90 hover:bg-[#eef5fd]"
                >
                  <X className="h-4 w-4" />
                </button>
              </div>
              <LicensePanel compact showHeader={false} />
            </div>
          </div>
        </div>
      ) : null}
    </>
  );
}
