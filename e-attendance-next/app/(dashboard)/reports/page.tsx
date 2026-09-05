"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  BarChart3,
  CalendarDays,
  Download,
  FileText,
  Filter,
  Printer,
  RefreshCcw,
  SlidersHorizontal,
} from "lucide-react";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import * as XLSX from "xlsx";
import { resolveApiBases } from "@/lib/api-bases";
import {
  SHIFT_CALENDAR_UPDATED_EVENT,
  type HolidayItem,
  type ShiftAssignment,
  type ShiftCalendarSettings,
  type ShiftDefinition,
  type ShiftGroup,
  type ShiftPlanDefinition,
  type TimetableDayRule,
  type TimetableDefinition,
  defaultShiftDefinitions,
  defaultShiftCalendarSettings,
  parseTimeToMinutes,
  readShiftAssignments,
  readShiftCalendarSettings,
  readShiftDefinitions,
  readShiftGroups,
  readShiftPlanDefinitions,
  readTimetableDefinitions,
  resolveScheduleForDate,
  resolveShiftAssignmentsForDate,
  resolveShiftPlanForDate,
  syncShiftCalendarHolidays,
  shiftStandardMinutes,
} from "@/components/data/shift-store";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { adToBs, bsToAd, daysInBsMonth } from "@/components/date/bs-converter";
import { formatCalendarDate, formatCalendarDateTime, formatCalendarMonth } from "@/components/date/date-display";
import { BsDateField } from "@/components/date/dual-date-input";

type ReportType =
  | "daily"
  | "weekly"
  | "monthly"
  | "monthlyCheckInOut"
  | "late"
  | "absent"
  | "overtime";
type StatusFilter = "all" | "present" | "late" | "onleave" | "absent";
type ShiftFilter = string;

type EmployeeRow = {
  id: string;
  employeeCode: string;
  fullName: string;
  department: string;
  isActive: boolean;
};
type TxRow = {
  id: string;
  employeeCode: string;
  employeeName: string;
  employeeDepartment: string;
  rawTimePc?: string | null;
  punchTime?: string | null;
  statusCode?: number | null;
  verifyMode?: number | null;
  deviceSerialNumber?: string | null;
  remarks?: string | null;
};
type PersonnelEmployeeApiRow = {
  id?: string | null;
  employeeCode?: string | null;
  fullName?: string | null;
  department?: string | null;
  isActive?: boolean;
};
type HolidayApiRow = {
  id?: number | null;
  holidayDate?: string | null;
  holidayName?: string | null;
  description?: string | null;
  isRecurring?: boolean;
  isActive?: boolean;
  department?: string | null;
  timeZoneId?: string | null;
};
type DailyRow = {
  key: string;
  date: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  department: string;
  checkIn: Date | null;
  checkOut: Date | null;
  workedMinutes: number;
  overtimeMinutes: number;
  shiftName: string;
  timetableLabel: string;
  weeklyOff: boolean;
  shiftStartMinutes: number;
  shiftEndMinutes: number;
  shiftStandardMinutes: number;
  lateGraceMinutes: number;
  earlyExit: boolean;
  earlyExitMinutes: number;
  halfDay: boolean;
  exemptAbsent: boolean;
  lateInMinutes: number;
  earlyOutMinutes: number;
  holidayName: string | null;
  holidayDescription: string | null;
  leaveType: string | null;
  leaveReason: string | null;
  status: "Present" | "Late" | "Absent" | "On Leave";
  deviceName: string;
  manualRemark: string;
};

const DEFAULT_SETUP_DEPARTMENT = "Default Department";

function normalizeDepartmentName(value: string | null | undefined): string {
  const token = (value || "").trim();
  if (!token) return DEFAULT_SETUP_DEPARTMENT;
  if (token.toLowerCase() === "general") return DEFAULT_SETUP_DEPARTMENT;
  return token;
}

function normalizeHolidayRows(rows: HolidayApiRow[]): HolidayItem[] {
  return rows
    .map((row, index) => ({
      id: String(row.id ?? `holiday-${index + 1}`),
      date: (row.holidayDate || "").trim(),
      name: (row.holidayName || "").trim() || "Holiday",
      description: (row.description || "").trim(),
      isRecurring: Boolean(row.isRecurring),
      department: (row.department || "all").trim() || "all",
      timeZoneId: (row.timeZoneId || "1").trim() || "1",
      isActive: row.isActive !== false,
    }))
    .filter((row) => /^\d{4}-\d{2}-\d{2}$/.test(row.date));
}

function filterEmployeesBySearch(rows: EmployeeRow[], token: string): EmployeeRow[] {
  if (!token) return rows;
  return rows.filter((row) =>
    [row.employeeCode, row.fullName, row.department]
      .join(" ")
      .toLowerCase()
      .includes(token),
  );
}

type ExportRow = Record<string, string | number>;
type PrintableSection = {
  employeeCode: string;
  employeeName: string;
  department: string;
  reportFrom: string;
  reportTo: string;
  monthStartDate?: string | null;
  rows: ExportRow[];
};

const REPORT_TYPES: Array<{ value: ReportType; label: string }> = [
  { value: "monthlyCheckInOut", label: "Monthly Check In/Check Out" },
  { value: "daily", label: "Daily Attendance" },
  { value: "weekly", label: "Weekly Attendance" },
  { value: "monthly", label: "Monthly Attendance" },
  { value: "late", label: "Late Coming" },
  { value: "absent", label: "Absent" },
  { value: "overtime", label: "Overtime" },
];
const COLUMN_CONFIG_KEY = "pb.attendance.reports.columns.v1";
const DEFAULT_UNCHECKED_COLUMNS = new Set([
  "EmployeeID",
  "Shift",
  "Timetable",
  "Device",
]);

const PREVIEW_PAGE_SIZE = 50;
const REPORT_TRANSACTION_LIMIT = 12000;
const REPORT_MAX_DAYS = 740;

const fmtTime = (d: Date | null) =>
  d
    ? d.toLocaleTimeString([], {
        hour: "numeric",
        minute: "2-digit",
        hour12: true,
      })
    : "-";
