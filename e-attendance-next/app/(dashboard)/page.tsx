"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { format } from "date-fns";
import {
  Activity,
  AlertCircle,
  BellRing,
  Cake,
  CalendarDays,
  ChartLine,
  CheckCircle2,
  Clock3,
  Download,
  FileOutput,
  FileText,
  Fingerprint,
  PieChart,
  QrCode,
  RefreshCw,
  Router,
  Settings,
  ShieldCheck,
  Sparkles,
  Umbrella,
  Upload,
  UserPlus,
  Users,
  UserX,
  X,
  Zap,
} from "lucide-react";
import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import {
  parseTimeToMinutes,
  readShiftDefinitions,
  readTimetableDefinitions,
} from "@/components/data/shift-store";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDate } from "@/components/date/date-display";
import { useBrandingSettings } from "@/components/data/app-settings";
import { resolveApiBases } from "@/lib/api-bases";
import { LeaveApplicationModal } from "@/components/modal/leave-application-modal";

type TrendRow = {
  day: string;
  present: number;
  absent: number;
  late: number;
};

type EmployeeRow = {
  id: string;
  employeeCode: string;
  fullName: string;
  department: string;
  hireDate?: string;
  gender?: string;
  isActive?: boolean;
};

type DeviceRow = {
  id: string;
  serialNumber: string;
  name: string;
  ipAddress: string;
  isOnline?: boolean;
  state?: string;
};

type TransactionRow = {
  id: string;
  employeeCode: string;
  employeeName?: string;
  employeeDepartment?: string;
  punchTime?: string;
  receivedUtc?: string;
};

type LeaveRow = {
  id: string;
  personnelId?: string;
  fromDate?: string;
  toDate?: string;
  status?: string;
};

type UpcomingBirthdayRow = {
  id: string;
  employeeCode: string;
  fullName: string;
  department: string;
  nextDate: Date;
  daysAway: number;
};

const DEFAULT_SETUP_DEPARTMENT = "General";
const BIRTHDAY_POPUP_STORAGE_PREFIX = "pb_birthday_popup_seen_";

function normalizeGender(value: string | null | undefined): "male" | "female" | "other" {
  const token = (value || "").trim().toLowerCase();
  if (token === "male" || token === "m" || token === "पुरुष") return "male";
  if (token === "female" || token === "f" || token === "महिला") return "female";
  return "other";
}

function normalizeDepartmentName(value: string | null | undefined): string {
  const token = (value || "").trim();
  return token.length > 0 ? token : DEFAULT_SETUP_DEPARTMENT;
}

function parseEventTime(row: TransactionRow): Date | null {
  const raw = row.punchTime || row.receivedUtc;
  if (!raw) return null;
  const d = new Date(raw);
  return Number.isNaN(d.getTime()) ? null : d;
}

