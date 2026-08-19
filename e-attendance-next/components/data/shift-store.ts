"use client";

import { resolveApiBases } from "@/lib/api-bases";

export type ShiftType = "general" | "night" | "flexible";

export type ShiftDefinition = {
  id: string;
  name: string;
  startTime: string; // HH:mm
  endTime: string; // HH:mm
  breakMinutes: number;
  lateGraceMinutes: number;
  earlyLeaveMinutes: number;
  minWorkMinutes: number;
  overtimeAfterMinutes: number;
  enableOvertime: boolean;
  allowGrace: boolean;
  shiftType: ShiftType;
  nightShift: boolean;
  isActive: boolean;
};

export type ShiftAssignmentTarget = "employee" | "department" | "group";

export type ShiftAssignment = {
  id: string;
  targetType: ShiftAssignmentTarget;
  employeeCode: string;
  employeeName: string;
  department: string;
  groupId: string;
  groupName: string;
  shiftId: string;
  effectiveFrom: string; // yyyy-MM-dd
  effectiveTo: string | null; // yyyy-MM-dd
  isActive: boolean;
};

export type ShiftGroup = {
  id: string;
  name: string;
  memberEmployeeCodes: string[];
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
};

export type TimetableDayRule = {
  dayOfWeek: number; // 0=Sun .. 6=Sat
  shiftId: string | null;
  isWeeklyOff: boolean;
  checkInTime: string; // HH:mm
  checkInStart: string; // HH:mm
  checkInEnd: string; // HH:mm
  checkOutStart: string; // HH:mm
  checkOutTime: string; // HH:mm
  checkOutEnd: string; // HH:mm
  crossDay: boolean;
  useBreakTime: boolean;
  breakTimeId: string | null;
  breakTimeIds: string[];
};

export type BreakTimeDefinition = {
  id: string;
  name: string;
  startTime: string; // HH:mm
  endTime: string; // HH:mm
  durationMinutes: number;
  calculateType: "auto_deduct" | "required_punch";
  minimumBreakMinutes: number;
  isActive: boolean;
};

export type TimetableDefinition = {
  id: string;
  name: string;
  days: TimetableDayRule[];
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
};

export type ShiftPlanCycleType = "daily" | "weekly" | "monthly";

export type ShiftPlanWeeklyRule = {
  dayOfWeek: number; // 0=Sun .. 6=Sat
  timetableId: string;
};

export type ShiftPlanMonthlyRule = {
  dayOfMonth: number; // 1..31
  timetableId: string;
};

export type ShiftPlanDefinition = {
  id: string;
  name: string;
  cycleType: ShiftPlanCycleType;
  effectiveFrom: string; // yyyy-MM-dd
  effectiveTo: string | null; // yyyy-MM-dd
  isCyclic: boolean;
  isActive: boolean;
  dailyTimetableId: string | null;
  weeklyRules: ShiftPlanWeeklyRule[];
  monthlyRules: ShiftPlanMonthlyRule[];
  createdAt: string;
  updatedAt: string;
};


export type HolidayItem = {
  id: string;
  date: string; // yyyy-MM-dd
  name: string;
  description: string;
  isRecurring: boolean;
  department: string; // "all" for company-wide
  timeZoneId: string;
  isActive: boolean;
};

export type ShiftCalendarSettings = {
  weeklyOffDays: number[]; // 0=Sun .. 6=Sat
  overtimeEnabled: boolean;
  weeklyOffWorkAsOvertime: boolean;
  holidayWorkAsOvertime: boolean;
  holidays: HolidayItem[];
};

export type ShiftConfigSnapshot = {
  shiftDefinitions: ShiftDefinition[];
  shiftAssignments: ShiftAssignment[];
  shiftGroups: ShiftGroup[];
  calendar: ShiftCalendarSettings;
  timetables: TimetableDefinition[];
  breakDefinitions: BreakTimeDefinition[];
  shiftPlans: ShiftPlanDefinition[];
};

export type ScheduleResolution = {
  shift: ShiftDefinition | null;
  holiday: HolidayItem | null;
  weeklyOff: boolean;
  weeklyOffFromTimetable: boolean;
  timetable: TimetableDefinition | null;
  timetableName: string | null;
  timetableDay: TimetableDayRule | null;
};

export const WEEKDAY_LABELS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"] as const;

const SHIFTS_KEY = "pb_attendance_shifts";
const ASSIGNMENTS_KEY = "pb_attendance_shift_assignments";
const SHIFT_GROUPS_KEY = "pb_attendance_shift_groups";
const CALENDAR_KEY = "pb_attendance_shift_calendar";
const TIMETABLES_KEY = "pb_attendance_timetables";
const BREAKS_KEY = "pb_attendance_break_profiles";
const SHIFT_PLANS_KEY = "pb_attendance_shift_plans";
export const SHIFT_CALENDAR_UPDATED_EVENT = "pb_attendance_shift_calendar_updated";
const DEFAULT_SHIFT_EFFECTIVE_FROM = "2026-01-01";
let preferredApiBase = "";

function todayIsoDate(): string {
  return new Date().toISOString().slice(0, 10);
}

export function defaultShiftDefinitions(): ShiftDefinition[] {
  return [
    {
      id: "general",
      name: "General",
      startTime: "09:00",
      endTime: "18:00",
      breakMinutes: 60,
      lateGraceMinutes: 15,
      earlyLeaveMinutes: 10,
      minWorkMinutes: 360,
      overtimeAfterMinutes: 0,
      enableOvertime: true,
      allowGrace: true,
      shiftType: "general",
      nightShift: false,
      isActive: true,
    },
  ];
}

export function defaultBreakDefinitions(): BreakTimeDefinition[] {
  return [];
}

function defaultTimetableDay(
  dayOfWeek: number,
  fallbackShiftId: string,
): TimetableDayRule {
  const isWeeklyOff = dayOfWeek === 6; // Saturday is the default weekly off
  return {
    dayOfWeek,
    shiftId: isWeeklyOff ? null : fallbackShiftId,
    isWeeklyOff,
    checkInTime: "09:00",
    checkInStart: "05:00",
    checkInEnd: "09:15",
    checkOutStart: "15:45",
    checkOutTime: "17:00",
    checkOutEnd: "23:59",
    crossDay: false,
    useBreakTime: false,
    breakTimeId: null,
    breakTimeIds: [],
  };
}

export function defaultTimetableDefinitions(
  shifts: ShiftDefinition[] = defaultShiftDefinitions(),
): TimetableDefinition[] {
  const defaultShiftId =
    shifts.find((x) => x.isActive)?.id ??
    shifts[0]?.id ??
    "general";
  const now = new Date().toISOString();
  return [
    {
      id: "default-weekly",
      name: "General",
      isActive: true,
      createdAt: now,
      updatedAt: now,
      days: Array.from({ length: 7 }, (_, day) => defaultTimetableDay(day, defaultShiftId)),
    },
  ];
}

function preferredTimetableId(timetables: TimetableDefinition[]): string {
  return timetables.find((x) => x.isActive && x.id === "default-weekly")?.id
    ?? timetables.find((x) => x.isActive)?.id
    ?? timetables[0]?.id
    ?? "";
}

