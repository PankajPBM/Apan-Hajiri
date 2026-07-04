import { adToBs, formatBs, fromAdString, toAdString, type CalendarType } from "@/components/date/bs-converter";

// DEVICE COMMUNICATION RULE: all dates sent to ZKTeco devices (or to the backend for
// device commands) MUST be AD (Gregorian) format — YYYY-MM-DD — regardless of which
// calendar the user has selected in the UI.  Use toDeviceAdDate() at every API boundary
// that involves device commands, attendance sync, or time-range queries sent to the server.

const CALENDAR_STORAGE_KEY = "pb_attendance_calendar_type";

type DateLike = Date | string | number | null | undefined;

function parseDate(value: DateLike): Date | null {
  if (value === null || value === undefined) return null;
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value;
  if (typeof value === "string" && /^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return fromAdString(value);
  }
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

export function getCalendarTypeFromStorage(fallback: CalendarType = "bs"): CalendarType {
  if (typeof window === "undefined") return fallback;
  try {
    const saved = window.localStorage.getItem(CALENDAR_STORAGE_KEY);
    return saved === "ad" || saved === "bs" ? saved : fallback;
  } catch {
    return fallback;
  }
}

export function formatCalendarDate(value: DateLike, calendarType: CalendarType, fallback = "-"): string {
  const date = parseDate(value);
  if (!date) return fallback;
  if (calendarType === "bs") {
    const bs = adToBs(date);
    if (bs) return formatBs(bs);
  }
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

export function formatCalendarDateTime(value: DateLike, calendarType: CalendarType, fallback = "-"): string {
  const date = parseDate(value);
  if (!date) return fallback;
  const timeText = date.toLocaleTimeString([], {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  });
  if (calendarType === "bs") {
    const bs = adToBs(date);
    if (bs) return `${formatBs(bs)} ${timeText}`;
  }
  return date.toLocaleString();
}

export function formatCalendarMonth(value: string, calendarType: CalendarType): string {
  if (!/^\d{4}-\d{2}$/.test(value)) return value;
  if (calendarType === "ad") return value;
  const parsed = fromAdString(`${value}-01`);
  if (!parsed) return value;
  const bs = adToBs(parsed);
  if (!bs) return value;
  return `${bs.year}-${String(bs.month).padStart(2, "0")}`;
}

/**
 * Converts any date value to an AD (Gregorian) YYYY-MM-DD string.
 *
 * Use this function whenever a date is destined for:
 *   - device commands (DATA QUERY ATTLOG, ENROLL_FP, etc.)
 *   - backend API parameters that the server forwards to a ZKTeco device
 *   - attendance sync start/end ranges
 *
 * The active calendar preference (BS/AD) is intentionally ignored — ZKTeco
 * firmware only understands Gregorian dates.
 */
export function toDeviceAdDate(value: DateLike): string {
  const date = parseDate(value);
  if (!date) return "";
  return toAdString(date);
}