function toInputDate(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function firstPunches(rows: TransactionRow[]) {
  const map = new Map<string, { employeeName: string; time: Date }>();
  for (const r of rows) {
    const code = (r.employeeCode || "").trim();
    const time = parseEventTime(r);
    if (!code || !time) continue;
    const existing = map.get(code);
    if (!existing || time.getTime() < existing.time.getTime()) {
      map.set(code, { employeeName: r.employeeName || code, time });
    }
  }
  return Array.from(map.values());
}

function lastPunches(rows: TransactionRow[]) {
  const map = new Map<string, { employeeName: string; time: Date }>();
  for (const r of rows) {
    const code = (r.employeeCode || "").trim();
    const time = parseEventTime(r);
    if (!code || !time) continue;
    const existing = map.get(code);
    if (!existing || time.getTime() > existing.time.getTime()) {
      map.set(code, { employeeName: r.employeeName || code, time });
    }
  }
  return Array.from(map.values());
}

function parseBirthdayMonthDay(hireDate?: string): { month: number; day: number } | null {
  if (!hireDate) return null;
  const match = hireDate.match(/(\d{4})[/-](\d{1,2})[/-](\d{1,2})/);
  if (match) {
    const month = parseInt(match[2], 10);
    const day = parseInt(match[3], 10);
    if (month >= 1 && month <= 12 && day >= 1 && day <= 31) {
      return { month, day };
    }
  }
  const d = new Date(hireDate);
  if (!Number.isNaN(d.getTime())) {
    return { month: d.getMonth() + 1, day: d.getDate() };
  }
  return null;
}

function nextBirthdayDate(month: number, day: number, todayStart: Date): Date {
  const currentYear = todayStart.getFullYear();
  const thisYearBirthday = new Date(currentYear, month - 1, day);
  if (thisYearBirthday.getTime() >= todayStart.getTime()) {
    return thisYearBirthday;
  }
  return new Date(currentYear + 1, month - 1, day);
}

function syncAgeText(lastSync: Date | null, nowMs: number, syncing: boolean): string {
  if (syncing) return "Syncing...";
  if (!lastSync) return "Not synced";
  const sec = Math.max(0, Math.floor((nowMs - lastSync.getTime()) / 1000));
  if (sec < 5) return "Just now";
  if (sec < 60) return `${sec}s ago`;
  const min = Math.floor(sec / 60);
  return `${min}m ago`;
}

export default function DashboardOverviewPage() {
  const [employees, setEmployees] = useState<EmployeeRow[]>([]);
  const [devices, setDevices] = useState<DeviceRow[]>([]);
  const [deviceTotalCount, setDeviceTotalCount] = useState<number>(0);
  const [transactions, setTransactions] = useState<TransactionRow[]>([]);
  const [approvedLeaves, setApprovedLeaves] = useState<LeaveRow[]>([]);
  const [pendingLeaves, setPendingLeaves] = useState<LeaveRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [syncing, setSyncing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastSync, setLastSync] = useState<Date | null>(null);
  const [nowMs, setNowMs] = useState(Date.now());
  const [activeModal, setActiveModal] = useState<string | null>(null);
  const [bannerDismissed, setBannerDismissed] = useState(false);

  const inFlightRef = useRef(false);
  const branding = useBrandingSettings();
  const { calendarType } = useCalendarPreference();
  const todayLabel = formatCalendarDate(new Date(), calendarType);

  const loadDashboard = useCallback(async (background = false) => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    if (background) setSyncing(true);
    else setLoading(true);

    try {
      const bases = resolveApiBases({ includeCurrentOrigin: true });
      let empData: EmployeeRow[] = [];
      let devData: DeviceRow[] = [];
      let txData: TransactionRow[] = [];
      let appLeaves: LeaveRow[] = [];
      let pendLeaves: LeaveRow[] = [];

      for (const base of bases) {
        try {
          const [empRes, devRes, txRes, appLeaveRes, pendLeaveRes] = await Promise.all([
            fetch(`${base}/api/employees?limit=2000`, { cache: "no-store" }),
            fetch(`${base}/api/devices?limit=500`, { cache: "no-store" }),
            fetch(`${base}/api/transactions?limit=2000`, { cache: "no-store" }),
            fetch(`${base}/api/leave?status=approved`, { cache: "no-store" }),
            fetch(`${base}/api/leave?status=pending`, { cache: "no-store" }),
          ]);

          if (empRes.ok) empData = (await empRes.json()).items || [];
          if (devRes.ok) {
            const json = await devRes.json();
            devData = json.items || [];
            setDeviceTotalCount(json.totalCount ?? devData.length);
          }
          if (txRes.ok) txData = (await txRes.json()).items || [];
          if (appLeaveRes.ok) appLeaves = (await appLeaveRes.json()).items || [];
          if (pendLeaveRes.ok) pendLeaves = (await pendLeaveRes.json()).items || [];

          if (empRes.ok || devRes.ok || txRes.ok) break;
        } catch {
          // Probe next fallback base URL
        }
      }

      setEmployees(empData);
      setDevices(devData);
      setTransactions(txData);
      setApprovedLeaves(appLeaves);
      setPendingLeaves(pendLeaves);
      setLastSync(new Date());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load dashboard data.");
    } finally {
      inFlightRef.current = false;
      setLoading(false);
      setSyncing(false);
    }
  }, []);

  useEffect(() => {
    void loadDashboard(false);
    const interval = setInterval(() => void loadDashboard(true), 25000);
    return () => clearInterval(interval);
  }, [loadDashboard]);

  useEffect(() => {
    const handle = window.setInterval(() => setNowMs(Date.now()), 10000);
    return () => window.clearInterval(handle);
  }, []);

  const todayKey = useMemo(() => toInputDate(new Date()), [nowMs]);
  const activeEmployees = useMemo(() => employees.filter((e) => e.isActive !== false), [employees]);
  const totalEmployees = activeEmployees.length;
  const maleEmployees = useMemo(() => activeEmployees.filter((e) => normalizeGender(e.gender) === "male").length, [activeEmployees]);
  const femaleEmployees = useMemo(() => activeEmployees.filter((e) => normalizeGender(e.gender) === "female").length, [activeEmployees]);

  const upcomingBirthdays = useMemo(() => {
    const today = new Date();
    const todayStart = new Date(today.getFullYear(), today.getMonth(), today.getDate());
    return activeEmployees
      .map((employee): UpcomingBirthdayRow | null => {
        const parts = parseBirthdayMonthDay(employee.hireDate);
        if (!parts) return null;
        const nextDate = nextBirthdayDate(parts.month, parts.day, todayStart);
        const daysAway = Math.max(0, Math.round((nextDate.getTime() - todayStart.getTime()) / (24 * 60 * 60 * 1000)));
        return {
          id: employee.id,
          employeeCode: employee.employeeCode,
          fullName: employee.fullName,
          department: normalizeDepartmentName(employee.department),
          nextDate,
          daysAway,
        };
      })
      .filter((row): row is UpcomingBirthdayRow => row !== null && row.daysAway <= 30)
      .sort((a, b) => (a.daysAway !== b.daysAway ? a.daysAway - b.daysAway : a.fullName.localeCompare(b.fullName)))
      .slice(0, 6);
  }, [activeEmployees, nowMs]);

  const todaysBirthdays = useMemo(() => upcomingBirthdays.filter((row) => row.daysAway === 0), [upcomingBirthdays]);

  useEffect(() => {
    if (typeof window === "undefined" || todaysBirthdays.length === 0) return;
    const key = `${BIRTHDAY_POPUP_STORAGE_PREFIX}${todayKey}`;
    try {
      if (window.localStorage.getItem(key) === "dismissed") {
        setBannerDismissed(true);
      }
    } catch {
      // ignore
    }
  }, [todayKey, todaysBirthdays.length]);

  const dismissCelebrationBanner = () => {
    setBannerDismissed(true);
    if (typeof window === "undefined") return;
    const key = `${BIRTHDAY_POPUP_STORAGE_PREFIX}${todayKey}`;
    try {
      window.localStorage.setItem(key, "dismissed");
    } catch {
      // ignore
    }
  };

  const txByDay = useMemo(() => {
    const map = new Map<string, TransactionRow[]>();
    for (const row of transactions) {
      const event = parseEventTime(row);
      if (!event) continue;
      const key = toInputDate(event);
      map.set(key, [...(map.get(key) ?? []), row]);
    }
    return map;
  }, [transactions]);

  const todayTransactions = useMemo(() => txByDay.get(todayKey) ?? [], [txByDay, todayKey]);
  const uniqueTodayCodes = useMemo(
    () => new Set(todayTransactions.map((r) => (r.employeeCode || "").trim()).filter((x) => x.length > 0)),
    [todayTransactions]
  );
  const uniquePunches = uniqueTodayCodes.size;
  const totalPunches = todayTransactions.length;

  const onLeaveToday = useMemo(() => {
    const ids = new Set<string>();
    for (const row of approvedLeaves) {
      if (!row.fromDate || !row.toDate) continue;
      if (row.fromDate <= todayKey && row.toDate >= todayKey) ids.add(String(row.personnelId ?? row.id));
    }
    return ids.size;
  }, [approvedLeaves, todayKey]);

  const absentCount = Math.max(0, totalEmployees - uniquePunches - onLeaveToday);
  const attendanceRate = totalEmployees > 0 ? Math.round((uniquePunches / totalEmployees) * 100) : 0;

  const timeWindow = useMemo(() => {
    const shifts = readShiftDefinitions();
    const timetables = readTimetableDefinitions(shifts);
    const defaultTimetable =
      timetables.find((x) => x.id === "default-weekly" && x.isActive) ??
      timetables.find((x) => x.isActive) ??
      timetables[0] ??
      null;
    const dayOfWeek = new Date().getDay();
    const currentDay = defaultTimetable?.days.find((x) => x.dayOfWeek === dayOfWeek) ?? defaultTimetable?.days.find((x) => !x.isWeeklyOff) ?? null;
    const lateCutoffLabel = currentDay?.checkInEnd ?? "10:15";
    return { lateCutoffMinutes: parseTimeToMinutes(lateCutoffLabel), lateCutoffLabel };
  }, [nowMs]);

  const lateEntries = useMemo(
    () =>
      firstPunches(todayTransactions)
        .map((row) => {
          const minutes = row.time.getHours() * 60 + row.time.getMinutes();
          return { name: row.employeeName, delay: Math.max(0, minutes - timeWindow.lateCutoffMinutes) };
        })
        .filter((x) => x.delay > 0)
        .sort((a, b) => b.delay - a.delay),
    [timeWindow.lateCutoffMinutes, todayTransactions]
  );

  const trendRows = useMemo(() => {
    const rows: TrendRow[] = [];
    const base = new Date();
    for (let i = 6; i >= 0; i -= 1) {
      const dayDate = new Date(base);
      dayDate.setDate(base.getDate() - i);
      const dayKey = toInputDate(dayDate);
      const dayTx = txByDay.get(dayKey) ?? [];
      const presentCount = new Set(dayTx.map((x) => (x.employeeCode || "").trim()).filter((x) => x.length > 0)).size;
      const lateCount = firstPunches(dayTx).filter(
        (item) => item.time.getHours() * 60 + item.time.getMinutes() > timeWindow.lateCutoffMinutes
      ).length;
      const leaveCount = new Set(
        approvedLeaves.filter((x) => x.fromDate! <= dayKey && x.toDate! >= dayKey).map((x) => String(x.personnelId ?? x.id))
      ).size;
      rows.push({
        day: format(dayDate, "EEE"),
        present: presentCount,
        absent: Math.max(0, totalEmployees - presentCount - leaveCount),
        late: lateCount,
      });
    }
    return rows;
  }, [approvedLeaves, timeWindow.lateCutoffMinutes, totalEmployees, txByDay]);

  const deptRows = useMemo(() => {
    const totalByDept = new Map<string, number>();
    for (const employee of activeEmployees) {
      const dept = normalizeDepartmentName(employee.department);
      totalByDept.set(dept, (totalByDept.get(dept) ?? 0) + 1);
    }
    const presentByDept = new Map<string, number>();
    for (const code of uniqueTodayCodes) {
      const employee = activeEmployees.find((e) => e.employeeCode.trim() === code);
      const dept = normalizeDepartmentName(employee?.department);
      presentByDept.set(dept, (presentByDept.get(dept) ?? 0) + 1);
    }
    const names = Array.from(new Set([...totalByDept.keys(), ...presentByDept.keys()])).sort((a, b) => a.localeCompare(b));
    return names.map((name) => ({
      name,
      present: presentByDept.get(name) ?? 0,
      total: totalByDept.get(name) ?? 0,
    }));
  }, [activeEmployees, uniqueTodayCodes]);

  const onlineDevices = useMemo(
    () => devices.filter((d) => (typeof d.isOnline === "boolean" ? d.isOnline : String(d.state || "").toLowerCase() === "online")).length,
    [devices]
  );
  const totalDevices = Math.max(deviceTotalCount, devices.length);
  const liveLabel = syncAgeText(lastSync, nowMs, syncing || loading);

  return (
    <div className="space-y-5">
      {/* 1. Precision Hero Header & Live Barometer */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 sm:p-6 shadow-sm">
        <div className="flex flex-col gap-5 lg:flex-row lg:items-center lg:justify-between">
          <div className="flex items-center gap-4">
            <div className="flex h-14 w-14 shrink-0 items-center justify-center">
              <img src={branding.logoUrl || "/apan_logo.png"} alt="Logo" className="h-full w-full object-contain drop-shadow-sm" />
            </div>
            <div>
              <div className="flex items-center gap-2">
                <h1 className="text-xl sm:text-2xl font-bold tracking-tight text-slate-900 font-nepali">
                  {branding.organizationName || "अपन हाजिरी"}
                </h1>
                <span className="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-2.5 py-0.5 text-xs font-semibold text-emerald-700 border border-emerald-200">
                  <span className="h-1.5 w-1.5 rounded-full bg-emerald-500 animate-pulse" />
                  Live System
                </span>
              </div>
              <p className="mt-0.5 text-xs font-medium text-slate-500">
                {branding.systemTitle || "Apan Hajiri"} • {todayLabel}
              </p>
            </div>
          </div>

          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => void loadDashboard(true)}
              className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-100 transition shadow-2xs"
            >
              <RefreshCw className={`h-3.5 w-3.5 text-slate-500 ${syncing || loading ? "animate-spin" : ""}`} />
              <span>{liveLabel}</span>
            </button>
          </div>
        </div>

        {/* Live Attendance Ratio Barometer */}
        <div className="mt-6 border-t border-slate-100 pt-5">
          <div className="flex items-end justify-between">
            <div>
              <span className="text-xs font-bold uppercase tracking-wider text-slate-500">Daily Attendance Rate</span>
              <div className="mt-1 flex items-baseline gap-2">
                <span className="text-3xl font-extrabold text-slate-900 tracking-tight">{attendanceRate}%</span>
                <span className="text-xs font-medium text-slate-500">
                  ({uniquePunches} of {totalEmployees} active employees present)
                </span>
              </div>
            </div>
            <div className="hidden sm:flex items-center gap-4 text-xs font-medium text-slate-600">
              <span className="flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-full bg-emerald-500" /> {uniquePunches} Present</span>
              <span className="flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-full bg-amber-500" /> {lateEntries.length} Late</span>
              <span className="flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-full bg-rose-500" /> {absentCount} Absent</span>
              <span className="flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-full bg-blue-500" /> {onLeaveToday} Leave</span>
            </div>
          </div>

          {/* Segmented Progress Bar */}
          <div className="mt-3 flex h-3 w-full overflow-hidden rounded-full bg-slate-100 p-0.5">
            <div
              style={{ width: `${totalEmployees > 0 ? (uniquePunches / totalEmployees) * 100 : 0}%` }}
              className="h-full rounded-full bg-emerald-500 transition-all duration-500"
            />
          </div>
        </div>
      </section>

      {/* 1b. Celebration Banner - Visible only when someone has a birthday today */}
      {todaysBirthdays.length > 0 && !bannerDismissed && (
        <section className="relative overflow-hidden rounded-2xl border border-amber-200/90 bg-gradient-to-r from-amber-500/10 via-rose-500/5 to-amber-500/5 p-4 sm:p-5 shadow-2xs">
          <div className="flex items-center justify-between gap-4">
            <div className="flex items-center gap-3.5">
              <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-amber-500/20 text-amber-700 shadow-inner">
                <Cake className="h-6 w-6 text-amber-600 animate-bounce" />
              </div>
              <div>
                <div className="flex items-center gap-2">
                  <span className="inline-flex items-center gap-1 rounded-full bg-amber-100 px-2.5 py-0.5 text-[11px] font-bold text-amber-800 border border-amber-300">
                    <Sparkles className="h-3 w-3 text-amber-600" />
                    Today&apos;s Celebration
                  </span>
                </div>
                <p className="mt-1 text-sm font-semibold text-slate-800">
                  Wishing {todaysBirthdays.map((b) => `${b.fullName} (${b.department})`).join(", ")} a very Happy Birthday! 🎉
                </p>
              </div>
            </div>
            <button
              type="button"
              onClick={dismissCelebrationBanner}
              className="rounded-lg p-1.5 text-slate-400 hover:bg-amber-100/60 hover:text-slate-700 transition"
              title="Dismiss for today"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        </section>
      )}

      {error && (
        <div className="flex items-center gap-2 rounded-xl border border-rose-200 bg-rose-50 p-4 text-xs font-medium text-rose-700">
          <AlertCircle className="h-4 w-4 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {/* 2. Structured Operational KPI Cards */}
      <section className="grid gap-3.5 sm:grid-cols-2 lg:grid-cols-4">
        {[
          { label: "Total Staff", value: totalEmployees, sub: `${maleEmployees} Male • ${femaleEmployees} Female`, icon: Users, color: "text-blue-600", bg: "bg-blue-50" },
          { label: "Present Today", value: uniquePunches, sub: `${totalPunches} Total Punches`, icon: CheckCircle2, color: "text-emerald-600", bg: "bg-emerald-50" },
          { label: "Late Check-in", value: lateEntries.length, sub: `After ${timeWindow.lateCutoffLabel}`, icon: Clock3, color: "text-amber-600", bg: "bg-amber-50" },
          { label: "Biometric Hardware", value: `${onlineDevices}/${totalDevices}`, sub: "Devices Connected", icon: Router, color: "text-indigo-600", bg: "bg-indigo-50" },
        ].map((card) => (
          <article key={card.label} className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-2xs hover:border-slate-300 transition">
            <div className="flex items-center justify-between">
              <span className="text-xs font-bold uppercase tracking-wider text-slate-500">{card.label}</span>
              <div className={`flex h-8 w-8 items-center justify-center rounded-xl ${card.bg} ${card.color}`}>
                <card.icon className="h-4 w-4" />
              </div>
            </div>
            <p className="mt-3 text-2xl font-bold text-slate-900 tracking-tight leading-none">{card.value}</p>
            <p className="mt-1.5 text-xs text-slate-500 font-medium">{card.sub}</p>
          </article>
        ))}
      </section>

      {/* 3. Operational Grid: Trend Curve & Split Breakdown (Birthdays + Departments) */}
      <section className="grid gap-4 lg:grid-cols-3 items-stretch">
        {/* Weekly Trend Chart */}
        <article className="lg:col-span-2 rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs flex flex-col justify-between">
          <div>
            <div className="mb-4 flex items-center justify-between">
              <div>
                <h2 className="text-sm font-bold text-slate-900">7-Day Attendance Trajectory</h2>
                <p className="text-xs text-slate-500">Daily presence vs absent metrics</p>
              </div>
            </div>
            <div className="h-[270px] w-full">
              <ResponsiveContainer width="100%" height="100%">
                <AreaChart data={trendRows} margin={{ top: 10, right: 10, left: -25, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="#f1f5f9" vertical={false} />
                  <XAxis dataKey="day" axisLine={false} tickLine={false} tick={{ fill: "#64748b", fontSize: 11, fontWeight: 500 }} />
                  <YAxis axisLine={false} tickLine={false} tick={{ fill: "#64748b", fontSize: 11, fontWeight: 500 }} />
                  <Tooltip contentStyle={{ borderRadius: 12, border: "1px solid #e2e8f0", boxShadow: "0 4px 12px rgba(0,0,0,0.05)", fontSize: 12 }} />
                  <Area type="monotone" dataKey="present" stroke="#059669" strokeWidth={2.5} fill="#059669" fillOpacity={0.08} />
                  <Area type="monotone" dataKey="absent" stroke="#e11d48" strokeWidth={1.5} fill="#e11d48" fillOpacity={0.03} />
                </AreaChart>
              </ResponsiveContainer>
            </div>
          </div>
        </article>

        {/* Right Column: Stacked Upcoming Birthdays & Department Status */}
        <div className="flex flex-col gap-4">
          {/* Upcoming Birthdays Card */}
          <article className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-2xs flex flex-col justify-between flex-1">
            <div>
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-amber-50 text-amber-600">
                    <Cake className="h-4 w-4" />
                  </div>
                  <div>
                    <h2 className="text-xs font-bold uppercase tracking-wider text-slate-700">Upcoming Birthdays</h2>
                  </div>
                </div>
                {upcomingBirthdays.length > 0 && (
                  <span className="rounded-full bg-amber-50 px-2 py-0.5 text-[10px] font-bold text-amber-700 border border-amber-200">
                    {upcomingBirthdays.length} upcoming
                  </span>
                )}
              </div>

              <div className="mt-3 space-y-2 max-h-[130px] overflow-y-auto pr-1">
                {upcomingBirthdays.length === 0 ? (
                  <div className="flex flex-col items-center justify-center py-5 text-center text-xs text-slate-400">
                    <CalendarDays className="h-5 w-5 mb-1.5 text-slate-300" />
                    <span>No upcoming birthdays this month</span>
                  </div>
                ) : (
                  upcomingBirthdays.map((row) => {
                    const dateFormatted =
                      calendarType === "bs" ? formatCalendarDate(row.nextDate, "bs") : format(row.nextDate, "MMM d");
                    return (
                      <Link
                        key={row.id}
                        href={`/employees?search=${encodeURIComponent(row.employeeCode || row.fullName)}`}
                        className="flex items-center justify-between gap-2 rounded-xl p-1.5 hover:bg-slate-50 transition border border-transparent hover:border-slate-100"
                      >
                        <div className="flex items-center gap-2.5 min-w-0">
                          <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-amber-50 text-[10px] font-bold text-amber-800 border border-amber-200/70">
                            {row.fullName.slice(0, 2).toUpperCase()}
                          </div>
                          <div className="min-w-0">
                            <p className="text-xs font-semibold text-slate-800 truncate">{row.fullName}</p>
                            <p className="text-[10px] text-slate-400 truncate">
                              {row.department} • {dateFormatted}
                            </p>
                          </div>
                        </div>

                        {row.daysAway === 0 ? (
                          <span className="shrink-0 inline-flex items-center gap-1 rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-bold text-amber-800 border border-amber-300 animate-pulse">
                            <Sparkles className="h-2.5 w-2.5" /> Today!
                          </span>
                        ) : row.daysAway === 1 ? (
                          <span className="shrink-0 rounded-md bg-amber-50 px-1.5 py-0.5 text-[10px] font-semibold text-amber-700 border border-amber-200">
                            Tomorrow
                          </span>
                        ) : (
                          <span className="shrink-0 rounded-md bg-slate-100 px-1.5 py-0.5 text-[10px] font-semibold text-slate-600">
                            {row.daysAway}d away
                          </span>
                        )}
                      </Link>
                    );
                  })
                )}
              </div>
            </div>

            <Link
              href="/employees"
              className="mt-2 block text-center text-[11px] font-semibold text-amber-700 hover:text-amber-800 pt-1.5 border-t border-slate-100"
            >
              View Employee Directory →
            </Link>
          </article>

          {/* Department Status */}
          <article className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-2xs flex flex-col justify-between flex-1">
            <div>
              <div className="flex items-center gap-2">
                <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-blue-50 text-blue-600">
                  <Users className="h-4 w-4" />
                </div>
                <div>
                  <h2 className="text-xs font-bold uppercase tracking-wider text-slate-700">Department Status</h2>
                </div>
              </div>

              <div className="mt-3 space-y-1.5 max-h-[130px] overflow-y-auto pr-1">
                {deptRows.length === 0 ? (
                  <div className="flex h-20 items-center justify-center text-xs text-slate-400 italic">
                    No departments found.
                  </div>
                ) : (
                  deptRows.map((dept) => {
                    const pct = dept.total > 0 ? Math.round((dept.present / dept.total) * 100) : 0;
                    return (
                      <div key={dept.name} className="flex items-center justify-between border-b border-slate-100 pb-1.5">
                        <div className="min-w-0 pr-2">
                          <p className="text-xs font-semibold text-slate-800 truncate">{dept.name}</p>
                          <p className="text-[10px] text-slate-400">{dept.present} of {dept.total} present</p>
                        </div>
                        <span className="rounded-md bg-slate-100 px-2 py-0.5 text-[11px] font-bold text-slate-700">
                          {pct}%
                        </span>
                      </div>
                    );
                  })
                )}
              </div>
            </div>

            <Link
              href="/reports"
              className="mt-2 block text-center text-[11px] font-semibold text-blue-600 hover:text-blue-700 pt-1.5 border-t border-slate-100"
            >
              View Full Detailed Reports →
            </Link>
          </article>
        </div>
      </section>

      {/* 4. Quick Module Navigation Hub */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs">
        <h2 className="text-xs font-bold uppercase tracking-wider text-slate-500 mb-3">Quick Actions</h2>
        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
          {[
            { label: "Register Employee", href: "/employees" as const, icon: UserPlus },
            { label: "Device Network & Status", href: "/devices" as const, icon: Fingerprint },
            { label: "Manage Shift Rosters", href: { pathname: "/shifts" as const, query: { tab: "setup" } }, icon: Clock3 },
            { label: "Live Transactions Feed", href: "/transactions" as const, icon: FileText },
          ].map((item) => (
            <Link
              key={item.label}
              href={item.href}
              className="flex items-center gap-3 rounded-xl border border-slate-200 bg-slate-50/70 p-3 hover:bg-white hover:border-slate-300 hover:shadow-sm transition"
            >
              <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-white border border-slate-200 text-slate-700 shadow-2xs">
                <item.icon className="h-4 w-4" />
              </div>
              <span className="text-xs font-semibold text-slate-800">{item.label}</span>
            </Link>
          ))}
        </div>
      </section>

      {activeModal === "leave" && <LeaveApplicationModal onClose={() => setActiveModal(null)} onSuccess={() => void loadDashboard(true)} />}
    </div>
  );
}