export function defaultShiftPlanDefinitions(
  timetables: TimetableDefinition[] = defaultTimetableDefinitions(),
): ShiftPlanDefinition[] {
  const fallbackTimetableId = preferredTimetableId(timetables);
  const now = new Date().toISOString();
  const today = todayIsoDate();
  return [
    {
      id: "default-shift-plan",
      name: "Default Shift",
      cycleType: "weekly",
      effectiveFrom: DEFAULT_SHIFT_EFFECTIVE_FROM,
      effectiveTo: today,
      isCyclic: true,
      isActive: true,
      dailyTimetableId: fallbackTimetableId || null,
      weeklyRules: Array.from({ length: 7 }, (_, dayOfWeek) => ({
        dayOfWeek,
        timetableId: dayOfWeek === 6 ? "" : fallbackTimetableId,
      })),
      monthlyRules: Array.from({ length: 31 }, (_, i) => ({
        dayOfMonth: i + 1,
        timetableId: fallbackTimetableId,
      })),
      createdAt: now,
      updatedAt: now,
    },
  ];
}

function parseJson<T>(raw: string | null, fallback: T): T {
  if (!raw) return fallback;
  try {
    const parsed = JSON.parse(raw) as T;
    return parsed;
  } catch {
    return fallback;
  }
}

async function shiftRequest<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch shift configuration.";

  for (const base of resolveApiBases({ preferredBase: preferredApiBase, includeCurrentOrigin: true })) {
    try {
      const response = await fetch(`${base}${path}`, {
        ...init,
        headers: {
          "Content-Type": "application/json",
          ...(init?.headers ?? {}),
        },
        cache: "no-store",
      });

      if (response.ok) {
        const contentType = response.headers.get("content-type") ?? "";
        if (!contentType.includes("application/json")) {
          lastError = `Expected JSON response, received ${contentType || "HTML"}`;
          continue;
        }
        preferredApiBase = base;
        return (await response.json()) as T;
      }

      try {
        const payload = await response.json();
        if (payload && typeof payload === "object" && typeof (payload as { message?: unknown }).message === "string") {
          lastError = (payload as { message: string }).message;
        } else {
          lastError = `Request failed (${response.status})`;
        }
      } catch {
        const text = await response.text();
        lastError = text.trim() || `Request failed (${response.status})`;
      }
    } catch (error) {
      lastError = error instanceof Error ? error.message : lastError;
    }
  }

  throw new Error(lastError);
}

function hasWindow() {
  return typeof window !== "undefined" && typeof window.localStorage !== "undefined";
}

function asString(value: unknown, fallback = ""): string {
  return typeof value === "string" ? value : fallback;
}

function asInt(value: unknown, fallback = 0): number {
  if (typeof value === "number" && Number.isFinite(value)) return Math.round(value);
  if (typeof value === "string") {
    const parsed = Number.parseInt(value, 10);
    if (Number.isFinite(parsed)) return parsed;
  }
  return fallback;
}

function asBool(value: unknown, fallback = false): boolean {
  if (typeof value === "boolean") return value;
  if (typeof value === "number") return value !== 0;
  if (typeof value === "string") {
    const val = value.trim().toLowerCase();
    if (val === "true" || val === "1" || val === "yes") return true;
    if (val === "false" || val === "0" || val === "no") return false;
  }
  return fallback;
}

function clampNonNegative(value: number): number {
  return Number.isFinite(value) ? Math.max(0, value) : 0;
}

function normalizeTime(value: unknown, fallback: string): string {
  const raw = asString(value, fallback);
  const [hRaw, mRaw] = raw.split(":");
  const h = Number.parseInt(hRaw ?? "0", 10);
  const m = Number.parseInt(mRaw ?? "0", 10);
  if (!Number.isFinite(h) || !Number.isFinite(m)) return fallback;
  const safeH = Math.min(23, Math.max(0, h));
  const safeM = Math.min(59, Math.max(0, m));
  return `${String(safeH).padStart(2, "0")}:${String(safeM).padStart(2, "0")}`;
}

