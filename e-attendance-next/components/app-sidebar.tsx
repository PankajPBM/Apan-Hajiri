"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import type { Route } from "next";
import { usePathname, useSearchParams } from "next/navigation";
import {
  X,
  BarChart3,
  Building2,
  CalendarDays,
  CalendarOff,
  CalendarClock,
  CheckSquare,
  ChevronDown,
  ClipboardCheck,
  Clock,
  Fingerprint,
  FileText,
  LayoutDashboard,
  Settings,
  Shield,
  UserCheck,
  Users,
  Lock,
} from "lucide-react";
import { pickForegroundColor, useBrandingSettings } from "@/components/data/app-settings";
import { initialsFromName, type AuthSession, type FeatureKey } from "@/components/data/auth-store";
import { fetchLicenseStatus } from "@/components/data/license-client";
import { ApanHajiriLogo } from "@/components/logo";

type AppSidebarProps = {
  collapsed: boolean;
  mobileOpen: boolean;
  onCloseMobile: () => void;
  onNavigate: () => void;
  session: AuthSession | null;
};

type NavItem = {
  href: Route | { pathname: Route; query?: Record<string, string> };
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  badge?: number;
  matchPath: Route;
  matchQuery?: { key: string; value: string };
  feature: FeatureKey;
};

const baseNavItems: NavItem[] = [
  { href: "/", label: "Dashboard", icon: LayoutDashboard, matchPath: "/", feature: "dashboard" },
  {
    href: { pathname: "/setup" as Route, query: { section: "areas" } },
    label: "Setup",
    icon: Building2,
    matchPath: "/setup" as Route,
    feature: "departments",
  },
  { href: "/devices", label: "Devices", icon: Fingerprint, matchPath: "/devices", feature: "devices" },
  { href: "/employees", label: "Employees", icon: Users, matchPath: "/employees", feature: "employees" },
];

const shiftSubItems: NavItem[] = [
  {
    href: { pathname: "/shifts" as Route, query: { tab: "break" } },
    label: "Break Time",
    icon: CalendarClock,
    matchPath: "/shifts" as Route,
    matchQuery: { key: "tab", value: "break" },
    feature: "timetable",
  },
  {
    href: { pathname: "/shifts" as Route, query: { tab: "timetable" } },
    label: "Time Table",
    icon: CalendarDays,
    matchPath: "/shifts" as Route,
    matchQuery: { key: "tab", value: "timetable" },
    feature: "timetable",
  },
  {
    href: { pathname: "/shifts" as Route, query: { tab: "setup" } },
    label: "Shifts & Rotations",
    icon: Clock,
    matchPath: "/shifts" as Route,
    matchQuery: { key: "tab", value: "setup" },
    feature: "shift",
  },
  {
    href: { pathname: "/shifts" as Route, query: { tab: "overtime" } },
    label: "Overtime Setup",
    icon: CalendarClock,
    matchPath: "/shifts" as Route,
    matchQuery: { key: "tab", value: "overtime" },
    feature: "shift",
  },
];

const scheduleSubItems: NavItem[] = [
  {
    href: "/assign" as Route,
    label: "Schedule Assignment",
    icon: UserCheck,
    matchPath: "/assign" as Route,
    feature: "assign",
  },
  {
    href: { pathname: "/assign" as Route, query: { section: "groups" } },
    label: "Group Management",
    icon: Users,
    matchPath: "/assign" as Route,
    matchQuery: { key: "section", value: "groups" },
    feature: "assign",
  },
  {
    href: { pathname: "/assign" as Route, query: { section: "preview" } },
    label: "Schedule Preview",
    icon: ClipboardCheck,
    matchPath: "/assign" as Route,
    matchQuery: { key: "section", value: "preview" },
    feature: "assign",
  },
];

