"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { format } from "date-fns";
import {
  Activity,
  AlertCircle,
  BellRing,
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
  Umbrella,
  Upload,
  UserPlus,
  Users,
  UserX,
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
  isActive: boolean;
  gender?: string;
  hireDate?: string;
};

type DeviceRow = {
  id: string;
  serialNumber: string;
  name: string;
  isOnline?: boolean;
  state?: string;
};

type PagedResponse<T> = {
  totalCount?: number;
  page?: number;
  pageSize?: number;
  items?: T[];
};

type TransactionRow = {
  id: string;
  employeeCode: string;
  employeeName: string;
  employeeDepartment: string;
  rawTimePc?: string | null;
  punchTime?: string | null;
};

type LeaveRow = {
  id: number;
  personnelId?: number | null;
  fromDate: string;
  toDate: string;
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

const BIRTHDAY_POPUP_STORAGE_PREFIX = "pb_birthday_popup_seen_";

const DEFAULT_SETUP_DEPARTMENT = "Default Department";

function normalizeDepartmentName(value: string | null | undefined): string {
  const token = (value || "").trim();
  if (!token) return DEFAULT_SETUP_DEPARTMENT;
  if (token.toLowerCase() === "general") return DEFAULT_SETUP_DEPARTMENT;
  return token;
}

const quickActions = [
  { label: "Add Employee", href: "/employees", icon: UserPlus },
  { label: "Sync Devices", href: "/devices", icon: RefreshCw },
  { label: "Generate Report", href: "/reports", icon: FileText },
  { label: "Download Attendance", href: "/transactions", icon: Download },
  { label: "Leaves & Holiday", href: "/leaves", icon: Umbrella },
  { label: "Add Shift", href: "/shift", icon: Clock3 },
] as const;

const suggestedActions = [
  { label: "Settings", href: "/settings", icon: Settings },
  { label: "Apply Leave", onClick: "leave-terminal", icon: QrCode },
  { label: "Alerts", href: "/notifications", icon: BellRing },
  { label: "QR Check-in", onClick: "qr", icon: QrCode },
  { label: "Export Logs", href: "/transactions", icon: FileOutput },
] as const;

function ActionModal({ type, onClose }: { type: "qr" | "leave", onClose: () => void }) {
  const [qrData, setQrData] = useState("");
  const [recentScans, setRecentScans] = useState<Array<{ name: string; time: string }>>([]);
  const [hostIp, setHostIp] = useState("");
  const [copied, setCopied] = useState(false);

  const copyLink = useCallback(async () => {
    if (!qrData) return;
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(qrData);
      } else {
        const ta = document.createElement("textarea");
        ta.value = qrData; document.body.appendChild(ta); ta.select();
        document.execCommand("copy"); document.body.removeChild(ta);
      }
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* ignore */ }
  }, [qrData]);

  const refreshCode = useCallback(async () => {
    try {
      const settings = await requestJson<any>("/api/settings");
      const ip = settings.serverIp || window.location.hostname;
      // Handle both camelCase and PascalCase from backend
      const port = settings.frontendPort || settings.FrontendPort || settings.webUiPort || window.location.port || 3000;
      setHostIp(ip);
      const path = type === "leave" ? "/apply-leave" : "/check-in";
      setQrData(`http://${ip}:${port}${path}?t=${Math.random().toString(36).slice(2, 8)}`);
    } catch {
      const p = window.location.port || "3000";
      const path = type === "leave" ? "/apply-leave" : "/check-in";
      setQrData(`http://${window.location.hostname}:${p}${path}?t=${Math.random().toString(36).slice(2, 8)}`);
    }
  }, [type]);

  useEffect(() => {
    void refreshCode();
    
    const poll = async () => {
      try {
        const today = new Date().toISOString().split("T")[0];
        if (type === "qr") {
          const txs = await requestJson<TransactionRow[]>(`/api/transactions?from=${today}&to=${today}&take=10`);
          if (Array.isArray(txs)) {
            const mapped = txs.map(t => ({
              name: t.employeeName || t.employeeCode,
              time: t.punchTime ? new Date(t.punchTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : "now"
            }));
            setRecentScans(mapped.slice(0, 4));
          }
        } else {
          const leaves = await requestJson<any[]>(`/api/leave?from=${today}&to=${today}&status=pending`);
          if (Array.isArray(leaves)) {
            const mapped = leaves.map(l => ({
              name: l.personnelName || l.personnelCode,
              time: l.appliedDate ? new Date(l.appliedDate).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : "today"
            }));
            setRecentScans(mapped.slice(0, 4));
          }
        }
      } catch { /* ignore */ }
    };

    const interval = setInterval(poll, 3000);
    return () => clearInterval(interval);
  }, [refreshCode, type]);

  const qrImageUrl = `https://api.qrserver.com/v1/create-qr-code/?size=300x300&data=${encodeURIComponent(qrData)}`;

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4 backdrop-blur-sm">
      <div className="w-full max-w-md overflow-hidden rounded-[32px] border border-[#d3e6fb] bg-white shadow-2xl transition-all animate-in fade-in zoom-in duration-300">
        <div className="bg-gradient-to-br from-[#1d4ed8] to-[#0284c7] p-5 text-white text-center">
          <div className="mx-auto mb-3 grid h-12 w-12 place-items-center rounded-2xl bg-white/20 shadow-inner backdrop-blur-md">
            {type === "leave" ? <Umbrella className="h-6 w-6" /> : <QrCode className="h-6 w-6" />}
          </div>
          <h2 className="text-lg font-bold tracking-tight">{type === "leave" ? "Apply Leave Terminal" : "QR Check-in Terminal"}</h2>
          <p className="mt-0.5 text-xs text-blue-100 font-medium">Terminal IP: {hostIp || "detecting..."}</p>
        </div>

        <div className="p-6 flex flex-col items-center">
          <div className="relative group">
             <div className="absolute -inset-4 rounded-[40px] bg-gradient-to-br from-blue-50 to-emerald-50 opacity-50 group-hover:opacity-100 transition-opacity" />
             <div className="relative rounded-3xl border-4 border-white bg-white p-3 shadow-xl shadow-blue-900/5 transition-transform group-hover:scale-[1.02]">
               {qrData ? (
                 <img src={qrImageUrl} alt="QR Code" className="h-48 w-48 rounded-lg" />
               ) : (
                 <div className="h-48 w-48 animate-pulse bg-gray-100 rounded-lg flex items-center justify-center text-gray-400">Generating...</div>
               )}
             </div>
          </div>

          <div className="mt-6 w-full space-y-3">
            <div className="flex items-center gap-3">
              <div className="h-px flex-1 bg-gray-100" />
              <span className="text-[9px] font-bold uppercase tracking-widest text-gray-400">{type === "leave" ? "Recent Applications" : "Live Scans (Realtime)"}</span>
              <div className="h-px flex-1 bg-gray-100" />
            </div>

            <div className="space-y-1.5 max-h-[160px] overflow-y-auto pr-1">
              {recentScans.length > 0 ? (
                recentScans.map((scan, i) => (
                  <div key={i} className="flex items-center justify-between rounded-xl bg-emerald-50/40 px-3 py-1.5 border border-emerald-100/50 animate-in slide-in-from-top-2 duration-500">
                    <div className="flex items-center gap-2">
                      <div className="h-1.5 w-1.5 rounded-full bg-emerald-500 shadow-[0_0_8px_rgba(16,185,129,0.5)]" />
                      <span className="text-xs font-bold text-emerald-900">{scan.name}</span>
                    </div>
                    <span className="text-[10px] font-bold text-emerald-600/80">{scan.time}</span>
                  </div>
                ))
              ) : (
                <p className="text-center text-[11px] text-gray-400 py-4 italic">{type === "leave" ? "Waiting for requests..." : "Waiting for incoming check-ins..."}</p>
              )}
            </div>
          </div>
        </div>

        {qrData ? (
          <button onClick={copyLink} title={qrData} className="mx-3 mb-1 flex w-[calc(100%-1.5rem)] items-center gap-2 rounded-xl border border-blue-200 bg-blue-50/60 px-3 py-2 text-left transition hover:bg-blue-50 active:scale-[0.99]">
            <QrCode className="h-4 w-4 shrink-0 text-blue-600" />
            <span className="flex-1 truncate text-[11px] font-medium text-blue-900">{qrData}</span>
            <span className="shrink-0 text-[10px] font-bold text-blue-600">{copied ? "Copied!" : "Copy Link"}</span>
          </button>
        ) : null}
        <div className="border-t border-gray-100 bg-gray-50/30 p-3 flex gap-2">
          <button onClick={() => refreshCode()} className="flex-1 rounded-xl border border-gray-200 bg-white py-2 text-[10px] font-bold text-gray-600 transition hover:bg-gray-50 active:scale-95">Refresh Code</button>
          <button onClick={onClose} className="flex-1 rounded-xl bg-gray-900 py-2 text-[10px] font-bold text-white transition hover:bg-gray-800 active:scale-95 shadow-lg shadow-gray-900/20">Close Terminal</button>
        </div>
      </div>
    </div>
  );
}