function normalizeDate(value: unknown): string {
  const raw = asString(value, "");
  if (/^\d{4}-\d{2}-\d{2}$/.test(raw)) return raw;
  const d = new Date(raw);
  if (Number.isNaN(d.getTime())) return "1970-01-01";
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function normalizeOptionalDate(value: unknown): string | null {
  const raw = asString(value, "").trim();
  if (!raw) return null;
  return normalizeDate(raw);
}

function normalizeShiftType(value: unknown, nightShift: boolean): ShiftType {
  const raw = asString(value, "").toLowerCase();
  if (raw === "general" || raw === "night" || raw === "flexible") return raw;
  return nightShift ? "night" : "general";
}

function normalizeDayOfWeek(value: unknown, fallback: number): number {
  const parsed = asInt(value, fallback);
  if (parsed < 0) return 0;
  if (parsed > 6) return 6;
  return parsed;
}

function normalizeDayOfMonth(value: unknown, fallback: number): number {
  const parsed = asInt(value, fallback);
  if (parsed < 1) return 1;
  if (parsed > 31) return 31;
  return parsed;
}

function normalizeShiftPlanCycleType(
  value: unknown,
  fallback: ShiftPlanCycleType = "weekly",
): ShiftPlanCycleType {
  const raw = asString(value, "").toLowerCase();
  if (raw === "daily" || raw === "weekly" || raw === "monthly") return raw;
  return fallback;
}

function normalizeShiftPlanTimetableRef(
  timetableId: unknown,
  timetableIds: Set<string>,
  fallbackTimetableId: string,
  allowBlank = false,
): string {
  const id = asString(timetableId, "").trim();
  if (!id) return allowBlank ? "" : fallbackTimetableId;
  if (id && timetableIds.has(id)) return id;
  return allowBlank ? "" : fallbackTimetableId;
}

function normalizeShiftPlanWeeklyRules(
  rawRules: unknown,
  timetableIds: Set<string>,
  fallbackTimetableId: string,
): ShiftPlanWeeklyRule[] {
  const byDay = new Map<number, ShiftPlanWeeklyRule>();
  if (Array.isArray(rawRules)) {
    for (let i = 0; i < rawRules.length; i += 1) {
      const raw = (rawRules[i] ?? {}) as Record<string, unknown>;
      const dayOfWeek = normalizeDayOfWeek(raw.dayOfWeek ?? raw.day, i);
      byDay.set(dayOfWeek, {
        dayOfWeek,
        timetableId: normalizeShiftPlanTimetableRef(
          raw.timetableId,
          timetableIds,
          fallbackTimetableId,
          dayOfWeek === 6,
        ),
      });
    }
  }

  return Array.from({ length: 7 }, (_, dayOfWeek) => {
    const found = byDay.get(dayOfWeek);
    if (found) return found;
    return { dayOfWeek, timetableId: dayOfWeek === 6 ? "" : fallbackTimetableId };
  });
}

function normalizeShiftPlanMonthlyRules(
  rawRules: unknown,
  timetableIds: Set<string>,
  fallbackTimetableId: string,
): ShiftPlanMonthlyRule[] {
  const byDay = new Map<number, ShiftPlanMonthlyRule>();
  if (Array.isArray(rawRules)) {
    for (let i = 0; i < rawRules.length; i += 1) {
      const raw = (rawRules[i] ?? {}) as Record<string, unknown>;
      const dayOfMonth = normalizeDayOfMonth(raw.dayOfMonth ?? raw.day, i + 1);
      byDay.set(dayOfMonth, {
        dayOfMonth,
        timetableId: normalizeShiftPlanTimetableRef(
          raw.timetableId,
          timetableIds,
          fallbackTimetableId,
        ),
      });
    }
  }

  return Array.from({ length: 31 }, (_, index) => {
    const dayOfMonth = index + 1;
    const found = byDay.get(dayOfMonth);
    if (found) return found;
    return { dayOfMonth, timetableId: fallbackTimetableId };
  });
}

function normalizeWeeklyOffDays(raw: unknown): number[] {
  if (!Array.isArray(raw)) return [6];
  return Array.from(
    new Set(
      raw
        .map((x) => asInt(x, -1))
        .filter((x) => Number.isFinite(x) && x >= 0 && x <= 6),
    ),
  ).sort((a, b) => a - b);
}

function normalizeShiftRow(row: unknown, index: number): ShiftDefinition {
  const raw = (row ?? {}) as Record<string, unknown>;
  const idRaw = asString(raw.id, "").trim();
  const nameRaw = asString(raw.name, "").trim();
  const startTime = normalizeTime(raw.startTime, "09:00");
  const endTime = normalizeTime(raw.endTime, "18:00");
  const inferredNight = parseTimeToMinutes(endTime) <= parseTimeToMinutes(startTime);
  const nightShift = asBool(raw.nightShift, inferredNight);
  const shiftType = normalizeShiftType(raw.shiftType, nightShift);

  return {
    id: idRaw || `shift-${index + 1}`,
    name: nameRaw || `Shift ${index + 1}`,
    startTime,
    endTime,
    breakMinutes: clampNonNegative(
      asInt(raw.breakMinutes, asInt(raw.breakTimeMinutes, asInt(raw.breakTimeMins, 0))),
    ),
    lateGraceMinutes: clampNonNegative(
      asInt(raw.lateGraceMinutes, asInt(raw.graceMinutes, 10)),
    ),
    earlyLeaveMinutes: clampNonNegative(asInt(raw.earlyLeaveMinutes, 10)),
    minWorkMinutes: clampNonNegative(
      asInt(raw.minWorkMinutes, asInt(raw.minimumWorkMinutes, asInt(raw.minWorkHours, 6) * 60)),
    ),
    overtimeAfterMinutes: clampNonNegative(
      asInt(raw.overtimeAfterMinutes, asInt(raw.overtimeAfterHours, 0) * 60),
    ),
    enableOvertime: asBool(raw.enableOvertime, true),
    allowGrace: asBool(raw.allowGrace, true),
    shiftType,
    nightShift: shiftType === "night" ? true : nightShift,
    isActive: asBool(raw.isActive, true),
  };
}

function normalizeAssignmentRow(row: unknown, index: number): ShiftAssignment {
  const raw = (row ?? {}) as Record<string, unknown>;
  const targetTypeRaw = asString(raw.targetType, "").toLowerCase();
  const targetType: ShiftAssignmentTarget =
    targetTypeRaw === "department" ? "department" : targetTypeRaw === "group" ? "group" : "employee";
  const effectiveTo = normalizeOptionalDate(
    raw.effectiveTo ?? raw.effectiveTill ?? raw.effectiveUntil ?? raw.effectiveEnd,
  );

  return {
    id: asString(raw.id, "") || `assign-${Date.now()}-${index}`,
    targetType,
    employeeCode: asString(raw.employeeCode, "").trim(),
    employeeName: asString(raw.employeeName, "").trim(),
    department: asString(raw.department, "").trim(),
    groupId: asString(raw.groupId, "").trim(),
    groupName: asString(raw.groupName, "").trim(),
    shiftId: asString(raw.shiftId, "").trim(),
    effectiveFrom: normalizeDate(raw.effectiveFrom),
    effectiveTo,
    isActive: asBool(raw.isActive, true),
  };
}

function normalizeTimetableDayRow(
  row: unknown,
  index: number,
  fallbackShiftId: string,
): TimetableDayRule {
  const raw = (row ?? {}) as Record<string, unknown>;
  const dayOfWeek = normalizeDayOfWeek(
    raw.dayOfWeek ?? raw.day ?? raw.weekday,
    index,
  );
  const shiftRaw = asString(raw.shiftId, "").trim();
  const isWeeklyOff = asBool(raw.isWeeklyOff, shiftRaw.length === 0);
  const defaults = defaultTimetableDay(dayOfWeek, fallbackShiftId);
  const breakTimeId = asString(raw.breakTimeId, "").trim() || null;
  const breakTimeIdsRaw = Array.isArray(raw.breakTimeIds) ? raw.breakTimeIds : [];
  const breakTimeIds = Array.from(
    new Set(
      breakTimeIdsRaw
        .map((x) => asString(x, "").trim())
        .filter((x) => x.length > 0),
    ),
  );
  if (breakTimeIds.length === 0 && breakTimeId) {
    breakTimeIds.push(breakTimeId);
  }
  return {
    dayOfWeek,
    shiftId: isWeeklyOff ? null : shiftRaw || fallbackShiftId,
    isWeeklyOff,
    checkInTime: normalizeTime(raw.checkInTime, defaults.checkInTime),
    checkInStart: normalizeTime(raw.checkInStart, defaults.checkInStart),
    checkInEnd: normalizeTime(raw.checkInEnd, defaults.checkInEnd),
    checkOutStart: normalizeTime(raw.checkOutStart, defaults.checkOutStart),
    checkOutTime: normalizeTime(raw.checkOutTime, defaults.checkOutTime),
    checkOutEnd: normalizeTime(raw.checkOutEnd, defaults.checkOutEnd),
    crossDay: asBool(raw.crossDay, defaults.crossDay),
    useBreakTime: asBool(raw.useBreakTime, breakTimeIds.length > 0 ? true : defaults.useBreakTime),
    breakTimeId,
    breakTimeIds,
  };
}

function completeTimetableDays(days: TimetableDayRule[], fallbackShiftId: string): TimetableDayRule[] {
  const byDay = new Map<number, TimetableDayRule>();
  for (const day of days) {
    if (!Number.isFinite(day.dayOfWeek) || day.dayOfWeek < 0 || day.dayOfWeek > 6) continue;
    const defaults = defaultTimetableDay(day.dayOfWeek, fallbackShiftId);
    byDay.set(day.dayOfWeek, {
      dayOfWeek: day.dayOfWeek,
      shiftId: day.isWeeklyOff ? null : (day.shiftId || fallbackShiftId),
      isWeeklyOff: day.isWeeklyOff,
      checkInTime: normalizeTime(day.checkInTime, defaults.checkInTime),
      checkInStart: normalizeTime(day.checkInStart, defaults.checkInStart),
      checkInEnd: normalizeTime(day.checkInEnd, defaults.checkInEnd),
      checkOutStart: normalizeTime(day.checkOutStart, defaults.checkOutStart),
      checkOutTime: normalizeTime(day.checkOutTime, defaults.checkOutTime),
      checkOutEnd: normalizeTime(day.checkOutEnd, defaults.checkOutEnd),
      crossDay: asBool(day.crossDay, defaults.crossDay),
      useBreakTime: asBool(day.useBreakTime, (day.breakTimeIds?.length ?? 0) > 0 ? true : defaults.useBreakTime),
      breakTimeId: asString(day.breakTimeId, "").trim() || null,
      breakTimeIds: Array.from(
        new Set(
          (Array.isArray(day.breakTimeIds) ? day.breakTimeIds : [])
            .map((x) => asString(x, "").trim())
            .filter((x) => x.length > 0),
        ),
      ),
    });
  }
  return Array.from({ length: 7 }, (_, day) => {
    const found = byDay.get(day);
    if (found) return found;
    return defaultTimetableDay(day, fallbackShiftId);
  });
}

function normalizeTimetableRow(
  row: unknown,
  index: number,
  fallbackShiftId: string,
): TimetableDefinition {
  const raw = (row ?? {}) as Record<string, unknown>;
  const id = asString(raw.id, "").trim() || `timetable-${index + 1}`;
  const name = asString(raw.name, "").trim() || `Timetable ${index + 1}`;
  const now = new Date().toISOString();
  const createdAt = asString(raw.createdAt, "").trim() || now;
  const updatedAt = asString(raw.updatedAt, "").trim() || now;
  const daysRaw = Array.isArray(raw.days)
    ? raw.days
    : Array.isArray(raw.timetableDays)
      ? raw.timetableDays
      : [];
  const normalizedDays = completeTimetableDays(
    daysRaw.map((x, i) => normalizeTimetableDayRow(x, i, fallbackShiftId)),
    fallbackShiftId,
  );
  return {
    id,
    name,
    days: normalizedDays,
    isActive: asBool(raw.isActive, true),
    createdAt,
    updatedAt,
  };
}

function normalizeShiftPlanRow(
  row: unknown,
  index: number,
  timetables: TimetableDefinition[],
): ShiftPlanDefinition {
  const raw = (row ?? {}) as Record<string, unknown>;
  const fallbackTimetableId = preferredTimetableId(timetables);
  const timetableIds = new Set(timetables.map((x) => x.id));
  const now = new Date().toISOString();
  const cycleType = normalizeShiftPlanCycleType(raw.cycleType, "weekly");
  const weeklyRulesRaw = Array.isArray(raw.weeklyRules)
    ? raw.weeklyRules
    : Array.isArray(raw.weeklyTimetables)
      ? raw.weeklyTimetables
      : [];
  const monthlyRulesRaw = Array.isArray(raw.monthlyRules)
    ? raw.monthlyRules
    : Array.isArray(raw.monthlyTimetables)
      ? raw.monthlyTimetables
      : [];

  const weeklyRules = normalizeShiftPlanWeeklyRules(
    weeklyRulesRaw,
    timetableIds,
    fallbackTimetableId,
  );
  const monthlyRules = normalizeShiftPlanMonthlyRules(
    monthlyRulesRaw,
    timetableIds,
    fallbackTimetableId,
  );

  return {
    id: asString(raw.id, "").trim() || `shift-plan-${index + 1}`,
    name: asString(raw.name, "").trim() || `Shift ${index + 1}`,
    cycleType,
    effectiveFrom: normalizeDate(asString(raw.effectiveFrom, DEFAULT_SHIFT_EFFECTIVE_FROM)),
    effectiveTo: normalizeOptionalDate(raw.effectiveTo),
    isCyclic: asBool(raw.isCyclic, true),
    isActive: asBool(raw.isActive, true),
    dailyTimetableId: normalizeShiftPlanTimetableRef(
      raw.dailyTimetableId,
      timetableIds,
      fallbackTimetableId,
    ) || null,
    weeklyRules,
    monthlyRules,
    createdAt: asString(raw.createdAt, "").trim() || now,
    updatedAt: asString(raw.updatedAt, "").trim() || now,
  };
}

function migrateLegacyDefaultTimetable(
  timetable: TimetableDefinition,
  fallbackShiftId: string,
): TimetableDefinition {
  if (timetable.id !== "default-weekly") return timetable;
  const weeklyOffDays = timetable.days
    .filter((x) => x.isWeeklyOff)
    .map((x) => x.dayOfWeek)
    .sort((a, b) => a - b);
  if (weeklyOffDays.length !== 1 || weeklyOffDays[0] !== 0) return timetable;

  return {
    ...timetable,
    updatedAt: new Date().toISOString(),
    days: timetable.days.map((day) => {
      if (day.dayOfWeek === 0) {
        return {
          ...day,
          isWeeklyOff: false,
          shiftId: day.shiftId || fallbackShiftId,
        };
      }
      if (day.dayOfWeek === 6) {
        return {
          ...day,
          isWeeklyOff: true,
          shiftId: null,
        };
      }
      return day;
    }),
  };
}

function migrateLegacyDefaultWindow(
  timetable: TimetableDefinition,
): TimetableDefinition {
  let changed = false;
  const days = timetable.days.map((day) => {
    const isLegacyWindow =
      day.checkInTime === "09:00" &&
      day.checkInStart === "08:30" &&
      day.checkInEnd === "10:30" &&
      day.checkOutStart === "17:00" &&
      day.checkOutTime === "18:00" &&
      day.checkOutEnd === "23:00";
    const isPreviousDefaultWindow =
      day.checkInTime === "10:00" &&
      day.checkInStart === "00:00" &&
      day.checkInEnd === "10:30" &&
      day.checkOutStart === "17:00" &&
      day.checkOutTime === "17:00" &&
      day.checkOutEnd === "00:59";
    const isCurrentDefaultWindow =
      day.checkInTime === "10:00" &&
      day.checkInStart === "00:00" &&
      day.checkInEnd === "10:30" &&
      day.checkOutStart === "17:00" &&
      day.checkOutTime === "23:59" &&
      day.checkOutEnd === "00:59";
    const isRecentDefaultWindow =
      day.checkInTime === "10:00" &&
      day.checkInStart === "00:00" &&
      day.checkInEnd === "10:15" &&
      day.checkOutStart === "16:15" &&
      day.checkOutTime === "17:00" &&
      day.checkOutEnd === "00:59";
    // The previously-shipped default (10:00 / 00:00-10:15 / 16:45-23:59... checkOutEnd 00:59).
    const isPriorDefaultWindow =
      day.checkInTime === "10:00" &&
      day.checkInStart === "00:00" &&
      day.checkInEnd === "10:15" &&
      day.checkOutStart === "16:45" &&
      day.checkOutTime === "17:00" &&
      day.checkOutEnd === "00:59";

    if (!isLegacyWindow && !isPreviousDefaultWindow && !isCurrentDefaultWindow && !isRecentDefaultWindow && !isPriorDefaultWindow) return day;
    changed = true;
    return {
      ...day,
      checkInTime: "09:00",
      checkInStart: "05:00",
      checkInEnd: "09:15",
      checkOutStart: "15:45",
      checkOutTime: "17:00",
      checkOutEnd: "23:59",
    };
  });

  if (!changed) return timetable;
  return {
    ...timetable,
    updatedAt: new Date().toISOString(),
    days,
  };
}

function migrateDefaultTimetableName(
  timetable: TimetableDefinition,
): TimetableDefinition {
  // Do not force-rename default timetable — allow user edits to persist
  return timetable;
}

function migrateDefaultShiftPlan(
  plan: ShiftPlanDefinition,
  fallbackTimetableId: string,
): ShiftPlanDefinition {
  if (plan.id !== "default-shift-plan") return plan;
  // Only fill in missing timetable references; do not force-rename or change other user-editable fields
  const weeklyRules = Array.from({ length: 7 }, (_, dayOfWeek) => {
    const current = plan.weeklyRules.find((x) => x.dayOfWeek === dayOfWeek);
    return {
      dayOfWeek,
      timetableId: dayOfWeek === 6
        ? (current?.timetableId ?? "")
        : (current?.timetableId || fallbackTimetableId),
    };
  });
  const hasChanges = weeklyRules.some((r, i) => r.timetableId !== (plan.weeklyRules[i]?.timetableId ?? ""));
  if (!hasChanges) return plan;
  return { ...plan, weeklyRules, updatedAt: new Date().toISOString() };
}

function normalizeHolidayRow(row: unknown, index: number): HolidayItem {
  const raw = (row ?? {}) as Record<string, unknown>;
  return {
    id: asString(raw.id, "") || `holiday-${index + 1}`,
    date: normalizeDate(raw.date),
    name: asString(raw.name, "").trim() || `Holiday ${index + 1}`,
    description: asString(raw.description, "").trim(),
    isRecurring: asBool(raw.isRecurring, false),
    department: asString(raw.department, "all").trim() || "all",
    timeZoneId: asString(raw.timeZoneId, "1").trim() || "1",
    isActive: asBool(raw.isActive, true),
  };
}

function normalizeBreakRow(row: unknown, index: number): BreakTimeDefinition {
  const raw = (row ?? {}) as Record<string, unknown>;
  const calculateTypeRaw = asString(raw.calculateType, "").trim().toLowerCase();
  const calculateType: BreakTimeDefinition["calculateType"] =
    calculateTypeRaw === "required_punch" ? "required_punch" : "auto_deduct";
  return {
    id: asString(raw.id, "").trim() || `break-${index + 1}`,
    name: asString(raw.name, "").trim() || `Break ${index + 1}`,
    startTime: normalizeTime(raw.startTime, "13:00"),
    endTime: normalizeTime(raw.endTime, "14:00"),
    durationMinutes: clampNonNegative(asInt(raw.durationMinutes, 60)),
    calculateType,
    minimumBreakMinutes: clampNonNegative(asInt(raw.minimumBreakMinutes, 0)),
    isActive: asBool(raw.isActive, true),
  };
}

function normalizeShiftGroupRow(row: unknown, index: number): ShiftGroup {
  const raw = (row ?? {}) as Record<string, unknown>;
  const now = new Date().toISOString();
  const id = asString(raw.id, "").trim() || `group-${index + 1}`;
  const name = asString(raw.name, "").trim() || `Group ${index + 1}`;
  const createdAt = asString(raw.createdAt, "").trim() || now;
  const updatedAt = asString(raw.updatedAt, "").trim() || now;
  const memberRaw = Array.isArray(raw.memberEmployeeCodes)
    ? raw.memberEmployeeCodes
    : Array.isArray(raw.members)
      ? raw.members
      : Array.isArray(raw.employeeCodes)
        ? raw.employeeCodes
        : [];
  const memberEmployeeCodes = Array.from(
    new Set(
      memberRaw
        .map((item) => asString(item, "").trim())
        .filter((item) => item.length > 0),
    ),
  );

  return {
    id,
    name,
    memberEmployeeCodes,
    isActive: asBool(raw.isActive, true),
    createdAt,
    updatedAt,
  };
}

export function readShiftDefinitions(): ShiftDefinition[] {
  if (!hasWindow()) return defaultShiftDefinitions();
  const rows = parseJson<unknown[]>(window.localStorage.getItem(SHIFTS_KEY), []);
  if (!Array.isArray(rows) || rows.length === 0) {
    const defaults = defaultShiftDefinitions();
    saveShiftDefinitions(defaults);
    return defaults;
  }

  const normalized = rows.map((row, index) => normalizeShiftRow(row, index));
  if (JSON.stringify(rows) !== JSON.stringify(normalized)) {
    saveShiftDefinitions(normalized);
  }
  return normalized;
}

export function saveShiftDefinitions(rows: ShiftDefinition[]) {
  if (!hasWindow()) return;
  const normalized = rows.map((row, index) => normalizeShiftRow(row, index));
  window.localStorage.setItem(SHIFTS_KEY, JSON.stringify(normalized));
}

export function readShiftAssignments(): ShiftAssignment[] {
  if (!hasWindow()) return [];
  const rows = parseJson<unknown[]>(
    window.localStorage.getItem(ASSIGNMENTS_KEY),
    [],
  );
  if (!Array.isArray(rows)) return [];
  const normalized = rows
    .map((row, index) => normalizeAssignmentRow(row, index))
    .filter((row) => row.shiftId.length > 0 && row.effectiveFrom.length > 0);
  if (JSON.stringify(rows) !== JSON.stringify(normalized)) {
    saveShiftAssignments(normalized);
  }
  return normalized;
}

export function saveShiftAssignments(rows: ShiftAssignment[]) {
  if (!hasWindow()) return;
  const normalized = rows.map((row, index) => normalizeAssignmentRow(row, index));
  window.localStorage.setItem(ASSIGNMENTS_KEY, JSON.stringify(normalized));
}

export function readShiftGroups(): ShiftGroup[] {
  if (!hasWindow()) return [];
  const rows = parseJson<unknown[]>(
    window.localStorage.getItem(SHIFT_GROUPS_KEY),
    [],
  );
  if (!Array.isArray(rows)) return [];
  const normalized = rows.map((row, index) => normalizeShiftGroupRow(row, index));
  if (JSON.stringify(rows) !== JSON.stringify(normalized)) {
    saveShiftGroups(normalized);
  }
  return normalized;
}

export function saveShiftGroups(rows: ShiftGroup[]) {
  if (!hasWindow()) return;
  const normalized = rows.map((row, index) => normalizeShiftGroupRow(row, index));
  window.localStorage.setItem(SHIFT_GROUPS_KEY, JSON.stringify(normalized));
}

export function readTimetableDefinitions(
  shifts: ShiftDefinition[] = readShiftDefinitions(),
): TimetableDefinition[] {
  if (!hasWindow()) return defaultTimetableDefinitions(shifts);
  const rows = parseJson<unknown[]>(window.localStorage.getItem(TIMETABLES_KEY), []);
  const fallbackShiftId = shifts.find((x) => x.isActive)?.id ?? shifts[0]?.id ?? "general";
  if (!Array.isArray(rows) || rows.length === 0) {
    const defaults = defaultTimetableDefinitions(shifts);
    saveTimetableDefinitions(defaults, shifts);
    return defaults;
  }

  const normalized = rows.map((row, index) =>
    normalizeTimetableRow(row, index, fallbackShiftId),
  );
  const migratedDefaultWeekly = normalized.map((row) =>
    migrateLegacyDefaultTimetable(row, fallbackShiftId),
  );
  const migratedWindow = migratedDefaultWeekly.map((row) =>
    migrateLegacyDefaultWindow(row),
  );
  const migrated = migratedWindow.map((row) =>
    migrateDefaultTimetableName(row),
  );
  if (JSON.stringify(rows) !== JSON.stringify(migrated)) {
    saveTimetableDefinitions(migrated, shifts);
  }
  return migrated;
}

export function saveTimetableDefinitions(
  rows: TimetableDefinition[],
  shifts: ShiftDefinition[] = readShiftDefinitions(),
) {
  if (!hasWindow()) return;
  const fallbackShiftId = shifts.find((x) => x.isActive)?.id ?? shifts[0]?.id ?? "general";
  const normalized = rows.map((row, index) =>
    normalizeTimetableRow(
      {
        ...row,
        updatedAt: row.updatedAt || new Date().toISOString(),
      },
      index,
      fallbackShiftId,
    ),
  );
  window.localStorage.setItem(TIMETABLES_KEY, JSON.stringify(normalized));
}

export function readShiftPlanDefinitions(
  timetables: TimetableDefinition[] = readTimetableDefinitions(),
): ShiftPlanDefinition[] {
  if (!hasWindow()) return defaultShiftPlanDefinitions(timetables);
  const rows = parseJson<unknown[]>(window.localStorage.getItem(SHIFT_PLANS_KEY), []);
  const fallbackTimetableId = preferredTimetableId(timetables);
  if (!Array.isArray(rows) || rows.length === 0) {
    const defaults = defaultShiftPlanDefinitions(timetables);
    saveShiftPlanDefinitions(defaults, timetables);
    return defaults;
  }
  const normalizedRows = rows.map((row, index) =>
    normalizeShiftPlanRow(row, index, timetables),
  );
  const normalized = normalizedRows.map((row) =>
    migrateDefaultShiftPlan(row, fallbackTimetableId),
  );
  if (JSON.stringify(rows) !== JSON.stringify(normalized)) {
    saveShiftPlanDefinitions(normalized, timetables);
  }
  return normalized;
}

export function saveShiftPlanDefinitions(
  rows: ShiftPlanDefinition[],
  timetables: TimetableDefinition[] = readTimetableDefinitions(),
) {
  if (!hasWindow()) return;
  const normalized = rows.map((row, index) =>
    normalizeShiftPlanRow(
      {
        ...row,
        updatedAt: row.updatedAt || new Date().toISOString(),
      },
      index,
      timetables,
    ),
  );
  window.localStorage.setItem(SHIFT_PLANS_KEY, JSON.stringify(normalized));
}

export function readBreakDefinitions(): BreakTimeDefinition[] {
  if (!hasWindow()) return defaultBreakDefinitions();
  const rows = parseJson<unknown[]>(window.localStorage.getItem(BREAKS_KEY), []);
  if (!Array.isArray(rows) || rows.length === 0) {
    const defaults = defaultBreakDefinitions();
    saveBreakDefinitions(defaults);
    return defaults;
  }
  const normalized = rows.map((row, index) => normalizeBreakRow(row, index));
  if (JSON.stringify(rows) !== JSON.stringify(normalized)) {
    saveBreakDefinitions(normalized);
  }
  return normalized;
}

export function saveBreakDefinitions(rows: BreakTimeDefinition[]) {
  if (!hasWindow()) return;
  const normalized = rows.map((row, index) => normalizeBreakRow(row, index));
  window.localStorage.setItem(BREAKS_KEY, JSON.stringify(normalized));
}

export function defaultShiftCalendarSettings(): ShiftCalendarSettings {
  return {
    weeklyOffDays: [6],
    overtimeEnabled: true,
    weeklyOffWorkAsOvertime: true,
    holidayWorkAsOvertime: true,
    holidays: [],
  };
}

export function readShiftCalendarSettings(): ShiftCalendarSettings {
  if (!hasWindow()) return defaultShiftCalendarSettings();
  const raw = parseJson<Record<string, unknown>>(
    window.localStorage.getItem(CALENDAR_KEY),
    {},
  );
  const parsedWeeklyOffDays = normalizeWeeklyOffDays(raw.weeklyOffDays);
  const weeklyOffDays = parsedWeeklyOffDays.length === 1 && parsedWeeklyOffDays[0] === 0
    ? [6]
    : parsedWeeklyOffDays;
  const overtimeEnabled = asBool(raw.overtimeEnabled, true);
  const weeklyOffWorkAsOvertime = asBool(raw.weeklyOffWorkAsOvertime, true);
  const holidayWorkAsOvertime = asBool(raw.holidayWorkAsOvertime, true);
  const holidaysRaw = Array.isArray(raw.holidays) ? raw.holidays : [];

  const holidays = holidaysRaw.map((row, index) => normalizeHolidayRow(row, index));

  const normalized: ShiftCalendarSettings = {
    weeklyOffDays,
    overtimeEnabled,
    weeklyOffWorkAsOvertime,
    holidayWorkAsOvertime,
    holidays,
  };

  if (JSON.stringify(raw) !== JSON.stringify(normalized)) {
    saveShiftCalendarSettings(normalized);
  }

  return normalized;
}

export function saveShiftCalendarSettings(settings: ShiftCalendarSettings) {
  if (!hasWindow()) return;
  const normalized: ShiftCalendarSettings = {
    weeklyOffDays: normalizeWeeklyOffDays(settings.weeklyOffDays),
    overtimeEnabled: asBool(settings.overtimeEnabled, true),
    weeklyOffWorkAsOvertime: asBool(settings.weeklyOffWorkAsOvertime, true),
    holidayWorkAsOvertime: asBool(settings.holidayWorkAsOvertime, true),
    holidays: (settings.holidays ?? []).map((row, index) => normalizeHolidayRow(row, index)),
  };
  window.localStorage.setItem(CALENDAR_KEY, JSON.stringify(normalized));
  window.dispatchEvent(new Event(SHIFT_CALENDAR_UPDATED_EVENT));
}

export function syncShiftCalendarHolidays(holidays: HolidayItem[]) {
  const current = readShiftCalendarSettings();
  const nextCalendar = {
    ...current,
    holidays: holidays.map((row, index) => normalizeHolidayRow(row, index)),
  };
  saveShiftCalendarSettings(nextCalendar);
  void saveShiftConfigToApi({ calendar: nextCalendar }).catch(() => {
    // Keep the local holiday mirror even if the server-side cache update fails.
  });
}

export function readShiftConfigSnapshot(): ShiftConfigSnapshot {
  const shiftDefinitions = readShiftDefinitions();
  const timetables = readTimetableDefinitions(shiftDefinitions);
  return {
    shiftDefinitions,
    shiftAssignments: readShiftAssignments(),
    shiftGroups: readShiftGroups(),
    calendar: readShiftCalendarSettings(),
    timetables,
    breakDefinitions: readBreakDefinitions(),
    shiftPlans: readShiftPlanDefinitions(timetables),
  };
}

export function cacheShiftConfigSnapshot(snapshot: Partial<ShiftConfigSnapshot>): ShiftConfigSnapshot {
  const current = readShiftConfigSnapshot();
  const nextShiftDefinitions = snapshot.shiftDefinitions ?? current.shiftDefinitions;
  saveShiftDefinitions(nextShiftDefinitions);

  const nextShiftAssignments = snapshot.shiftAssignments ?? current.shiftAssignments;
  saveShiftAssignments(nextShiftAssignments);

  const nextShiftGroups = snapshot.shiftGroups ?? current.shiftGroups;
  saveShiftGroups(nextShiftGroups);

  const nextBreakDefinitions = snapshot.breakDefinitions ?? current.breakDefinitions;
  saveBreakDefinitions(nextBreakDefinitions);

  const nextTimetables = snapshot.timetables ?? current.timetables;
  saveTimetableDefinitions(nextTimetables, nextShiftDefinitions);

  const nextShiftPlans = snapshot.shiftPlans ?? current.shiftPlans;
  saveShiftPlanDefinitions(nextShiftPlans, nextTimetables);

  const nextCalendar = snapshot.calendar ?? current.calendar;
  saveShiftCalendarSettings(nextCalendar);

  const merged = readShiftConfigSnapshot();
  if (typeof window !== "undefined") {
    window.dispatchEvent(new Event(SHIFT_CALENDAR_UPDATED_EVENT));
  }
  return merged;
}

export async function loadShiftConfigFromApi(): Promise<ShiftConfigSnapshot> {
  const payload = await shiftRequest<Partial<ShiftConfigSnapshot>>("/api/app-config/shifts");
  return cacheShiftConfigSnapshot(payload ?? {});
}

export async function saveShiftConfigToApi(snapshot: Partial<ShiftConfigSnapshot>): Promise<ShiftConfigSnapshot> {
  const current = readShiftConfigSnapshot();
  const merged: ShiftConfigSnapshot = {
    shiftDefinitions: snapshot.shiftDefinitions ?? current.shiftDefinitions,
    shiftAssignments: snapshot.shiftAssignments ?? current.shiftAssignments,
    shiftGroups: snapshot.shiftGroups ?? current.shiftGroups,
    calendar: snapshot.calendar ?? current.calendar,
    timetables: snapshot.timetables ?? current.timetables,
    breakDefinitions: snapshot.breakDefinitions ?? current.breakDefinitions,
    shiftPlans: snapshot.shiftPlans ?? current.shiftPlans,
  };
  const saved = await shiftRequest<Partial<ShiftConfigSnapshot>>("/api/app-config/shifts", {
    method: "PUT",
    body: JSON.stringify(merged),
  });
  return cacheShiftConfigSnapshot(saved ?? merged);
}

export async function ensureDefaultDepartmentShiftAssignments(
  departments: Array<{ name: string }>,
  currentSnapshot: ShiftConfigSnapshot
): Promise<ShiftConfigSnapshot> {
  let changed = false;
  const nextAssignments = [...currentSnapshot.shiftAssignments];
  const defaultShiftId =
    currentSnapshot.shiftDefinitions.find((x) => x.isActive)?.id ??
    currentSnapshot.shiftDefinitions[0]?.id ??
    "general";

  for (const dept of departments) {
    const deptName = dept.name.trim();
    if (!deptName) continue;

    const existing = nextAssignments.find(
      (a) => a.targetType === "department" && a.department.toLowerCase() === deptName.toLowerCase()
    );

    if (!existing) {
      nextAssignments.push({
        id: `assign-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        targetType: "department",
        employeeCode: "",
        employeeName: "",
        department: deptName,
        groupId: "",
        groupName: "",
        shiftId: defaultShiftId,
        effectiveFrom: DEFAULT_SHIFT_EFFECTIVE_FROM,
        effectiveTo: null,
        isActive: true,
      });
      changed = true;
    }
  }

  if (changed) {
    return await saveShiftConfigToApi({ ...currentSnapshot, shiftAssignments: nextAssignments });
  }

  return currentSnapshot;
}


export function parseTimeToMinutes(value: string): number {
  const [hRaw, mRaw] = (value || "").split(":");
  const h = Number.parseInt(hRaw ?? "0", 10);
  const m = Number.parseInt(mRaw ?? "0", 10);
  if (!Number.isFinite(h) || !Number.isFinite(m)) return 0;
  return Math.min(23 * 60 + 59, Math.max(0, h * 60 + m));
}

export function minutesToTime(value: number): string {
  const safe = Math.max(0, Math.min(23 * 60 + 59, Math.round(value)));
  const h = Math.floor(safe / 60);
  const m = safe % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

export function shiftWindowMinutes(shift: ShiftDefinition): number {
  const start = parseTimeToMinutes(shift.startTime);
  const end = parseTimeToMinutes(shift.endTime);
  if (shift.nightShift || end <= start) {
    return end + 24 * 60 - start;
  }
  return end - start;
}

export function shiftStandardMinutes(shift: ShiftDefinition): number {
  return Math.max(0, shiftWindowMinutes(shift) - Math.max(0, shift.breakMinutes));
}

function assignmentPriority(assignment: ShiftAssignment): number {
  if (assignment.targetType === "employee") return 3;
  if (assignment.targetType === "group") return 2;
  return 1;
}

export function resolveShiftForDate(
  employeeCode: string,
  date: string,
  shifts: ShiftDefinition[],
  assignments: ShiftAssignment[],
  department = "",
  shiftGroups: ShiftGroup[] = [],
): ShiftDefinition | null {
  const code = (employeeCode || "").trim();
  const dept = (department || "").trim().toLowerCase();
  const normalizedCode = code.toLowerCase();
  if (!code && !dept) return null;

  const activeShiftMap = new Map(shifts.filter((x) => x.isActive).map((x) => [x.id, x]));
  const activeGroupMembers = new Map<string, Set<string>>();
  for (const group of shiftGroups) {
    if (!group.isActive) continue;
    const members = new Set(
      (group.memberEmployeeCodes ?? [])
        .map((member) => member.trim().toLowerCase())
        .filter((member) => member.length > 0),
    );
    activeGroupMembers.set(group.id, members);
  }
  const candidates = assignments
    .filter((x) =>
      x.isActive &&
      x.effectiveFrom <= date &&
      (!x.effectiveTo || date <= x.effectiveTo) &&
      activeShiftMap.has(x.shiftId),
    )
    .filter((x) =>
      x.targetType === "employee"
        ? x.employeeCode.trim().toLowerCase() === normalizedCode
        : x.targetType === "group"
          ? activeGroupMembers.get(x.groupId)?.has(normalizedCode) ?? false
          : (() => {
              const targetDept = (x.department || "").trim().toLowerCase();
              if (!targetDept || targetDept === "all") return true;
              return targetDept === dept;
            })(),
    )
    .sort((a, b) => {
      const byDate = b.effectiveFrom.localeCompare(a.effectiveFrom);
      if (byDate !== 0) return byDate;
      return assignmentPriority(b) - assignmentPriority(a);
    });
  if (candidates.length === 0) return null;
  const shift = activeShiftMap.get(candidates[0].shiftId);
  return shift ?? null;
}

export function resolveShiftAssignmentsForDate(
  employeeCode: string,
  date: string,
  shifts: ShiftDefinition[],
  assignments: ShiftAssignment[],
  department = "",
  shiftGroups: ShiftGroup[] = [],
): ShiftAssignment[] {
  const code = (employeeCode || "").trim();
  const dept = (department || "").trim().toLowerCase();
  const normalizedCode = code.toLowerCase();
  if (!code && !dept) return [];

  const activeShiftMap = new Map(shifts.filter((x) => x.isActive).map((x) => [x.id, x]));
  const activeGroupMembers = new Map<string, Set<string>>();
  for (const group of shiftGroups) {
    if (!group.isActive) continue;
    const members = new Set(
      (group.memberEmployeeCodes ?? [])
        .map((member) => member.trim().toLowerCase())
        .filter((member) => member.length > 0),
    );
    activeGroupMembers.set(group.id, members);
  }

  const candidates = assignments
    .filter((x) =>
      x.isActive &&
      x.effectiveFrom <= date &&
      (!x.effectiveTo || date <= x.effectiveTo) &&
      activeShiftMap.has(x.shiftId),
    )
    .filter((x) =>
      x.targetType === "employee"
        ? x.employeeCode.trim().toLowerCase() === normalizedCode
        : x.targetType === "group"
          ? activeGroupMembers.get(x.groupId)?.has(normalizedCode) ?? false
          : (() => {
              const targetDept = (x.department || "").trim().toLowerCase();
              if (!targetDept || targetDept === "all") return true;
              return targetDept === dept;
            })(),
    );

  if (candidates.length === 0) return [];
  const maxPriority = Math.max(...candidates.map((x) => assignmentPriority(x)));
  const scoped = candidates
    .filter((x) => assignmentPriority(x) === maxPriority)
    .sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom));

  const deduped = new Map<string, ShiftAssignment>();
  for (const row of scoped) {
    if (!deduped.has(row.shiftId)) {
      deduped.set(row.shiftId, row);
    }
  }

  return Array.from(deduped.values()).sort((a, b) => {
    const shiftA = activeShiftMap.get(a.shiftId);
    const shiftB = activeShiftMap.get(b.shiftId);
    const startA = shiftA ? parseTimeToMinutes(shiftA.startTime) : 0;
    const startB = shiftB ? parseTimeToMinutes(shiftB.startTime) : 0;
    if (startA !== startB) return startA - startB;
    return a.shiftId.localeCompare(b.shiftId);
  });
}

function getDayOfWeek(date: string): number {
  const d = new Date(`${date}T00:00:00`);
  if (Number.isNaN(d.getTime())) return 0;
  return d.getDay();
}

function getDayOfMonth(date: string): number {
  const d = new Date(`${date}T00:00:00`);
  if (Number.isNaN(d.getTime())) return 1;
  return d.getDate();
}

function resolveTimetableIdFromShiftPlan(
  date: string,
  shiftPlan: ShiftPlanDefinition,
): string | null {
  if (shiftPlan.cycleType === "daily") {
    return shiftPlan.dailyTimetableId ?? null;
  }
  if (shiftPlan.cycleType === "weekly") {
    const dayOfWeek = getDayOfWeek(date);
    return shiftPlan.weeklyRules.find((x) => x.dayOfWeek === dayOfWeek)?.timetableId ?? null;
  }
  const dayOfMonth = getDayOfMonth(date);
  return shiftPlan.monthlyRules.find((x) => x.dayOfMonth === dayOfMonth)?.timetableId ?? null;
}

function resolveShiftPlanForDate(
  employeeCode: string,
  date: string,
  shiftPlans: ShiftPlanDefinition[],
  assignments: ShiftAssignment[],
  department = "",
  shiftGroups: ShiftGroup[] = [],
): ShiftPlanDefinition | null {
  const activePlanMap = new Map(shiftPlans.filter((x) => x.isActive).map((x) => [x.id, x]));
  if (activePlanMap.size === 0) return null;

  const code = (employeeCode || "").trim();
  const dept = (department || "").trim().toLowerCase();
  const normalizedCode = code.toLowerCase();

  const activeGroupMembers = new Map<string, Set<string>>();
  for (const group of shiftGroups) {
    if (!group.isActive) continue;
    const members = new Set(
      (group.memberEmployeeCodes ?? [])
        .map((member) => member.trim().toLowerCase())
        .filter((member) => member.length > 0),
    );
    activeGroupMembers.set(group.id, members);
  }

  const candidates = assignments
    .filter((x) =>
      x.isActive &&
      x.effectiveFrom <= date &&
      (!x.effectiveTo || date <= x.effectiveTo) &&
      activePlanMap.has(x.shiftId),
    )
    .filter((x) =>
      x.targetType === "employee"
        ? x.employeeCode.trim().toLowerCase() === normalizedCode
        : x.targetType === "group"
          ? (activeGroupMembers.get(x.groupId)?.has(normalizedCode) ?? false)
          : (() => {
              const targetDept = (x.department || "").trim().toLowerCase();
              if (!targetDept || targetDept === "all") return true;
              return targetDept === dept;
            })(),
    )
    .sort((a, b) => {
      const byDate = b.effectiveFrom.localeCompare(a.effectiveFrom);
      if (byDate !== 0) return byDate;
      return assignmentPriority(b) - assignmentPriority(a);
    });

  if (candidates.length === 0) return null;
  return activePlanMap.get(candidates[0].shiftId) ?? null;
}

export function resolveTimetableForDate(
  employeeCode: string,
  date: string,
  timetables: TimetableDefinition[],
  department = "",
  shiftPlans: ShiftPlanDefinition[] = [],
  shiftAssignments: ShiftAssignment[] = [],
  shiftGroups: ShiftGroup[] = [],
): TimetableDefinition | null {
  const activeTimetables = timetables.filter((x) => x.isActive);
  const activeTimetableMap = new Map(activeTimetables.map((x) => [x.id, x]));
  if (activeTimetables.length === 0) return null;

  const plan = resolveShiftPlanForDate(employeeCode, date, shiftPlans, shiftAssignments, department, shiftGroups);
  if (plan) {
    const timetableId = resolveTimetableIdFromShiftPlan(date, plan);
    if (timetableId && activeTimetableMap.has(timetableId)) {
      return activeTimetableMap.get(timetableId) ?? null;
    }
  }

  return activeTimetableMap.get("default-weekly") ?? activeTimetables[0] ?? null;
}

export function resolveScheduleForDate(
  employeeCode: string,
  date: string,
  shifts: ShiftDefinition[],
  shiftAssignments: ShiftAssignment[],
  timetables: TimetableDefinition[],
  department: string,
  settings: ShiftCalendarSettings,
  shiftPlans: ShiftPlanDefinition[] = [],
  shiftGroups: ShiftGroup[] = [],
): ScheduleResolution {
  const holiday = isHoliday(date, department, settings);
  const assignedShift = resolveShiftForDate(
    employeeCode,
    date,
    shifts,
    shiftAssignments,
    department,
    shiftGroups,
  );

  const timetable = assignedShift
    ? null
    : resolveTimetableForDate(
        employeeCode,
        date,
        timetables,
        department,
        shiftPlans,
        shiftAssignments,
        shiftGroups,
      );

  let timetableDay: TimetableDayRule | null = null;
  let weeklyOffFromTimetable = false;
  const activeShiftMap = new Map(shifts.filter((x) => x.isActive).map((x) => [x.id, x]));

  if (timetable) {
    const dayOfWeek = getDayOfWeek(date);
    timetableDay = timetable.days.find((x) => x.dayOfWeek === dayOfWeek) ?? null;
    weeklyOffFromTimetable = timetableDay?.isWeeklyOff ?? false;
  }

  let shift: ShiftDefinition | null = assignedShift;
  if (!shift && timetableDay?.shiftId) {
    shift = activeShiftMap.get(timetableDay.shiftId) ?? null;
  }

  const weeklyOffFallback = !timetable && isWeeklyOff(date, settings);
  const weeklyOff = !holiday && (weeklyOffFromTimetable || weeklyOffFallback);

  return {
    shift,
    holiday,
    weeklyOff,
    weeklyOffFromTimetable,
    timetable,
    timetableName: timetable?.name ?? null,
    timetableDay,
  };
}

export function isWeeklyOff(date: string, settings: ShiftCalendarSettings): boolean {
  const d = new Date(`${date}T00:00:00`);
  if (Number.isNaN(d.getTime())) return false;
  return (settings.weeklyOffDays ?? []).includes(d.getDay());
}

export function isHoliday(
  date: string,
  department: string,
  settings: ShiftCalendarSettings,
): HolidayItem | null {
  const dept = (department || "").trim().toLowerCase();
  const found = (settings.holidays ?? []).find(
    (x) =>
      x.isActive &&
      (x.date === date || (x.isRecurring && x.date.slice(5) === date.slice(5))) &&
      ((x.department || "").trim().toLowerCase() === "all" ||
        (x.department || "").trim().toLowerCase() === dept),
  );
  return found ?? null;
}
