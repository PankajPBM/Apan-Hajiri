"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  BarChart3,
  CalendarDays,
  ChevronLeft,
  ChevronRight,
  Download,
  FileText,
  Filter,
  Printer,
  RefreshCcw,
  SlidersHorizontal,
  X,
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
  presentDays?: number;
  absentDays?: number;
  lateDays?: number;
  earlyOutDays?: number;
};

function computeSectionAttendanceStats(rows: ExportRow[]) {
  let presentDays = 0;
  let absentDays = 0;
  let lateDays = 0;
  let earlyOutDays = 0;

  for (const r of rows) {
    const statusVal = String(r.Status ?? r.status ?? "").trim();
    const isSat = String(r.Day ?? "").trim().toLowerCase() === "saturday";
    const isWeeklyOff =
      statusVal.toLowerCase().includes("weekly off") ||
      (isSat && (statusVal === "" || statusVal === "-"));
    const isHoliday = statusVal.toLowerCase().includes("holiday");
    const isOnLeave = statusVal.toLowerCase().includes("leave");

    const checkIn = String(r["Check In"] ?? r.CheckIn ?? "").trim();
    const checkOut = String(r["Check Out"] ?? r.CheckOut ?? "").trim();
    const hasPunch =
      (checkIn !== "" && checkIn !== "-") || (checkOut !== "" && checkOut !== "-");

    const isPresent =
      statusVal === "Present" ||
      statusVal === "Late" ||
      (!isWeeklyOff && !isHoliday && !isOnLeave && hasPunch);

    const isAbsent =
      (statusVal === "Absent" || (!hasPunch && !isWeeklyOff && !isHoliday && !isOnLeave)) &&
      !statusVal.includes("Present") &&
      !statusVal.includes("Late");

    if (isPresent) presentDays++;
    else if (isAbsent) absentDays++;

    const lateVal = String(r["Late In"] ?? r.LateIn ?? "").trim();
    if (
      (lateVal !== "" && lateVal !== "-" && lateVal !== "00:00" && lateVal !== "0") ||
      statusVal === "Late"
    ) {
      lateDays++;
    }

    const earlyVal = String(r["Early Out"] ?? r.EarlyOut ?? "").trim();
    if (earlyVal !== "" && earlyVal !== "-" && earlyVal !== "00:00" && earlyVal !== "0") {
      earlyOutDays++;
    }
  }

  return {
    totalDays: rows.length,
    presentDays,
    absentDays,
    lateDays,
    earlyOutDays,
  };
}

const REPORT_TYPES: Array<{ value: ReportType; label: string }> = [
  { value: "monthlyCheckInOut", label: "Monthly Check In/Check Out" },
  { value: "daily", label: "Daily Attendance" },
  { value: "weekly", label: "Weekly Attendance" },
  { value: "monthly", label: "Monthly Attendance" },
  { value: "late", label: "Late Coming" },
  { value: "absent", label: "Absent" },
  { value: "overtime", label: "Overtime" },
];
const COLUMN_CONFIG_KEY = "pb.attendance.reports.columns.v3";
const DEFAULT_UNCHECKED_COLUMNS = new Set([
  "EmployeeID",
  "Employee ID",
  "EmployeeName",
  "Employee Name",
  "Department",
  "Shift",
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
const renderReportStatusBadge = (statusText: string) => {
  const s = statusText.toLowerCase().trim();
  if (s === "present") {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-200 bg-emerald-50 px-2.5 py-0.5 text-[11px] font-bold text-emerald-800 shadow-2xs">
        <span className="h-1.5 w-1.5 rounded-full bg-emerald-500 shrink-0" />
        Present
      </span>
    );
  }
  if (s === "late" || s.startsWith("late")) {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-amber-200 bg-amber-50 px-2.5 py-0.5 text-[11px] font-bold text-amber-800 shadow-2xs">
        <span className="h-1.5 w-1.5 rounded-full bg-amber-500 shrink-0" />
        {statusText}
      </span>
    );
  }
  if (s === "absent") {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-rose-200 bg-rose-50 px-2.5 py-0.5 text-[11px] font-bold text-rose-700 shadow-2xs">
        <span className="h-1.5 w-1.5 rounded-full bg-rose-500 shrink-0" />
        Absent
      </span>
    );
  }
  if (s.includes("holiday")) {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-purple-200 bg-purple-50 px-2.5 py-0.5 text-[11px] font-bold text-purple-800 shadow-2xs max-w-[240px] truncate" title={statusText}>
        <span className="h-1.5 w-1.5 rounded-full bg-purple-500 shrink-0" />
        <span className="truncate">{statusText}</span>
      </span>
    );
  }
  if (s.includes("weekly off") || s === "off") {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-slate-200 bg-slate-100 px-2.5 py-0.5 text-[11px] font-semibold text-slate-700">
        <span className="h-1.5 w-1.5 rounded-full bg-slate-400 shrink-0" />
        Weekly Off
      </span>
    );
  }
  if (s.includes("leave")) {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-blue-200 bg-blue-50 px-2.5 py-0.5 text-[11px] font-bold text-blue-700 shadow-2xs">
        <span className="h-1.5 w-1.5 rounded-full bg-blue-500 shrink-0" />
        {statusText}
      </span>
    );
  }
  return (
    <span className="inline-flex items-center rounded-md border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] font-medium text-slate-700">
      {statusText}
    </span>
  );
};

const columnColorClass = (_colName: string): string => "";

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

const ENGLISH_WEEKDAYS = [
  "Sunday",
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
] as const;

function getEnglishWeekday(dateStr: string): string {
  if (!dateStr) return "-";
  const [y, m, d] = dateStr.split("-").map(Number);
  if (!y || !m || !d) return "-";
  const dt = new Date(y, m - 1, d);
  const dayIndex = dt.getDay();
  return ENGLISH_WEEKDAYS[dayIndex] || "-";
}

const DAILY_COLUMN_ORDER = [
  "Date",
  "Day",
  "Employee ID",
  "Employee Name",
  "Department",
  "Shift",
  "Time Table",
  "Device",
  "Check In",
  "Late In",
  "Check Out",
  "Early Out",
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
  "Day",
  "Employee ID",
  "Employee Name",
  "Time Table",
  "Check In",
  "Late In",
  "Check Out",
  "Early Out",
  "Working Hours",
  "Worked Hours",
  "Status",
  "Remark",
];

const MONTHLY_CHECKINOUT_COLUMN_ORDER = [
  "Date",
  "Day",
  "EmployeeID",
  "EmployeeName",
  "Department",
  "Timetable",
  "Shift",
  "CheckIn",
  "LateIn",
  "CheckOut",
  "EarlyOut",
  "WorkingHours",
  "WorkedHours",
  "OTHours",
  "Status",
  "Remark",
];