let preferredApiBase = "";
const DASHBOARD_CACHE_KEY = "pb_dashboard_cache_v1";

type DashboardCache = {
  savedAt: string;
  employees: EmployeeRow[];
  devices: DeviceRow[];
  deviceTotalCount?: number;
  transactions: TransactionRow[];
  approvedLeaves: LeaveRow[];
  pendingLeaves: LeaveRow[];
};

function getApiBases(): string[] {
  return resolveApiBases({ preferredBase: preferredApiBase });
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch";
  for (const base of getApiBases()) {
    try {
      if (!base) continue;
      const call = async () => {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 4500);
        try {
          return await fetch(`${base}${path}`, {
            ...init,
            headers: { "Content-Type": "application/json", ...(init?.headers ?? {}), },
            cache: "no-store",
            signal: controller.signal,
          });
        } finally { clearTimeout(timer); }
      };
      let response = await call();
      if (response.status === 503) {
        try { await fetch(`${base}/api/server/start`, { method: "POST", cache: "no-store" }); response = await call(); } catch { /* ignore */ }
      }
      if (response.ok) {
        const contentType = response.headers.get("content-type") ?? "";
        if (!contentType.includes("application/json")) {
          lastError = `Expected JSON response, received ${contentType || "HTML"}`;
          continue;
        }
        preferredApiBase = base;
        return (await response.json()) as T;
      }
      lastError = (await response.text()) || `Request failed (${response.status})`;
    } catch (error) { lastError = error instanceof Error ? error.message : lastError; }
  }
  throw new Error(lastError);
}