const accessControlSubItems: NavItem[] = [
  {
    href: { pathname: "/access-control" as Route, query: { section: "time-period" } },
    label: "Time Period",
    icon: CalendarDays,
    matchPath: "/access-control" as Route,
    matchQuery: { key: "section", value: "time-period" },
    feature: "timetable",
  },
  {
    href: { pathname: "/access-control" as Route, query: { section: "groups" } },
    label: "Access Group",
    icon: Users,
    matchPath: "/access-control" as Route,
    matchQuery: { key: "section", value: "groups" },
    feature: "assign",
  },
  {
    href: { pathname: "/access-control" as Route, query: { section: "combination" } },
    label: "Combination",
    icon: CheckSquare,
    matchPath: "/access-control" as Route,
    matchQuery: { key: "section", value: "combination" },
    feature: "assign",
  },
  {
    href: { pathname: "/access-control" as Route, query: { section: "privilege" } },
    label: "Privilege",
    icon: Shield,
    matchPath: "/access-control" as Route,
    matchQuery: { key: "section", value: "privilege" },
    feature: "assign",
  },
  {
    href: { pathname: "/access-control" as Route, query: { section: "duplicate-punch" } },
    label: "Device Duplicate Punch",
    icon: Clock,
    matchPath: "/access-control" as Route,
    matchQuery: { key: "section", value: "duplicate-punch" },
    feature: "devices",
  },
];

const afterShiftNavItems: NavItem[] = [
  { href: "/transactions", label: "Transactions", icon: ClipboardCheck, matchPath: "/transactions", feature: "transactions" },
  { href: "/leaves", label: "Leaves & Holidays", icon: CalendarOff, matchPath: "/leaves", feature: "leaves" },
  { href: "/reports", label: "Reports", icon: BarChart3, matchPath: "/reports", feature: "reports" },
  { href: "/logs", label: "System Logs", icon: FileText, matchPath: "/logs", feature: "logs" },
];