function sortColumnsForReport(reportType: ReportType, columns: string[]): string[] {
  if (reportType === "daily") {
    const order = new Map<string, number>(DAILY_COLUMN_ORDER.map((c, i) => [c, i]));
    return columns
      .slice()
      .sort((a, b) => (order.get(a) ?? 9999) - (order.get(b) ?? 9999) || a.localeCompare(b));
  }
  if (reportType === "monthlyCheckInOut") {
    const order = new Map<string, number>(MONTHLY_CHECKINOUT_COLUMN_ORDER.map((c, i) => [c, i]));
    return columns
      .slice()
      .sort((a, b) => (order.get(a) ?? 9999) - (order.get(b) ?? 9999) || a.localeCompare(b));
  }
  return columns;
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

export type PrintSetupOptions = {
  paperSize: "A4" | "Letter" | "Legal" | "A3";
  orientation: "landscape" | "portrait";
  scaleMode: "fit-width" | "100" | "85" | "75" | "custom";
  customScale: number;
  margin: "8" | "4" | "2";
  tableDensity: "normal" | "compact";
  showMetadata: boolean;
  oneEmployeePerPage: boolean;
};

export const DEFAULT_PRINT_OPTIONS: PrintSetupOptions = {
  paperSize: "A4",
  orientation: "landscape",
  scaleMode: "fit-width",
  customScale: 100,
  margin: "4",
  tableDensity: "compact",
  showMetadata: true,
  oneEmployeePerPage: true,
};

function openPrintable(
  title: string,
  generatedBy: string,
  generatedAtText: string,
  rows: ExportRow[],
  autoPrint: boolean,
  sections?: PrintableSection[],
  options?: Partial<PrintSetupOptions>,
): boolean {
  const opts: PrintSetupOptions = { ...DEFAULT_PRINT_OPTIONS, ...options };
  const w = window.open("", "_blank", "width=1280,height=900");
  if (!w) return false;
  const renderTable = (tableRows: ExportRow[], isEmployeeSheet?: boolean) => {
    let cols = tableRows[0] ? Object.keys(tableRows[0]) : [];
    if (isEmployeeSheet) {
      cols = cols.filter(
        (c) =>
          c !== "EmployeeID" &&
          c !== "Employee ID" &&
          c !== "EmployeeName" &&
          c !== "Employee Name" &&
          c !== "Department" &&
          c !== "Shift" &&
          c !== "Timetable" &&
          c !== "Time Table" &&
          c !== "Device",
      );
    }
    const th = cols.map((c) => `<th>${escHtml(c)}</th>`).join("");
    const tb = tableRows
      .slice(0, 3000)
      .map(
        (r) =>
          `<tr>${cols
            .map((c) => {
              const val = String(r[c] ?? "");
              const isSat = c === "Day" && val === "Saturday";
              const isStatus = c === "Status";
              let tdClass = "";
              if (isSat) tdClass = ' class="sat-cell"';
              else if (isStatus) tdClass = ' class="status-cell"';
              return `<td${tdClass}>${escHtml(val)}</td>`;
            })
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
              ? `<div><strong>Month Start:</strong> ${escHtml(section.monthStartDate)}</div>`
              : "";
            const pDays = section.presentDays ?? 0;
            const aDays = section.absentDays ?? 0;
            const lDays = section.lateDays ?? 0;
            const eDays = section.earlyOutDays ?? 0;
            return `<div class="paper-sheet employee-sheet ${opts.oneEmployeePerPage ? "one-page-fit" : ""} ${index > 0 ? "sec-break" : ""}">
              <div class="h">
                <div style="display:flex;gap:10px;align-items:center">
                  <div class="logo">EA</div>
                  <div>
                    <div style="font-weight:800;font-size:16px;color:#0f172a;line-height:1.2">E-Attendance</div>
                    <div style="font-size:12px;font-weight:600;color:#2563eb">${escHtml(title)}</div>
                  </div>
                </div>
                <div class="m">
                  <div><strong>Period:</strong> ${escHtml(section.reportFrom)} to ${escHtml(section.reportTo)}</div>
                  <div>Generated: ${escHtml(generatedAtText)} | By: ${escHtml(generatedBy)}</div>
                </div>
              </div>
              <div class="sec-meta">
                <div class="sec-meta-grid">
                  <div><strong>Employee Code:</strong> ${escHtml(section.employeeCode)}</div>
                  <div><strong>Employee Name:</strong> ${escHtml(section.employeeName)}</div>
                  <div><strong>Department:</strong> ${escHtml(section.department)}</div>
                  ${monthStartLine}
                  <div><strong>Total Days:</strong> ${section.rows.length}</div>
                </div>
                <div class="sec-summary-kpis">
                  <span class="kpi-tag kpi-present"><strong>Present Days:</strong> ${pDays}</span>
                  <span class="kpi-tag kpi-absent"><strong>Absent Days:</strong> ${aDays}</span>
                  <span class="kpi-tag kpi-late"><strong>Late In:</strong> ${lDays}</span>
                  <span class="kpi-tag kpi-early"><strong>Early Out:</strong> ${eDays}</span>
                </div>
              </div>
              <div class="tbl-wrap">
                ${renderTable(section.rows, true)}
              </div>
            </div>`;
          })
          .join("")
      : `<div class="paper-sheet ${opts.oneEmployeePerPage ? "one-page-fit" : ""}">
          <div class="h">
            <div style="display:flex;gap:10px;align-items:center">
              <div class="logo">EA</div>
              <div>
                <div style="font-weight:800;font-size:16px;color:#0f172a;line-height:1.2">E-Attendance</div>
                <div style="font-size:12px;font-weight:600;color:#2563eb">${escHtml(title)}</div>
              </div>
            </div>
            <div class="m">
              <div>Generated: ${escHtml(generatedAtText)}</div>
              <div>Generated By: ${escHtml(generatedBy)}</div>
            </div>
          </div>
          <div class="tbl-wrap">
            ${renderTable(rows)}
          </div>
        </div>`;

  w.document.write(`<!doctype html><html><head><meta charset="utf-8"/><title>${escHtml(title)}</title>
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <style>
    * { box-sizing: border-box; }
    body {
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif;
      margin: 0;
      padding: 0;
      background: #f1f5f9;
      color: #0f172a;
    }
    .toolbar {
      position: sticky;
      top: 0;
      z-index: 1000;
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 8px 14px;
      background: #ffffff;
      border-bottom: 1px solid #cbd5e1;
      padding: 8px 16px;
      box-shadow: 0 4px 12px rgba(15, 23, 42, 0.06);
    }
    .toolbar-left, .toolbar-right {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 10px;
    }
    .ctrl-item {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      font-weight: 600;
      color: #334155;
    }
    .ctrl-item select, .ctrl-item input {
      font-size: 12px;
      font-weight: 500;
      color: #0f172a;
      background: #f8fafc;
      border: 1px solid #cbd5e1;
      border-radius: 6px;
      padding: 4px 8px;
      outline: none;
      cursor: pointer;
    }
    .ctrl-item select:focus {
      border-color: #2563eb;
      box-shadow: 0 0 0 2px rgba(37,99,235,0.15);
    }
    .btn {
      font-size: 12px;
      font-weight: 600;
      border-radius: 6px;
      padding: 5px 12px;
      cursor: pointer;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      border: 1px solid #cbd5e1;
      background: #ffffff;
      color: #334155;
      transition: all 0.15s ease;
    }
    .btn:hover { background: #f8fafc; }
    .btn-primary {
      background: #2563eb;
      color: #ffffff;
      border-color: #1d4ed8;
      box-shadow: 0 1px 3px rgba(37,99,235,0.3);
    }
    .btn-primary:hover { background: #1d4ed8; }
    
    .canvas-wrap {
      padding: 24px;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 24px;
      min-height: calc(100vh - 60px);
      overflow: auto;
    }
    .paper-sheet {
      background: #ffffff;
      box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.1), 0 8px 10px -6px rgba(0, 0, 0, 0.05);
      border: 1px solid #e2e8f0;
      border-radius: 4px;
      transform-origin: top center;
      transition: width 0.15s;
      box-sizing: border-box;
    }
    .h {
      display: flex;
      justify-content: space-between;
      align-items: center;
      border-bottom: 2px solid #0f172a;
      padding-bottom: 8px;
      margin-bottom: 10px;
    }
    .logo {
      width: 36px;
      height: 36px;
      border-radius: 8px;
      background: #1e3a8a;
      color: #fff;
      display: grid;
      place-items: center;
      font-weight: 800;
      font-size: 14px;
    }
    .m { font-size: 11px; color: #475569; text-align: right; line-height: 1.4; }
    table { width: 100%; border-collapse: collapse; table-layout: fixed; }
    th, td { border: 1px solid #cbd5e1; text-align: left; word-break: break-word; white-space: normal; }
    th { background: #f1f5f9; color: #1e293b; font-weight: 700; }
    tr:nth-child(even) td { background: #f8fafc; }
    .sec-meta {
      margin: 8px 0;
      background: #f8fafc;
      padding: 8px 12px;
      border-radius: 6px;
      border: 1px solid #cbd5e1;
      font-size: 11px;
    }
    .sec-meta-grid {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 5px 12px;
    }
    .sec-summary-kpis {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-top: 6px;
      padding-top: 6px;
      border-top: 1px dashed #cbd5e1;
    }
    .kpi-tag {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 2px 8px;
      border-radius: 4px;
      font-size: 10.5px;
      font-weight: 600;
    }
    .kpi-present { background: #ecfdf5; color: #065f46; border: 1px solid #a7f3d0; }
    .kpi-absent { background: #fff1f2; color: #9f1239; border: 1px solid #fecdd3; }
    .kpi-late { background: #fffbeb; color: #92400e; border: 1px solid #fde68a; }
    .kpi-early { background: #fff7ed; color: #9a3412; border: 1px solid #fed7aa; }
    .sec-break {
      page-break-before: always;
      break-before: page;
    }

    @media print {
      body {
        background: transparent !important;
        margin: 0 !important;
        padding: 0 !important;
        -webkit-print-color-adjust: exact !important;
        print-color-adjust: exact !important;
      }
      .toolbar { display: none !important; }
      .canvas-wrap {
        padding: 0 !important;
        display: block !important;
        min-height: auto !important;
        gap: 0 !important;
      }
      .paper-sheet {
        border: none !important;
        box-shadow: none !important;
        border-radius: 0 !important;
        margin: 0 !important;
        width: 100% !important;
        max-width: none !important;
      }
      .employee-sheet {
        page-break-before: always !important;
        page-break-after: always !important;
        page-break-inside: avoid !important;
        break-before: page !important;
        break-after: page !important;
        break-inside: avoid !important;
        box-sizing: border-box !important;
      }
      .employee-sheet:first-of-type {
        page-break-before: avoid !important;
        break-before: avoid !important;
      }
      .employee-sheet:last-of-type {
        page-break-after: avoid !important;
        break-after: avoid !important;
      }
    }
  </style>
  <style id="dynamicPrintStyle"></style>
  </head><body>
  <div class="toolbar">
    <div class="toolbar-left">
      <div class="ctrl-item">
        <span>Paper:</span>
        <select id="optPaper">
          <option value="A4">A4 (210×297mm)</option>
          <option value="Letter">Letter (8.5×11")</option>
          <option value="Legal">Legal (8.5×14")</option>
          <option value="A3">A3 (297×420mm)</option>
        </select>
      </div>
      <div class="ctrl-item">
        <span>Orientation:</span>
        <select id="optOrient">
          <option value="landscape">Landscape</option>
          <option value="portrait">Portrait</option>
        </select>
      </div>
      <div class="ctrl-item">
        <span>Fit & Scale:</span>
        <select id="optScale">
          <option value="fit-width">Fit to Page Width</option>
          <option value="100">100% (Actual)</option>
          <option value="85">85% (Compact)</option>
          <option value="75">75% (Dense)</option>
          <option value="custom">Custom Scale</option>
        </select>
      </div>
      <div class="ctrl-item" id="zoomBox" style="display:none">
        <button class="btn" type="button" id="btnZmOut">-</button>
        <span id="lblZoom" style="min-width:38px;text-align:center">100%</span>
        <button class="btn" type="button" id="btnZmIn">+</button>
      </div>
      <div class="ctrl-item">
        <span>Margins:</span>
        <select id="optMargin">
          <option value="8">Normal (8mm)</option>
          <option value="4">Narrow (4mm)</option>
          <option value="2">Minimal (2mm)</option>
        </select>
      </div>
      <div class="ctrl-item">
        <span>Density:</span>
        <select id="optDensity">
          <option value="normal">Normal</option>
          <option value="compact">Compact</option>
        </select>
      </div>
      <div class="ctrl-item">
        <label style="display:flex;align-items:center;gap:5px;cursor:pointer">
          <input type="checkbox" id="chkOnePage" ${opts.oneEmployeePerPage ? "checked" : ""} />
          <span>1 Emp / Page</span>
        </label>
      </div>
    </div>
    <div class="toolbar-right">
      <button class="btn btn-primary" type="button" id="btnPrint">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="6 9 6 2 18 2 18 9"></polyline><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"></path><rect x="6" y="14" width="12" height="8"></rect></svg>
        <span>Print Document</span>
      </button>
      <button class="btn" type="button" id="btnClose">Close</button>
    </div>
  </div>

  <div class="canvas-wrap" id="canvasWrap">
    ${bodyHtml}
  </div>

  <script>
    (function(){
      var initial = ${JSON.stringify(opts)};
      var optPaper = document.getElementById('optPaper');
      var optOrient = document.getElementById('optOrient');
      var optScale = document.getElementById('optScale');
      var optMargin = document.getElementById('optMargin');
      var optDensity = document.getElementById('optDensity');
      var chkOnePage = document.getElementById('chkOnePage');
      var btnZmOut = document.getElementById('btnZmOut');
      var btnZmIn = document.getElementById('btnZmIn');
      var lblZoom = document.getElementById('lblZoom');
      var zoomBox = document.getElementById('zoomBox');
      var sheets = document.querySelectorAll('.paper-sheet');
      var dynStyle = document.getElementById('dynamicPrintStyle');

      // Set initial values
      optPaper.value = initial.paperSize || 'A4';
      optOrient.value = initial.orientation || 'landscape';
      optScale.value = initial.scaleMode || 'fit-width';
      optMargin.value = initial.margin || '4';
      optDensity.value = initial.tableDensity || 'compact';

      var currentZoom = initial.customScale ? (initial.customScale / 100) : 1;

      var sheetSizes = {
        'A4': { w: 794, h: 1123 },
        'Letter': { w: 816, h: 1056 },
        'Legal': { w: 816, h: 1344 },
        'A3': { w: 1123, h: 1587 }
      };

      function update() {
        var paper = optPaper.value;
        var orient = optOrient.value;
        var scaleMode = optScale.value;
        var margin = parseFloat(optMargin.value) || 4;
        var density = optDensity.value;
        var isOnePage = chkOnePage ? chkOnePage.checked : true;

        zoomBox.style.display = scaleMode === 'custom' ? 'flex' : 'none';

        var base = sheetSizes[paper] || sheetSizes['A4'];
        var targetW = orient === 'landscape' ? base.h : base.w;
        var paddingPx = Math.round(margin * 3.78);

        var cellPadding = density === 'compact' ? '3px 5px' : '5px 8px';
        var tableFont = density === 'compact' ? '9.5px' : '11px';

        // Scaling computation
        var zoom = 1;
        if (scaleMode === '100') zoom = 1;
        else if (scaleMode === '85') zoom = 0.85;
        else if (scaleMode === '75') zoom = 0.75;
        else if (scaleMode === 'custom') zoom = Math.max(0.4, Math.min(1.8, currentZoom));
        else if (scaleMode === 'fit-width') {
          var maxNaturalW = 0;
          sheets.forEach(function(s) {
            s.style.zoom = '1';
            maxNaturalW = Math.max(maxNaturalW, s.scrollWidth);
          });
          if (maxNaturalW > targetW) {
            zoom = Math.max(0.45, Math.min(1.0, targetW / maxNaturalW));
          } else {
            zoom = 1;
          }
        }

        currentZoom = zoom;
        lblZoom.textContent = Math.round(zoom * 100) + '%';

        sheets.forEach(function(sheet) {
          if (isOnePage) {
            sheet.classList.add('one-page-fit');
          } else {
            sheet.classList.remove('one-page-fit');
          }
          sheet.style.width = targetW + 'px';
          sheet.style.padding = paddingPx + 'px';
          sheet.style.zoom = zoom;
        });

        var onePagePrintCss = isOnePage ? (
          '  .employee-sheet { height: 100vh !important; max-height: 100vh !important; overflow: hidden !important; }' +
          '  .one-page-fit table { font-size: ' + (density === 'compact' ? '8.5px' : '9.5px') + ' !important; }' +
          '  .one-page-fit th, .one-page-fit td { padding: ' + (density === 'compact' ? '1.5px 3px' : '2.5px 4px') + ' !important; line-height: 1.15 !important; }' +
          '  .one-page-fit .sec-meta { margin: 2px 0 5px 0 !important; padding: 4px 8px !important; font-size: 10px !important; }' +
          '  .one-page-fit .sec-summary-kpis { margin-top: 3px !important; padding-top: 3px !important; gap: 6px !important; }' +
          '  .one-page-fit .kpi-tag { padding: 1px 6px !important; font-size: 9px !important; }' +
          '  .one-page-fit .h { margin-bottom: 5px !important; padding-bottom: 5px !important; }'
        ) : '';

        dynStyle.innerHTML =
          '@page { size: ' + paper + ' ' + orient + '; margin: ' + margin + 'mm; }' +
          '@media print {' +
          '  .paper-sheet { zoom: ' + zoom + ' !important; padding: 0 !important; }' +
          '  table { font-size: ' + tableFont + ' !important; }' +
          '  th, td { padding: ' + cellPadding + ' !important; }' +
          onePagePrintCss +
          '}';
      }

      optPaper.addEventListener('change', update);
      optOrient.addEventListener('change', update);
      optScale.addEventListener('change', update);
      optMargin.addEventListener('change', update);
      optDensity.addEventListener('change', update);
      if (chkOnePage) chkOnePage.addEventListener('change', update);

      btnZmOut.addEventListener('click', function(){
        currentZoom = Math.max(0.4, currentZoom - 0.05);
        update();
      });
      btnZmIn.addEventListener('click', function(){
        currentZoom = Math.min(1.8, currentZoom + 0.05);
        update();
      });

      document.getElementById('btnPrint').addEventListener('click', function(){
        window.print();
      });
      document.getElementById('btnClose').addEventListener('click', function(){
        window.close();
      });

      // Initial execution
      update();

      if (${autoPrint ? "true" : "false"}) {
        setTimeout(function(){
          window.focus();
          window.print();
        }, 400);
      }
    })();
  </script>
  </body></html>`);
  w.document.close();
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
  const [printModalOpen, setPrintModalOpen] = useState(false);
  const [printOptions, setPrintOptions] = useState<PrintSetupOptions>(DEFAULT_PRINT_OPTIONS);
  const [previewSheetIndex, setPreviewSheetIndex] = useState(0);
  const [previewZoomLevel, setPreviewZoomLevel] = useState<"fit" | "100" | "85">("fit");

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

  const summaryKpis = useMemo(() => {
    const totalRecords = dailyRows.length;
    const present = dailyRows.filter((r) => r.status === "Present").length;
    const late = dailyRows.filter((r) => r.status === "Late").length;
    const onLeave = dailyRows.filter((r) => r.status === "On Leave").length;
    const absent = dailyRows.filter((r) => r.status === "Absent" && !r.exemptAbsent).length;
    const totalOtMinutes = dailyRows.reduce((sum, r) => sum + (r.overtimeMinutes || 0), 0);
    const scheduledWorkDays = dailyRows.filter((r) => !r.exemptAbsent).length;
    const attendanceRate =
      scheduledWorkDays > 0
        ? Math.round(((present + late) / scheduledWorkDays) * 100)
        : totalRecords > 0
          ? Math.round(((present + late) / totalRecords) * 100)
          : 0;
    const totalWorkedMinutes = dailyRows.reduce((sum, r) => sum + (r.workedMinutes || 0), 0);
    const avgWorkMinutes =
      present + late > 0 ? Math.round(totalWorkedMinutes / (present + late)) : 0;

    return {
      totalRecords,
      scheduledWorkDays,
      present,
      late,
      onLeave,
      absent,
      attendanceRate,
      totalOtMinutes,
      avgWorkMinutes,
    };
  }, [dailyRows]);

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
          Day: getEnglishWeekday(r.date),
          "Employee ID": r.employeeCode,
          "Employee Name": r.employeeName,
          Department: r.department,
          Shift: r.shiftName,
          "Time Table": r.timetableLabel,
          Device: r.deviceName,
          "Check In": fmtTime(r.checkIn),
          "Late In": r.lateInMinutes > 0 ? fmtDuration(r.lateInMinutes) : "-",
          "Check Out": fmtTime(r.checkOut),
          "Early Out": r.earlyOutMinutes > 0 ? fmtDuration(r.earlyOutMinutes) : "-",
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
          Day: getEnglishWeekday(r.date),
          EmployeeID: r.employeeCode,
          EmployeeName: r.employeeName,
          Department: r.department,
          Timetable: r.timetableLabel,
          Shift: r.shiftName,
          CheckIn: fmtTime(r.checkIn),
          LateIn: r.lateInMinutes > 0 ? fmtDuration(r.lateInMinutes) : "-",
          CheckOut: fmtTime(r.checkOut),
          EarlyOut: r.earlyOutMinutes > 0 ? fmtDuration(r.earlyOutMinutes) : "-",
          WorkingHours: fmtDuration(r.shiftStandardMinutes),
          WorkedHours: fmtDuration(r.workedMinutes),
          OTHours: fmtDuration(r.overtimeMinutes),
          Status: rowStatusLabel(r),
          Remark: rowRemark(r),
        }));
      case "late":
        return lateRows.map((r) => ({
          Date: formatCalendarDate(r.date, calendarType, r.date),
          Day: getEnglishWeekday(r.date),
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
          Day: getEnglishWeekday(r.date),
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
          Day: getEnglishWeekday(r.date),
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

  const getPrintSections = useCallback((): PrintableSection[] | undefined => {
    const isMonthly = reportType === "monthly" || reportType === "monthlyCheckInOut";

    // 1. Multiple employees in monthly view
    if (isMonthly && employeeId === "all" && monthlyPreviewGroups.length > 0) {
      return monthlyPreviewGroups.map((group) => {
        const stats = computeSectionAttendanceStats(group.rows);
        return {
          employeeCode: group.employeeCode,
          employeeName: group.employeeName,
          department: group.department,
          reportFrom: displayFromDate,
          reportTo: displayToDate,
          monthStartDate: reportType === "monthly" ? displayMonthStartDate : null,
          rows: applySelectedColumns(group.rows),
          presentDays: stats.presentDays,
          absentDays: stats.absentDays,
          lateDays: stats.lateDays,
          earlyOutDays: stats.earlyOutDays,
        };
      });
    }

    // 2. Specific employee selected
    if (employeeId !== "all" && visibleRows.length > 0) {
      const first = visibleRows[0];
      const selectedEmp = empById.get(employeeId) ?? employees.find(e => e.id === employeeId || e.employeeCode === employeeId);
      const code = selectedEmp?.employeeCode || String(first?.EmployeeID ?? first?.["Employee ID"] ?? employeeId);
      const name = selectedEmp?.fullName || String(first?.EmployeeName ?? first?.["Employee Name"] ?? "-");
      const dept = selectedEmp?.department || normalizeDepartmentName(String(first?.Department ?? "-"));
      const stats = computeSectionAttendanceStats(visibleRows);
      return [
        {
          employeeCode: code,
          employeeName: name,
          department: dept,
          reportFrom: displayFromDate,
          reportTo: displayToDate,
          monthStartDate: isMonthly ? displayMonthStartDate : null,
          rows: applySelectedColumns(visibleRows),
          presentDays: stats.presentDays,
          absentDays: stats.absentDays,
          lateDays: stats.lateDays,
          earlyOutDays: stats.earlyOutDays,
        },
      ];
    }

    return undefined;
  }, [applySelectedColumns, displayFromDate, displayMonthStartDate, displayToDate, employeeId, employees, empById, monthlyPreviewGroups, reportType, visibleRows]);

  const launchPrint = useCallback(
    (autoPrint: boolean, customOpts?: Partial<PrintSetupOptions>) => {
      if (visibleRows.length === 0) {
        setError("No report data available to print. Please generate a report first.");
        return;
      }
      const opts = { ...printOptions, ...customOpts };
      const sections = getPrintSections();
      const ok = openPrintable(
        reportTitle,
        generatedBy,
        formatCalendarDateTime(new Date(), calendarType),
        visibleRows,
        autoPrint,
        sections,
        opts,
      );
      if (!ok) {
        setError("Popup blocked. Please allow popups for this site to print.");
      }
    },
    [calendarType, generatedBy, getPrintSections, printOptions, reportTitle, visibleRows],
  );

  const exportPdf = () => {
    launchPrint(true);
  };

  const printPreview = () => {
    launchPrint(false);
  };

  return (
    <div className="space-y-4">
      {/* 1. Header Overview & Generate Action */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 sm:p-6 shadow-2xs">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <div className="flex items-center gap-2">
              <h1 className="text-xl font-bold tracking-tight text-slate-900">
                Attendance Reports & Analytics
              </h1>
              <span className="rounded-md bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700 border border-blue-100">
                Export & Print Hub
              </span>
            </div>
            <p className="mt-1 text-xs text-slate-500">
              Generate daily, weekly, monthly check-in/out reports, calculate work hours, and export to Excel or PDF.
            </p>
          </div>
          <button
            type="button"
            onClick={() => void generateReport()}
            className="inline-flex items-center gap-1.5 rounded-xl bg-blue-600 px-4 py-2 text-xs font-semibold text-white hover:bg-blue-700 shadow-xs transition"
          >
            <RefreshCcw className={`h-4 w-4 ${loading ? "animate-spin" : ""}`} />
            {loading ? "Generating..." : "Generate Report"}
          </button>
        </div>
      </section>

      <section className="grid gap-4 lg:grid-cols-[300px_minmax(0,1fr)]">
        {/* Filter Panel */}
        <aside className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-2xs space-y-3 h-fit">
          <div className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-slate-700 border-b border-slate-100 pb-2.5">
            <Filter className="h-3.5 w-3.5 text-blue-600" />
            Report Parameters
          </div>

          <label className="grid gap-1">
            <span className="text-[11px] font-semibold text-slate-700">Report Category</span>
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
              className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 outline-none focus:border-blue-600"
            >
              {REPORT_TYPES.map((r) => (
                <option key={r.value} value={r.value}>
                  {r.label}
                </option>
              ))}
            </select>
          </label>

          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-1">
            <label className="grid gap-1">
              <span className="text-[11px] font-semibold text-slate-700">From Date</span>
              <BsDateField value={fromDate} onChange={setFromDate} />
            </label>
            <label className="grid gap-1">
              <span className="text-[11px] font-semibold text-slate-700">To Date</span>
              <BsDateField value={toDate} onChange={setToDate} />
            </label>
          </div>

          <label className="grid gap-1">
            <span className="text-[11px] font-semibold text-slate-700">Department</span>
            <select
              value={department}
              onChange={(e) => setDepartment(e.target.value)}
              className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 outline-none focus:border-blue-600"
            >
              <option value="all">All Departments</option>
              {deptOptions.map((d) => (
                <option key={d} value={d}>
                  {d}
                </option>
              ))}
            </select>
          </label>

          <label className="grid gap-1">
            <span className="text-[11px] font-semibold text-slate-700">Employee</span>
            <select
              value={employeeId}
              onChange={(e) => setEmployeeId(e.target.value)}
              className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 outline-none focus:border-blue-600"
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

          <label className="grid gap-1">
            <span className="text-[11px] font-semibold text-slate-700">Status</span>
            <select
              value={status}
              onChange={(e) => setStatus(e.target.value as StatusFilter)}
              className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 outline-none focus:border-blue-600"
            >
              <option value="all">All Status</option>
              <option value="present">Present</option>
              <option value="late">Late</option>
              <option value="onleave">On Leave</option>
              <option value="absent">Absent</option>
            </select>
          </label>

          <label className="grid gap-1">
            <span className="text-[11px] font-semibold text-slate-700">Shift Roster</span>
            <select
              value={shift}
              onChange={(e) => setShift(e.target.value)}
              className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 outline-none focus:border-blue-600"
            >
              {shiftOptions.map((s) => (
                <option key={s.value} value={s.value}>
                  {s.label}
                </option>
              ))}
            </select>
          </label>
          <div className="grid grid-cols-2 gap-2 pt-2">
            <button
              type="button"
              onClick={() => void generateReport()}
              className="rounded-lg bg-blue-600 px-3 py-2 text-xs font-semibold text-white hover:bg-blue-700 transition"
            >
              Generate
            </button>
            <button
              type="button"
              onClick={clearFilters}
              className="rounded-lg border border-slate-300 bg-white px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 transition"
            >
              Clear
            </button>
          </div>
        </aside>

        {/* Main Report Results */}
        <div className="space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-slate-200/90 bg-white p-4 shadow-2xs">
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={exportExcel}
                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 transition"
              >
                <Download className="h-3.5 w-3.5 text-emerald-600" />
                Export Excel
              </button>
              <button
                type="button"
                onClick={exportCsv}
                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 transition"
              >
                <FileText className="h-3.5 w-3.5 text-blue-600" />
                Export CSV
              </button>
              <button
                type="button"
                onClick={() => {
                  setPreviewSheetIndex(0);
                  setPrintModalOpen(true);
                }}
                className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-3.5 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 shadow-xs transition"
              >
                <Printer className="h-3.5 w-3.5" />
                Print / Live Preview
              </button>
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

          {/* Executive Summary KPI Cards */}
          {dailyRows.length > 0 ? (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
              <div className="rounded-xl border border-sky-200/80 bg-[linear-gradient(135deg,#f8fcff,#edf6ff)] p-3 shadow-2xs">
                <span className="text-[10px] font-bold uppercase tracking-wider text-sky-800">Total Shifts</span>
                <p className="mt-1 text-2xl font-bold text-sky-950">{summaryKpis.totalRecords}</p>
                <p className="text-[11px] text-sky-700/80">{summaryKpis.scheduledWorkDays} rostered working</p>
              </div>
              <div className="rounded-xl border border-emerald-200/80 bg-[linear-gradient(135deg,#f4fdf8,#e7f9ef)] p-3 shadow-2xs">
                <span className="text-[10px] font-bold uppercase tracking-wider text-emerald-800">Attendance Rate</span>
                <p className="mt-1 text-2xl font-bold text-emerald-950">{summaryKpis.attendanceRate}%</p>
                <div className="mt-1.5 h-1.5 w-full overflow-hidden rounded-full bg-emerald-200/60">
                  <div
                    className="h-full bg-emerald-600 rounded-full transition-all"
                    style={{ width: `${Math.min(100, summaryKpis.attendanceRate)}%` }}
                  />
                </div>
              </div>
              <div className="rounded-xl border border-emerald-200/80 bg-emerald-50/50 p-3 shadow-2xs">
                <span className="text-[10px] font-bold uppercase tracking-wider text-emerald-800">Present</span>
                <p className="mt-1 text-2xl font-bold text-emerald-700">{summaryKpis.present}</p>
                <p className="text-[11px] text-emerald-600/90">On-time attendances</p>
              </div>
              <div className="rounded-xl border border-amber-200/80 bg-amber-50/50 p-3 shadow-2xs">
                <span className="text-[10px] font-bold uppercase tracking-wider text-amber-800">Late In</span>
                <p className="mt-1 text-2xl font-bold text-amber-700">{summaryKpis.late}</p>
                <p className="text-[11px] text-amber-700/80">Arrived after grace</p>
              </div>
              <div className="rounded-xl border border-rose-200/80 bg-rose-50/50 p-3 shadow-2xs">
                <span className="text-[10px] font-bold uppercase tracking-wider text-rose-800">Absent</span>
                <p className="mt-1 text-2xl font-bold text-rose-700">{summaryKpis.absent}</p>
                <p className="text-[11px] text-rose-600/80">{summaryKpis.onLeave} on approved leave</p>
              </div>
              <div className="rounded-xl border border-indigo-200/80 bg-indigo-50/50 p-3 shadow-2xs">
                <span className="text-[10px] font-bold uppercase tracking-wider text-indigo-800">Overtime</span>
                <p className="mt-1 text-2xl font-bold text-indigo-900">{fmtDuration(summaryKpis.totalOtMinutes)}</p>
                <p className="text-[11px] text-indigo-700/80">Avg work: {fmtDuration(summaryKpis.avgWorkMinutes)}</p>
              </div>
            </div>
          ) : null}

          <section className="surface overflow-visible rounded-2xl border border-slate-200/90 shadow-2xs">
            <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-200 bg-slate-50/70 px-4 py-3">
              <div>
                <div className="text-sm font-bold text-slate-900">{reportTitle}</div>
                <div className="text-xs text-slate-500">
                  Company: E-Attendance | Generated:{" "}
                  {generatedAt ? formatCalendarDateTime(generatedAt, calendarType) : "-"} | By: {generatedBy}
                </div>
              </div>
              <div className="flex items-center gap-2">
                <div className="flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1 text-xs text-slate-600 font-medium shadow-2xs">
                  <CalendarDays className="h-3.5 w-3.5 text-blue-600" />
                  {displayFromDate} to {displayToDate}
                </div>
                <div className="relative group">
                  <button
                    type="button"
                    title="Column preferences"
                    className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-300 bg-white text-slate-700 hover:bg-slate-50 shadow-2xs transition"
                  >
                    <SlidersHorizontal className="h-4 w-4" />
                  </button>
                  <div className="absolute right-0 top-full mt-1 hidden rounded-xl border border-slate-200 bg-white shadow-xl w-64 max-h-96 overflow-y-auto z-50 group-hover:block">
                    <div className="sticky top-0 bg-slate-50 border-b border-slate-200 px-3 py-2">
                      <div className="text-xs font-bold text-slate-800">Columns ({selectedColumns.length}/{availableColumns.length})</div>
                    </div>
                    <div className="p-2 space-y-1">
                      {availableColumns.length === 0 ? (
                        <div className="px-2 py-2 text-xs text-slate-400">Generate report first</div>
                      ) : (
                        availableColumns.map((col) => (
                          <label key={col} className="flex items-center gap-2 cursor-pointer px-2 py-1 hover:bg-slate-50 rounded text-xs text-slate-700">
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
                              className="h-3.5 w-3.5 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                            />
                            <span>{col}</span>
                          </label>
                        ))
                      )}
                    </div>
                    <div className="sticky bottom-0 border-t border-slate-200 bg-slate-50 px-2 py-2 flex gap-1">
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
                        className="text-xs px-2 py-1 rounded border border-slate-200 bg-white font-medium text-slate-700 hover:bg-slate-50 flex-1 shadow-2xs"
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
                        className="text-xs px-2 py-1 rounded border border-slate-200 bg-white font-medium text-slate-700 hover:bg-slate-50 flex-1 shadow-2xs"
                      >
                        All
                      </button>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            {isMonthlySplitPreview && activeMonthlyPreviewGroup ? (
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 bg-slate-50/80 px-4 py-2.5 text-xs">
                <div className="flex flex-wrap items-center gap-2">
                  <div className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 shadow-2xs">
                    <span className="text-xs font-bold text-slate-600">Employee:</span>
                    <strong className="text-xs sm:text-sm font-extrabold text-slate-900">
                      {activeMonthlyPreviewGroup.employeeCode} - {activeMonthlyPreviewGroup.employeeName}
                    </strong>
                  </div>
                  <div className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 shadow-2xs">
                    <span className="text-xs font-bold text-slate-600">Department:</span>
                    <strong className="text-xs sm:text-sm font-extrabold text-slate-900">{activeMonthlyPreviewGroup.department}</strong>
                  </div>
                  <div className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 shadow-2xs">
                    <span className="text-xs font-bold text-slate-600">Period:</span>
                    <strong className="text-xs sm:text-sm font-extrabold text-slate-900">{displayFromDate} — {displayToDate}</strong>
                  </div>
                </div>

                <div className="flex items-center gap-2 shrink-0">
                  <span className="text-xs font-bold text-slate-700">Employee:</span>
                  <div className="inline-flex items-center rounded-lg border border-slate-300 bg-white shadow-2xs overflow-hidden">
                    <button
                      type="button"
                      onClick={() => setMonthlyPreviewEmployeeIndex((x) => Math.max(0, x - 1))}
                      disabled={monthlyPreviewEmployeeIndex <= 0}
                      className="inline-flex items-center gap-1 px-3 py-1.5 text-xs font-bold text-slate-700 hover:bg-slate-50 disabled:opacity-40 transition"
                    >
                      <ChevronLeft className="h-3.5 w-3.5" /> Prev
                    </button>
                    <span className="border-x border-slate-200 bg-slate-50 px-3.5 py-1.5 text-xs font-extrabold text-blue-700">
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
                      className="inline-flex items-center gap-1 px-3 py-1.5 text-xs font-bold text-slate-700 hover:bg-slate-50 disabled:opacity-40 transition"
                    >
                      Next <ChevronRight className="h-3.5 w-3.5" />
                    </button>
                  </div>
                </div>
              </div>
            ) : null}

            <div className="overflow-x-auto max-h-[580px] overflow-y-auto">
              {previewRows.length === 0 ? (
                <div className="px-4 py-12 text-center text-sm text-slate-400">
                  {loading ? "Generating report..." : "No data available for selected filters."}
                </div>
              ) : (
                <table className="min-w-full text-left text-xs divide-y divide-slate-200">
                  <thead className="sticky top-0 z-10 border-b border-slate-200 bg-slate-100/95 backdrop-blur-xs text-[11px] font-bold uppercase tracking-wider text-slate-700 shadow-2xs">
                    <tr>
                      {Object.keys(previewRows[0]).map((k) => {
                        const isCheckCol = ["CheckIn", "CheckOut", "Check In", "Check Out"].includes(k);
                        const isLateEarlyCol = ["LateIn", "EarlyOut", "Late In", "Early Out"].includes(k);
                        const colHeaderClass = isCheckCol
                          ? "bg-sky-100/80 text-sky-950 border-x border-sky-200/70"
                          : isLateEarlyCol
                            ? "bg-amber-100/80 text-amber-950 border-x border-amber-200/70"
                            : "text-slate-700";
                        return (
                          <th key={k} className={`whitespace-nowrap px-3.5 py-2.5 ${colHeaderClass}`}>
                            <button
                              type="button"
                              onClick={() => handleSort(k)}
                              className="font-bold hover:text-blue-700 inline-flex items-center gap-1 transition"
                            >
                              <span>{k}</span>
                              {sortField === k ? (
                                <span className="text-[10px] text-blue-600 font-black">{sortAsc ? "▲" : "▼"}</span>
                              ) : (
                                <span className="text-[10px] text-slate-400">↕</span>
                              )}
                            </button>
                          </th>
                        );
                      })}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {previewRows.map((r, i) => {
                      const dayStr = String(r.Day ?? "").toLowerCase();
                      const statusStr = String(r.Status ?? "").toLowerCase();
                      const remarkStr = String(r.Remark ?? "").toLowerCase();
                      const isWeekend = dayStr === "saturday";
                      const isHoliday = statusStr.includes("holiday") || remarkStr.includes("holiday");
                      const isWeeklyOff = statusStr.includes("weekly off") || statusStr === "off" || remarkStr.includes("weekly off");
                      const isOffDayRow = isWeekend || isHoliday || isWeeklyOff;

                      const rowStyle = isOffDayRow
                        ? "bg-rose-50/70 hover:bg-rose-100/80 border-l-4 border-l-rose-500 font-medium"
                        : "hover:bg-blue-50/40 transition-colors odd:bg-white even:bg-slate-50/30";

                      return (
                        <tr
                          key={`${i}_${Object.values(r).join("_")}`}
                          className={rowStyle}
                        >
                          {Object.entries(r).map(([k, v]) => {
                            const valStr = String(v ?? "").trim();
                            const isCheckCol = ["CheckIn", "CheckOut", "Check In", "Check Out"].includes(k);
                            const isLateEarlyCol = ["LateIn", "EarlyOut", "Late In", "Early Out"].includes(k);
                            const colCellClass = isOffDayRow
                              ? isCheckCol
                                ? "bg-sky-100/35 border-x border-sky-100/40"
                                : isLateEarlyCol
                                  ? "bg-amber-100/35 border-x border-amber-100/40"
                                  : ""
                              : isCheckCol
                                ? "bg-sky-50/50 border-x border-sky-100/60"
                                : isLateEarlyCol
                                  ? "bg-amber-50/50 border-x border-amber-100/60"
                                  : "";

                            if (k === "Status") {
                              return (
                                <td key={k} className={`whitespace-nowrap px-3.5 py-2 ${colCellClass}`}>
                                  {renderReportStatusBadge(valStr)}
                                </td>
                              );
                            }
                            if (k === "Day") {
                              return (
                                <td key={k} className={`whitespace-nowrap px-3.5 py-2 ${colCellClass}`}>
                                  <span
                                    className={`inline-block rounded-md px-2 py-0.5 text-xs font-semibold ${
                                      isWeekend
                                        ? "border border-rose-300 bg-rose-100 text-rose-900 font-bold"
                                        : "text-slate-700 font-medium"
                                    }`}
                                  >
                                    {valStr}
                                  </span>
                                </td>
                              );
                            }
                            if (["CheckIn", "CheckOut", "Check In", "Check Out"].includes(k)) {
                              const isDash = !valStr || valStr === "-" || valStr === "00:00" || valStr === "0";
                              return (
                                <td key={k} className={`whitespace-nowrap px-3.5 py-2 font-mono text-xs ${colCellClass}`}>
                                  {isDash ? (
                                    <span className="text-slate-300 font-normal">—</span>
                                  ) : (
                                    <span className="font-bold text-sky-950">{valStr}</span>
                                  )}
                                </td>
                              );
                            }
                            if (["LateIn", "Late In", "EarlyOut", "Early Out"].includes(k)) {
                              const isZero = !valStr || valStr === "-" || valStr === "00:00" || valStr === "0";
                              return (
                                <td key={k} className={`whitespace-nowrap px-3.5 py-2 font-mono text-xs ${colCellClass}`}>
                                  {isZero ? (
                                    <span className="text-slate-300 font-normal">—</span>
                                  ) : (
                                    <span className="font-bold text-amber-800 bg-amber-100/80 px-1.5 py-0.5 rounded border border-amber-200">
                                      {valStr}
                                    </span>
                                  )}
                                </td>
                              );
                            }
                            if (["WorkingHours", "Working Hours", "WorkedHours", "Worked Hours", "OTHours", "OT"].includes(k)) {
                              const isDash = !valStr || valStr === "-" || valStr === "00:00" || valStr === "0";
                              return (
                                <td key={k} className={`whitespace-nowrap px-3.5 py-2 font-mono text-xs ${colCellClass}`}>
                                  {isDash ? (
                                    <span className="text-slate-300 font-normal">—</span>
                                  ) : (
                                    <span className="font-semibold text-slate-800">{valStr}</span>
                                  )}
                                </td>
                              );
                            }
                            if (k === "Date") {
                              return (
                                <td key={k} className={`whitespace-nowrap px-3.5 py-2 font-mono text-xs font-medium ${isOffDayRow ? "text-rose-950 font-bold" : "text-slate-800"} ${colCellClass}`}>
                                  {valStr}
                                </td>
                              );
                            }
                            return (
                              <td key={k} className={`whitespace-nowrap px-3.5 py-2 text-xs text-slate-700 ${colCellClass}`}>
                                {valStr === "-" || !valStr ? (
                                  <span className="text-slate-300">—</span>
                                ) : (
                                  valStr
                                )}
                              </td>
                            );
                          })}
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )}
            </div>

            {/* Table Footer with Summary Stats & Pagination */}
            <div className="border-t border-slate-200 bg-slate-50/80 px-4 py-3 text-xs">
              <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
                {/* Statistics Pills */}
                <div className="flex flex-wrap items-center gap-2.5 text-slate-600">
                  <span className="rounded-lg bg-white px-2.5 py-1 border border-slate-200 font-medium shadow-2xs">
                    Total: <strong className="text-slate-900">{footer.total}</strong>
                  </span>
                  <span className="rounded-lg bg-emerald-50 px-2.5 py-1 border border-emerald-200 text-emerald-800 font-medium shadow-2xs">
                    Present: <strong className="text-emerald-950">{footer.present}</strong>
                  </span>
                  <span className="rounded-lg bg-rose-50 px-2.5 py-1 border border-rose-200 text-rose-800 font-medium shadow-2xs">
                    Absent: <strong className="text-rose-950">{footer.absent}</strong>
                  </span>
                  <span className="rounded-lg bg-amber-50 px-2.5 py-1 border border-amber-200 text-amber-800 font-medium shadow-2xs">
                    Late: <strong className="text-amber-950">{footer.late}</strong>
                  </span>
                  <span className="rounded-lg bg-white px-2.5 py-1 border border-slate-200 text-slate-700 font-medium shadow-2xs">
                    Avg Hours: <strong className="text-slate-900">{fmtDuration(footer.avgMinutes)}</strong>
                  </span>
                </div>

                {/* Table Page Pagination */}
                <div className="flex items-center justify-between lg:justify-end gap-3 text-slate-600 shrink-0">
                  <span className="text-xs text-slate-500">
                    Showing <strong className="text-slate-800">{previewRangeStart}–{previewRangeEnd}</strong> of <strong className="text-slate-800">{previewRowCount}</strong> rows
                  </span>
                  <div className="inline-flex items-center rounded-lg border border-slate-300 bg-white shadow-2xs overflow-hidden">
                    <button
                      type="button"
                      onClick={() => setPreviewPage((p) => Math.max(1, p - 1))}
                      disabled={previewCurrentPage === 1 || previewRowCount === 0}
                      className="inline-flex items-center gap-1 px-2.5 py-1 text-xs font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-40 transition"
                    >
                      <ChevronLeft className="h-3.5 w-3.5" /> Prev
                    </button>
                    <span className="border-x border-slate-200 bg-slate-50 px-3 py-1 text-xs font-bold text-slate-700">
                      Page {previewCurrentPage} / {previewTotalPages}
                    </span>
                    <button
                      type="button"
                      onClick={() => setPreviewPage((p) => Math.min(previewTotalPages, p + 1))}
                      disabled={previewCurrentPage >= previewTotalPages || previewRowCount === 0}
                      className="inline-flex items-center gap-1 px-2.5 py-1 text-xs font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-40 transition"
                    >
                      Next <ChevronRight className="h-3.5 w-3.5" />
                    </button>
                  </div>
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

      {printModalOpen ? (() => {
        const printSections = getPrintSections();
        const totalSheets = printSections && printSections.length > 0 ? printSections.length : 1;
        const activePrintSection =
          printSections && printSections.length > 0
            ? printSections[Math.min(previewSheetIndex, printSections.length - 1)]
            : null;
        const activeSheetRows = activePrintSection
          ? activePrintSection.rows
          : applySelectedColumns(visibleRows);
        const previewCols = activeSheetRows.length > 0
          ? Object.keys(activeSheetRows[0]).filter((c) => {
              if (activePrintSection) {
                return (
                  c !== "EmployeeID" &&
                  c !== "Employee ID" &&
                  c !== "EmployeeName" &&
                  c !== "Employee Name" &&
                  c !== "Department" &&
                  c !== "Shift" &&
                  c !== "Timetable" &&
                  c !== "Time Table" &&
                  c !== "Device"
                );
              }
              return true;
            })
          : [];

        return (
          <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-xs p-2 sm:p-4 overflow-hidden"
            onClick={() => setPrintModalOpen(false)}
          >
            <div
              className="w-full max-w-[1400px] h-[94vh] rounded-2xl border border-slate-300 bg-white shadow-2xl flex flex-col overflow-hidden animate-in fade-in zoom-in-95 duration-150"
              onClick={(e) => e.stopPropagation()}
            >
              {/* Top Modal Header */}
              <div className="flex items-center justify-between border-b border-slate-200 bg-white px-5 py-3 shrink-0">
                <div className="flex items-center gap-2.5">
                  <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-blue-50 text-blue-600 border border-blue-100">
                    <Printer className="h-5 w-5" />
                  </div>
                  <div>
                    <div className="flex items-center gap-2">
                      <h3 className="text-sm font-bold text-slate-900">Print Setup & Live Preview</h3>
                      <span className="rounded-md bg-blue-100 px-2 py-0.5 text-[11px] font-semibold text-blue-800">
                        {printOptions.orientation === "landscape" ? "Landscape" : "Portrait"} • {printOptions.paperSize}
                      </span>
                    </div>
                    <p className="text-[11px] text-slate-500">
                      Adjust page size, orientation, and layout with live sheet preview
                    </p>
                  </div>
                </div>
                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={() => {
                      launchPrint(true);
                    }}
                    className="inline-flex items-center gap-1.5 rounded-xl bg-blue-600 px-4 py-2 text-xs font-semibold text-white hover:bg-blue-700 shadow-sm transition"
                  >
                    <Printer className="h-4 w-4" />
                    Print Now
                  </button>
                  <button
                    type="button"
                    onClick={() => setPrintModalOpen(false)}
                    className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-100 hover:text-slate-600 transition"
                    title="Close dialog"
                  >
                    <X className="h-5 w-5" />
                  </button>
                </div>
              </div>

              {/* Split Screen Body: Left Controls, Right Live Preview */}
              <div className="flex-1 grid grid-cols-1 lg:grid-cols-[380px_minmax(0,1fr)] overflow-hidden">
                {/* Left Controls Panel */}
                <div className="flex flex-col border-r border-slate-200 bg-slate-50/50 overflow-y-auto">
                  <div className="p-4 space-y-4 text-xs flex-1">
                    {/* One Employee Per Page Card */}
                    <div
                      onClick={() =>
                        setPrintOptions((prev) => ({
                          ...prev,
                          oneEmployeePerPage: !prev.oneEmployeePerPage,
                        }))
                      }
                      className={`flex items-start gap-3 rounded-xl border p-3 cursor-pointer transition shadow-2xs ${
                        printOptions.oneEmployeePerPage
                          ? "border-blue-600 bg-blue-50/80 text-blue-900 ring-1 ring-blue-600"
                          : "border-slate-200 bg-white text-slate-700 hover:bg-slate-50"
                      }`}
                    >
                      <input
                        type="checkbox"
                        checked={printOptions.oneEmployeePerPage}
                        onChange={(e) =>
                          setPrintOptions((prev) => ({
                            ...prev,
                            oneEmployeePerPage: e.target.checked,
                          }))
                        }
                        className="mt-0.5 h-4 w-4 rounded accent-blue-600 cursor-pointer"
                      />
                      <div className="flex-1">
                        <div className="flex items-center gap-2">
                          <span className="text-xs font-bold">1 Employee Per Page</span>
                          <span className="rounded bg-blue-100 px-1.5 py-0.5 text-[10px] font-semibold text-blue-700">
                            Recommended
                          </span>
                        </div>
                        <p className="mt-0.5 text-[11px] text-slate-500">
                          Scales each employee's full monthly records (Days 1–31) to fit completely on 1 single sheet.
                        </p>
                      </div>
                    </div>

                    {/* Paper Size */}
                    <div>
                      <label className="block text-[11px] font-bold uppercase tracking-wider text-slate-600 mb-1.5">
                        Paper Size
                      </label>
                      <div className="grid grid-cols-4 gap-2">
                        {(
                          [
                            { id: "A4", label: "A4", desc: "210×297mm" },
                            { id: "Letter", label: "Letter", desc: '8.5×11"' },
                            { id: "Legal", label: "Legal", desc: '8.5×14"' },
                            { id: "A3", label: "A3", desc: "297×420mm" },
                          ] as const
                        ).map((p) => {
                          const active = printOptions.paperSize === p.id;
                          return (
                            <button
                              key={p.id}
                              type="button"
                              onClick={() =>
                                setPrintOptions((prev) => ({ ...prev, paperSize: p.id }))
                              }
                              className={`rounded-xl border p-2 text-center transition ${
                                active
                                  ? "border-blue-600 bg-blue-50/80 text-blue-900 font-semibold ring-1 ring-blue-600 shadow-2xs"
                                  : "border-slate-200 bg-white text-slate-700 hover:bg-slate-100"
                              }`}
                            >
                              <div className="text-xs font-bold">{p.label}</div>
                              <div className="text-[10px] text-slate-500 mt-0.5">{p.desc}</div>
                            </button>
                          );
                        })}
                      </div>
                    </div>

                    {/* Orientation */}
                    <div>
                      <label className="block text-[11px] font-bold uppercase tracking-wider text-slate-600 mb-1.5">
                        Orientation
                      </label>
                      <div className="grid grid-cols-2 gap-2">
                        <button
                          type="button"
                          onClick={() =>
                            setPrintOptions((prev) => ({ ...prev, orientation: "landscape" }))
                          }
                          className={`flex items-center gap-2.5 rounded-xl border p-2.5 text-left transition ${
                            printOptions.orientation === "landscape"
                              ? "border-blue-600 bg-blue-50/80 text-blue-900 font-semibold ring-1 ring-blue-600 shadow-2xs"
                              : "border-slate-200 bg-white text-slate-700 hover:bg-slate-100"
                          }`}
                        >
                          <div className="flex h-6 w-9 items-center justify-center rounded border border-current text-[10px] opacity-75 font-mono">
                            ▭
                          </div>
                          <div>
                            <div className="text-xs font-bold">Landscape</div>
                            <div className="text-[10px] text-slate-500">Wide attendance tables</div>
                          </div>
                        </button>

                        <button
                          type="button"
                          onClick={() =>
                            setPrintOptions((prev) => ({ ...prev, orientation: "portrait" }))
                          }
                          className={`flex items-center gap-2.5 rounded-xl border p-2.5 text-left transition ${
                            printOptions.orientation === "portrait"
                              ? "border-blue-600 bg-blue-50/80 text-blue-900 font-semibold ring-1 ring-blue-600 shadow-2xs"
                              : "border-slate-200 bg-white text-slate-700 hover:bg-slate-100"
                          }`}
                        >
                          <div className="flex h-8 w-6 items-center justify-center rounded border border-current text-[10px] opacity-75 font-mono">
                            ▯
                          </div>
                          <div>
                            <div className="text-xs font-bold">Portrait</div>
                            <div className="text-[10px] text-slate-500">Standard vertical page</div>
                          </div>
                        </button>
                      </div>
                    </div>

                    {/* View / Fit / Scaling */}
                    <div>
                      <label className="block text-[11px] font-bold uppercase tracking-wider text-slate-600 mb-1.5">
                        Page Fit & Scaling
                      </label>
                      <select
                        value={printOptions.scaleMode}
                        onChange={(e) =>
                          setPrintOptions((prev) => ({
                            ...prev,
                            scaleMode: e.target.value as PrintSetupOptions["scaleMode"],
                          }))
                        }
                        className="w-full rounded-xl border border-slate-300 bg-white px-3 py-2 text-xs text-slate-900 outline-none focus:border-blue-600 focus:ring-1 focus:ring-blue-600"
                      >
                        <option value="fit-width">Fit to Page Width (Auto scale columns)</option>
                        <option value="100">100% (Actual size)</option>
                        <option value="85">85% (Compact - Best for 8-12 cols)</option>
                        <option value="75">75% (Dense - Best for 12+ cols)</option>
                        <option value="custom">Custom Scale</option>
                      </select>

                      {printOptions.scaleMode === "custom" ? (
                        <div className="mt-2 flex items-center gap-3 rounded-xl border border-slate-200 bg-white p-2.5 shadow-2xs">
                          <span className="text-[11px] font-semibold text-slate-700">Custom Zoom:</span>
                          <input
                            type="range"
                            min={40}
                            max={150}
                            step={5}
                            value={printOptions.customScale}
                            onChange={(e) =>
                              setPrintOptions((prev) => ({
                                ...prev,
                                customScale: Number(e.target.value),
                              }))
                            }
                            className="flex-1 accent-blue-600"
                          />
                          <span className="w-12 text-right font-mono font-bold text-blue-700">
                            {printOptions.customScale}%
                          </span>
                        </div>
                      ) : null}
                    </div>

                    {/* Density & Margins Grid */}
                    <div className="grid grid-cols-2 gap-2.5">
                      <div>
                        <label className="block text-[11px] font-bold uppercase tracking-wider text-slate-600 mb-1.5">
                          Table Spacing
                        </label>
                        <select
                          value={printOptions.tableDensity}
                          onChange={(e) =>
                            setPrintOptions((prev) => ({
                              ...prev,
                              tableDensity: e.target.value as "normal" | "compact",
                            }))
                          }
                          className="w-full rounded-xl border border-slate-300 bg-white px-3 py-2 text-xs text-slate-900 outline-none focus:border-blue-600"
                        >
                          <option value="compact">Compact (Tighter Rows)</option>
                          <option value="normal">Normal Spacing</option>
                        </select>
                      </div>

                      <div>
                        <label className="block text-[11px] font-bold uppercase tracking-wider text-slate-600 mb-1.5">
                          Sheet Margins
                        </label>
                        <select
                          value={printOptions.margin}
                          onChange={(e) =>
                            setPrintOptions((prev) => ({
                              ...prev,
                              margin: e.target.value as "8" | "4" | "2",
                            }))
                          }
                          className="w-full rounded-xl border border-slate-300 bg-white px-3 py-2 text-xs text-slate-900 outline-none focus:border-blue-600"
                        >
                          <option value="4">Narrow (4 mm)</option>
                          <option value="8">Normal (8 mm)</option>
                          <option value="2">Minimal (2 mm)</option>
                        </select>
                      </div>
                    </div>
                  </div>

                  {/* Left Bottom Action Bar */}
                  <div className="border-t border-slate-200 bg-white p-3.5 flex items-center justify-between">
                    <button
                      type="button"
                      onClick={() => setPrintModalOpen(false)}
                      className="rounded-xl border border-slate-300 bg-white px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 transition"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      onClick={() => {
                        launchPrint(true);
                      }}
                      className="inline-flex items-center gap-1.5 rounded-xl bg-blue-600 px-4 py-2 text-xs font-semibold text-white hover:bg-blue-700 shadow-sm transition"
                    >
                      <Printer className="h-4 w-4" />
                      Print Now
                    </button>
                  </div>
                </div>

                {/* Right Live Preview Panel */}
                <div className="flex flex-col bg-slate-200/70 overflow-hidden">
                  {/* Preview Top Toolbar */}
                  <div className="flex items-center justify-between border-b border-slate-200 bg-white px-4 py-2 shrink-0">
                    <div className="flex items-center gap-2">
                      <span className="text-xs font-bold text-slate-700">Sheet Preview:</span>
                      {totalSheets > 1 ? (
                        <div className="flex items-center gap-1">
                          <button
                            type="button"
                            onClick={() => setPreviewSheetIndex((x) => Math.max(0, x - 1))}
                            disabled={previewSheetIndex <= 0}
                            className="rounded-md border border-slate-200 p-1 text-slate-600 hover:bg-slate-50 disabled:opacity-40"
                            title="Previous employee sheet"
                          >
                            <ChevronLeft className="h-3.5 w-3.5" />
                          </button>
                          <span className="px-2 text-xs font-semibold text-slate-800">
                            {previewSheetIndex + 1} / {totalSheets}
                            {activePrintSection ? ` (${activePrintSection.employeeCode} - ${activePrintSection.employeeName})` : ""}
                          </span>
                          <button
                            type="button"
                            onClick={() => setPreviewSheetIndex((x) => Math.min(totalSheets - 1, x + 1))}
                            disabled={previewSheetIndex >= totalSheets - 1}
                            className="rounded-md border border-slate-200 p-1 text-slate-600 hover:bg-slate-50 disabled:opacity-40"
                            title="Next employee sheet"
                          >
                            <ChevronRight className="h-3.5 w-3.5" />
                          </button>
                        </div>
                      ) : (
                        <span className="text-xs text-slate-600 font-medium">
                          {activePrintSection ? `Single Employee (${activePrintSection.employeeCode} - ${activePrintSection.employeeName})` : "All Records"}
                        </span>
                      )}
                    </div>

                    <div className="flex items-center gap-2">
                      <span className="text-[11px] text-slate-500 font-medium">Zoom:</span>
                      <button
                        type="button"
                        onClick={() => setPreviewZoomLevel("fit")}
                        className={`rounded-md px-2 py-0.5 text-[11px] font-semibold transition ${
                          previewZoomLevel === "fit" ? "bg-blue-600 text-white" : "border border-slate-200 bg-white text-slate-700 hover:bg-slate-50"
                        }`}
                      >
                        Fit View
                      </button>
                      <button
                        type="button"
                        onClick={() => setPreviewZoomLevel("85")}
                        className={`rounded-md px-2 py-0.5 text-[11px] font-semibold transition ${
                          previewZoomLevel === "85" ? "bg-blue-600 text-white" : "border border-slate-200 bg-white text-slate-700 hover:bg-slate-50"
                        }`}
                      >
                        85%
                      </button>
                      <button
                        type="button"
                        onClick={() => setPreviewZoomLevel("100")}
                        className={`rounded-md px-2 py-0.5 text-[11px] font-semibold transition ${
                          previewZoomLevel === "100" ? "bg-blue-600 text-white" : "border border-slate-200 bg-white text-slate-700 hover:bg-slate-50"
                        }`}
                      >
                        100%
                      </button>
                    </div>
                  </div>

                  {/* Preview Canvas Area */}
                  <div className="flex-1 overflow-auto p-4 sm:p-6 flex justify-center items-start">
                    <div
                      className="bg-white border border-slate-300 rounded shadow-xl transition-all duration-150 flex flex-col w-full max-w-[1080px]"
                      style={{
                        padding: printOptions.margin === "2" ? "12px 18px" : printOptions.margin === "4" ? "18px 24px" : "24px 32px",
                        fontSize: printOptions.tableDensity === "compact" || printOptions.oneEmployeePerPage ? "11px" : "12px",
                      }}
                    >
                      {/* Header */}
                      <div className="flex items-center justify-between border-b-2 border-slate-900 pb-2.5 mb-3">
                        <div className="flex items-center gap-2.5">
                          <div className="h-9 w-9 rounded-lg bg-blue-900 text-white font-extrabold text-sm grid place-items-center">
                            EA
                          </div>
                          <div>
                            <div className="font-extrabold text-slate-900 text-base leading-tight">E-Attendance</div>
                            <div className="text-xs font-semibold text-blue-600">{reportTitle}</div>
                          </div>
                        </div>
                        <div className="text-xs text-slate-600 text-right leading-relaxed">
                          <div><strong>Period:</strong> {activePrintSection ? `${activePrintSection.reportFrom} to ${activePrintSection.reportTo}` : `${displayFromDate} to ${displayToDate}`}</div>
                          <div>Generated: {generatedAt ? formatCalendarDateTime(generatedAt, calendarType) : "-"} | By: {generatedBy}</div>
                        </div>
                      </div>

                      {/* Employee Metadata Box */}
                      {activePrintSection ? (() => {
                        const pDays = activePrintSection.presentDays ?? 0;
                        const aDays = activePrintSection.absentDays ?? 0;
                        const lDays = activePrintSection.lateDays ?? 0;
                        const eDays = activePrintSection.earlyOutDays ?? 0;
                        return (
                          <div className="bg-slate-50 border border-slate-200 rounded-lg p-2.5 mb-3 text-xs space-y-2">
                            <div className="grid grid-cols-2 sm:grid-cols-4 gap-2 text-slate-700">
                              <div><span className="text-slate-500 font-medium">Employee Code:</span> <strong className="text-slate-900 ml-1">{activePrintSection.employeeCode}</strong></div>
                              <div><span className="text-slate-500 font-medium">Employee Name:</span> <strong className="text-slate-900 ml-1">{activePrintSection.employeeName}</strong></div>
                              <div><span className="text-slate-500 font-medium">Department:</span> <strong className="text-slate-900 ml-1">{activePrintSection.department}</strong></div>
                              <div><span className="text-slate-500 font-medium">Total Days:</span> <strong className="text-slate-900 ml-1">{activePrintSection.rows.length} Days</strong></div>
                            </div>
                            <div className="flex flex-wrap items-center gap-2 pt-2 border-t border-slate-200/80">
                              <span className="text-[11px] font-bold text-slate-500 uppercase tracking-wider mr-1">Summary:</span>
                              <div className="inline-flex items-center gap-1.5 rounded-md border border-emerald-200 bg-emerald-50 px-2 py-0.5 text-xs font-semibold text-emerald-800">
                                <span className="h-2 w-2 rounded-full bg-emerald-500"></span>
                                <span>Present Days:</span>
                                <strong className="font-mono text-emerald-950">{pDays}</strong>
                              </div>
                              <div className="inline-flex items-center gap-1.5 rounded-md border border-rose-200 bg-rose-50 px-2 py-0.5 text-xs font-semibold text-rose-800">
                                <span className="h-2 w-2 rounded-full bg-rose-500"></span>
                                <span>Absent Days:</span>
                                <strong className="font-mono text-rose-950">{aDays}</strong>
                              </div>
                              <div className="inline-flex items-center gap-1.5 rounded-md border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-semibold text-amber-800">
                                <span className="h-2 w-2 rounded-full bg-amber-500"></span>
                                <span>Late In:</span>
                                <strong className="font-mono text-amber-950">{lDays}</strong>
                              </div>
                              <div className="inline-flex items-center gap-1.5 rounded-md border border-orange-200 bg-orange-50 px-2 py-0.5 text-xs font-semibold text-orange-800">
                                <span className="h-2 w-2 rounded-full bg-orange-500"></span>
                                <span>Early Out:</span>
                                <strong className="font-mono text-orange-950">{eDays}</strong>
                              </div>
                            </div>
                          </div>
                        );
                      })() : null}

                      {/* Table */}
                      <div className="overflow-x-auto">
                        <table className="w-full border-collapse text-left text-xs">
                          <thead>
                            <tr className="bg-slate-100 text-slate-800 font-bold border-y border-slate-300">
                              {previewCols.map((col) => (
                                <th
                                  key={col}
                                  className="border border-slate-300 px-2.5 py-1.5 whitespace-nowrap text-[11px]"
                                >
                                  {col}
                                </th>
                              ))}
                            </tr>
                          </thead>
                          <tbody>
                            {activeSheetRows.map((r, idx) => (
                              <tr
                                key={idx}
                                className={`border-b border-slate-200 ${idx % 2 === 1 ? "bg-slate-50/70" : "bg-white"}`}
                              >
                                {previewCols.map((col) => {
                                  const val = String(r[col] ?? "");
                                  const isStatus = col === "Status";
                                  const isSat = col === "Day" && val === "Saturday";
                                  return (
                                    <td
                                      key={col}
                                      className={`border border-slate-200 px-2.5 py-1 text-[11px] ${
                                        col === "Date" || col === "Day" || col.includes("Check") || col.includes("Hours") || col.includes("OT")
                                          ? "whitespace-nowrap"
                                          : ""
                                      } ${columnColorClass(col)}`}
                                    >
                                      {isStatus ? (
                                        renderReportStatusBadge(val)
                                      ) : isSat ? (
                                        <span className="font-semibold text-amber-900 bg-amber-100 px-1.5 py-0.5 rounded text-[11px]">
                                          {val}
                                        </span>
                                      ) : (
                                        val || "-"
                                      )}
                                    </td>
                                  );
                                })}
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>

                      {/* Sheet Footer */}
                      <div className="border-t border-slate-200 pt-2.5 mt-3 text-[11px] text-slate-400 flex items-center justify-between">
                        <span>E-Attendance System • Confidential Report</span>
                        <span className="font-medium text-slate-600">
                          Sheet {totalSheets > 1 ? `${previewSheetIndex + 1} of ${totalSheets}` : "1 of 1"}
                        </span>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        );
      })() : null}
    </div>
  );
}