function unwrapItems<T>(payload: unknown): T[] {
  if (Array.isArray(payload)) return payload as T[];
  if (payload && typeof payload === "object") {
    const source = payload as PagedResponse<T>;
    if (Array.isArray(source.items)) return source.items;
  }
  return [];
}

function readTotalCount<T>(payload: unknown, rows: T[]): number {
  if (payload && typeof payload === "object") {
    const totalCount = (payload as PagedResponse<T>).totalCount;
    if (typeof totalCount === "number" && Number.isFinite(totalCount)) return totalCount;
  }
  return rows.length;
}

function toInputDate(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, "0");
  const d = String(date.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

function normalizeGender(value: string | undefined): "male" | "female" | "other" {
  const raw = (value || "").trim().toLowerCase();
  if (raw.startsWith("m")) return "male";
  if (raw.startsWith("f")) return "female";
  return "other";
}

function parseBirthdayMonthDay(value: string | undefined): { month: number; day: number } | null {
  const raw = (value || "").trim();
  if (!raw) return null;
  const isoMatch = raw.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (isoMatch) {
    const month = Number.parseInt(isoMatch[2] ?? "0", 10);
    const day = Number.parseInt(isoMatch[3] ?? "0", 10);
    if (month >= 1 && month <= 12 && day >= 1 && day <= 31) return { month, day };
  }
  const parsed = new Date(raw);
  if (Number.isNaN(parsed.getTime())) return null;
  return { month: parsed.getMonth() + 1, day: parsed.getDate(), };
}

function nextBirthdayDate(month: number, day: number, today: Date): Date {
  const build = (year: number) => {
    const maxDay = new Date(year, month, 0).getDate();
    const safeDay = Math.min(day, maxDay);
    return new Date(year, month - 1, safeDay, 0, 0, 0, 0);
  };
  const todayStart = new Date(today.getFullYear(), today.getMonth(), today.getDate());
  let next = build(todayStart.getFullYear());
  if (next.getTime() < todayStart.getTime()) next = build(todayStart.getFullYear() + 1);
  return next;
}

function parseEventTime(row: TransactionRow): Date | null {
  const raw = row.rawTimePc ?? row.punchTime ?? "";
  const parsed = new Date(raw);
  if (Number.isNaN(parsed.getTime())) return null;
  return parsed;
}

function firstPunches(rows: TransactionRow[]): Array<{ employeeCode: string; employeeName: string; employeeDepartment: string; time: Date; }> {
  const map = new Map<string, { employeeCode: string; employeeName: string; employeeDepartment: string; time: Date }>();
  for (const row of rows) {
    const code = (row.employeeCode || "").trim();
    if (!code) continue;
    const time = parseEventTime(row);
    if (!time) continue;
    const existing = map.get(code);
    if (!existing || time.getTime() < existing.time.getTime()) {
      map.set(code, { employeeCode: code, employeeName: (row.employeeName || code).trim() || code, employeeDepartment: normalizeDepartmentName(row.employeeDepartment), time, });
    }
  }
  return Array.from(map.values());
}

function lastPunches(rows: TransactionRow[]): Array<{ employeeCode: string; employeeName: string; employeeDepartment: string; time: Date; }> {
  const map = new Map<string, { employeeCode: string; employeeName: string; employeeDepartment: string; time: Date }>();
  for (const row of rows) {
    const code = (row.employeeCode || "").trim();
    if (!code) continue;
    const time = parseEventTime(row);
    if (!time) continue;
    const existing = map.get(code);
    if (!existing || time.getTime() > existing.time.getTime()) {
      map.set(code, { employeeCode: code, employeeName: (row.employeeName || code).trim() || code, employeeDepartment: normalizeDepartmentName(row.employeeDepartment), time, });
    }
  }
  return Array.from(map.values());
}

function syncAgeText(lastSync: Date | null, nowMs: number, syncing: boolean): string {
  if (!lastSync) return syncing ? "syncing..." : "not synced";
  if (syncing) return "syncing...";
  const sec = Math.max(0, Math.floor((nowMs - lastSync.getTime()) / 1000));
  if (sec < 5) return "just now";
  if (sec < 60) return `${sec}s ago`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ago`;
  const hrs = Math.floor(min / 60);
  return `${hrs}h ago`;
}

function readDashboardCache(): DashboardCache | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.localStorage.getItem(DASHBOARD_CACHE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<DashboardCache>;
    if (!parsed || typeof parsed !== "object") return null;
    return {
      savedAt: typeof parsed.savedAt === "string" ? parsed.savedAt : "",
      employees: Array.isArray(parsed.employees) ? (parsed.employees as EmployeeRow[]) : [],
      devices: Array.isArray(parsed.devices) ? (parsed.devices as DeviceRow[]) : [],
      deviceTotalCount: typeof parsed.deviceTotalCount === "number" ? parsed.deviceTotalCount : undefined,
      transactions: Array.isArray(parsed.transactions) ? (parsed.transactions as TransactionRow[]) : [],
      approvedLeaves: Array.isArray(parsed.approvedLeaves) ? (parsed.approvedLeaves as LeaveRow[]) : [],
      pendingLeaves: Array.isArray(parsed.pendingLeaves) ? (parsed.pendingLeaves as LeaveRow[]) : [],
    };
  } catch { return null; }
}

function writeDashboardCache(payload: DashboardCache) {
  if (typeof window === "undefined") return;
  try { window.localStorage.setItem(DASHBOARD_CACHE_KEY, JSON.stringify(payload)); } catch { /* ignore */ }
}

export default function DashboardPage() {
  const { calendarType } = useCalendarPreference();
  const todayLabel = formatCalendarDate(new Date(), calendarType);
  const [employees, setEmployees] = useState<EmployeeRow[]>([]);
  const [devices, setDevices] = useState<DeviceRow[]>([]);
  const [deviceTotalCount, setDeviceTotalCount] = useState(0);
  const [transactions, setTransactions] = useState<TransactionRow[]>([]);
  const [approvedLeaves, setApprovedLeaves] = useState<LeaveRow[]>([]);
  const [pendingLeaves, setPendingLeaves] = useState<LeaveRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [syncing, setSyncing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastSync, setLastSync] = useState<Date | null>(null);
  const [nowMs, setNowMs] = useState(Date.now());
  const [birthdayPopupOpen, setBirthdayPopupOpen] = useState(false);
  const [activeModal, setActiveModal] = useState<"qr" | "leave" | "leave-terminal" | null>(null);
  const inFlightRef = useRef(false);

  const loadDashboard = useCallback(async (background: boolean) => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    if (background) setSyncing(true); else setLoading(true);
    try {
      const now = new Date();
      const today = toInputDate(now);
      const weekStart = new Date(now);
      weekStart.setDate(weekStart.getDate() - 6);
      const weekFrom = toInputDate(weekStart);
      const [employeePayload, devicePayload, txRows] = await Promise.all([
        requestJson<unknown>("/api/personnel/employees?status=active&pageSize=5000"),
        requestJson<unknown>("/api/devices?page=1&pageSize=5000"),
        requestJson<TransactionRow[]>(`/api/transactions?from=${encodeURIComponent(weekFrom)}&to=${encodeURIComponent(today)}&take=7000`),
      ]);
      const normalizedEmployees = unwrapItems<EmployeeRow>(employeePayload);
      const normalizedDevices = unwrapItems<DeviceRow>(devicePayload);
      const totalDevices = readTotalCount<DeviceRow>(devicePayload, normalizedDevices);
      const normalizedTransactions = Array.isArray(txRows) ? txRows : [];
      setEmployees(normalizedEmployees);
      setDevices(normalizedDevices);
      setDeviceTotalCount(totalDevices);
      setTransactions(normalizedTransactions);
      setError(null);
      const approvedPromise = requestJson<LeaveRow[]>(`/api/leave?status=approved&from=${encodeURIComponent(weekFrom)}&to=${encodeURIComponent(today)}`).catch(() => [] as LeaveRow[]);
      const pendingPromise = requestJson<LeaveRow[]>("/api/leave?status=pending").catch(() => [] as LeaveRow[]);
      const [approvedRows, pendingRows] = await Promise.all([approvedPromise, pendingPromise]);
      const normalizedApproved = Array.isArray(approvedRows) ? approvedRows : [];
      const normalizedPending = Array.isArray(pendingRows) ? pendingRows : [];
      setApprovedLeaves(normalizedApproved);
      setPendingLeaves(normalizedPending);
      const syncAt = new Date();
      setLastSync(syncAt);
      writeDashboardCache({ savedAt: syncAt.toISOString(), employees: normalizedEmployees, devices: normalizedDevices, deviceTotalCount: totalDevices, transactions: normalizedTransactions, approvedLeaves: normalizedApproved, pendingLeaves: normalizedPending, });
    } catch (err) { setError(err instanceof Error ? err.message : "Failed to load dashboard."); } finally { inFlightRef.current = false; if (background) setSyncing(false); else setLoading(false); }
  }, []);

  useEffect(() => {
    const cached = readDashboardCache();
    if (cached) {
      setEmployees(cached.employees); setDevices(cached.devices); setDeviceTotalCount(cached.deviceTotalCount ?? cached.devices.length);
      setTransactions(cached.transactions); setApprovedLeaves(cached.approvedLeaves); setPendingLeaves(cached.pendingLeaves);
      const parsedSync = new Date(cached.savedAt); if (!Number.isNaN(parsedSync.getTime())) setLastSync(parsedSync);
      setLoading(false); void loadDashboard(true);
    } else { void loadDashboard(false); }
    const handle = window.setInterval(() => void loadDashboard(true), 30000);
    return () => window.clearInterval(handle);
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
    const today = new Date(); const todayStart = new Date(today.getFullYear(), today.getMonth(), today.getDate());
    return activeEmployees.map((employee): UpcomingBirthdayRow | null => {
        const parts = parseBirthdayMonthDay(employee.hireDate); if (!parts) return null;
        const nextDate = nextBirthdayDate(parts.month, parts.day, todayStart);
        const daysAway = Math.max(0, Math.round((nextDate.getTime() - todayStart.getTime()) / (24 * 60 * 60 * 1000)));
        return { id: employee.id, employeeCode: employee.employeeCode, fullName: employee.fullName, department: employee.department, nextDate, daysAway, };
      }).filter((row): row is UpcomingBirthdayRow => row !== null).sort((a, b) => (a.daysAway !== b.daysAway) ? a.daysAway - b.daysAway : a.fullName.localeCompare(b.fullName)).slice(0, 5);
  }, [activeEmployees, nowMs]);
  
  const todaysBirthdays = useMemo(() => upcomingBirthdays.filter((row) => row.daysAway === 0), [upcomingBirthdays]);
  const txByDay = useMemo(() => {
    const map = new Map<string, TransactionRow[]>();
    for (const row of transactions) { const event = parseEventTime(row); if (!event) continue; const key = toInputDate(event); map.set(key, [...(map.get(key) ?? []), row]); }
    return map;
  }, [transactions]);
  const todayTransactions = useMemo(() => txByDay.get(todayKey) ?? [], [txByDay, todayKey]);
  const uniqueTodayCodes = useMemo(() => new Set(todayTransactions.map((r) => (r.employeeCode || "").trim()).filter((x) => x.length > 0)), [todayTransactions]);
  const uniquePunches = uniqueTodayCodes.size;
  const totalPunches = todayTransactions.length;
  const onLeaveToday = useMemo(() => {
    const ids = new Set<string>();
    for (const row of approvedLeaves) { if (!row.fromDate || !row.toDate) continue; if (row.fromDate <= todayKey && row.toDate >= todayKey) ids.add(String(row.personnelId ?? row.id)); }
    return ids.size;
  }, [approvedLeaves, todayKey]);
  const pendingLeaveCount = pendingLeaves.length;
  const absentCount = Math.max(0, totalEmployees - uniquePunches - onLeaveToday);

  const timeWindow = useMemo(() => {
    const shifts = readShiftDefinitions(); const timetables = readTimetableDefinitions(shifts);
    const defaultTimetable = timetables.find((x) => x.id === "default-weekly" && x.isActive) ?? timetables.find((x) => x.isActive) ?? timetables[0] ?? null;
    const dayOfWeek = new Date().getDay();
    const currentDay = defaultTimetable?.days.find((x) => x.dayOfWeek === dayOfWeek) ?? defaultTimetable?.days.find((x) => !x.isWeeklyOff) ?? null;
    const lateCutoffLabel = currentDay?.checkInEnd ?? "10:15";
    const checkOutStartLabel = currentDay?.checkOutStart ?? "16:45";
    return { lateCutoffMinutes: parseTimeToMinutes(lateCutoffLabel), lateCutoffLabel, checkOutStartMinutes: parseTimeToMinutes(checkOutStartLabel), checkOutStartLabel, };
  }, [nowMs]);

  const lateCutoffMinutes = timeWindow.lateCutoffMinutes;
  const lateEntries = useMemo(() => firstPunches(todayTransactions).map((row) => { const minutes = row.time.getHours() * 60 + row.time.getMinutes(); return { name: row.employeeName, delay: Math.max(0, minutes - lateCutoffMinutes) }; }).filter((x) => x.delay > 0).sort((a, b) => b.delay - a.delay), [lateCutoffMinutes, todayTransactions]);
  const topLate = lateEntries.slice(0, 2).map((x) => ({ name: x.name, delay: `${x.delay}m late` }));
  const earlyOutEntries = useMemo(() => { const cutoffMinutes = timeWindow.checkOutStartMinutes; return lastPunches(todayTransactions).map((row) => { const minutes = row.time.getHours() * 60 + row.time.getMinutes(); return { name: row.employeeName, earlyBy: Math.max(0, cutoffMinutes - minutes) }; }).filter((x) => x.earlyBy > 0).sort((a, b) => b.earlyBy - a.earlyBy); }, [timeWindow.checkOutStartMinutes, todayTransactions]);
  const earlyLeavers = earlyOutEntries.slice(0, 2).map((x) => ({ name: x.name, earlyBy: `${x.earlyBy}m early` }));

  const trendRows = useMemo(() => {
    const rows: TrendRow[] = []; const base = new Date();
    for (let i = 6; i >= 0; i -= 1) {
      const dayDate = new Date(base); dayDate.setDate(base.getDate() - i); const dayKey = toInputDate(dayDate); const dayTx = txByDay.get(dayKey) ?? [];
      const presentCount = new Set(dayTx.map((x) => (x.employeeCode || "").trim()).filter((x) => x.length > 0)).size;
      const lateCount = firstPunches(dayTx).filter((item) => (item.time.getHours() * 60 + item.time.getMinutes()) > lateCutoffMinutes).length;
      const leaveCount = new Set(approvedLeaves.filter((x) => x.fromDate <= dayKey && x.toDate >= dayKey).map((x) => String(x.personnelId ?? x.id))).size;
      rows.push({ day: format(dayDate, "EEE"), present: presentCount, absent: Math.max(0, totalEmployees - presentCount - leaveCount), late: lateCount, });
    }
    return rows;
  }, [approvedLeaves, lateCutoffMinutes, totalEmployees, txByDay]);

  const employeeDeptByCode = useMemo(() => new Map<string, string>(activeEmployees.map((e): [string, string] => [(e.employeeCode || "").trim(), normalizeDepartmentName(e.department)]).filter((entry) => entry[0].length > 0)), [activeEmployees]);
  const deptRows = useMemo(() => {
    const totalByDept = new Map<string, number>(); for (const employee of activeEmployees) { const dept = normalizeDepartmentName(employee.department); totalByDept.set(dept, (totalByDept.get(dept) ?? 0) + 1); }
    const txDeptByCode = new Map<string, string>(); for (const row of todayTransactions) { const code = (row.employeeCode || "").trim(); if (!code || txDeptByCode.has(code)) continue; txDeptByCode.set(code, normalizeDepartmentName(row.employeeDepartment)); }
    const presentByDept = new Map<string, number>(); for (const code of uniqueTodayCodes) { const dept = employeeDeptByCode.get(code) ?? txDeptByCode.get(code) ?? DEFAULT_SETUP_DEPARTMENT; presentByDept.set(dept, (presentByDept.get(dept) ?? 0) + 1); }
    const names = Array.from(new Set([...totalByDept.keys(), ...presentByDept.keys()])).sort((a, b) => a.localeCompare(b));
    return names.map((name) => ({ name, present: presentByDept.get(name) ?? 0, total: totalByDept.get(name) ?? 0, }));
  }, [activeEmployees, employeeDeptByCode, todayTransactions, uniqueTodayCodes]);

  const onlineDevices = useMemo(() => devices.filter((d) => (typeof d.isOnline === "boolean" ? d.isOnline : String(d.state || "").toLowerCase() === "online")).length, [devices]);
  const totalDevices = Math.max(deviceTotalCount, devices.length);
  const offlineDevices = Math.max(0, totalDevices - onlineDevices);

  const stats = [
    { label: "Total Staff", value: totalEmployees, hint: "Active", icon: Users, tone: "blue" as const },
    { label: "Male / Female", value: `${maleEmployees} / ${femaleEmployees}`, hint: "Staff Count", icon: Users, tone: "sky" as const },
    { label: "Devices", value: `${onlineDevices} / ${totalDevices}`, hint: "Online Total", icon: Router, tone: "sky" as const },
    { label: "Present Today", value: uniquePunches, hint: "Checked In", icon: CheckCircle2, tone: "green" as const },
    { label: "Abs / Leave", value: `${absentCount} / ${onLeaveToday}`, hint: "Today's Status", icon: Umbrella, tone: "lime" as const },
  ];

  const liveLabel = syncAgeText(lastSync, nowMs, syncing || loading);

  useEffect(() => {
    if (todaysBirthdays.length === 0 || typeof window === "undefined") return;
    const popupKey = `${BIRTHDAY_POPUP_STORAGE_PREFIX}${todayKey}`;
    try {
      const seenRaw = window.localStorage.getItem(popupKey); const seenIds = new Set((seenRaw || "").split(",").filter((token) => token.length > 0));
      if (todaysBirthdays.some((row) => !seenIds.has(row.id))) setBirthdayPopupOpen(true);
    } catch { setBirthdayPopupOpen(true); }
  }, [todayKey, todaysBirthdays]);

  const closeBirthdayPopup = useCallback(() => {
    setBirthdayPopupOpen(false); if (typeof window === "undefined" || todaysBirthdays.length === 0) return;
    const popupKey = `${BIRTHDAY_POPUP_STORAGE_PREFIX}${todayKey}`;
    try { window.localStorage.setItem(popupKey, Array.from(new Set(todaysBirthdays.map((row) => row.id))).join(",")); } catch { /* ignore */ }
  }, [todayKey, todaysBirthdays]);

  return (
    <div className="dashboard-page w-full space-y-3">
      <section className="relative overflow-hidden rounded-2xl border border-[#cfe4ff] bg-gradient-to-br from-[#eef7ff] via-white to-[#e8f6ff] p-4 shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2.5">
            <div className="grid h-9 w-9 place-items-center rounded-xl bg-gradient-to-br from-[#1d4ed8] via-[#2563eb] to-[#0284c7] text-white"><Fingerprint className="h-4 w-4" /></div>
            <h1 className="text-lg font-bold tracking-tight text-[#0d334d]">E-Attendance</h1>
          </div>
          <div className="flex flex-wrap items-center gap-1.5">
            <button type="button" onClick={() => void loadDashboard(true)} className="rounded-full border border-[#bfdbfe] bg-white px-3 py-1.5 text-xs font-semibold text-[#1d446b] transition hover:bg-[#f0f9ff]">
              <span className="inline-flex items-center gap-1.5"><Activity className={`h-3.5 w-3.5 text-emerald-600 ${syncing || loading ? "animate-pulse" : ""}`} />{liveLabel}</span>
            </button>
            <div className="rounded-full border border-[#d3e2f2] bg-white/90 px-3 py-1.5 text-xs font-semibold text-[#1d446b]">
              <span className="inline-flex items-center gap-1.5"><CalendarDays className="h-3.5 w-3.5" />{todayLabel}</span>
            </div>
          </div>
        </div>
      </section>

      {error && <div className="rounded-lg border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-xs font-medium text-[#8d2f2f]">{error}</div>}

      <section className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5">
        {stats.map(({ label, value, hint, icon: Icon, tone }) => {
          const t = tone === "blue" ? "border-[#cfe0ff] from-white to-[#eef4ff] text-[#1d4ed8]" : tone === "sky" ? "border-[#c9ebff] from-white to-[#edf9ff] text-[#0e7490]" : tone === "green" ? "border-[#ccefd8] from-white to-[#ecfdf3] text-[#15803d]" : tone === "lime" ? "border-[#ddefc9] from-white to-[#f7fee7] text-[#4d7c0f]" : "border-[#ffd0d8] from-white to-[#fff1f2] text-[#be185d]";
          return (
            <article key={label} className={`group rounded-2xl border bg-gradient-to-b p-3 shadow-sm transition hover:-translate-y-0.5 ${t}`}>
              <div className="flex items-start justify-between gap-2">
                <p className="text-[10px] font-bold uppercase tracking-wider opacity-70">{label}</p>
                <Icon className="h-3.5 w-3.5 opacity-80" />
              </div>
              <p className="mt-1 text-2xl font-bold leading-none">{value}</p>
              <p className="mt-1 text-[10px] font-medium opacity-60">{hint}</p>
            </article>
          );
        })}
      </section>

      <section className="grid gap-3 xl:grid-cols-[1.3fr_1fr_1.3fr]">
        <article className="rounded-2xl border border-[#d8e8fa] bg-white p-3 shadow-sm">
          <h2 className="mb-3 inline-flex items-center gap-2 text-xs font-bold text-[#1b4b73] uppercase tracking-wider"><ChartLine className="h-3.5 w-3.5 text-[#3a74a6]" />Weekly Trend</h2>
          <div className="h-[180px] rounded-xl bg-white p-1">
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={trendRows} margin={{ top: 5, right: 5, left: -30, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#f1f5f9" vertical={false} />
                <XAxis dataKey="day" axisLine={false} tickLine={false} tick={{ fill: "#94a3b8", fontSize: 9 }} />
                <YAxis axisLine={false} tickLine={false} tick={{ fill: "#94a3b8", fontSize: 9 }} />
                <Tooltip contentStyle={{ borderRadius: 10, fontSize: 10, padding: "4px 8px" }} />
                <Area type="monotone" dataKey="present" stroke="#10b981" strokeWidth={2} fill="#10b981" fillOpacity={0.1} />
                <Area type="monotone" dataKey="absent" stroke="#fb7185" strokeWidth={1.5} fill="#fb7185" fillOpacity={0.05} />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </article>

        <article className="rounded-2xl border border-[#d8e8fa] bg-white p-3 shadow-sm">
          <h2 className="mb-3 inline-flex items-center gap-2 text-xs font-bold text-[#1b4b73] uppercase tracking-wider"><Users className="h-3.5 w-3.5 text-[#3a74a6]" />Departments</h2>
          <div className="space-y-0.5 text-[11px] max-h-[180px] overflow-y-auto pr-1">
            {deptRows.map((dept) => (
              <div key={dept.name} className="flex items-center justify-between border-b border-[#f1f5f9] py-1.5 hover:bg-slate-50/50">
                <span className="font-medium text-[#475569] truncate max-w-[120px]">{dept.name}</span>
                <span className="rounded bg-[#f1f5f9] px-2 py-0.5 font-bold text-[#64748b]">{dept.present} / {dept.total}</span>
              </div>
            ))}
          </div>
        </article>

        <article className="rounded-2xl border border-[#d8e8fa] bg-white p-3 shadow-sm">
          <h2 className="mb-3 inline-flex items-center gap-2 text-xs font-bold text-[#1b4b73] uppercase tracking-wider"><Clock3 className="h-3.5 w-3.5 text-[#3a74a6]" />Recent Punches</h2>
          <div className="grid gap-2 grid-cols-2">
            {[ { label: "Punches", val: totalPunches, icon: Activity, color: "text-blue-600", bg: "bg-blue-50/30" }, { label: "Unique", val: uniquePunches, icon: CheckCircle2, color: "text-emerald-600", bg: "bg-emerald-50/30" }, { label: "Top Late", val: topLate.length > 0 ? topLate[0].name : "None", icon: Clock3, color: "text-amber-600", bg: "bg-amber-50/30" }, { label: "Absent", val: absentCount, icon: AlertCircle, color: "text-rose-600", bg: "bg-rose-50/30" }, ].map((item, i) => (
              <div key={i} className={`rounded-xl p-2 border border-slate-50 ${item.bg}`}>
                <p className={`flex items-center gap-1 text-[9px] font-bold uppercase ${item.color}`}><item.icon className="h-2.5 w-2.5" />{item.label}</p>
                <p className="mt-0.5 truncate text-xs font-bold text-[#1e293b]">{item.val}</p>
              </div>
            ))}
          </div>
          <div className="mt-3 space-y-1.5">
            <h2 className="inline-flex items-center gap-2 text-xs font-bold text-[#1b4b73] uppercase tracking-wider"><CalendarDays className="h-3.5 w-3.5 text-[#3a74a6]" />Birthdays</h2>
            <div className="space-y-1 max-h-[80px] overflow-y-auto pr-1 text-[10px]">
              {upcomingBirthdays.map((row) => (
                <div key={row.id} className={`flex items-center justify-between gap-2 rounded-lg px-2 py-1 ${row.daysAway === 0 ? "bg-orange-50 text-orange-900 font-bold border border-orange-100" : "text-slate-500"}`}>
                  <span className="truncate">{row.daysAway === 0 && "🎂 "}{row.fullName}</span>
                  <span className="shrink-0 text-[9px]">{row.daysAway === 0 ? "Today" : `${row.daysAway}d`}</span>
                </div>
              ))}
            </div>
          </div>
        </article>
      </section>

      <section className="space-y-3 rounded-2xl border border-[#d3e6fb] bg-white p-4 shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-[10px] font-bold uppercase tracking-widest text-[#38658a]">Navigation</h2>
          <div className="rounded-full bg-[#f1f5f9] px-3 py-1 text-[10px] font-bold text-[#475569]">{onlineDevices} Online | {offlineDevices} Offline</div>
        </div>
        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
          {quickActions.map(({ label, href, icon: Icon }) => (
            <Link key={label} href={href} prefetch={false} className="group flex items-center gap-2 rounded-xl border border-[#bfdbfe] bg-white px-3 py-2 text-xs font-bold text-[#1e4c78] transition hover:-translate-y-0.5 hover:bg-blue-50">
              <div className="grid h-7 w-7 place-items-center rounded-lg bg-slate-50 transition group-hover:scale-110"><Icon className="h-3.5 w-3.5" /></div>
              <span className="truncate">{label}</span>
            </Link>
          ))}
        </div>
        <div className="mt-2 grid gap-2 sm:grid-cols-3 lg:grid-cols-6 border-t border-dashed border-[#d3e3f5] pt-3">
          {suggestedActions.map((action) => {
            const className = "group flex items-center gap-2 rounded-xl border border-[#bfdbfe] bg-white px-3 py-2 text-xs font-bold text-[#1e4c78] transition hover:-translate-y-0.5 hover:bg-blue-50";
            const content = (
              <>
                <div className="grid h-7 w-7 place-items-center rounded-lg bg-slate-50 transition group-hover:scale-110">
                  <action.icon className="h-3.5 w-3.5" />
                </div>
                <span className="truncate">{action.label}</span>
              </>
            );
            return 'onClick' in action 
              ? <button key={action.label} onClick={() => setActiveModal(action.onClick)} className={className}>{content}</button> 
              : <Link key={action.label} href={action.href} prefetch={false} className={className}>{content}</Link>;
          })}
        </div>
      </section>

      <section className="flex justify-end gap-4 text-[9px] font-medium text-slate-400 uppercase tracking-widest">
        <p>Sync: {liveLabel}</p>
        <p>PostgreSQL Realtime</p>
      </section>

      {activeModal === "qr" && <ActionModal type="qr" onClose={() => setActiveModal(null)} />}
      {activeModal === "leave-terminal" && <ActionModal type="leave" onClose={() => setActiveModal(null)} />}
      {activeModal === "leave" && (
        <LeaveApplicationModal 
          onClose={() => setActiveModal(null)} 
          onSuccess={() => void loadDashboard(true)}
        />
      )}
      {birthdayPopupOpen && todaysBirthdays.length > 0 && (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4 backdrop-blur-sm">
          <div className="relative w-full max-w-lg rounded-[32px] border border-[#ffd7a6] bg-white p-6 shadow-2xl">
            <div className="flex items-start justify-between gap-3">
              <div><p className="text-[10px] font-bold uppercase tracking-widest text-[#b45309]">Celebration</p><h2 className="mt-1 text-2xl font-bold text-[#7c2d12]">Happy Birthday!</h2></div>
              <button type="button" onClick={closeBirthdayPopup} className="rounded-full border border-[#f3c88d] px-4 py-1.5 text-xs font-bold text-[#8a3f18] transition hover:bg-orange-50">Close</button>
            </div>
            <div className="mt-6 space-y-3">
              {todaysBirthdays.map((row) => (
                <div key={row.id} className="rounded-2xl border border-[#ffd7a6] bg-orange-50/50 p-4">
                  <p className="text-lg font-bold text-[#7c2d12]">{row.fullName}</p>
                  <p className="text-xs text-[#8b5e34]">{row.employeeCode} | {row.department}</p>
                </div>
              ))}
            </div>
            <div className="absolute -bottom-10 -right-10 text-[8rem] opacity-10 grayscale">🎂</div>
          </div>
        </div>
      )}
    </div>
  );
}
