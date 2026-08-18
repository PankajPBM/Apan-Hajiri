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
    label: "Shifts",
    icon: Clock,
    matchPath: "/shifts" as Route,
    matchQuery: { key: "tab", value: "setup" },
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
    label: "Preview",
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
    icon: ClipboardCheck,
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
];

const afterShiftNavItems: NavItem[] = [
  { href: "/attendance" as Route, label: "Transactions", icon: ClipboardCheck, matchPath: "/attendance" as Route, feature: "transactions" },
  { href: "/leaves" as Route, label: "Leaves and Holiday", icon: CalendarOff, matchPath: "/leaves" as Route, feature: "leaves" },
  { href: "/reports" as Route, label: "Reports", icon: BarChart3, matchPath: "/reports" as Route, feature: "reports" },
  { href: "/logs" as Route, label: "Logs", icon: FileText, matchPath: "/logs" as Route, feature: "logs" },
];

export function AppSidebar({ collapsed, mobileOpen, onCloseMobile, onNavigate, session }: AppSidebarProps) {
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const tab = searchParams.get("tab");
  const activeTab = tab === "timetable" ? "timetable" : tab === "break" ? "break" : "setup";
  const accessSection = searchParams.get("section");
  const legacyShifts = pathname === "/shifts" || pathname.startsWith("/shifts/");
  const isShiftRoute = pathname === "/shift" || pathname.startsWith("/shift/");
  const isTimetableRoute = pathname === "/timetable" || pathname.startsWith("/timetable/");
  const isAssignRoute = pathname === "/assign" || pathname.startsWith("/assign/");
  const isAccessControlRoute = pathname === "/access-control" || pathname.startsWith("/access-control/");
  
  const visibleBaseNavItems = baseNavItems.filter((item) => session?.featureAccess[item.feature]);
  const visibleShiftSubItems = shiftSubItems.filter((item) => session?.featureAccess[item.feature]);
  const visibleScheduleSubItems = scheduleSubItems.filter((item) => session?.featureAccess[item.feature]);
  const visibleAccessControlSubItems = accessControlSubItems.filter((item) => session?.featureAccess[item.feature]);
  const visibleAfterShiftNavItems = afterShiftNavItems.filter((item) => session?.featureAccess[item.feature]);
  
  const initials = initialsFromName(session?.fullName || session?.username || "NA");
  const branding = useBrandingSettings();
  // Sidebar text is dark or light depending on how bright the configured brand color is, so it
  // stays readable no matter what color is picked in Settings — not just for a dark navy default.
  const sidebarFg = pickForegroundColor(branding.primaryColor);
  
  const shiftGroupActive = isShiftRoute || isTimetableRoute || legacyShifts;
  const scheduleGroupActive = isAssignRoute;
  const accessControlActive = isAccessControlRoute;

  const [shiftOpen, setShiftOpen] = useState(shiftGroupActive);
  const [scheduleOpen, setScheduleOpen] = useState(scheduleGroupActive);
  const [accessControlOpen, setAccessControlOpen] = useState(accessControlActive);
  const [isLocked, setIsLocked] = useState(false);

  useEffect(() => { if (shiftGroupActive) setShiftOpen(true); }, [shiftGroupActive]);
  useEffect(() => { if (scheduleGroupActive) setScheduleOpen(true); }, [scheduleGroupActive]);
  useEffect(() => { if (accessControlActive) setAccessControlOpen(true); }, [accessControlActive]);

  useEffect(() => {
    let active = true;
    fetchLicenseStatus()
      .then((status) => {
        if (active) {
          setIsLocked(!status.isValid);
        }
      })
      .catch((err) => {
        console.error("Sidebar license fetch failed", err);
      });
    return () => { active = false; };
  }, []);

  return (
    <aside
      className={`fixed inset-y-0 left-0 z-40 flex h-screen flex-col border-r border-[#1c4670] text-[color:var(--sidebar-fg)] shadow-[0_0_35px_-28px_rgba(8,31,61,0.95)] transition-transform duration-200 md:sticky md:translate-x-0 ${
        mobileOpen ? "translate-x-0" : "-translate-x-full"
      }`}
      style={{
        width: collapsed ? "4.4rem" : "18.5rem",
        // Derive the sidebar from the configured branding color so it updates live — a plain,
        // subtle top-to-bottom gradient (brand color fading slightly darker), nothing busier.
        background: `linear-gradient(180deg, ${branding.primaryColor} 0%, color-mix(in srgb, ${branding.primaryColor}, #000 22%) 100%)`,
        // Every text/icon color in this sidebar reads from this variable (at full or reduced
        // opacity) instead of hardcoded light-blue hex values, so it stays legible regardless of
        // which brand color is configured — light text on a dark brand color, dark text on light.
        ["--sidebar-fg" as string]: sidebarFg,
      }}
    >
      <div className="flex h-[4.5rem] min-h-[4.5rem] items-center gap-3 border-b border-white/20 px-4 py-3">
        {branding.logoUrl ? (
          <div className="grid h-9 w-9 place-items-center overflow-hidden rounded-full bg-white/90 shadow-[0_14px_22px_-16px_rgba(56,189,248,0.95)]">
            <img src={branding.logoUrl} alt={branding.systemTitle} className="h-full w-full object-cover" />
          </div>
        ) : (
          <div className="grid h-9 w-9 place-items-center rounded-full bg-[linear-gradient(145deg,#38bdf8,#2563eb)] font-bold text-white shadow-[0_14px_22px_-16px_rgba(56,189,248,0.95)]">
            PB
          </div>
        )}
        {!collapsed && (
          <div className="min-w-0">
            <div className="truncate text-sm font-semibold text-white">{branding.organizationName}</div>
            <div className="truncate text-xs text-[color:var(--sidebar-fg)]/75">Attendance Console</div>
          </div>
        )}
        <button
          type="button"
          onClick={onCloseMobile}
          className="ml-auto inline-flex h-8 w-8 items-center justify-center rounded-lg border border-white/30 text-[color:var(--sidebar-fg)]/85 hover:bg-white/10 md:hidden"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <nav className="flex-1 space-y-1 overflow-y-auto px-2 py-3">
        {visibleBaseNavItems.map((item) => {
          let active = false;
          if (item.matchPath === "/") {
            active = pathname === "/";
          } else {
            active = pathname.startsWith(item.matchPath as string);
          }
          const Icon = item.icon;
          return (
            <Link
              key={item.label}
              href={item.href}
              onClick={onNavigate}
              prefetch={false}
              className={`group flex items-center rounded-xl px-3 py-2.5 text-[0.95rem] font-semibold tracking-[0.01em] transition ${
                active
                  ? "border border-white/30 bg-white/18 text-white shadow-[0_12px_20px_-16px_rgba(8,31,61,0.9)]"
                  : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:-translate-y-0.5 hover:border-white/20 hover:bg-white/10 hover:text-white"
              }`}
              title={collapsed ? item.label : undefined}
            >
              <Icon className="h-4 w-4 shrink-0" />
              {!collapsed && <span className="ml-3 flex-1 text-left leading-tight">{item.label}</span>}
              {!collapsed && item.badge ? (
                <span className="rounded-full bg-[#2f6e9c] px-2 py-0.5 text-xs text-white">
                  {item.badge}
                </span>
              ) : null}
            </Link>
          );
        })}

        {visibleShiftSubItems.length > 0 ? (
          <div className="space-y-1">
            <button
              type="button"
              onClick={() => { if (!collapsed) setShiftOpen((prev) => !prev); }}
              className={`group flex w-full items-center rounded-xl px-3 py-2.5 text-left text-[0.95rem] font-semibold tracking-[0.01em] transition ${
                shiftGroupActive
                  ? "border border-white/30 bg-white/18 text-white shadow-[0_12px_20px_-16px_rgba(8,31,61,0.9)]"
                  : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:-translate-y-0.5 hover:border-white/20 hover:bg-white/10 hover:text-white"
              }`}
              title={collapsed ? "Shift Setup" : undefined}
            >
              <Clock className="h-4 w-4 shrink-0" />
              {!collapsed && <span className="ml-3 flex-1 text-left leading-tight">Shift Setup</span>}
              {!collapsed && <ChevronDown className={`ml-auto h-4 w-4 transition ${shiftOpen ? "rotate-180" : ""}`} />}
            </button>

            {!collapsed && shiftOpen ? (
              <div className="space-y-1 pl-6">
                {visibleShiftSubItems.map((item) => {
                  let active = pathname.startsWith(item.matchPath as string);
                  if (item.matchQuery) {
                    active = active && searchParams.get(item.matchQuery.key) === item.matchQuery.value;
                  }
                  if (item.label === "Time Table") {
                    active = (isTimetableRoute && !searchParams.get("section")) || (legacyShifts && activeTab === "timetable");
                  }
                  if (item.label === "Break Time") {
                    active = (isTimetableRoute && searchParams.get("section") === "break") || (legacyShifts && searchParams.get("tab") === "break");
                  }
                  if (item.label === "Shifts") {
                    active = isShiftRoute || (legacyShifts && activeTab === "setup");
                  }
                  const Icon = item.icon;
                  return (
                    <Link
                      key={item.label}
                      href={item.href}
                      onClick={onNavigate}
                      prefetch={false}
                      className={`group flex items-center rounded-lg px-3 py-2 text-[0.95rem] font-semibold transition ${
                        active
                          ? "border border-white/30 bg-white/18 text-white shadow-[0_8px_16px_-12px_rgba(8,31,61,0.9)]"
                          : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:border-white/20 hover:bg-white/10 hover:text-white"
                      }`}
                    >
                      <Icon className="h-4 w-4 shrink-0" />
                      <span className="ml-3 flex-1">{item.label}</span>
                    </Link>
                  );
                })}
              </div>
            ) : null}
          </div>
        ) : null}

        {visibleScheduleSubItems.length > 0 ? (
          <div className="space-y-1">
            <button
              type="button"
              onClick={() => { if (!collapsed) setScheduleOpen((prev) => !prev); }}
              className={`group flex w-full items-center rounded-xl px-3 py-2.5 text-left text-[0.95rem] font-semibold tracking-[0.01em] transition ${
                scheduleGroupActive
                  ? "border border-white/30 bg-white/18 text-white shadow-[0_12px_20px_-16px_rgba(8,31,61,0.9)]"
                  : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:-translate-y-0.5 hover:border-white/20 hover:bg-white/10 hover:text-white"
              }`}
              title={collapsed ? "Schedule Shift" : undefined}
            >
              <UserCheck className="h-4 w-4 shrink-0" />
              {!collapsed && <span className="ml-3 flex-1 text-left leading-tight">Schedule Shift</span>}
              {!collapsed && <ChevronDown className={`ml-auto h-4 w-4 transition ${scheduleOpen ? "rotate-180" : ""}`} />}
            </button>

            {!collapsed && scheduleOpen ? (
              <div className="space-y-1 pl-6">
                {visibleScheduleSubItems.map((item) => {
                  let active = pathname.startsWith(item.matchPath as string);
                  if (item.matchQuery) {
                    active = active && searchParams.get(item.matchQuery.key) === item.matchQuery.value;
                  } else {
                    active = active && !searchParams.get("section");
                  }
                  const Icon = item.icon;
                  return (
                    <Link
                      key={item.label}
                      href={item.href}
                      onClick={onNavigate}
                      prefetch={false}
                      className={`group flex items-center rounded-lg px-3 py-2 text-[0.95rem] font-semibold transition ${
                        active
                          ? "border border-white/30 bg-white/18 text-white shadow-[0_8px_16px_-12px_rgba(8,31,61,0.9)]"
                          : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:border-white/20 hover:bg-white/10 hover:text-white"
                      }`}
                    >
                      <Icon className="h-4 w-4 shrink-0" />
                      <span className="ml-3 flex-1 text-left leading-tight">{item.label}</span>
                    </Link>
                  );
                })}
              </div>
            ) : null}
          </div>
        ) : null}

        {visibleAccessControlSubItems.length > 0 ? (
          <div className="space-y-1">
            <button
              type="button"
              onClick={() => { if (!collapsed) setAccessControlOpen((prev) => !prev); }}
              className={`group flex w-full items-center rounded-xl px-3 py-2.5 text-left text-[0.95rem] font-semibold tracking-[0.01em] transition ${
                accessControlActive
                  ? "border border-white/30 bg-white/18 text-white shadow-[0_12px_20px_-16px_rgba(8,31,61,0.9)]"
                  : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:-translate-y-0.5 hover:border-white/20 hover:bg-white/10 hover:text-white"
              }`}
              title={collapsed ? (isLocked ? "Access Control (Locked)" : "Access Control") : undefined}
            >
              <Shield className="h-4 w-4 shrink-0" />
              {!collapsed && <span className="ml-3 flex-1 text-left leading-tight">Access Control</span>}
              {!collapsed && isLocked && (
                <span className="mr-1.5 flex items-center gap-0.5 rounded bg-red-500/20 px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-wider text-red-300">
                  <Lock className="h-2.5 w-2.5" /> Locked
                </span>
              )}
              {!collapsed && <ChevronDown className={`ml-auto h-4 w-4 transition ${accessControlOpen ? "rotate-180" : ""}`} />}
            </button>

            {!collapsed && accessControlOpen ? (
              <div className="space-y-1 pl-6">
                {visibleAccessControlSubItems.map((item) => {
                  const active = pathname === item.matchPath && (!item.matchQuery || accessSection === item.matchQuery.value);
                  const Icon = item.icon;
                  return (
                    <Link
                      key={item.label}
                      href={item.href}
                      onClick={onNavigate}
                      prefetch={false}
                      className={`group flex items-center rounded-lg px-3 py-2 text-[0.95rem] font-semibold transition ${
                        active
                          ? "border border-white/30 bg-white/18 text-white shadow-[0_8px_16px_-12px_rgba(8,31,61,0.9)]"
                          : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:border-white/20 hover:bg-white/10 hover:text-white"
                      }`}
                    >
                      <Icon className="h-4 w-4 shrink-0" />
                      <span className="ml-3 flex-1 text-left leading-tight">{item.label}</span>
                    </Link>
                  );
                })}
              </div>
            ) : null}
          </div>
        ) : null}

        {visibleAfterShiftNavItems.map((item) => {
          let active = false;
          if (item.matchPath === "/") {
            active = pathname === "/";
          } else {
            active = pathname.startsWith(item.matchPath as string);
          }
          const Icon = item.icon;
          return (
            <Link
              key={item.label}
              href={item.href}
              onClick={onNavigate}
              prefetch={false}
              className={`group flex items-center rounded-xl px-3 py-2.5 text-[0.95rem] font-semibold tracking-[0.01em] transition ${
                active
                  ? "border border-white/30 bg-white/18 text-white shadow-[0_12px_20px_-16px_rgba(8,31,61,0.9)]"
                  : "border border-transparent text-[color:var(--sidebar-fg)]/90 hover:-translate-y-0.5 hover:border-white/20 hover:bg-white/10 hover:text-white"
              }`}
              title={collapsed ? item.label : undefined}
            >
              <Icon className="h-4 w-4 shrink-0" />
              {!collapsed && <span className="ml-3 flex-1 text-left leading-tight">{item.label}</span>}
              {!collapsed && item.badge ? (
                <span className="rounded-full bg-[#2f6e9c] px-2 py-0.5 text-xs text-white">
                  {item.badge}
                </span>
              ) : null}
            </Link>
          );
        })}
      </nav>

      <div className="mt-auto border-t border-white/20 bg-white/5 p-2">
        {session?.featureAccess.settings ? (
          <Link
            href="/settings"
            onClick={onNavigate}
            prefetch={false}
            className="flex items-center rounded-xl border border-transparent px-3 py-2 text-sm font-bold text-[color:var(--sidebar-fg)]/90 transition hover:border-white/20 hover:bg-white/10 hover:text-white"
            title={collapsed ? "Settings" : undefined}
          >
            <Settings className="h-4 w-4" />
            {!collapsed && <span className="ml-3">Settings</span>}
          </Link>
        ) : null}
        <button
          className="mt-1 flex w-full items-center rounded-xl border border-transparent px-3 py-2 text-sm text-[color:var(--sidebar-fg)]/90 transition hover:border-white/20 hover:bg-white/10 hover:text-white"
          title={collapsed ? "Admin" : undefined}
          type="button"
        >
          <div className="grid h-7 w-7 place-items-center rounded-full bg-white/20 text-xs font-bold text-white">{initials}</div>
          {!collapsed && (
            <div className="ml-3 min-w-0 text-left">
              <div className="truncate text-white">{session?.fullName || "User"}</div>
              <div className="truncate text-xs text-[color:var(--sidebar-fg)]/75">{session?.role || "Role"}</div>
            </div>
          )}
          {!collapsed && <Shield className="ml-auto h-4 w-4 text-[color:var(--sidebar-fg)]/75" />}
        </button>
      </div>
    </aside>
  );
}