const fmtDuration = (totalMinutes: number) => {
  const safe = Math.max(0, Math.round(totalMinutes));
  const h = Math.floor(safe / 60);
  const m = safe % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
};
const toInputDate = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
const csvEscape = (s: string) =>
  /[,"\n\r]/.test(s) ? `"${s.replace(/"/g, "\"\"")}"` : s;
const statusClass = (s: "Present" | "Late" | "Absent" | "On Leave") =>
  s === "Present"
    ? "bg-[#e7f9ef] text-[#0f8a45]"
    : s === "Late"
      ? "bg-[#fff5e8] text-[#cc7a00]"
      : s === "On Leave"
        ? "bg-[#f3e8ff] text-[#7e22ce]"
        : "bg-[#ffecec] text-[#be2f2f]";

const columnColorClass = (colName: string): string => {
  if (colName === "CheckIn" || colName === "CheckOut" || colName === "Check In" || colName === "Check Out") {
    return "bg-[#e7f4f9]"; // Light blue
  }
  if (colName === "LateIn" || colName === "EarlyOut" || colName === "Late In" || colName === "Early Out") {
    return "bg-[#fff5e8]"; // Light orange
  }
  if (colName === "Status") return "bg-[#f0f0f0]"; // Light gray
  return "";
};

function lowerBound(values: number[], target: number): number {
  let lo = 0;
  let hi = values.length;
  while (lo < hi) {
    const mid = Math.floor((lo + hi) / 2);
    if (values[mid] < target) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}

function upperBound(values: number[], target: number): number {
  let lo = 0;
  let hi = values.length;
  while (lo < hi) {
    const mid = Math.floor((lo + hi) / 2);
    if (values[mid] <= target) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}

function currentMonthStartForCalendar(calendarType: "ad" | "bs"): string {
  const now = new Date();
  if (calendarType === "ad") {
    return toInputDate(new Date(now.getFullYear(), now.getMonth(), 1));
  }
  const bs = adToBs(now);
  if (!bs) {
    return toInputDate(new Date(now.getFullYear(), now.getMonth(), 1));
  }
  const adStart = bsToAd(bs.year, bs.month, 1);
  if (!adStart) {
    return toInputDate(new Date(now.getFullYear(), now.getMonth(), 1));
  }
  return toInputDate(adStart);
}

function currentMonthEndForCalendar(calendarType: "ad" | "bs"): string {
  const now = new Date();
  if (calendarType === "ad") {
    return toInputDate(new Date(now.getFullYear(), now.getMonth() + 1, 0));
  }
  const bs = adToBs(now);
  if (!bs) {
    return toInputDate(new Date(now.getFullYear(), now.getMonth() + 1, 0));
  }
  const lastDay = daysInBsMonth(bs.year, bs.month);
  const adEnd = bsToAd(bs.year, bs.month, lastDay);
  if (!adEnd) {
    return toInputDate(new Date(now.getFullYear(), now.getMonth() + 1, 0));
  }
  return toInputDate(adEnd);
}

function rowStatusLabel(row: DailyRow): string {
  if (!row.exemptAbsent) return row.status;
  return row.holidayName ? `Holiday (${row.holidayName})` : "Weekly Off";
}

function rowRemark(row: DailyRow): string {
  const manual = row.manualRemark?.trim();
  const base = (() => {
    const holidayDetail = row.holidayDescription?.trim() || row.holidayName || "";
    if (row.exemptAbsent) return row.holidayName ? holidayDetail || "Holiday" : "Weekly Off";
    if (row.holidayName) return holidayDetail ? `Worked on Holiday - ${holidayDetail}` : "Worked on Holiday";
    if (row.weeklyOff) return "Worked on Weekly Off";
    if (row.status === "On Leave") {
      const reason = row.leaveReason?.trim();
      const type = row.leaveType?.trim();
      if (reason && type) return `${type} - ${reason}`;
      return reason || type || "On Leave";
    }
    const parts: string[] = [];
    if (row.halfDay) parts.push("Half Day");
    if (row.earlyExit) parts.push("Early Exit");
    return parts.join(", ") || "-";
  })();
  if (manual) return base && base !== "-" ? `${manual} (${base})` : manual;
  return base;
}

const escHtml = (s: string) =>
  s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");

let preferredApiBase = "";

function apiBases(): string[] {
  return resolveApiBases({ preferredBase: preferredApiBase });
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch";
  for (const base of apiBases()) {
    try {
      if (!base) continue;
      const call = async () => {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 4500);
        try {
          return await fetch(`${base}${path}`, {
            ...init,
            headers: {
              "Content-Type": "application/json",
              ...(init?.headers ?? {}),
            },
            cache: "no-store",
            signal: controller.signal,
          });
        } finally {
          clearTimeout(timeout);
        }
      };

      let r = await call();
      if (r.status === 503) {
        try {
          await fetch(`${base}/api/server/start`, {
            method: "POST",
            cache: "no-store",
          });
          r = await call();
        } catch {
          // keep original 503 failure handling below
        }
      }

      if (r.ok) {
        preferredApiBase = base;
        return (await r.json()) as T;
      }
      lastError = (await r.text()) || `Request failed (${r.status})`;
    } catch (e) {
      lastError = e instanceof Error ? e.message : lastError;
    }
  }
  throw new Error(lastError);
}

function dateRange(from: string, to: string): string[] {
  const a = new Date(`${from}T00:00:00`);
  const b = new Date(`${to}T00:00:00`);
  if (Number.isNaN(a.getTime()) || Number.isNaN(b.getTime())) return [];
  const s = a <= b ? a : b;
  const e = a <= b ? b : a;
  const out: string[] = [];
  const cur = new Date(s);
  while (cur <= e && out.length < REPORT_MAX_DAYS) {
    out.push(toInputDate(cur));
    cur.setDate(cur.getDate() + 1);
  }
  return out;
}

function weekStartDate(date: string): Date {
  const d = new Date(`${date}T00:00:00`);
  const offset = (d.getDay() + 6) % 7;
  d.setDate(d.getDate() - offset);
  d.setHours(0, 0, 0, 0);
  return d;
}

function formatTimetableLabel(
  checkInTime?: string | null,
  checkOutStart?: string | null,
  checkOutTime?: string | null,
  shiftStart?: string | null,
  shiftEnd?: string | null,
): string {
  if (checkInTime && checkOutTime) return `${checkInTime} - ${checkOutTime}`;
  if (checkInTime && checkOutStart) return `${checkInTime} - ${checkOutStart}`;
  if (shiftStart && shiftEnd) return `${shiftStart} - ${shiftEnd}`;
  return "-";
}

function resolveAssignedSchedule(
  dayStart: Date,
  shift: ShiftDefinition,
  timetableDay?: TimetableDayRule | null,
) {
  const useTimetable = Boolean(timetableDay);
  const shiftStartMinutes = useTimetable
    ? parseTimeToMinutes(timetableDay?.checkInTime ?? shift.startTime)
    : parseTimeToMinutes(shift.startTime);
  const shiftEndMinutes = useTimetable
    ? parseTimeToMinutes(timetableDay?.checkOutTime || timetableDay?.checkOutStart || shift.endTime)
    : parseTimeToMinutes(shift.endTime);
  const crossDay =
    shift.shiftType === "flexible"
      ? false
      : useTimetable
        ? Boolean(timetableDay?.crossDay) || shiftEndMinutes <= shiftStartMinutes
        : shift.nightShift || shiftEndMinutes <= shiftStartMinutes;
  const windowMinutes =
    shift.shiftType === "flexible"
      ? 24 * 60
      : crossDay
        ? shiftEndMinutes + 24 * 60 - shiftStartMinutes
        : shiftEndMinutes - shiftStartMinutes;
  const standardMinutes =
    shift.shiftType === "flexible"
      ? shiftStandardMinutes(shift)
      : useTimetable
        ? windowMinutes
        : Math.max(0, windowMinutes - Math.max(0, shift.breakMinutes));
  const lateGraceMinutes = shift.allowGrace ? Math.max(0, shift.lateGraceMinutes) : 0;
  const earlyLeaveMinutes = Math.max(0, shift.earlyLeaveMinutes);
  const startAt = new Date(dayStart.getTime() + shiftStartMinutes * 60000);
  const endAt = new Date(startAt.getTime() + windowMinutes * 60000);
  return {
    startMinutes: shiftStartMinutes,
    windowMinutes,
    standardMinutes,
    lateGraceMinutes,
    earlyLeaveMinutes,
    startAt,
    endAt,
  };
}

function defaultVisibleColumns(columns: string[]): string[] {
  const visible = columns.filter((x) => !DEFAULT_UNCHECKED_COLUMNS.has(x));
  return visible.length > 0 ? visible : columns;
}

const DAILY_COLUMN_ORDER = [
  "Date",
  "Employee ID",
  "Employee Name",
  "Department",
  "Shift",
  "Time Table",
  "Device",
  "Late In",
  "Check In",
  "Early Out",
  "Check Out",
  "Working Hours",
  "Worked Hours",
  "OT",
  "Status",
  "Remark",
] as const;

type DailyColumnName = (typeof DAILY_COLUMN_ORDER)[number];

const DAILY_INITIAL_SELECTED_COLUMNS: DailyColumnName[] = [...DAILY_COLUMN_ORDER];
const DAILY_RESET_SELECTED_COLUMNS: DailyColumnName[] = [
  "Date",
  "Employee ID",
  "Employee Name",
  "Time Table",
  "Late In",
  "Check In",
  "Early Out",
  "Check Out",
  "Working Hours",
  "Worked Hours",
  "Status",
  "Remark",
];

function sortColumnsForReport(reportType: ReportType, columns: string[]): string[] {
  if (reportType !== "daily") return columns;
  const order = new Map<string, number>(DAILY_COLUMN_ORDER.map((c, i) => [c, i]));
  return columns
    .slice()
    .sort((a, b) => (order.get(a) ?? 9999) - (order.get(b) ?? 9999) || a.localeCompare(b));
}

function initialDefaultColumnsForReport(reportType: ReportType, available: string[]): string[] {
  if (reportType !== "daily") return defaultVisibleColumns(available);
  const desired = DAILY_INITIAL_SELECTED_COLUMNS.filter((x) => available.includes(x));
  return desired.length > 0 ? desired : defaultVisibleColumns(available);
}

function resetDefaultColumnsForReport(reportType: ReportType, available: string[]): string[] {
  if (reportType !== "daily") return defaultVisibleColumns(available);
  const desired = DAILY_RESET_SELECTED_COLUMNS.filter((x) => available.includes(x));
  return desired.length > 0 ? desired : defaultVisibleColumns(available);
}

function toExcelSheetName(value: string): string {
  const cleaned = value.replace(/[:\\/?*\[\]]/g, " ").trim();
  return (cleaned || "Report").slice(0, 31);
}

function buildColumnWidths(rows: ExportRow[]): Array<{ wch: number }> {
  if (rows.length === 0) return [];
  const cols = Object.keys(rows[0]);
  return cols.map((col) => {
    let maxLen = col.length;
    for (const row of rows) {
      maxLen = Math.max(maxLen, String(row[col] ?? "").length);
    }
    return { wch: Math.min(48, Math.max(10, maxLen + 2)) };
  });
}

function openPrintable(
  title: string,
  generatedBy: string,
  generatedAtText: string,
  rows: ExportRow[],
  autoPrint: boolean,
  sections?: PrintableSection[],
): boolean {
  const w = window.open("", "_blank", "width=1200,height=900");
  if (!w) return false;
  const renderTable = (tableRows: ExportRow[]) => {
    const cols = tableRows[0] ? Object.keys(tableRows[0]) : [];
    const th = cols.map((c) => `<th>${escHtml(c)}</th>`).join("");
    const tb = tableRows
      .slice(0, 3000)
      .map(
        (r) =>
          `<tr>${cols
            .map((c) => `<td>${escHtml(String(r[c] ?? ""))}</td>`)
            .join("")}</tr>`,
      )
      .join("");
    return `<table><thead><tr>${th}</tr></thead><tbody>${tb}</tbody></table>`;
  };

  const bodyHtml =
    sections && sections.length > 0
      ? sections
          .map((section, index) => {
            const monthStartLine = section.monthStartDate
              ? `<div><strong>Month Start Date:</strong> ${escHtml(section.monthStartDate)}</div>`
              : "";
            return `<section class="sec ${index > 0 ? "sec-break" : ""}">
              <div class="sec-meta">
                <div><strong>Employee Code:</strong> ${escHtml(section.employeeCode)}</div>
                <div><strong>Employee Name:</strong> ${escHtml(section.employeeName)}</div>
                <div><strong>Department:</strong> ${escHtml(section.department)}</div>
                ${monthStartLine}
                <div><strong>Report From:</strong> ${escHtml(section.reportFrom)}</div>
                <div><strong>Report To:</strong> ${escHtml(section.reportTo)}</div>
              </div>
              ${renderTable(section.rows)}
            </section>`;
          })
          .join("")
      : renderTable(rows);

  w.document.write(`<!doctype html><html><head><meta charset="utf-8"/><title>${escHtml(title)}</title>
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <style>
  @page{size:A4 landscape;margin:8mm}
  body{font-family:Segoe UI,Arial,sans-serif;margin:16px;color:#153a5b}
  .h{display:flex;justify-content:space-between;align-items:center;margin-bottom:10px}
  .logo{width:42px;height:42px;border-radius:10px;background:#2f6e9c;color:#fff;display:grid;place-items:center;font-weight:700}
  .m{font-size:12px;color:#406481}
  table{width:100%;border-collapse:collapse;font-size:11px;table-layout:fixed}
  th,td{border:1px solid #d4e2ef;padding:6px;text-align:left;word-break:break-word;white-space:normal}
  th{background:#edf5ff;color:#23527d}
  .sec-meta{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px;margin:10px 0 8px;font-size:11px;color:#355a7a}
  .sec-break{page-break-before:always}

  .toolbar{position:sticky;top:0;z-index:999;display:flex;gap:6px;align-items:center;justify-content:flex-end;background:#f2f8ff;border:1px solid #d8e6f4;border-radius:10px;padding:8px 10px;margin-bottom:10px}
  .btn{font-size:12px;border:1px solid #bfd4ea;background:#fff;color:#1f5c8f;border-radius:8px;padding:6px 10px;cursor:pointer}
  .btn:hover{background:#fbfdff}
  .pct{min-width:54px;text-align:center;font-size:12px;color:#355a7a}

  #zoomRoot{transform-origin:top left}
  @media print{.toolbar{display:none !important}#zoomRoot{transform:none !important;width:auto !important}}
  </style></head><body>
  <div class="toolbar">
    <button class="btn" type="button" id="zmOut">-</button>
    <button class="btn" type="button" id="zmReset">100%</button>
    <button class="btn" type="button" id="zmIn">+</button>
    <span class="pct" id="zmPct">100%</span>
  </div>
  <div id="zoomRoot">
  <div class="h"><div style="display:flex;gap:10px;align-items:center"><div class="logo">EA</div><div><div style="font-weight:700;font-size:18px">E-Attendance</div><div>${escHtml(title)}</div></div></div>
  <div class="m"><div>Generated: ${escHtml(generatedAtText)}</div><div>Generated By: ${escHtml(generatedBy)}</div></div></div>
  ${bodyHtml}
  </div>
  <script>
    (function(){
      var zoom = 1;
      var root = document.getElementById('zoomRoot');
      var pct = document.getElementById('zmPct');
      function clamp(v){ return Math.max(0.5, Math.min(2.0, v)); }
      function apply(){
        zoom = clamp(zoom);
        root.style.transform = 'scale(' + zoom.toFixed(2) + ')';
        root.style.width = (100/zoom).toFixed(2) + '%';
        var p = Math.round(zoom*100) + '%';
        pct.textContent = p;
      }
      document.getElementById('zmOut').addEventListener('click', function(){ zoom -= 0.1; apply(); });
      document.getElementById('zmIn').addEventListener('click', function(){ zoom += 0.1; apply(); });
      document.getElementById('zmReset').addEventListener('click', function(){ zoom = 1; apply(); });
      apply();
    })();
  </script>
  </body></html>`);
  w.document.close();
  if (autoPrint) {
    setTimeout(() => {
      w.focus();
      w.print();
    }, 250);
  }
  return true;
}

export default function ReportsPage() {
  const { calendarType } = useCalendarPreference();

  const defaultFromDate = useMemo(() => currentMonthStartForCalendar(calendarType), [calendarType]);
  const defaultToDate = useMemo(() => currentMonthEndForCalendar(calendarType), [calendarType]);

  const [reportType, setReportType] = useState<ReportType>("daily");
  const [fromDate, setFromDate] = useState(defaultFromDate);
  const [toDate, setToDate] = useState(defaultToDate);
  const [department, setDepartment] = useState("all");
  const [employeeId, setEmployeeId] = useState("all");
  const [status, setStatus] = useState<StatusFilter>("all");
  const [shift, setShift] = useState<ShiftFilter>("assigned");
  const [search, setSearch] = useState("");

  const [employees, setEmployees] = useState<EmployeeRow[]>([]);
  const [transactions, setTransactions] = useState<TxRow[]>([]);
  const [shiftDefinitions, setShiftDefinitions] = useState<ShiftDefinition[]>(
    defaultShiftDefinitions(),
  );
  const [shiftAssignments, setShiftAssignments] = useState<ShiftAssignment[]>([]);
  const [shiftGroups, setShiftGroups] = useState<ShiftGroup[]>([]);
  const [timetables, setTimetables] = useState<TimetableDefinition[]>([]);
  const [shiftPlans, setShiftPlans] = useState<ShiftPlanDefinition[]>([]);
  const [leaves, setLeaves] = useState<any[]>([]);
  const [shiftCalendar, setShiftCalendar] = useState<ShiftCalendarSettings>(
    defaultShiftCalendarSettings(),
  );

  const [generatedAt, setGeneratedAt] = useState<Date | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [warning, setWarning] = useState<string | null>(null);
  const [monthlyPreviewEmployeeIndex, setMonthlyPreviewEmployeeIndex] = useState(0);
  const [previewPage, setPreviewPage] = useState(1);
  const [sortField, setSortField] = useState<string>("");
  const [sortAsc, setSortAsc] = useState<boolean>(true);

  const handleSort = (field: string) => {
    if (sortField === field) {
      setSortAsc((prev) => !prev);
    } else {
      setSortField(field);
      setSortAsc(true);
    }
  };
  const [selectedColumnsByType, setSelectedColumnsByType] = useState<
    Partial<Record<ReportType, string[]>>
  >({});

  const lastDefaultRangeRef = useRef({ start: defaultFromDate, end: defaultToDate });
  useEffect(() => {
    const last = lastDefaultRangeRef.current;
    const isDefaultRange = fromDate === last.start && toDate === last.end;
    if (isDefaultRange && (fromDate !== defaultFromDate || toDate !== defaultToDate)) {
      setFromDate(defaultFromDate);
      setToDate(defaultToDate);
    }
    lastDefaultRangeRef.current = { start: defaultFromDate, end: defaultToDate };
  }, [defaultFromDate, defaultToDate, fromDate, toDate]);

  const generatedBy = "Admin User";
  const shiftOptions = useMemo(() => {
    const rows = shiftDefinitions
      .filter((x) => x.isActive)
      .slice()
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
    return [{ value: "assigned", label: "Assigned Schedule" }].concat(
      rows.map((x) => ({
        value: x.id,
        label: `${x.name} (${x.startTime}-${x.endTime})`,
      })),
    );
  }, [shiftDefinitions]);

  useEffect(() => {
    if (shift === "assigned") return;
    const exists = shiftDefinitions.some((x) => x.isActive && x.id === shift);
    if (!exists) setShift("assigned");
  }, [shift, shiftDefinitions]);

  const [allDepartments, setAllDepartments] = useState<string[]>([]);

  useEffect(() => {
    void (async () => {
      try {
        const [empData, deptData] = await Promise.all([
          requestJson<unknown>("/api/personnel/employees?status=active").catch(() => []),
          requestJson<unknown>("/api/personnel/departments").catch(() => []),
        ]);
        const unwrap = <T,>(payload: unknown): T[] => {
          if (Array.isArray(payload)) return payload as T[];
          const items = (payload as { items?: unknown })?.items;
          return Array.isArray(items) ? (items as T[]) : [];
        };
        const empRows = unwrap<PersonnelEmployeeApiRow>(empData)
          .map((row) => {
            const employeeCode = (row.employeeCode || "").trim();
            if (!employeeCode) return null;
            return {
              id: (row.id || employeeCode).trim(),
              employeeCode,
              fullName: (row.fullName || employeeCode).trim(),
              department: normalizeDepartmentName(row.department),
              isActive: row.isActive !== false,
            } satisfies EmployeeRow;
          })
          .filter((x): x is EmployeeRow => x !== null);
        if (empRows.length > 0) setEmployees(empRows);

        const deptRows = unwrap<{ name?: string; code?: string }>(deptData)
          .map((d) => normalizeDepartmentName(d.name || d.code || ""))
          .filter((n) => n.length > 0);
        if (deptRows.length > 0) setAllDepartments(deptRows);
      } catch {
        // Fall back to employee department list if personnel endpoints fail
      }
    })();
  }, []);

  const empById = useMemo(() => new Map(employees.map((e) => [e.id, e])), [employees]);

  const deptOptions = useMemo(
    () =>
      Array.from(
        new Set([
          ...allDepartments,
          ...employees.map((e) => e.department).filter((x) => x && x.length > 0),
        ]),
      ).sort((a, b) => a.localeCompare(b)),
    [allDepartments, employees],
  );
  const days = useMemo(() => dateRange(fromDate, toDate), [fromDate, toDate]);
  const searchedTransactionCodes = useMemo(() => {
    const token = search.trim();
    if (!token) return new Set<string>();
    return new Set(
      transactions
        .map((row) => (row.employeeCode || "").trim())
        .filter((row) => row.length > 0),
    );
  }, [transactions, search]);

  const selectedEmployees = useMemo(
    () => {
      const token = search.trim().toLowerCase();
      const baseRows = employees
        .filter((e) => e.isActive)
        .filter(
          (e) =>
            (department === "all" || e.department === department) &&
            (employeeId === "all" || e.id === employeeId),
        );
      if (!token) return baseRows;
      const employeeMatches = filterEmployeesBySearch(baseRows, token);
      const deviceMatches = baseRows.filter((row) => searchedTransactionCodes.has(row.employeeCode));
      return Array.from(new Map([...employeeMatches, ...deviceMatches].map((row) => [row.id, row])).values());
    },
    [employees, department, employeeId, search, searchedTransactionCodes],
  );

  const filteredTransactions = useMemo(() => {
    const token = search.trim().toLowerCase();
    const selectedEmp = employeeId !== "all"
      ? (empById.get(employeeId) ?? employees.find(e => e.id === employeeId || e.employeeCode === employeeId))
      : null;
    const selectedCode = selectedEmp?.employeeCode?.trim().toLowerCase() ?? "";

    return transactions.filter((r) => {
      const emp = empById.get(r.employeeCode) || employees.find(e => e.employeeCode === r.employeeCode);
      const effectiveDept = (r.employeeDepartment?.trim() || emp?.department?.trim() || "").toLowerCase();
      const effectiveEmpCode = (r.employeeCode || "").trim().toLowerCase();

      if (department !== "all" && effectiveDept && effectiveDept !== department.trim().toLowerCase()) {
        return false;
      }
      if (selectedCode && effectiveEmpCode !== selectedCode) {
        return false;
      }
      if (!token) return true;
      const haystack = [
        r.employeeCode,
        r.employeeName || emp?.fullName || "",
        effectiveDept,
        r.deviceSerialNumber || "",
        r.verifyMode || "",
      ].join(" ").toLowerCase();

      return haystack.includes(token);
    });
  }, [transactions, search, department, employeeId, empById, employees]);

  const dailyRows = useMemo(() => {
    const fallbackShift =
      shiftDefinitions.find((x) => x.isActive) ??
      shiftDefinitions[0] ??
      defaultShiftDefinitions()[0];

    const txByEmployee = new Map<
      string,
      { items: Array<{ row: TxRow; ts: number }>; times: number[] }
    >();
    for (const row of filteredTransactions) {
      const code = (row.employeeCode || "").trim();
      if (!code) continue;
      const rawTime = row.punchTime ?? row.rawTimePc ?? "";
      const parsed = new Date(rawTime);
      const ts = parsed.getTime();
      if (Number.isNaN(ts)) continue;
      const bucket = txByEmployee.get(code) ?? { items: [], times: [] };
      bucket.items.push({ row, ts });
      txByEmployee.set(code, bucket);
    }
    for (const bucket of txByEmployee.values()) {
      bucket.items.sort((a, b) => a.ts - b.ts);
      bucket.times = bucket.items.map((item) => item.ts);
    }

    const out: DailyRow[] = [];
    for (const employee of selectedEmployees) {
      const employeeBucket = txByEmployee.get(employee.employeeCode);
      const employeePunches = employeeBucket?.items ?? [];
      const employeeTimes = employeeBucket?.times ?? [];
      
      // The /api/leave response's Guid field is "employeeId" (personnelId doesn't exist on it —
      // only personnelCode/personnelName/personnelArea/personnelDepartment, which are resolved
      // display strings, not the id). Match on employeeId primarily, personnelCode as a fallback.
      const employeeLeaves = leaves.filter(l =>
        String(l.employeeId) === employee.id ||
        String(l.personnelCode) === employee.employeeCode
      );

      for (const date of days) {
        const key = `${employee.id}|${date}`;
        
        const activeLeave = employeeLeaves.find(l => {
          const from = l.fromDate?.split("T")[0] || l.fromDate;
          const to = l.toDate?.split("T")[0] || l.toDate;
          return date >= from && date <= to;
        });

        const schedule = resolveScheduleForDate(
          employee.employeeCode,
          date,
          shiftDefinitions,
          shiftAssignments,
          timetables,
          employee.department,
          shiftCalendar,
          shiftPlans,
          shiftGroups,
        );
        const holiday = schedule.holiday;
        const weeklyOff = schedule.weeklyOff;
        const timetableDay = schedule.timetableDay;
        const useTimetableWindow = Boolean(timetableDay && !timetableDay.isWeeklyOff);
        const strictShiftRules = !holiday && !weeklyOff;
        const dayStart = new Date(`${date}T00:00:00`);
        const selectedShift =
          shift === "assigned" ? null : shiftDefinitions.find((x) => x.id === shift) ?? null;
        if (shift !== "assigned" && !selectedShift) continue;

        const matchedAssignments = resolveShiftAssignmentsForDate(
          employee.employeeCode,
          date,
          shiftDefinitions,
          shiftAssignments,
          employee.department,
          shiftGroups,
        );
        const assignedShifts = matchedAssignments
          .map((row) => shiftDefinitions.find((x) => x.id === row.shiftId))
          .filter((row): row is ShiftDefinition => Boolean(row));
        const scheduleShift = schedule.shift ?? fallbackShift;
        const shiftsForDay = selectedShift
          ? [selectedShift]
          : assignedShifts.length > 0
            ? assignedShifts
            : [scheduleShift];
        const activeShifts = shiftsForDay.filter((row) => row.isActive);
        if (activeShifts.length === 0) {
          activeShifts.push(scheduleShift ?? fallbackShift);
        }

        const segments = activeShifts
          .map((shiftRow) => {
            const assignedSchedule = resolveAssignedSchedule(
              dayStart,
              shiftRow,
              useTimetableWindow ? timetableDay : null,
            );
            const isOffDay = weeklyOff || Boolean(holiday);
            const windowStart =
              shiftRow.shiftType === "flexible" || isOffDay
                ? dayStart
                : new Date(assignedSchedule.startAt.getTime() - 4 * 60 * 60000);
            const windowEnd =
              shiftRow.shiftType === "flexible" || isOffDay
                ? new Date(dayStart.getTime() + 24 * 60 * 60000)
                : new Date(assignedSchedule.endAt.getTime() + 6 * 60 * 60000);
            return { shift: shiftRow, assignedSchedule, windowStart, windowEnd };
          })
          .sort((a, b) => a.assignedSchedule.startAt.getTime() - b.assignedSchedule.startAt.getTime());

        let combinedCheckIn: Date | null = null;
        let combinedCheckOut: Date | null = null;
        let workedMinutesTotal = 0;
        let spanMinutesTotal = 0;
        let overtimeMinutesTotal = 0;
        let standardMinutesTotal = 0;
        let minWorkMinutesTotal = 0;
        let anyLate = false;
        let anyEarlyExit = false;
        let anyPunch = false;
        let usedUntilTs = Number.NEGATIVE_INFINITY;
        let shiftStartMinutes = 0;
        let lateGraceMinutes = 0;
        let earlyLeaveMinutes = 0;
        let lastDeviceName = "-";
        const dayRemarks: string[] = [];

        for (const segment of segments) {
          const { shift: segmentShift, assignedSchedule, windowStart, windowEnd } = segment;
          if (!anyPunch) {
            shiftStartMinutes = assignedSchedule.startMinutes;
            lateGraceMinutes = assignedSchedule.lateGraceMinutes;
            earlyLeaveMinutes = assignedSchedule.earlyLeaveMinutes;
          }

          const windowStartTs = windowStart.getTime();
          const windowEndTs = windowEnd.getTime();
          const startIndex = lowerBound(employeeTimes, windowStartTs);
          const endIndex = upperBound(employeeTimes, windowEndTs) - 1;
          let slice =
            startIndex <= endIndex && startIndex >= 0
              ? employeePunches.slice(startIndex, endIndex + 1)
              : [];
          if (Number.isFinite(usedUntilTs)) {
            slice = slice.filter((item) => item.ts > usedUntilTs);
          }

          const checkIn = slice[0] ? new Date(slice[0].ts) : null;
          const checkOut =
            slice.length > 1
              ? new Date(slice[slice.length - 1].ts)
              : checkIn;

          if (checkIn && (!combinedCheckIn || checkIn.getTime() < combinedCheckIn.getTime())) {
            combinedCheckIn = checkIn;
          }
          if (checkOut && (!combinedCheckOut || checkOut.getTime() > combinedCheckOut.getTime())) {
            combinedCheckOut = checkOut;
          }
          if (checkOut) {
            usedUntilTs = Math.max(usedUntilTs, checkOut.getTime());
          }

          const totalSpanMinutes =
            checkIn && checkOut
              ? Math.max(0, Math.round((checkOut.getTime() - checkIn.getTime()) / 60000))
              : 0;
          spanMinutesTotal += totalSpanMinutes;

          let breakFromPunches = 0;
          let breakStart: Date | null = null;
          for (const item of slice) {
            const punchTime = new Date(item.ts);
            const remarkText = (item.row.remarks ?? "").trim();
            if (remarkText && !dayRemarks.includes(remarkText)) {
              dayRemarks.push(remarkText);
            }
            if (item.row.statusCode === 4 && !breakStart) {
              breakStart = punchTime;
              continue;
            }
            if (item.row.statusCode === 5 && breakStart) {
              breakFromPunches += Math.max(
                0,
                Math.round((punchTime.getTime() - breakStart.getTime()) / 60000),
              );
              breakStart = null;
            }
          }
          if (breakStart && checkOut) {
            breakFromPunches += Math.max(
              0,
              Math.round((checkOut.getTime() - breakStart.getTime()) / 60000),
            );
          }
          const isOffDay = weeklyOff || Boolean(holiday);
          const breakMinutes =
            breakFromPunches > 0
              ? breakFromPunches
              : isOffDay || useTimetableWindow
                ? 0
                : totalSpanMinutes > segmentShift.breakMinutes
                  ? Math.max(0, segmentShift.breakMinutes)
                  : 0;
          const workedMinutes = Math.max(0, totalSpanMinutes - breakMinutes);

          standardMinutesTotal += strictShiftRules ? assignedSchedule.standardMinutes : 0;
          minWorkMinutesTotal += Math.max(0, segmentShift.minWorkMinutes);
          workedMinutesTotal += workedMinutes;

          const lateBaseline = new Date(assignedSchedule.startAt.getTime() + assignedSchedule.lateGraceMinutes * 60000);
          const late =
            strictShiftRules &&
            Boolean(checkIn) &&
            segmentShift.shiftType !== "flexible" &&
            checkIn!.getTime() > lateBaseline.getTime();
          if (late) {
            anyLate = true;
          }

          const earlyExitBaseline = new Date(assignedSchedule.endAt.getTime() - assignedSchedule.earlyLeaveMinutes * 60000);
          const earlyExit =
            strictShiftRules &&
            Boolean(checkOut) &&
            segmentShift.shiftType !== "flexible" &&
            checkOut!.getTime() < earlyExitBaseline.getTime();
          if (earlyExit) {
            anyEarlyExit = true;
          }

          const assignedPlan = resolveShiftPlanForDate(
            employee.employeeCode,
            date,
            shiftPlans,
            shiftAssignments,
            employee.department,
            shiftGroups,
          );
          const planOtEnabled =
            assignedPlan?.enableOvertime !== undefined
              ? assignedPlan.enableOvertime
              : segmentShift.enableOvertime;
          const planOtAfter =
            assignedPlan?.overtimeAfterMinutes !== undefined
              ? assignedPlan.overtimeAfterMinutes
              : segmentShift.overtimeAfterMinutes;
          const overtimeThreshold =
            assignedSchedule.standardMinutes + Math.max(0, planOtAfter);
          const segmentOvertime =
            shiftCalendar.overtimeEnabled && planOtEnabled && workedMinutes > overtimeThreshold
              ? workedMinutes - overtimeThreshold
              : 0;
          overtimeMinutesTotal += segmentOvertime;

          if (checkIn) {
            anyPunch = true;
            const lastDevice = slice[slice.length - 1]?.row.deviceSerialNumber;
            if (lastDevice) {
              lastDeviceName = lastDevice;
            }
          }
        }

        if ((weeklyOff || Boolean(holiday)) && anyPunch) {
          overtimeMinutesTotal = Math.max(overtimeMinutesTotal, workedMinutesTotal);
        }

        const exemptAbsent = !anyPunch && (Boolean(holiday) || weeklyOff || Boolean(activeLeave));
        const rowStatus: "Present" | "Late" | "Absent" | "On Leave" = activeLeave
          ? "On Leave"
          : anyPunch
            ? (strictShiftRules && anyLate ? "Late" : "Present")
            : "Absent";
        const halfDayBasisMinutes = useTimetableWindow ? spanMinutesTotal : workedMinutesTotal;
        const halfDay = strictShiftRules && anyPunch && halfDayBasisMinutes < minWorkMinutesTotal;
        const primaryShift = activeShifts[0] ?? fallbackShift;
        const useTimetableLabel = useTimetableWindow;
        const timetableLabel =
          activeShifts.length > 1
            ? "Multiple Shifts"
            : formatTimetableLabel(
                useTimetableLabel ? timetableDay?.checkInTime : undefined,
                useTimetableLabel ? timetableDay?.checkOutStart : undefined,
                useTimetableLabel ? timetableDay?.checkOutTime : undefined,
                primaryShift.startTime,
                primaryShift.endTime,
              );

        const primaryEndMinutes = activeShifts[0]
          ? parseTimeToMinutes(activeShifts[0].endTime)
          : 1020;
        const dayBaseTs = new Date(`${date}T00:00:00`).getTime();
        const shiftStartTs = dayBaseTs + shiftStartMinutes * 60000;
        const shiftEndTs = dayBaseTs + primaryEndMinutes * 60000;
        const lateInMin =
          combinedCheckIn && strictShiftRules
            ? Math.max(0, Math.round((combinedCheckIn.getTime() - shiftStartTs - lateGraceMinutes * 60000) / 60000))
            : 0;
        const earlyOutMin =
          combinedCheckOut && strictShiftRules
            ? Math.max(0, Math.round((shiftEndTs - combinedCheckOut.getTime()) / 60000))
            : 0;

        out.push({
          key,
          date,
          employeeId: employee.id,
          employeeCode: employee.employeeCode,
          employeeName: employee.fullName,
          department: normalizeDepartmentName(employee.department),
          checkIn: combinedCheckIn,
          checkOut: combinedCheckOut,
          workedMinutes: workedMinutesTotal,
          overtimeMinutes: overtimeMinutesTotal,
          shiftName: activeShifts.map((x) => x.name).join(" + "),
          timetableLabel,
          weeklyOff,
          shiftStartMinutes,
          shiftEndMinutes: primaryEndMinutes,
          shiftStandardMinutes: standardMinutesTotal,
          lateGraceMinutes,
          earlyExit: anyEarlyExit,
          earlyExitMinutes: earlyOutMin,
          halfDay,
          exemptAbsent,
          lateInMinutes: lateInMin,
          earlyOutMinutes: earlyOutMin,
          holidayName: holiday?.name ?? null,
          holidayDescription: holiday?.description ?? null,
          leaveType: activeLeave?.leaveType ?? null,
          leaveReason: activeLeave?.reason ?? null,
          status: rowStatus,
          deviceName: lastDeviceName,
          manualRemark: dayRemarks.join("; "),
        } satisfies DailyRow);
      }
    }

    if (status === "all") return out;
    const wanted: DailyRow["status"] = status === "present" ? "Present" : status === "late" ? "Late" : (status === "onleave" ? "On Leave" : "Absent");
    return out.filter((r) => {
      if (r.status !== (wanted as any)) return false;
      if (wanted === "Absent") return !r.exemptAbsent;
      return true;
    });
  }, [
    filteredTransactions,
    selectedEmployees,
    days,
    status,
    shift,
    shiftDefinitions,
    shiftAssignments,
    shiftGroups,
    shiftCalendar,
    timetables,
    shiftPlans,
  ]);

  const monthlyRows = useMemo(() => {
    type Monthly = {
      month: string;
      employeeCode: string;
      employeeName: string;
      department: string;
      present: number;
      absent: number;
      late: number;
      assignedMinutes: number;
      workedMinutes: number;
      overtimeMinutes: number;
    };
    const map = new Map<string, Monthly>();

    for (const r of dailyRows) {
      const month = r.date.slice(0, 7);
      const key = `${r.employeeId}|${month}`;
      const item = map.get(key) ?? {
        month,
        employeeCode: r.employeeCode,
        employeeName: r.employeeName,
        department: r.department,
        present: 0,
        absent: 0,
        late: 0,
        assignedMinutes: 0,
        workedMinutes: 0,
        overtimeMinutes: 0,
      };
      if (r.status === "Present") item.present += 1;
      if (r.status === "Late") item.late += 1;
      if (r.status === "Absent" && !r.exemptAbsent) item.absent += 1;
      item.assignedMinutes += r.shiftStandardMinutes;
      item.workedMinutes += r.workedMinutes;
      item.overtimeMinutes += r.overtimeMinutes;
      map.set(key, item);
    }

    return Array.from(map.values()).sort((a, b) =>
      a.month === b.month ? a.employeeCode.localeCompare(b.employeeCode) : b.month.localeCompare(a.month),
    );
  }, [dailyRows]);

  const weeklyRows = useMemo(() => {
    type Weekly = {
      weekStart: string;
      weekEnd: string;
      employeeCode: string;
      employeeName: string;
      department: string;
      present: number;
      absent: number;
      late: number;
      assignedMinutes: number;
      workedMinutes: number;
      overtimeMinutes: number;
    };
    const map = new Map<string, Weekly>();

    for (const r of dailyRows) {
      const start = weekStartDate(r.date);
      const end = new Date(start);
      end.setDate(end.getDate() + 6);
      const weekStart = toInputDate(start);
      const weekEnd = toInputDate(end);
      const key = `${r.employeeId}|${weekStart}`;
      const item = map.get(key) ?? {
        weekStart,
        weekEnd,
        employeeCode: r.employeeCode,
        employeeName: r.employeeName,
        department: r.department,
        present: 0,
        absent: 0,
        late: 0,
        assignedMinutes: 0,
        workedMinutes: 0,
        overtimeMinutes: 0,
      };

      if (r.status === "Present") item.present += 1;
      if (r.status === "Late") item.late += 1;
      if (r.status === "Absent" && !r.exemptAbsent) item.absent += 1;
      item.assignedMinutes += r.shiftStandardMinutes;
      item.workedMinutes += r.workedMinutes;
      item.overtimeMinutes += r.overtimeMinutes;
      map.set(key, item);
    }

    return Array.from(map.values()).sort((a, b) =>
      a.weekStart === b.weekStart
        ? a.employeeCode.localeCompare(b.employeeCode)
        : b.weekStart.localeCompare(a.weekStart),
    );
  }, [dailyRows]);

  const lateRows = useMemo(
    () =>
      dailyRows
        .filter((r) => r.status === "Late" && r.checkIn)
        .map((r) => {
          const s = new Date(`${r.date}T00:00:00`);
          s.setMinutes(r.shiftStartMinutes + r.lateGraceMinutes);
          const lateMinutes = r.checkIn
            ? Math.max(0, Math.round((r.checkIn.getTime() - s.getTime()) / 60000))
            : 0;
          return { ...r, lateMinutes };
        }),
    [dailyRows],
  );

  const absentRows = useMemo(
    () => dailyRows.filter((r) => r.status === "Absent" && !r.exemptAbsent),
    [dailyRows],
  );

  const overtimeRows = useMemo(
    () =>
      dailyRows
        .filter((r) => r.status !== "Absent" && r.overtimeMinutes > 0)
        .map((r) => ({ ...r })),
    [dailyRows],
  );

  const footer = useMemo(() => {
    const target = days[days.length - 1];
    const rows = dailyRows.filter((r) => r.date === target);
    const present = rows.filter((r) => r.status === "Present").length;
    const late = rows.filter((r) => r.status === "Late").length;
    const onLeave = rows.filter((r) => r.status === "On Leave").length;
    const absent = rows.filter((r) => r.status === "Absent" && !r.exemptAbsent).length;
    const worked = rows.filter((r) => r.status === "Present" || r.status === "Late");
    const avgMinutes = worked.length
      ? Math.round(worked.reduce((sum, r) => sum + r.workedMinutes, 0) / worked.length)
      : 0;
    return { total: selectedEmployees.length, present, late, absent, onLeave, avgMinutes };
  }, [dailyRows, days, selectedEmployees.length]);

  const trend = useMemo(() => {
    const map = new Map<string, { month: string; Present: number; Late: number; Absent: number; "On Leave": number }>();
    for (const r of dailyRows) {
      const month = r.date.slice(0, 7);
      const item = map.get(month) ?? { month, Present: 0, Late: 0, Absent: 0, "On Leave": 0 };
      if (r.status === "Present") item.Present += 1;
      if (r.status === "Late") item.Late += 1;
      if (r.status === "On Leave") item["On Leave"] += 1;
      if (r.status === "Absent" && !r.exemptAbsent) item.Absent += 1;
      map.set(month, item);
    }
    return Array.from(map.values()).sort((a, b) => a.month.localeCompare(b.month));
  }, [dailyRows]);

  const exportRows = useMemo<ExportRow[]>(() => {
    switch (reportType) {
      case "daily":
        return dailyRows.map((r) => ({
          Date: formatCalendarDate(r.date, calendarType, r.date),
          "Employee ID": r.employeeCode,
          "Employee Name": r.employeeName,
          Department: r.department,
          Shift: r.shiftName,
          "Time Table": r.timetableLabel,
          Device: r.deviceName,
          "Late In": r.lateInMinutes > 0 ? fmtDuration(r.lateInMinutes) : "-",
          "Check In": fmtTime(r.checkIn),
          "Early Out": r.earlyOutMinutes > 0 ? fmtDuration(r.earlyOutMinutes) : "-",
          "Check Out": fmtTime(r.checkOut),
          "Working Hours": fmtDuration(r.shiftStandardMinutes),
          "Worked Hours": fmtDuration(r.workedMinutes),
          OT: fmtDuration(r.overtimeMinutes),
          Status: rowStatusLabel(r),
          "Leave Type": r.leaveType ?? "-",
          "Leave Reason": r.leaveReason ?? "-",
          Remark: rowRemark(r),
        }));
      case "weekly":
        return weeklyRows.map((r) => ({
          Week: `${formatCalendarDate(r.weekStart, calendarType, r.weekStart)} to ${formatCalendarDate(r.weekEnd, calendarType, r.weekEnd)}`,
          EmployeeID: r.employeeCode,
          EmployeeName: r.employeeName,
          Department: r.department,
          PresentDays: r.present,
          AbsentDays: r.absent,
          LateDays: r.late,
          WorkingHours: fmtDuration(r.assignedMinutes),
          WorkedHours: fmtDuration(r.workedMinutes),
          OTHours: fmtDuration(r.overtimeMinutes),
        }));
      case "monthly":
        return monthlyRows.map((r) => ({
          Month: formatCalendarMonth(r.month, calendarType),
          EmployeeID: r.employeeCode,
          EmployeeName: r.employeeName,
          Department: r.department,
          PresentDays: r.present,
          AbsentDays: r.absent,
          LateDays: r.late,
          WorkingHours: fmtDuration(r.assignedMinutes),
          WorkedHours: fmtDuration(r.workedMinutes),
          OTHours: fmtDuration(r.overtimeMinutes),
        }));
      case "monthlyCheckInOut":
        return dailyRows.map((r) => ({
          Date: formatCalendarDate(r.date, calendarType, r.date),
          EmployeeID: r.employeeCode,
          EmployeeName: r.employeeName,
          Department: r.department,
          Timetable: r.timetableLabel,
          Shift: r.shiftName,
          CheckIn: fmtTime(r.checkIn),
          CheckOut: fmtTime(r.checkOut),
          WorkingHours: fmtDuration(r.shiftStandardMinutes),
          WorkedHours: fmtDuration(r.workedMinutes),
          OTHours: fmtDuration(r.overtimeMinutes),
          Status: rowStatusLabel(r),
          Remark: rowRemark(r),
        }));
      case "late":
        return lateRows.map((r) => ({
          Date: formatCalendarDate(r.date, calendarType, r.date),
          EmployeeID: r.employeeCode,
          EmployeeName: r.employeeName,
          Department: r.department,
          Timetable: r.timetableLabel,
          Shift: r.shiftName,
          CheckIn: fmtTime(r.checkIn),
          LateMinutes: r.lateMinutes,
        }));
      case "absent":
        return absentRows.map((r) => ({
          Date: formatCalendarDate(r.date, calendarType, r.date),
          EmployeeID: r.employeeCode,
          EmployeeName: r.employeeName,
          Department: r.department,
          Timetable: r.timetableLabel,
          Shift: r.shiftName,
          Status: rowStatusLabel(r),
          Remark: rowRemark(r),
        }));
      default:
        return overtimeRows.map((r) => ({
          Date: formatCalendarDate(r.date, calendarType, r.date),
          EmployeeID: r.employeeCode,
          EmployeeName: r.employeeName,
          Department: r.department,
          Timetable: r.timetableLabel,
          Shift: r.shiftName,
          WorkingHours: fmtDuration(r.shiftStandardMinutes),
          WorkedHours: fmtDuration(r.workedMinutes),
          OTHours: fmtDuration(r.overtimeMinutes),
        }));
    }
  }, [
    calendarType,
    reportType,
    dailyRows,
    weeklyRows,
    monthlyRows,
    lateRows,
    absentRows,
    overtimeRows,
  ]);

  const reportTitle =
    REPORT_TYPES.find((r) => r.value === reportType)?.label ?? "Attendance Report";
  const availableColumns = useMemo(
    () => (exportRows[0] ? Object.keys(exportRows[0]) : []),
    [exportRows],
  );

  const initialDefaultColumns = useMemo(
    () => sortColumnsForReport(reportType, initialDefaultColumnsForReport(reportType, availableColumns)),
    [availableColumns, reportType],
  );
  const resetDefaultColumns = useMemo(
    () => sortColumnsForReport(reportType, resetDefaultColumnsForReport(reportType, availableColumns)),
    [availableColumns, reportType],
  );

  const selectedColumns = useMemo(() => {
    const saved = selectedColumnsByType[reportType];
    if (!saved || saved.length === 0) return initialDefaultColumns;
    const valid = saved.filter((x) => availableColumns.includes(x));
    return valid.length > 0 ? sortColumnsForReport(reportType, valid) : initialDefaultColumns;
  }, [selectedColumnsByType, reportType, availableColumns, initialDefaultColumns]);
  const visibleRows = useMemo<ExportRow[]>(() => {
    if (exportRows.length === 0) return [];
    if (selectedColumns.length === 0) return exportRows;
    return exportRows.map((row) => {
      const next: ExportRow = {};
      for (const col of selectedColumns) {
        if (col in row) next[col] = row[col] as string | number;
      }
      return next;
    });
  }, [exportRows, selectedColumns]);
  const isMonthlySplitPreview =
    employeeId === "all" && (reportType === "monthly" || reportType === "monthlyCheckInOut");
  const monthStartDateText =
    /^\d{4}-\d{2}-\d{2}$/.test(fromDate) ? `${fromDate.slice(0, 7)}-01` : fromDate;
  const displayFromDate = useMemo(
    () => formatCalendarDate(fromDate, calendarType, fromDate),
    [fromDate, calendarType],
  );
  const displayToDate = useMemo(
    () => formatCalendarDate(toDate, calendarType, toDate),
    [toDate, calendarType],
  );
  const displayMonthStartDate = useMemo(
    () => formatCalendarDate(monthStartDateText, calendarType, monthStartDateText),
    [monthStartDateText, calendarType],
  );
  const monthlyPreviewGroups = useMemo(
    () =>
      isMonthlySplitPreview
        ? Array.from(
            exportRows.reduce(
              (
                map,
                row,
              ) => {
                const code = String(row.EmployeeID ?? "").trim() || "Unknown";
                const item = map.get(code) ?? {
                  employeeCode: code,
                  employeeName: String(row.EmployeeName ?? "-"),
                  department: normalizeDepartmentName(String(row.Department ?? "-")),
                  rows: [] as ExportRow[],
                };
                item.rows.push(row);
                map.set(code, item);
                return map;
              },
              new Map<
                string,
                {
                  employeeCode: string;
                  employeeName: string;
                  department: string;
                  rows: ExportRow[];
                }
              >(),
            ),
          )
            .map(([, value]) => value)
            .sort((a, b) => a.employeeCode.localeCompare(b.employeeCode))
        : [],
    [isMonthlySplitPreview, exportRows],
  );
  const activeMonthlyPreviewGroup =
    isMonthlySplitPreview && monthlyPreviewGroups.length > 0
      ? monthlyPreviewGroups[Math.min(monthlyPreviewEmployeeIndex, monthlyPreviewGroups.length - 1)]
      : null;
  const previewSourceRows = useMemo<ExportRow[]>(() => {
    if (!activeMonthlyPreviewGroup) return visibleRows;
    if (selectedColumns.length === 0) return activeMonthlyPreviewGroup.rows;
    return activeMonthlyPreviewGroup.rows.map((row) => {
      const next: ExportRow = {};
      for (const col of selectedColumns) {
        if (col in row) next[col] = row[col] as string | number;
      }
      return next;
    });
  }, [activeMonthlyPreviewGroup, visibleRows, selectedColumns]);

  // Reset sort when source rows change
  useEffect(() => {
    setSortField("");
  }, [previewSourceRows]);

  const sortedPreviewSourceRows = useMemo(() => {
    const data = [...previewSourceRows];
    if (!sortField) return data;
    data.sort((a, b) => {
      const valA = a[sortField];
      const valB = b[sortField];

      if (typeof valA === "number" && typeof valB === "number") {
        return sortAsc ? valA - valB : valB - valA;
      }

      let strA = "";
      let strB = "";
      if (valA !== undefined && valA !== null) {
        strA = String(valA).toLowerCase();
      }
      if (valB !== undefined && valB !== null) {
        strB = String(valB).toLowerCase();
      }

      if (strA < strB) return sortAsc ? -1 : 1;
      if (strA > strB) return sortAsc ? 1 : -1;
      return 0;
    });
    return data;
  }, [previewSourceRows, sortField, sortAsc]);

  const previewRowCount = previewSourceRows.length;
  const previewTotalPages = Math.max(1, Math.ceil(previewRowCount / PREVIEW_PAGE_SIZE));
  const previewCurrentPage = Math.min(previewPage, previewTotalPages);
  const previewRangeStart = previewRowCount === 0 ? 0 : (previewCurrentPage - 1) * PREVIEW_PAGE_SIZE + 1;
  const previewRangeEnd = Math.min(previewCurrentPage * PREVIEW_PAGE_SIZE, previewRowCount);
  const previewRows = useMemo<ExportRow[]>(() => {
    if (previewRowCount === 0) return [];
    const start = (previewCurrentPage - 1) * PREVIEW_PAGE_SIZE;
    return sortedPreviewSourceRows.slice(start, start + PREVIEW_PAGE_SIZE);
  }, [previewCurrentPage, previewRowCount, sortedPreviewSourceRows]);

  const clearFilters = () => {
    setFromDate(defaultFromDate);
    setToDate(defaultToDate);
    setDepartment("all");
    setEmployeeId("all");
    setStatus("all");
    setShift("assigned");
    setSearch("");
    setReportType("daily");
  };

  const generateReport = useCallback(async () => {
    setLoading(true);
    setError(null);
    setWarning(null);
    setPreviewPage(1);
    try {
      if (days.length >= REPORT_MAX_DAYS) {
        setWarning(`Date range is limited to ${REPORT_MAX_DAYS} days. Shorten the range for complete long-period reports.`);
      }

      const requestTo = new Date(`${toDate}T00:00:00`);
      requestTo.setDate(requestTo.getDate() + 1);
      const transactionQuery = new URLSearchParams({
        from: fromDate,
        to: toInputDate(requestTo),
        take: String(REPORT_TRANSACTION_LIMIT),
        usePunchTime: "true",
        lite: "true",
        // Reports count only approved punches; pending QR/manual requests appear only after
        // an admin approves them.
        approvedOnly: "true",
      });
      const searchToken = search.trim();
      if (searchToken) transactionQuery.set("search", searchToken);
      if (department !== "all") transactionQuery.set("department", department);
      if (employeeId !== "all") {
        const emp = empById.get(employeeId) ?? employees.find(e => e.id === employeeId || e.employeeCode === employeeId);
        if (emp?.employeeCode) transactionQuery.set("employeeCode", emp.employeeCode);
      }

      const [tx, employeePayload, holidayPayload, leavePayload, deptPayload] = await Promise.all([
        requestJson<unknown>(`/api/transactions?${transactionQuery.toString()}`),
        requestJson<unknown>("/api/personnel/employees?status=active"),
        requestJson<unknown>("/api/personnel/holidays").catch(() => []),
        // Leave data is optional for reports; don't let a leave-endpoint failure break the
        // whole report (the EF /api/leave join can error on some schemas).
        requestJson<unknown>(`/api/leave?from=${fromDate}&to=${toDate}&status=approved`).catch(() => []),
        requestJson<unknown>("/api/personnel/departments").catch(() => []),
      ]);

      // Endpoints may return a bare array or a { items: [...] } wrapper.
      const unwrap = <T,>(payload: unknown): T[] => {
        if (Array.isArray(payload)) return payload as T[];
        const items = (payload as { items?: unknown })?.items;
        return Array.isArray(items) ? (items as T[]) : [];
      };

      const txRows = unwrap<TxRow>(tx);
      setTransactions(txRows);
      if (txRows.length >= REPORT_TRANSACTION_LIMIT) {
        setWarning((current) =>
          current
            ? `${current} Also, only the latest ${REPORT_TRANSACTION_LIMIT.toLocaleString()} punches were loaded; narrow filters for exact totals.`
            : `Only the latest ${REPORT_TRANSACTION_LIMIT.toLocaleString()} punches were loaded; narrow filters for exact totals.`,
        );
      }
      const leaveRows = unwrap<any>(leavePayload);
      setLeaves(leaveRows);

      const deptRows = unwrap<{ name?: string; code?: string }>(deptPayload)
        .map((d) => normalizeDepartmentName(d.name || d.code || ""))
        .filter((n) => n.length > 0);
      if (deptRows.length > 0) setAllDepartments(deptRows);
      const employeeRows = unwrap<PersonnelEmployeeApiRow>(employeePayload)
        .map((row) => {
          const employeeCode = (row.employeeCode || "").trim();
          if (!employeeCode) return null;
          return {
            id: (row.id || employeeCode).trim(),
            employeeCode,
            fullName: (row.fullName || employeeCode).trim(),
            department: normalizeDepartmentName(row.department),
            isActive: row.isActive !== false,
          } satisfies EmployeeRow;
        })
        .filter((row): row is EmployeeRow => Boolean(row))
        .sort((a, b) => a.employeeCode.localeCompare(b.employeeCode));
      setEmployees(employeeRows);

      const holidayItems = normalizeHolidayRows(unwrap<HolidayApiRow>(holidayPayload));
      const nextCalendar: ShiftCalendarSettings = {
        ...readShiftCalendarSettings(),
        holidays: holidayItems,
      };
      setShiftCalendar(nextCalendar);
      syncShiftCalendarHolidays(holidayItems);

      const shiftRows = readShiftDefinitions();
      const timetableRows = readTimetableDefinitions(shiftRows);
      const shiftPlanRows = readShiftPlanDefinitions(timetableRows);
      setShiftDefinitions(shiftRows);
      setShiftAssignments(readShiftAssignments());
      setShiftGroups(readShiftGroups());
      setTimetables(timetableRows);
      setShiftPlans(shiftPlanRows);

      setGeneratedAt(new Date());
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to generate report.");
      setWarning(null);
      setTransactions([]);
      setEmployees([]);
    } finally {
      setLoading(false);
    }
  }, [days.length, department, employeeId, employees, empById, fromDate, search, toDate]);

  useEffect(() => {
    void generateReport();
    // Initial load only; avoid dependency-driven re-fetch loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const shiftRows = readShiftDefinitions();
    const timetableRows = readTimetableDefinitions(shiftRows);
    const shiftPlanRows = readShiftPlanDefinitions(timetableRows);
    setShiftDefinitions(shiftRows);
    setShiftAssignments(readShiftAssignments());
    setShiftGroups(readShiftGroups());
    setTimetables(timetableRows);
    setShiftPlans(shiftPlanRows);
    setShiftCalendar(readShiftCalendarSettings());
  }, []);

  useEffect(() => {
    const syncShiftState = () => {
      const shiftRows = readShiftDefinitions();
      const timetableRows = readTimetableDefinitions(shiftRows);
      const shiftPlanRows = readShiftPlanDefinitions(timetableRows);
      setShiftDefinitions(shiftRows);
      setShiftAssignments(readShiftAssignments());
      setShiftGroups(readShiftGroups());
      setTimetables(timetableRows);
      setShiftPlans(shiftPlanRows);
      setShiftCalendar(readShiftCalendarSettings());
    };
    window.addEventListener("storage", syncShiftState);
    window.addEventListener(SHIFT_CALENDAR_UPDATED_EVENT, syncShiftState);
    return () => {
      window.removeEventListener("storage", syncShiftState);
      window.removeEventListener(SHIFT_CALENDAR_UPDATED_EVENT, syncShiftState);
    };
  }, []);

  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(COLUMN_CONFIG_KEY);
      if (!raw) return;
      const parsed = JSON.parse(raw) as Partial<Record<ReportType, string[]>>;
      const next: Partial<Record<ReportType, string[]>> = {};
      for (const key of Object.keys(parsed) as ReportType[]) {
        const rows = parsed[key];
        if (!Array.isArray(rows)) continue;
        next[key] = rows.filter((x): x is string => typeof x === "string");
      }
      setSelectedColumnsByType(next);
    } catch {
      // ignore invalid persisted config
    }
  }, []);

  useEffect(() => {
    try {
      window.localStorage.setItem(COLUMN_CONFIG_KEY, JSON.stringify(selectedColumnsByType));
    } catch {
      // ignore storage write failure
    }
  }, [selectedColumnsByType]);

  useEffect(() => {
    if (!isMonthlySplitPreview) {
      if (monthlyPreviewEmployeeIndex !== 0) setMonthlyPreviewEmployeeIndex(0);
      return;
    }
    if (monthlyPreviewEmployeeIndex >= monthlyPreviewGroups.length) {
      setMonthlyPreviewEmployeeIndex(0);
    }
  }, [
    isMonthlySplitPreview,
    monthlyPreviewEmployeeIndex,
    monthlyPreviewGroups.length,
  ]);

  useEffect(() => {
    if (previewPage !== previewCurrentPage) setPreviewPage(previewCurrentPage);
  }, [previewCurrentPage, previewPage]);

  const applySelectedColumns = (rows: ExportRow[]): ExportRow[] => {
    if (selectedColumns.length === 0) return rows;
    return rows.map((row) => {
      const next: ExportRow = {};
      for (const col of selectedColumns) {
        if (col in row) next[col] = row[col] as string | number;
      }
      return next;
    });
  };

  const exportCsv = () => {
    if (visibleRows.length === 0) return;
    if (isMonthlySplitPreview && monthlyPreviewGroups.length > 0) {
      const lines: string[] = [];
      for (const group of monthlyPreviewGroups) {
        const groupRows = applySelectedColumns(group.rows);
        if (groupRows.length === 0) continue;
        const cols = Object.keys(groupRows[0]);
        lines.push(`Employee Code,${csvEscape(group.employeeCode)}`);
        lines.push(`Employee Name,${csvEscape(group.employeeName)}`);
        lines.push(`Department,${csvEscape(group.department)}`);
        if (reportType === "monthly") {
          lines.push(`Month Start Date,${csvEscape(displayMonthStartDate)}`);
        }
        lines.push(`Report From,${csvEscape(displayFromDate)}`);
        lines.push(`Report To,${csvEscape(displayToDate)}`);
        lines.push("");
        lines.push(cols.join(","));
        lines.push(
          ...groupRows.map((r) => cols.map((c) => csvEscape(String(r[c] ?? ""))).join(",")),
        );
        lines.push("");
      }
      const text = lines.join("\r\n");
      const blob = new Blob([text], { type: "text/csv;charset=utf-8;" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `${reportTitle.replace(/\s+/g, "_").toLowerCase()}_${fromDate}_${toDate}.csv`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
      return;
    }

    const cols = Object.keys(visibleRows[0]);
    const text = [
      cols.join(","),
      ...visibleRows.map((r) => cols.map((c) => csvEscape(String(r[c] ?? ""))).join(",")),
    ].join("\r\n");
    const blob = new Blob([text], { type: "text/csv;charset=utf-8;" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${reportTitle.replace(/\s+/g, "_").toLowerCase()}_${fromDate}_${toDate}.csv`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  };

  const exportExcel = () => {
    if (visibleRows.length === 0) return;
    const splitByEmployee =
      employeeId === "all" && (reportType === "monthly" || reportType === "monthlyCheckInOut");

    if (splitByEmployee) {
      const grouped = new Map<string, ExportRow[]>();
      for (const row of exportRows) {
        const code = String(row.EmployeeID ?? "").trim() || "Unknown";
        grouped.set(code, [...(grouped.get(code) ?? []), row]);
      }

      const wb = XLSX.utils.book_new();

      const codes = Array.from(grouped.keys()).sort((a, b) => a.localeCompare(b));
      for (const code of codes) {
        const rows = grouped.get(code) ?? [];
        if (rows.length === 0) continue;
        const first = rows[0];
        const employeeName = String(first.EmployeeName ?? "-");
        const departmentName = normalizeDepartmentName(String(first.Department ?? "-"));

        const employeeRows =
          selectedColumns.length === 0
            ? rows
            : rows.map((row) => {
                const next: ExportRow = {};
                for (const col of selectedColumns) {
                  if (col in row) next[col] = row[col] as string | number;
                }
                return next;
              });

        const metaRows: string[][] = [
          ["Employee Code", code],
          ["Employee Name", employeeName],
          ["Department", departmentName],
        ];
        if (reportType === "monthly") {
          metaRows.push(["Month Start Date", displayMonthStartDate]);
        }
        metaRows.push(["Report From", displayFromDate]);
        metaRows.push(["Report To", displayToDate]);
        metaRows.push([]);

        const ws = XLSX.utils.aoa_to_sheet(metaRows);
        XLSX.utils.sheet_add_json(ws, employeeRows, {
          origin: `A${metaRows.length + 1}`,
          skipHeader: false,
        });
        const colWidths = buildColumnWidths(employeeRows);
        if (colWidths.length === 0) {
          ws["!cols"] = [{ wch: 22 }, { wch: 30 }];
        } else {
          colWidths[0] = { wch: Math.max(colWidths[0].wch, 20) };
          if (colWidths.length === 1) colWidths.push({ wch: 24 });
          else colWidths[1] = { wch: Math.max(colWidths[1].wch, 24) };
          ws["!cols"] = colWidths;
        }

        const sheetName = toExcelSheetName(`${code} ${employeeName}`);
        XLSX.utils.book_append_sheet(wb, ws, sheetName);
      }

      XLSX.writeFile(
        wb,
        `${reportTitle.replace(/\s+/g, "_").toLowerCase()}_${fromDate}_${toDate}.xlsx`,
      );
      return;
    }

    const ws = XLSX.utils.json_to_sheet(visibleRows);
    const colWidths = buildColumnWidths(visibleRows);
    if (colWidths.length > 0) ws["!cols"] = colWidths;
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, "Report");
    XLSX.writeFile(
      wb,
      `${reportTitle.replace(/\s+/g, "_").toLowerCase()}_${fromDate}_${toDate}.xlsx`,
    );
  };

  const exportPdf = () => {
    if (visibleRows.length === 0) return;
    const sections =
      isMonthlySplitPreview && monthlyPreviewGroups.length > 0
        ? monthlyPreviewGroups.map((group) => ({
            employeeCode: group.employeeCode,
            employeeName: group.employeeName,
            department: group.department,
            reportFrom: displayFromDate,
            reportTo: displayToDate,
            monthStartDate: reportType === "monthly" ? displayMonthStartDate : null,
            rows: applySelectedColumns(group.rows),
          }))
        : undefined;
    if (!openPrintable(reportTitle, generatedBy, formatCalendarDateTime(new Date(), calendarType), visibleRows, true, sections)) {
      setError("Popup blocked. Allow popups to export PDF.");
    }
  };

  const printPreview = () => {
    const sections =
      isMonthlySplitPreview && monthlyPreviewGroups.length > 0
        ? monthlyPreviewGroups.map((group) => ({
            employeeCode: group.employeeCode,
            employeeName: group.employeeName,
            department: group.department,
            reportFrom: displayFromDate,
            reportTo: displayToDate,
            monthStartDate: reportType === "monthly" ? displayMonthStartDate : null,
            rows: applySelectedColumns(group.rows),
          }))
        : undefined;
    if (!openPrintable(reportTitle, generatedBy, formatCalendarDateTime(new Date(), calendarType), visibleRows, false, sections)) {
      setError("Popup blocked. Allow popups for print preview.");
    }
  };

  return (
    <div className="space-y-4">
      <div className="tab-header flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold text-[#0e3456]">Reports</h1>
          <p className="text-sm text-[#53738d]">
            Professional, simple, fast, export-ready reporting for employee and device attendance data.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void generateReport()}
          className="inline-flex items-center gap-2 rounded-lg border border-[#1f5c8f] bg-[#1f5c8f] px-3 py-2 text-sm text-white"
        >
          <RefreshCcw className="h-4 w-4" />
          {loading ? "Generating..." : "Generate"}
        </button>
      </div>

      <section className="grid gap-4 lg:grid-cols-[320px_minmax(0,1fr)]">
        <aside className="surface h-fit overflow-visible space-y-3 p-4">
          <div className="flex items-center gap-2 text-sm font-semibold text-[#1d4f7a]">
            <Filter className="h-4 w-4" />
            Filter Panel
          </div>

          <label className="grid gap-1 text-xs text-[#2d5d86]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Report Type</span>
            <select
              value={reportType}
              onChange={(e) => {
                const next = e.target.value as ReportType;
                setReportType(next);
                if (next === "monthlyCheckInOut") {
                  setFromDate(currentMonthStartForCalendar(calendarType));
                  setToDate(currentMonthEndForCalendar(calendarType));
                }
              }}
              className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-2 text-sm"
            >
              {REPORT_TYPES.map((r) => (
                <option key={r.value} value={r.value}>
                  {r.label}
                </option>
              ))}
            </select>
          </label>

          <div className="grid grid-cols-1 gap-2 md:grid-cols-2 lg:grid-cols-1">
            <label className="grid gap-1 text-xs text-[#2d5d86]">
              <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">From</span>
              <BsDateField value={fromDate} onChange={setFromDate} className="min-w-[170px]" />
            </label>
            <label className="grid gap-1 text-xs text-[#2d5d86]">
              <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">To</span>
              <BsDateField value={toDate} onChange={setToDate} className="min-w-[170px]" />
            </label>
          </div>

          <label className="grid gap-1 text-xs text-[#2d5d86]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Department</span>
            <select
              value={department}
              onChange={(e) => setDepartment(e.target.value)}
              className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-2 text-sm"
            >
              <option value="all">All Departments</option>
              {deptOptions.map((d) => (
                <option key={d} value={d}>
                  {d}
                </option>
              ))}
            </select>
          </label>

          <label className="grid gap-1 text-xs text-[#2d5d86]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Employee</span>
            <select
              value={employeeId}
              onChange={(e) => setEmployeeId(e.target.value)}
              className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-2 text-sm"
            >
              <option value="all">All Employees</option>
              {employees
                .filter((x) => x.isActive)
                .map((e) => (
                  <option key={e.id} value={e.id}>
                    {e.employeeCode} - {e.fullName}
                  </option>
                ))}
            </select>
          </label>

          <label className="grid gap-1 text-xs text-[#2d5d86]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Status</span>
            <select
              value={status}
              onChange={(e) => setStatus(e.target.value as StatusFilter)}
              className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-2 text-sm"
            >
              <option value="all">All Status</option>
              <option value="present">Present</option>
              <option value="late">Late</option>
              <option value="onleave">On Leave</option>
              <option value="absent">Absent</option>
            </select>
          </label>

          <label className="grid gap-1 text-xs text-[#2d5d86]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Shift (Schedule)</span>
            <select
              value={shift}
              onChange={(e) => setShift(e.target.value)}
              className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-2 text-sm"
            >
              {shiftOptions.map((s) => (
                <option key={s.value} value={s.value}>
                  {s.label}
                </option>
              ))}
            </select>
            <span className="text-[10px] text-[#5f7f9f]">Calculated from Schedule Shift assignments and effective dates.</span>
          </label>

          <div className="grid grid-cols-2 gap-2">
            <button
              type="button"
              onClick={() => void generateReport()}
              className="rounded-lg border border-[#1f5c8f] bg-[#1f5c8f] px-3 py-2 text-sm text-white"
            >
              Generate
            </button>
            <button
              type="button"
              onClick={clearFilters}
              className="rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-sm text-[#1f5c8f]"
            >
              Clear
            </button>
          </div>

          <div className="grid grid-cols-2 gap-2">
            <button
              type="button"
              onClick={exportPdf}
              className="inline-flex items-center justify-center gap-1 rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-xs text-[#1f5c8f]"
            >
              <FileText className="h-3.5 w-3.5" />
              PDF
            </button>
            <button
              type="button"
              onClick={exportExcel}
              className="inline-flex items-center justify-center gap-1 rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-xs text-[#1f5c8f]"
            >
              <Download className="h-3.5 w-3.5" />
              Excel
            </button>
            <button
              type="button"
              onClick={exportCsv}
              className="inline-flex items-center justify-center gap-1 rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-xs text-[#1f5c8f]"
            >
              <Download className="h-3.5 w-3.5" />
              CSV
            </button>
            <button
              type="button"
              onClick={printPreview}
              className="inline-flex items-center justify-center gap-1 rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-xs text-[#1f5c8f]"
            >
              <Printer className="h-3.5 w-3.5" />
              Print
            </button>
          </div>

        </aside>

        <div className="space-y-4">
          <div className="metric-grid sm:grid-cols-2 xl:grid-cols-5">
            <div className="metric-card">
              <div className="metric-card__label">Total Employees</div>
              <div className="metric-card__value">{footer.total}</div>
            </div>
            <div className="metric-card">
              <div className="metric-card__label">Present</div>
              <div className="metric-card__value">{footer.present}</div>
            </div>
            <div className="metric-card">
              <div className="metric-card__label">On Leave</div>
              <div className="metric-card__value text-purple-600">{footer.onLeave}</div>
            </div>
            <div className="metric-card">
              <div className="metric-card__label">Absent</div>
              <div className="metric-card__value">{footer.absent}</div>
            </div>
            <div className="metric-card">
              <div className="metric-card__label">Avg Working Hours</div>
              <div className="metric-card__value">
                {fmtDuration(footer.avgMinutes)}
              </div>
            </div>
          </div>

          {error ? (
            <div className="rounded-lg border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-sm text-[#8d2f2f]">
              {error}
            </div>
          ) : null}

          {warning ? (
            <div className="rounded-lg border border-[#f0d18a] bg-[#fff8e6] px-3 py-2 text-sm text-[#7a5415]">
              {warning}
            </div>
          ) : null}

          <section className="surface overflow-visible rounded-xl">
            <div className="flex flex-wrap items-center justify-between gap-2 border-b border-[#d8e6f4] bg-[#f2f8ff] px-4 py-3">
              <div>
                <div className="text-sm font-semibold text-[#1f4f79]">{reportTitle}</div>
                <div className="text-xs text-[#5f7f9f]">
                  Company: E-Attendance | Generated:{" "}
                  {generatedAt ? formatCalendarDateTime(generatedAt, calendarType) : "-"} | By: {generatedBy}
                </div>
              </div>
            <div className="flex items-center gap-2">
                <div className="flex items-center gap-2 text-xs text-[#5f7f9f]">
                  <CalendarDays className="h-4 w-4" />
                  {displayFromDate} to {displayToDate}
                </div>
                <div className="relative group">
                  <button
                    type="button"
                    title="Column preferences"
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-[#bfd4ea] bg-white text-[#1f5c8f] hover:bg-[#f2f8ff]"
                  >
                    <SlidersHorizontal className="h-4 w-4" />
                  </button>
                  <div className="absolute right-0 top-full mt-1 hidden rounded-lg border border-[#bfd4ea] bg-white shadow-lg w-64 max-h-96 overflow-y-auto z-50 group-hover:block">
                    <div className="sticky top-0 bg-[#f2f8ff] border-b border-[#d8e6f4] px-3 py-2">
                      <div className="text-xs font-semibold text-[#1f4f79]">Columns ({selectedColumns.length}/{availableColumns.length})</div>
                    </div>
                    <div className="p-2 space-y-1">
                      {availableColumns.length === 0 ? (
                        <div className="px-2 py-2 text-xs text-[#5f7f9f]">Generate report first</div>
                      ) : (
                        availableColumns.map((col) => (
                          <label key={col} className="flex items-center gap-2 cursor-pointer px-2 py-1 hover:bg-[#f9fcff] rounded text-xs text-[#2d5d86]">
                            <input
                              type="checkbox"
                              checked={selectedColumns.includes(col)}
                              onChange={() => {
                                setSelectedColumnsByType((prev) => {
                                  const updated = { ...prev };
                                  const hasSaved = Array.isArray(updated[reportType]) && (updated[reportType]?.length ?? 0) > 0;
                                  const cols = hasSaved ? (updated[reportType] as string[]) : selectedColumns;

                                  if (cols.includes(col)) {
                                    updated[reportType] = sortColumnsForReport(
                                      reportType,
                                      cols.filter((c) => c !== col),
                                    );
                                  } else {
                                    updated[reportType] = sortColumnsForReport(reportType, [...cols, col]);
                                  }

                                  localStorage.setItem(COLUMN_CONFIG_KEY, JSON.stringify(updated));
                                  return updated;
                                });
                              }}
                              className="h-3 w-3 accent-[#1f5c8f]"
                            />
                            <span>{col}</span>
                          </label>
                        ))
                      )}
                    </div>
                    <div className="sticky bottom-0 border-t border-[#d8e6f4] bg-[#f9fcff] px-2 py-2 flex gap-1">
                      <button
                        type="button"
                        onClick={() =>
                          setSelectedColumnsByType((prev) => {
                            const updated = { ...prev };
                            updated[reportType] = resetDefaultColumns;
                            localStorage.setItem(COLUMN_CONFIG_KEY, JSON.stringify(updated));
                            return updated;
                          })
                        }
                        className="text-xs px-2 py-1 rounded border border-[#bfd4ea] text-[#1f5c8f] hover:bg-white flex-1"
                      >
                        Reset
                      </button>
                      <button
                        type="button"
                        onClick={() =>
                          setSelectedColumnsByType((prev) => {
                            const updated = { ...prev };
                            updated[reportType] = sortColumnsForReport(reportType, availableColumns);
                            localStorage.setItem(COLUMN_CONFIG_KEY, JSON.stringify(updated));
                            return updated;
                          })
                        }
                        className="text-xs px-2 py-1 rounded border border-[#bfd4ea] text-[#1f5c8f] hover:bg-white flex-1"
                      >
                        All
                      </button>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            {isMonthlySplitPreview && activeMonthlyPreviewGroup ? (
              <div className="flex flex-wrap items-center justify-between gap-2 border-b border-[#d8e6f4] bg-[#fbfdff] px-4 py-2 text-xs text-[#4c6f8f]">
                <div className="grid flex-1 gap-1 sm:grid-cols-2 lg:grid-cols-3">
                  <span className="rounded bg-white px-2 py-1">
                    Employee:{" "}
                    <span className="font-semibold">
                      {activeMonthlyPreviewGroup.employeeCode} - {activeMonthlyPreviewGroup.employeeName}
                    </span>
                  </span>
                  <span className="rounded bg-white px-2 py-1">
                    Department: <span className="font-semibold">{activeMonthlyPreviewGroup.department}</span>
                  </span>
                  {reportType === "monthly" ? (
                    <span className="rounded bg-white px-2 py-1">
                      Month Start Date: <span className="font-semibold">{displayMonthStartDate}</span>
                    </span>
                  ) : null}
                  <span className="rounded bg-white px-2 py-1">
                    Report From: <span className="font-semibold">{displayFromDate}</span>
                  </span>
                  <span className="rounded bg-white px-2 py-1">
                    Report To: <span className="font-semibold">{displayToDate}</span>
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={() => setMonthlyPreviewEmployeeIndex((x) => Math.max(0, x - 1))}
                    disabled={monthlyPreviewEmployeeIndex <= 0}
                    className="rounded border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f] disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    Prev
                  </button>
                  <span className="font-semibold text-[#355a7a]">
                    {Math.min(monthlyPreviewEmployeeIndex + 1, monthlyPreviewGroups.length)} / {monthlyPreviewGroups.length}
                  </span>
                  <button
                    type="button"
                    onClick={() =>
                      setMonthlyPreviewEmployeeIndex((x) =>
                        Math.min(monthlyPreviewGroups.length - 1, x + 1),
                      )
                    }
                    disabled={monthlyPreviewEmployeeIndex >= monthlyPreviewGroups.length - 1}
                    className="rounded border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f] disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    Next
                  </button>
                </div>
              </div>
            ) : null}

            <div className="flex items-center justify-between gap-2 border-b border-[#d8e6f4] bg-[#fcfeff] px-4 py-2 text-xs text-[#5f7f9f]">
              <span>
                Showing {previewRangeStart}–{previewRangeEnd} of {previewRowCount} row(s).
              </span>
              <div className="inline-flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPreviewPage((p) => Math.max(1, p - 1))}
                  disabled={previewCurrentPage === 1 || previewRowCount === 0}
                  className="inline-flex items-center gap-1 rounded-lg border border-[--border] bg-white px-2 py-1 disabled:opacity-40"
                >
                  Prev
                </button>
                <span className="px-1">
                  Page {previewCurrentPage} / {previewTotalPages}
                </span>
                <button
                  type="button"
                  onClick={() => setPreviewPage((p) => Math.min(previewTotalPages, p + 1))}
                  disabled={previewCurrentPage >= previewTotalPages || previewRowCount === 0}
                  className="inline-flex items-center gap-1 rounded-lg border border-[--border] bg-white px-2 py-1 disabled:opacity-40"
                >
                  Next
                </button>
              </div>
            </div>

            <div className="overflow-x-auto">
              {previewRows.length === 0 ? (
                <div className="px-4 py-10 text-center text-sm text-[#5f7f9f]">
                  {loading ? "Generating report..." : "No data available for selected filters."}
                </div>
              ) : (
                <table className="min-w-max text-left text-sm">
                  <thead className="bg-[#fbfdff] text-xs uppercase text-[#4a6d8d]">
                    <tr>
                      {Object.keys(previewRows[0]).map((k) => (
                        <th key={k} className="whitespace-nowrap px-3 py-2">
                          <button
                            type="button"
                            onClick={() => handleSort(k)}
                            className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5"
                          >
                            <span>{k}</span>
                            {sortField === k && (
                              <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                            )}
                          </button>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {previewRows.map((r, i) => (
                      <tr
                        key={`${i}_${Object.values(r).join("_")}`}
                        className="border-t border-[#e5effa]"
                      >
                        {Object.entries(r).map(([k, v]) => {
                          const bgColor = columnColorClass(k);
                          if (k === "Status" && typeof v === "string" && ["Present", "Late", "Absent"].includes(v)) {
                            return (
                              <td key={k} className={`px-3 py-2 ${bgColor}`}>
                                <span className={`rounded-full px-2 py-1 text-xs font-semibold ${statusClass(v as "Present" | "Late" | "Absent")}`}>
                                  {v}
                                </span>
                              </td>
                            );
                          }
                          return (
                            <td key={k} className={`whitespace-nowrap px-3 py-2 ${bgColor}`}>
                              {String(v)}
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            <div className="border-t border-[#d8e6f4] bg-[#fcfeff] px-4 py-3 text-xs text-[#355a7a]">
              <div className="grid gap-2 sm:grid-cols-5">
                <div>
                  Total Employees: <span className="font-semibold">{footer.total}</span>
                </div>
                <div>
                  Present: <span className="font-semibold text-[#0f8a45]">{footer.present}</span>
                </div>
                <div>
                  Absent: <span className="font-semibold text-[#be2f2f]">{footer.absent}</span>
                </div>
                <div>
                  Late: <span className="font-semibold text-[#cc7a00]">{footer.late}</span>
                </div>
                <div>
                  Average Hours: <span className="font-semibold">{fmtDuration(footer.avgMinutes)}</span>
                </div>
              </div>
            </div>
          </section>

          <section className="surface rounded-xl p-4">
            <div className="mb-2 flex items-center gap-2 text-sm font-semibold text-[#1f4f79]">
              <BarChart3 className="h-4 w-4" />
              Attendance Trend
            </div>
            <div className="h-[240px] w-full">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={trend}>
                  <CartesianGrid strokeDasharray="3 3" stroke="#d8e6f4" />
                  <XAxis dataKey="month" />
                  <YAxis />
                  <Tooltip />
                  <Legend />
                  <Bar dataKey="Present" fill="#10b981" radius={[4, 4, 0, 0]} />
                  <Bar dataKey="Late" fill="#f59e0b" radius={[4, 4, 0, 0]} />
                  <Bar dataKey="On Leave" fill="#8b5cf6" radius={[4, 4, 0, 0]} />
                  <Bar dataKey="Absent" fill="#ef4444" radius={[4, 4, 0, 0]} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </section>
        </div>
      </section>

    </div>
  );
}