export function AppSidebar({
  collapsed,
  mobileOpen,
  onCloseMobile,
  onNavigate,
  session,
}: AppSidebarProps) {
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const accessSection = searchParams.get("section");
  const branding = useBrandingSettings();

  const [shiftOpen, setShiftOpen] = useState(false);
  const [scheduleOpen, setScheduleOpen] = useState(false);
  const [accessControlOpen, setAccessControlOpen] = useState(false);
  const [isLocked, setIsLocked] = useState(false);

  useEffect(() => {
    fetchLicenseStatus()
      .then((status) => {
        setIsLocked(!status.isValid);
      })
      .catch(() => {});
  }, []);

  const hasFeature = (key: FeatureKey) => {
    if (!session) return false;
    if (session.role === "Admin" || session.role === "Manager") return true;
    return Boolean(session.featureAccess[key]);
  };

  const isShiftRoute = pathname.startsWith("/shift");
  const isTimetableRoute = pathname.startsWith("/timetable");
  const legacyShifts = pathname.startsWith("/shifts");
  const shiftGroupActive = isShiftRoute || isTimetableRoute || legacyShifts;
  const scheduleGroupActive = pathname.startsWith("/assign");
  const accessControlActive = pathname.startsWith("/access-control");

  useEffect(() => {
    if (shiftGroupActive) {
      setShiftOpen(true);
      setScheduleOpen(false);
      setAccessControlOpen(false);
    } else if (scheduleGroupActive) {
      setScheduleOpen(true);
      setShiftOpen(false);
      setAccessControlOpen(false);
    } else if (accessControlActive) {
      setAccessControlOpen(true);
      setShiftOpen(false);
      setScheduleOpen(false);
    }
  }, [shiftGroupActive, scheduleGroupActive, accessControlActive]);

  const toggleGroup = (group: "shift" | "schedule" | "accessControl") => {
    if (group === "shift") {
      setShiftOpen((prev) => {
        const next = !prev;
        if (next) {
          setScheduleOpen(false);
          setAccessControlOpen(false);
        }
        return next;
      });
    } else if (group === "schedule") {
      setScheduleOpen((prev) => {
        const next = !prev;
        if (next) {
          setShiftOpen(false);
          setAccessControlOpen(false);
        }
        return next;
      });
    } else if (group === "accessControl") {
      setAccessControlOpen((prev) => {
        const next = !prev;
        if (next) {
          setShiftOpen(false);
          setScheduleOpen(false);
        }
        return next;
      });
    }
  };

  const visibleBaseNavItems = baseNavItems.filter((item) => hasFeature(item.feature));
  const visibleShiftSubItems = shiftSubItems.filter((item) => hasFeature(item.feature));
  const visibleScheduleSubItems = scheduleSubItems.filter((item) => hasFeature(item.feature));
  const visibleAccessControlSubItems = accessControlSubItems.filter((item) => hasFeature(item.feature));
  const visibleAfterShiftNavItems = afterShiftNavItems.filter((item) => hasFeature(item.feature));

  return (
    <aside
      className={`fixed inset-y-0 left-0 z-40 flex flex-col border-r border-slate-200 bg-white transition-all duration-200 md:sticky md:top-0 md:h-screen md:translate-x-0 shrink-0 ${
        collapsed ? "w-16" : "w-64"
      } ${mobileOpen ? "translate-x-0" : "-translate-x-full"}`}
    >
      {/* Brand Header */}
      <div
        className={`flex h-16 shrink-0 items-center border-b border-slate-200 ${
          collapsed ? "justify-center px-0" : "justify-between px-4"
        }`}
      >
        <div className={`flex items-center ${collapsed ? "justify-center" : "gap-3 overflow-hidden"}`}>
          <div className="flex h-10 w-10 shrink-0 items-center justify-center">
            <img
              src={branding.logoUrl || "/apan_logo.png"}
              alt={branding.systemTitle || "Apan Hajiri"}
              className="max-h-full max-w-full object-contain drop-shadow-sm select-none"
            />
          </div>
          {!collapsed && (
            <div className="min-w-0">
              <span className="block truncate text-sm font-bold text-slate-900 font-nepali leading-tight">
                {branding.organizationName || "अपन हाजिरी"}
              </span>
              <span className="block truncate text-[11px] font-medium text-slate-500">
                {branding.systemTitle || "Apan Hajiri"}
              </span>
            </div>
          )}
        </div>
        {!collapsed && (
          <button
            type="button"
            onClick={onCloseMobile}
            className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-100 md:hidden"
            aria-label="Close sidebar navigation"
          >
            <X className="h-5 w-5" />
          </button>
        )}
      </div>

      {/* Nav items list */}
      <nav className={`flex-1 min-h-0 space-y-1 overflow-y-auto py-4 ${collapsed ? "px-1.5" : "px-2.5"}`}>
        {visibleBaseNavItems.map((item) => {
          const active = item.matchPath === "/" ? pathname === "/" : pathname.startsWith(item.matchPath as string);
          const Icon = item.icon;
          return (
            <Link
              key={item.label}
              href={item.href}
              onClick={onNavigate}
              prefetch={false}
              className={`flex items-center rounded-lg py-2 text-[13.5px] font-medium transition ${
                collapsed ? "justify-center px-0 w-full" : "px-3"
              } ${
                active
                  ? "bg-blue-50 text-blue-700 font-semibold"
                  : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              }`}
              title={collapsed ? item.label : undefined}
            >
              <Icon className={`h-4.5 w-4.5 shrink-0 ${active ? "text-blue-600" : "text-slate-400"}`} />
              {!collapsed && <span className="ml-3 truncate">{item.label}</span>}
              {!collapsed && item.badge ? (
                <span className="ml-auto rounded-full bg-blue-100 px-2 py-0.5 text-[10px] font-bold text-blue-700">
                  {item.badge}
                </span>
              ) : null}
            </Link>
          );
        })}

        {/* Shift Setup Dropdown */}
        {visibleShiftSubItems.length > 0 ? (
          <div>
            <button
              type="button"
              onClick={() => toggleGroup("shift")}
              className={`flex w-full items-center rounded-lg py-2 text-[13.5px] font-medium transition ${
                collapsed ? "justify-center px-0" : "justify-between px-3"
              } ${
                shiftGroupActive
                  ? "bg-blue-50/60 text-blue-800 font-semibold"
                  : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              }`}
              title={collapsed ? "Shift Setup" : undefined}
            >
              <div className={`flex items-center ${collapsed ? "justify-center" : ""}`}>
                <Clock className={`h-4.5 w-4.5 shrink-0 ${shiftGroupActive ? "text-blue-600" : "text-slate-400"}`} />
                {!collapsed && <span className="ml-3 truncate">Shift Setup</span>}
              </div>
              {!collapsed && (
                <ChevronDown className={`h-3.5 w-3.5 text-slate-400 transition ${shiftOpen ? "rotate-180" : ""}`} />
              )}
            </button>

            {!collapsed && shiftOpen ? (
              <div className="mt-1 space-y-0.5 pl-7">
                {visibleShiftSubItems.map((item) => {
                  const currentShiftTab = searchParams.get("tab") || "break";
                  const active =
                    pathname.startsWith(item.matchPath as string) &&
                    (!item.matchQuery || currentShiftTab === item.matchQuery.value);
                  return (
                    <Link
                      key={item.label}
                      href={item.href}
                      onClick={onNavigate}
                      prefetch={false}
                      className={`block rounded-md px-2.5 py-1.5 text-xs font-medium transition ${
                        active
                          ? "bg-blue-100/70 text-blue-700 font-semibold"
                          : "text-slate-500 hover:bg-slate-50 hover:text-slate-800"
                      }`}
                    >
                      {item.label}
                    </Link>
                  );
                })}
              </div>
            ) : null}
          </div>
        ) : null}

        {/* Schedule Assignment Dropdown */}
        {visibleScheduleSubItems.length > 0 ? (
          <div>
            <button
              type="button"
              onClick={() => toggleGroup("schedule")}
              className={`flex w-full items-center rounded-lg py-2 text-[13.5px] font-medium transition ${
                collapsed ? "justify-center px-0" : "justify-between px-3"
              } ${
                scheduleGroupActive
                  ? "bg-blue-50/60 text-blue-800 font-semibold"
                  : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              }`}
              title={collapsed ? "Schedule Shift" : undefined}
            >
              <div className={`flex items-center ${collapsed ? "justify-center" : ""}`}>
                <UserCheck className={`h-4.5 w-4.5 shrink-0 ${scheduleGroupActive ? "text-blue-600" : "text-slate-400"}`} />
                {!collapsed && <span className="ml-3 truncate">Schedule Shift</span>}
              </div>
              {!collapsed && (
                <ChevronDown className={`h-3.5 w-3.5 text-slate-400 transition ${scheduleOpen ? "rotate-180" : ""}`} />
              )}
            </button>

            {!collapsed && scheduleOpen ? (
              <div className="mt-1 space-y-0.5 pl-7">
                {visibleScheduleSubItems.map((item) => {
                  const currentAssignSection = searchParams.get("section");
                  const active =
                    pathname.startsWith(item.matchPath as string) &&
                    (item.matchQuery
                      ? currentAssignSection === item.matchQuery.value
                      : !currentAssignSection || currentAssignSection === "assignment");
                  return (
                    <Link
                      key={item.label}
                      href={item.href}
                      onClick={onNavigate}
                      prefetch={false}
                      className={`block rounded-md px-2.5 py-1.5 text-xs font-medium transition ${
                        active
                          ? "bg-blue-100/70 text-blue-700 font-semibold"
                          : "text-slate-500 hover:bg-slate-50 hover:text-slate-800"
                      }`}
                    >
                      {item.label}
                    </Link>
                  );
                })}
              </div>
            ) : null}
          </div>
        ) : null}

        {/* Access Control Dropdown */}
        {visibleAccessControlSubItems.length > 0 ? (
          <div>
            <button
              type="button"
              onClick={() => toggleGroup("accessControl")}
              className={`flex w-full items-center rounded-lg py-2 text-[13.5px] font-medium transition ${
                collapsed ? "justify-center px-0" : "justify-between px-3"
              } ${
                accessControlActive
                  ? "bg-blue-50/60 text-blue-800 font-semibold"
                  : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              }`}
              title={collapsed ? "Access Control" : undefined}
            >
              <div className={`flex items-center ${collapsed ? "justify-center" : ""}`}>
                <Shield className={`h-4.5 w-4.5 shrink-0 ${accessControlActive ? "text-blue-600" : "text-slate-400"}`} />
                {!collapsed && <span className="ml-3 truncate">Access Control</span>}
              </div>
              {!collapsed && (
                <ChevronDown className={`h-3.5 w-3.5 text-slate-400 transition ${accessControlOpen ? "rotate-180" : ""}`} />
              )}
            </button>

            {!collapsed && accessControlOpen ? (
              <div className="mt-1 space-y-0.5 pl-7">
                {visibleAccessControlSubItems.map((item) => {
                  const pathSection = pathname.startsWith("/access-control/")
                    ? pathname.replace("/access-control/", "")
                    : null;
                  const effectiveAccessSection = accessSection || pathSection || "time-period";
                  const active =
                    (pathname === item.matchPath || pathname.startsWith((item.matchPath as string) + "/")) &&
                    (!item.matchQuery || effectiveAccessSection === item.matchQuery.value);
                  return (
                    <Link
                      key={item.label}
                      href={item.href}
                      onClick={onNavigate}
                      prefetch={false}
                      className={`block rounded-md px-2.5 py-1.5 text-xs font-medium transition ${
                        active
                          ? "bg-blue-100/70 text-blue-700 font-semibold"
                          : "text-slate-500 hover:bg-slate-50 hover:text-slate-800"
                      }`}
                    >
                      {item.label}
                    </Link>
                  );
                })}
              </div>
            ) : null}
          </div>
        ) : null}

        {/* Remaining Main Nav items */}
        {visibleAfterShiftNavItems.map((item) => {
          const active = pathname.startsWith(item.matchPath as string);
          const Icon = item.icon;
          return (
            <Link
              key={item.label}
              href={item.href}
              onClick={onNavigate}
              prefetch={false}
              className={`flex items-center rounded-lg py-2 text-[13.5px] font-medium transition ${
                collapsed ? "justify-center px-0 w-full" : "px-3"
              } ${
                active
                  ? "bg-blue-50 text-blue-700 font-semibold"
                  : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              }`}
              title={collapsed ? item.label : undefined}
            >
              <Icon className={`h-4.5 w-4.5 shrink-0 ${active ? "text-blue-600" : "text-slate-400"}`} />
              {!collapsed && <span className="ml-3 truncate">{item.label}</span>}
              {!collapsed && item.badge ? (
                <span className="ml-auto rounded-full bg-blue-100 px-2 py-0.5 text-[10px] font-bold text-blue-700">
                  {item.badge}
                </span>
              ) : null}
            </Link>
          );
        })}
      </nav>

      {/* Settings at bottom */}
      {hasFeature("settings") ? (
        <div className="shrink-0 border-t border-slate-200 p-2.5 bg-white">
          <Link
            href="/settings"
            onClick={onNavigate}
            prefetch={false}
            className={`flex items-center rounded-lg py-2 text-[13.5px] font-medium transition ${
              collapsed ? "justify-center px-0 w-full" : "px-3"
            } ${
              pathname.startsWith("/settings")
                ? "bg-blue-50 text-blue-700 font-semibold"
                : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
            }`}
            title={collapsed ? "Settings" : undefined}
          >
            <Settings className="h-4.5 w-4.5 text-slate-400 shrink-0" />
            {!collapsed && <span className="ml-3 truncate">Settings</span>}
          </Link>
        </div>
      ) : null}
    </aside>
  );
}
