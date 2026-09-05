"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { CalendarClock, CalendarDays, ChevronDown, ChevronRight, Clock3 } from "lucide-react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { resolveApiBases } from "@/lib/api-bases";
import {
  WEEKDAY_LABELS,
  defaultShiftCalendarSettings,
  defaultShiftDefinitions,
  loadShiftConfigFromApi,
  parseTimeToMinutes,
  readShiftConfigSnapshot,
  resolveScheduleForDate,
  saveShiftConfigToApi,
  type BreakTimeDefinition,
  type ShiftAssignment,
  type ShiftCalendarSettings,
  type ShiftConfigSnapshot,
  type ShiftDefinition,
  type ShiftGroup,
  type ShiftPlanCycleType,
  type ShiftPlanDefinition,
  type TimetableDefinition,
} from "@/components/data/shift-store";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { resolveCalendarYearRange } from "@/components/date/calendar-range";
import { formatCalendarDate } from "@/components/date/date-display";
import { BsDateField } from "@/components/date/dual-date-input";

type EmployeeRow = { id: string; employeeCode: string; fullName: string; department: string; isActive: boolean };

type DepartmentRow = {
  id: string;
  code: string;
  name: string;
  areaCode?: string;
  areaName?: string;
  isDefault?: boolean;
};
type TimetableDayInput = {
  dayOfWeek: number;
  isWeeklyOff: boolean;
  checkInTime: string;
  checkInStart: string;
  checkInEnd: string;
  checkOutStart: string;
  checkOutTime: string;
  checkOutEnd: string;
  crossDay: boolean;
  useBreakTime: boolean;
  breakTimeId: string;
  breakTimeIds: string[];
};
type ShiftPlanWeeklyInput = { dayOfWeek: number; timetableId: string };
type ShiftPlanMonthlyInput = { dayOfMonth: number; timetableId: string };
type ShiftPlanForm = {
  id: string | null;
  name: string;
  cycleType: ShiftPlanCycleType;
  effectiveFrom: string;
  effectiveTo: string;
  isCyclic: boolean;
  isActive: boolean;
  dailyTimetableId: string;
  weeklyRules: ShiftPlanWeeklyInput[];
  monthlyRules: ShiftPlanMonthlyInput[];
};
type TimetableForm = {
  id: string | null;
  name: string;
  isActive: boolean;
  days: TimetableDayInput[];
};
type BreakForm = {
  id: string | null;
  name: string;
  startTime: string;
  endTime: string;
  durationMinutes: number;
  calculateType: BreakTimeDefinition["calculateType"];
  minimumBreakMinutes: number;
  isActive: boolean;
};

type ShiftGroupForm = {
  id: string | null;
  name: string;
  memberEmployeeCodes: string[];
};

type ShiftAssignmentForm = {
  id: string | null;
  targetType: "employee" | "department" | "group";
  employeeCode: string;
  areaCode: string;
  departmentId: string;
  groupId: string;
  shiftPlanId: string;
  effectiveFrom: string;
  effectiveTo: string;
};

const toDate = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
const sundayOfWeek = (d: Date) => {
  const n = new Date(d);
  const day = n.getDay();
  n.setDate(n.getDate() - day);
  return toDate(n);
};
const slug = (v: string) => v.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "");
const normalizeShiftName = (v: string) => v.trim().replace(/\s+/g, " ");
const weeklyOffDaysForTimetable = (timetables: TimetableDefinition[], timetableId: string): Set<number> => {
  const timetable = timetables.find((x) => x.id === timetableId) ?? timetables[0] ?? null;
  if (!timetable) return new Set([6]);
  const weeklyOffDays = timetable.days.filter((day) => day.isWeeklyOff).map((day) => day.dayOfWeek);
  return new Set(weeklyOffDays);
};
const shiftPlanBlank = (
  defaults: { effectiveFrom: string; effectiveTo: string },
  defaultTimetableId: string,
  weeklyOffDays: Set<number>,
): ShiftPlanForm => ({
  id: null,
  name: "Default Shift",
  cycleType: "weekly",
  effectiveFrom: defaults.effectiveFrom,
  effectiveTo: defaults.effectiveTo,
  isCyclic: true,
  isActive: true,
  dailyTimetableId: defaultTimetableId,
  weeklyRules: Array.from({ length: 7 }, (_, dayOfWeek) => ({ dayOfWeek, timetableId: weeklyOffDays.has(dayOfWeek) ? "" : defaultTimetableId })),
  monthlyRules: Array.from({ length: 31 }, (_, i) => ({ dayOfMonth: i + 1, timetableId: defaultTimetableId })),
});
const defaultTimeWindow = {
  checkInTime: "09:00",
  checkInStart: "05:00",
  checkInEnd: "09:15",
  checkOutStart: "15:45",
  checkOutTime: "17:00",
  checkOutEnd: "23:59",
  crossDay: false,
  useBreakTime: false,
  breakTimeId: "",
  breakTimeIds: [] as string[],
};
const timetableBlank = (): TimetableForm => ({
  id: null,
  name: "",
  isActive: true,
  days: Array.from({ length: 7 }, (_, day) => ({
    dayOfWeek: day,
    isWeeklyOff: day === 6,
    ...defaultTimeWindow,
  })),
});
const breakBlank = (): BreakForm => ({
  id: null,
  name: "",
  startTime: "13:00",
  endTime: "14:00",
  durationMinutes: 60,
  calculateType: "auto_deduct",
  minimumBreakMinutes: 0,
  isActive: true,
});

const shiftGroupBlank = (): ShiftGroupForm => ({
  id: null,
  name: "",
  memberEmployeeCodes: [],
});

const shiftAssignmentBlank = (today: string): ShiftAssignmentForm => ({
  id: null,
  targetType: "employee",
  employeeCode: "",
  areaCode: "",
  departmentId: "",
  groupId: "",
  shiftPlanId: "",
  effectiveFrom: today,
  effectiveTo: "",
});
const timetableToForm = (tt: TimetableDefinition): TimetableForm => {
  return {
    id: tt.id,
    name: tt.name,
    isActive: tt.isActive,
    days: Array.from({ length: 7 }, (_, day) => {
      const row = tt.days.find((x) => x.dayOfWeek === day);
      return {
        dayOfWeek: day,
        isWeeklyOff: row?.isWeeklyOff ?? day === 6,
        checkInTime: row?.checkInTime ?? defaultTimeWindow.checkInTime,
        checkInStart: row?.checkInStart ?? defaultTimeWindow.checkInStart,
        checkInEnd: row?.checkInEnd ?? defaultTimeWindow.checkInEnd,
        checkOutStart: row?.checkOutStart ?? defaultTimeWindow.checkOutStart,
        checkOutTime: row?.checkOutTime ?? defaultTimeWindow.checkOutTime,
        checkOutEnd: row?.checkOutEnd ?? defaultTimeWindow.checkOutEnd,
        crossDay: row?.crossDay ?? defaultTimeWindow.crossDay,
        useBreakTime: row?.useBreakTime ?? defaultTimeWindow.useBreakTime,
        breakTimeId: row?.breakTimeId ?? defaultTimeWindow.breakTimeId,
        breakTimeIds: (row?.breakTimeIds?.length ? row.breakTimeIds : row?.breakTimeId ? [row.breakTimeId] : []),
      };
    }),
  };
};

const shiftPlanToForm = (
  plan: ShiftPlanDefinition,
  defaults: { effectiveFrom: string; effectiveTo: string },
  defaultTimetableId: string,
  weeklyOffDays: Set<number>,
): ShiftPlanForm => ({
  id: plan.id,
  name: plan.name,
  cycleType: plan.cycleType,
  effectiveFrom: plan.effectiveFrom || defaults.effectiveFrom,
  effectiveTo: plan.effectiveTo ?? defaults.effectiveTo,
  isCyclic: plan.isCyclic,
  isActive: plan.isActive,
  dailyTimetableId: plan.dailyTimetableId ?? defaultTimetableId,
  weeklyRules: Array.from({ length: 7 }, (_, dayOfWeek) => ({
    dayOfWeek,
    timetableId: (plan.weeklyRules.find((x) => x.dayOfWeek === dayOfWeek)?.timetableId ?? "").trim() || (weeklyOffDays.has(dayOfWeek) ? "" : defaultTimetableId),
  })),
  monthlyRules: Array.from({ length: 31 }, (_, i) => {
    const dayOfMonth = i + 1;
    return {
      dayOfMonth,
      timetableId: plan.monthlyRules.find((x) => x.dayOfMonth === dayOfMonth)?.timetableId ?? defaultTimetableId,
    };
  }),
});

const normalizeShiftPlanForm = (
  form: ShiftPlanForm,
  defaults: { effectiveFrom: string; effectiveTo: string },
  defaultTimetableId: string,
  weeklyOffDays: Set<number>,
): ShiftPlanForm => ({
  ...form,
  effectiveFrom: form.effectiveFrom || defaults.effectiveFrom,
  effectiveTo: form.effectiveTo || defaults.effectiveTo,
  dailyTimetableId: form.dailyTimetableId || defaultTimetableId,
  weeklyRules: Array.from({ length: 7 }, (_, dayOfWeek) => ({
    dayOfWeek,
    timetableId: (form.weeklyRules.find((x) => x.dayOfWeek === dayOfWeek)?.timetableId ?? "").trim() || (weeklyOffDays.has(dayOfWeek) ? "" : defaultTimetableId),
  })),
  monthlyRules: Array.from({ length: 31 }, (_, i) => {
    const dayOfMonth = i + 1;
    return {
      dayOfMonth,
      timetableId: form.monthlyRules.find((x) => x.dayOfMonth === dayOfMonth)?.timetableId || defaultTimetableId,
    };
  }),
});

const TIME_24H_REGEX = /^([01]\d|2[0-3]):([0-5]\d)$/;
function toAmPmLabel(value: string): string {
  if (!TIME_24H_REGEX.test(value.trim())) return "--";
  const hour = Number.parseInt(value.slice(0, 2), 10);
  return hour >= 12 ? "PM" : "AM";
}

function formatOtMinutes(value: number): string {
  const minutes = Math.max(0, Math.round(value || 0));
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours > 0 && rest > 0) return `${hours}h ${rest}m`;
  if (hours > 0) return `${hours}h`;
  return `${rest}m`;
}

function TimeField({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const popupRef = useRef<HTMLDivElement | null>(null);
  const [open, setOpen] = useState(false);
  const [portalReady, setPortalReady] = useState(false);
  const [popupPos, setPopupPos] = useState<{ top: number; left: number; width: number } | null>(null);
  const amPm = toAmPmLabel(value);
  const options = useMemo(
    () =>
      Array.from({ length: 96 }, (_, index) => {
        const hour = Math.floor(index / 4);
        const minute = (index % 4) * 15;
        const val = `${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`;
        return { value: val, label: `${val} ${hour >= 12 ? "PM" : "AM"}` };
      }),
    [],
  );

  useEffect(() => {
    setPortalReady(true);
  }, []);

  useEffect(() => {
    if (!open) {
      return;
    }
    const updatePopupPosition = () => {
      if (!containerRef.current) return;
      const rect = containerRef.current.getBoundingClientRect();
      const viewportPadding = 8;
      const popupWidth = Math.max(220, rect.width);
      const maxLeft = Math.max(viewportPadding, window.innerWidth - popupWidth - viewportPadding);
      const left = Math.min(Math.max(rect.left, viewportPadding), maxLeft);
      const estimatedHeight = 220;
      const spaceBelow = window.innerHeight - rect.bottom;
      const openUpward = spaceBelow < estimatedHeight && rect.top > estimatedHeight;
      const top = openUpward ? Math.max(viewportPadding, rect.top - estimatedHeight - 6) : rect.bottom + 6;
      setPopupPos({ top, left, width: popupWidth });
    };
    updatePopupPosition();
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (containerRef.current?.contains(target)) return;
      if (popupRef.current?.contains(target)) return;
      setOpen(false);
    };
    const onEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(false);
    };
    const onWindowChange = () => updatePopupPosition();
    window.addEventListener("mousedown", onPointerDown);
    window.addEventListener("keydown", onEscape);
    window.addEventListener("resize", onWindowChange);
    window.addEventListener("scroll", onWindowChange, true);
    return () => {
      window.removeEventListener("mousedown", onPointerDown);
      window.removeEventListener("keydown", onEscape);
      window.removeEventListener("resize", onWindowChange);
      window.removeEventListener("scroll", onWindowChange, true);
    };
  }, [open]);

  return (
    <label className="grid gap-1 text-xs text-[--muted-foreground]">
      <span className="text-sm font-medium text-[#1f4f79]">{label}</span>
      <div ref={containerRef} className="relative overflow-visible">
        <div className="relative overflow-hidden rounded-lg border border-[--input] bg-white">
        <input
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          onFocus={() => setOpen(false)}
          placeholder="HH:mm"
          inputMode="numeric"
          maxLength={5}
          className="w-full border-0 bg-transparent px-3 py-2 pr-20 text-sm text-[--foreground] outline-none [appearance:none] [-webkit-appearance:none] [-moz-appearance:none]"
        />
        <span className="pointer-events-none absolute right-10 top-1/2 -translate-y-1/2 text-[11px] font-semibold text-[#5f7f9f]">
          {amPm}
        </span>
        <button
          type="button"
          onClick={() => setOpen((prev) => !prev)}
          className="!absolute !right-2 !top-1/2 inline-flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-md border border-[#d3e2f3] bg-white text-[#1f4f79] hover:bg-[#eef6ff]"
          aria-label={`Toggle time options for ${label}`}
          aria-expanded={open}
        >
          <Clock3 className="h-4 w-4" />
        </button>
        </div>
        {open && portalReady
          ? createPortal(
              <div
                ref={popupRef}
                className="fixed z-[9999] overflow-y-auto rounded-lg border border-[#c8dbf0] bg-white p-1 shadow-[0_16px_30px_-14px_rgba(20,60,90,0.55)]"
                style={{
                  top: popupPos?.top ?? 0,
                  left: popupPos?.left ?? 0,
                  width: popupPos?.width ?? 220,
                  maxHeight: "calc(100vh - 2rem)",
                }}
              >
                {options.map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => {
                      onChange(option.value);
                      setOpen(false);
                    }}
                    className={`block w-full rounded-md px-2 py-1.5 text-left text-xs ${
                      value.trim() === option.value ? "bg-[#e8f2ff] text-[#1f4f79]" : "text-[#294a68] hover:bg-[#f3f8ff]"
                    }`}
                  >
                    {option.label}
                  </button>
                ))}
              </div>,
              document.body,
            )
          : null}
      </div>
    </label>
  );
}

let apiBase = "";
async function req<T>(path: string): Promise<T> {
  const bases = resolveApiBases({ preferredBase: apiBase });
  let err = "Failed to fetch";
  for (const b of bases) {
    try {
      const r = await fetch(`${b}${path}`, { cache: "no-store" });
      if (r.ok) {
        apiBase = b;
        return (await r.json()) as T;
      }
      err = (await r.text()) || `Request failed (${r.status})`;
    } catch (e) {
      err = e instanceof Error ? e.message : err;
    }
  }
  throw new Error(err);
}

export default function ShiftsPage() {
  const { calendarType } = useCalendarPreference();
  const now = useMemo(() => new Date(), []);
  const today = useMemo(() => toDate(now), [now]);
  const yearRange = useMemo(() => resolveCalendarYearRange(now, calendarType), [now, calendarType]);
  const defaultShiftRange = useMemo(
    () => ({ effectiveFrom: yearRange.start, effectiveTo: yearRange.end }),
    [yearRange.end, yearRange.start],
  );
  const searchParams = useSearchParams();
  const pathname = usePathname();
  const router = useRouter();
  const isShiftRoute = pathname === "/shift" || pathname.startsWith("/shift/");
  const isTimetableRoute = pathname === "/timetable" || pathname.startsWith("/timetable/");
  const isShiftsRoute = pathname === "/shifts" || pathname.startsWith("/shifts/");
  const isAccessTimePeriod = pathname.includes("/access-control/time-period");
  const querySection = searchParams.get("section");
  const queryTab = searchParams.get("tab");
  const routeTab = isShiftRoute
    ? "setup"
    : (isTimetableRoute && querySection === "break")
      ? "break"
      : (isTimetableRoute || isAccessTimePeriod)
        ? "timetable"
        : isShiftsRoute
          ? (queryTab === "break" ? "break" : queryTab === "overtime" ? "overtime" : queryTab === "timetable" ? "timetable" : "setup")
          : null;
  const initialTab = routeTab ?? (queryTab === "timetable" ? "timetable" : queryTab === "overtime" ? "overtime" : queryTab === "break" ? "break" : "setup");
  const [tab, setTab] = useState<"setup" | "timetable" | "overtime" | "break">(initialTab);
  const [weekStart, setWeekStart] = useState(sundayOfWeek(now));
  const [msg, setMsg] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [shiftPlanSaving, setShiftPlanSaving] = useState(false);
  const [timetableSaving, setTimetableSaving] = useState(false);
  const [overtimeSaving, setOvertimeSaving] = useState(false);
  const [breakSaving, setBreakSaving] = useState(false);

  const [employees, setEmployees] = useState<EmployeeRow[]>([]);
  const [departments, setDepartments] = useState<DepartmentRow[]>([]);
  const [breakProfiles, setBreakProfiles] = useState<BreakTimeDefinition[]>([]);
  const [deptFilter, setDeptFilter] = useState("all");
  const [shifts, setShifts] = useState<ShiftDefinition[]>([]);
  const [shiftPlans, setShiftPlans] = useState<ShiftPlanDefinition[]>([]);
  const [shiftAssignments, setShiftAssignments] = useState<ShiftAssignment[]>([]);
  const [shiftGroups, setShiftGroups] = useState<ShiftGroup[]>([]);
  const [timetables, setTimetables] = useState<TimetableDefinition[]>([]);
  const [calendar, setCalendar] = useState<ShiftCalendarSettings>(defaultShiftCalendarSettings());

  const [shiftPlanForm, setShiftPlanForm] = useState<ShiftPlanForm>(
    shiftPlanBlank(defaultShiftRange, "", new Set([6])),
  );
  const [ttForm, setTtForm] = useState<TimetableForm>(timetableBlank());
  // Each day card collapses to a one-line summary by default (Daily Configuration list can get
  // long — 7 full cards x 6 time fields each); expand only the day being edited.
  const [expandedDays, setExpandedDays] = useState<Set<number>>(new Set());
  const toggleDayExpanded = (dayOfWeek: number) => {
    setExpandedDays((prev) => {
      const next = new Set(prev);
      if (next.has(dayOfWeek)) next.delete(dayOfWeek);
      else next.add(dayOfWeek);
      return next;
    });
  };
  const [breakForm, setBreakForm] = useState<BreakForm>(breakBlank());
  const [shiftGroupForm, setShiftGroupForm] = useState<ShiftGroupForm>(shiftGroupBlank());
  const [shiftAssignmentForm, setShiftAssignmentForm] = useState<ShiftAssignmentForm>(shiftAssignmentBlank(today));

  const lastDefaultShiftRangeRef = useRef(defaultShiftRange);
  useEffect(() => {
    const last = lastDefaultShiftRangeRef.current;
    setShiftPlanForm((prev) => {
      const isDefaultRange =
        prev.effectiveFrom === last.effectiveFrom && prev.effectiveTo === last.effectiveTo;
      if (!isDefaultRange) return prev;
      return {
        ...prev,
        effectiveFrom: defaultShiftRange.effectiveFrom,
        effectiveTo: defaultShiftRange.effectiveTo,
      };
    });
    lastDefaultShiftRangeRef.current = defaultShiftRange;
  }, [defaultShiftRange.effectiveFrom, defaultShiftRange.effectiveTo]);

  const defaultShiftId = useMemo(() => shifts.find((x) => x.isActive)?.id ?? shifts[0]?.id ?? "general", [shifts]);
  const activeTimetables = useMemo(() => timetables, [timetables]);
  const defaultTimetableId = useMemo(() => activeTimetables[0]?.id ?? timetables[0]?.id ?? "", [activeTimetables, timetables]);
  const timetableById = useMemo(() => new Map(timetables.map((x) => [x.id, x.name])), [timetables]);
  const shiftPlanById = useMemo(() => new Map(shiftPlans.map((x) => [x.id, x.name])), [shiftPlans]);
  const shiftGroupById = useMemo(() => new Map(shiftGroups.map((x) => [x.id, x.name])), [shiftGroups]);
  const deptById = useMemo(() => new Map(departments.map((d) => [d.id, d.name])), [departments]);
  const orderedDepartments = useMemo(
    () =>
      departments
        .filter((d) => d.id && d.name?.trim())
        .slice()
        .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" })),
    [departments],
  );
  const orderedAreas = useMemo(
    () =>
      Array.from(
        new Map(
          departments
            .filter((d) => d.areaCode && d.areaName)
            .map((d) => [d.areaCode, { code: d.areaCode, name: d.areaName! }])
        ).values()
      ).sort((a, b) => (a.name ?? "").localeCompare(b.name ?? "", undefined, { sensitivity: "base" })),
    [departments],
  );
  const departmentsByArea = useMemo(
    () =>
      new Map(
        orderedAreas.map((area) => [
          area.code,
          orderedDepartments.filter((d) => d.areaCode === area.code),
        ])
      ),
    [orderedAreas, orderedDepartments],
  );
  const activeBreakProfiles = useMemo(() => breakProfiles, [breakProfiles]);
  const breakById = useMemo(() => new Map(breakProfiles.map((x) => [x.id, x.name])), [breakProfiles]);
  const depts = useMemo(() => Array.from(new Set(employees.map((x) => x.department).filter(Boolean))).sort((a, b) => a.localeCompare(b)), [employees]);
  const weeklyOffDays = useMemo(() => weeklyOffDaysForTimetable(timetables, defaultTimetableId), [timetables, defaultTimetableId]);
  const previewEmployees = useMemo(() => employees.filter((x) => (deptFilter === "all" ? true : x.department === deptFilter)), [employees, deptFilter]);
  const days = useMemo(() => {
    const s = new Date(`${weekStart}T00:00:00`);
    if (Number.isNaN(s.getTime())) return [] as string[];
    return Array.from({ length: 7 }, (_, i) => toDate(new Date(s.getFullYear(), s.getMonth(), s.getDate() + i)));
  }, [weekStart]);

  const summarizeTimetableDay = (d: TimetableDayInput): string => {
    if (d.isWeeklyOff) return "Weekly Off";
    const breakLabels = d.breakTimeIds
      .map((id) => breakById.get(id) ?? id)
      .filter(Boolean)
      .join(", ");
    return `In ${d.checkInTime} (${d.checkInStart}-${d.checkInEnd}) | Out ${d.checkOutTime} (${d.checkOutStart}-${d.checkOutEnd})` +
      (d.crossDay ? " | Cross Day" : "") +
      (d.useBreakTime && breakLabels ? ` | Break: ${breakLabels}` : "");
  };

  const summarizeShiftPlan = (plan: ShiftPlanDefinition) => {
    if (plan.cycleType === "daily") {
      return `Daily: ${timetableById.get(plan.dailyTimetableId ?? "") ?? "Not set"}`;
    }
    if (plan.cycleType === "weekly") {
      return plan.weeklyRules
        .slice()
        .sort((a, b) => a.dayOfWeek - b.dayOfWeek)
        .map((x) => `${WEEKDAY_LABELS[x.dayOfWeek]}: ${x.timetableId ? (timetableById.get(x.timetableId) ?? "Not set") : "Weekly Off"}`)
        .join(" | ");
    }
    return plan.monthlyRules
      .slice()
      .sort((a, b) => a.dayOfMonth - b.dayOfMonth)
      .slice(0, 10)
      .map((x) => `Day ${x.dayOfMonth}: ${timetableById.get(x.timetableId) ?? "Not set"}`)
  };

  const updateShiftPlanOvertime = (planId: string, patch: { enableOvertime?: boolean; overtimeAfterMinutes?: number }) => {
    setShiftPlans((prev) =>
      prev.map((plan) => (plan.id === planId ? { ...plan, ...patch } : plan))
    );
  };

  const saveOvertimeSettings = async () => {
    if (overtimeSaving) return;
    setErr(null);
    setMsg(null);

    const normalizedPlans = shiftPlans.map((plan) => ({
      ...plan,
      enableOvertime: plan.enableOvertime !== false,
      overtimeAfterMinutes: Math.max(0, Math.round(plan.overtimeAfterMinutes || 0)),
    }));
    const normalizedCalendar: ShiftCalendarSettings = {
      ...calendar,
      overtimeEnabled: Boolean(calendar.overtimeEnabled),
      weeklyOffWorkAsOvertime: Boolean(calendar.weeklyOffWorkAsOvertime),
      holidayWorkAsOvertime: Boolean(calendar.holidayWorkAsOvertime),
    };

    setOvertimeSaving(true);
    try {
      const saved = await saveShiftConfigToApi({
        shiftPlans: normalizedPlans,
        calendar: normalizedCalendar,
      });
      applyShiftConfig(saved);
      setMsg("OT settings saved.");
    } catch (error) {
      setErr(error instanceof Error ? error.message : "Failed to save OT settings.");
    } finally {
      setOvertimeSaving(false);
    }
  };

  const applyShiftConfig = (config: ShiftConfigSnapshot) => {
    setShifts(config.shiftDefinitions);
    setCalendar(config.calendar);
    setTimetables(config.timetables);
    setShiftPlans(config.shiftPlans);
    setShiftAssignments(config.shiftAssignments);
    setShiftGroups(config.shiftGroups);
    setBreakProfiles(config.breakDefinitions);
    const firstTimetableId = config.timetables.find((x) => x.isActive)?.id ?? config.timetables[0]?.id ?? "";
    const firstShiftPlan = config.shiftPlans[0] ?? null;
    const weeklyOffDays = weeklyOffDaysForTimetable(config.timetables, firstTimetableId);
    setShiftPlanForm(
      firstShiftPlan
        ? shiftPlanToForm(firstShiftPlan, defaultShiftRange, firstTimetableId, weeklyOffDays)
        : shiftPlanBlank(defaultShiftRange, firstTimetableId, weeklyOffDays),
    );
    const firstBreak = config.breakDefinitions.find((x) => x.isActive)?.id ?? "";
    const nextTimetableForm = timetableBlank();
    setTtForm({
      ...nextTimetableForm,
      days: nextTimetableForm.days.map((day) => ({ ...day, breakTimeId: firstBreak, breakTimeIds: firstBreak ? [firstBreak] : [] })),
    });
    setBreakForm(breakBlank());

    setShiftGroupForm(shiftGroupBlank());
    setShiftAssignmentForm((prev) => ({ ...shiftAssignmentBlank(today), shiftPlanId: prev.shiftPlanId || config.shiftPlans[0]?.id || "" }));
  };

  useEffect(() => {
    if (routeTab) {
      setTab(routeTab);
      return;
    }
    const tabFromQuery = searchParams.get("tab");
    if (tabFromQuery === "timetable") {
      setTab("timetable");
      return;
    }
    if (tabFromQuery === "overtime") {
      setTab("overtime");
      return;
    }
    if (tabFromQuery === "break") {
      setTab("break");
      return;
    }
    setTab("setup");
  }, [routeTab, searchParams]);

  useEffect(() => {
    if (typeof window === "undefined") return;
    if (isShiftRoute || isTimetableRoute || isShiftsRoute) return;
    const q = new URLSearchParams(window.location.search);
    if (tab === "timetable") q.set("tab", "timetable");
    else if (tab === "overtime") q.set("tab", "overtime");
    else if (tab === "break") q.set("tab", "break");
    else q.set("tab", "setup");
    const next = q.toString() ? `${window.location.pathname}?${q.toString()}` : window.location.pathname;
    window.history.replaceState(null, "", next);
  }, [isShiftRoute, isTimetableRoute, isShiftsRoute, pathname, tab]);

  useEffect(() => {
    if (typeof window === "undefined") return;
    const sectionFromQuery = searchParams.get("section");
    const sectionFromPath = isShiftRoute ? "shift" : isTimetableRoute ? "timetable" : null;
    const section = sectionFromQuery ?? sectionFromPath;
    if (!section) return;
    const targetMap: Record<string, string> = {
      shift: "shift-section",
      timetable: "timetable-section",
      overtime: "overtime-section",
      break: "break-section",
    };
    const targetId = targetMap[section];
    if (!targetId) return;
    const timer = window.setTimeout(() => {
      document.getElementById(targetId)?.scrollIntoView({ behavior: "smooth", block: "start" });
    }, 80);
    return () => window.clearTimeout(timer);
  }, [isShiftRoute, isTimetableRoute, searchParams, tab]);

  useEffect(() => {
    applyShiftConfig(readShiftConfigSnapshot());
    void (async () => {
      try {
        const remoteConfig = await loadShiftConfigFromApi();
        // Only sync data arrays after first load — never reset forms mid-session
        setShifts(remoteConfig.shiftDefinitions);
        setTimetables(remoteConfig.timetables);
        setShiftPlans(remoteConfig.shiftPlans);
        setShiftAssignments(remoteConfig.shiftAssignments);
        setShiftGroups(remoteConfig.shiftGroups);
        setBreakProfiles(remoteConfig.breakDefinitions);
        setCalendar(remoteConfig.calendar);
      } catch {
        // Keep local cache if the backend config endpoint is unavailable.
      }

      try {
        const rows = await req<EmployeeRow[]>("/api/personnel/employees");
        const active = (Array.isArray(rows) ? rows : []).filter((x) => x.isActive);
        setEmployees(active);
      } catch (e) {
        setErr(e instanceof Error ? e.message : "Failed to load employees.");
      }

      try {
        const rows = await req<DepartmentRow[]>("/api/personnel/departments");
        const normalized = (Array.isArray(rows) ? rows : [])
          .map((item) => {
            const raw = item as DepartmentRow & {
              Id?: string;
              Code?: string;
              Name?: string;
              AreaCode?: string;
              AreaName?: string;
            };
            return {
              id: raw.id ?? raw.Id ?? "",
              code: raw.code ?? raw.Code ?? "",
              name: raw.name ?? raw.Name ?? "",
              areaCode: raw.areaCode ?? raw.AreaCode ?? "",
              areaName: raw.areaName ?? raw.AreaName ?? "",
              isDefault: raw.isDefault,
            } as DepartmentRow;
          })
          .filter((d) => d.id && d.name);
        setDepartments(normalized);
      } catch {
        setDepartments([]);
      }
    })();
  }, [today]);

  useEffect(() => {
    setShiftPlanForm((prev) => normalizeShiftPlanForm(prev, defaultShiftRange, defaultTimetableId, weeklyOffDays));
  }, [defaultShiftRange.effectiveFrom, defaultShiftRange.effectiveTo, defaultTimetableId, weeklyOffDays]);

  const showTimetableTab = true;
  const showShiftTab = true;
  const showOvertimeTab = true;
  const showBreakTab = true;

  return (
    <div className="space-y-4">
      <div className="tab-header">
        <h1 className="text-2xl font-semibold">Shift and Timetable Management</h1>
        <p className="text-sm text-[--muted-foreground]">Timetable defines time windows. Shift maps timetables into daily, weekly, or monthly cycles.</p>
      </div>
      {err ? <div className="rounded-lg border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-sm text-[#8d2f2f]">{err}</div> : null}
      {msg ? <div className="rounded-lg border border-[#c9e7d1] bg-[#eefcf1] px-3 py-2 text-sm text-[#22603a]">{msg}</div> : null}

      <section className="surface p-4 shadow-sm">
        <div className="tab-toolbar tab-toolbar-controls mb-3 justify-start">
          {showBreakTab ? (
            <button
              type="button"
              onClick={() => {
                setTab("break");
                router.replace("/shifts?tab=break");
              }}
              className={`inline-flex min-w-[150px] items-center justify-center gap-2 rounded-lg border px-4 py-2 text-sm ${tab === "break" ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[--border] bg-white text-[#355a7a]"}`}
            >
              <CalendarClock className="h-4 w-4" />Break Time
            </button>
          ) : null}
          {showTimetableTab ? (
            <button
              type="button"
              onClick={() => {
                setTab("timetable");
                router.replace("/shifts?tab=timetable");
              }}
              className={`inline-flex min-w-[150px] items-center justify-center gap-2 rounded-lg border px-4 py-2 text-sm ${tab === "timetable" ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[--border] bg-white text-[#355a7a]"}`}
            >
              <CalendarDays className="h-4 w-4" />Time Table
            </button>
          ) : null}
          {showShiftTab ? (
            <button
              type="button"
              onClick={() => {
                setTab("setup");
                router.replace("/shifts?tab=setup");
              }}
              className={`inline-flex min-w-[150px] items-center justify-center gap-2 rounded-lg border px-4 py-2 text-sm ${tab === "setup" ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[--border] bg-white text-[#355a7a]"}`}
            >
              <Clock3 className="h-4 w-4" />Shifts
            </button>
          ) : null}
          {showOvertimeTab ? (
            <button
              type="button"
              onClick={() => {
                setTab("overtime");
                router.replace("/shifts?tab=overtime");
              }}
              className={`inline-flex min-w-[150px] items-center justify-center gap-2 rounded-lg border px-4 py-2 text-sm ${tab === "overtime" ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[--border] bg-white text-[#355a7a]"}`}
            >
              <CalendarClock className="h-4 w-4" />OT
            </button>
          ) : null}
        </div>

        {tab === "setup" ? (
          <div className="space-y-4">
            <div id="shift-section" className="rounded-lg border border-[--border] p-3">
              <div className="mb-2 text-sm font-semibold">Created Shifts</div>
              <p className="mb-3 text-xs text-[--muted-foreground]">Create shift cycles using saved timetables. A shift can run daily, weekly, or monthly and repeat continuously.</p>
              <div className="overflow-x-auto rounded-lg border border-[--border]">
                <table className="min-w-full text-left text-sm">
                  <thead className="bg-[#f7fbff] text-xs uppercase text-[--muted-foreground]">
                    <tr>
                      <th className="px-3 py-2">Shift</th>
                      <th className="px-3 py-2">Cycle</th>
                      <th className="px-3 py-2">Timetable Mapping</th>
                      <th className="px-3 py-2">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {shiftPlans.map((plan) => (
                      <tr key={plan.id} className="border-t border-[--border]">
                        <td className="px-3 py-2">
                          <div className="font-medium">{plan.name}</div>
                          <div className="text-xs text-[--muted-foreground]">
                            Effective: {formatCalendarDate(plan.effectiveFrom, calendarType, plan.effectiveFrom)} to {plan.effectiveTo ? formatCalendarDate(plan.effectiveTo, calendarType, plan.effectiveTo) : "Open"} | {plan.isCyclic ? "Cyclic" : "Fixed"}
                          </div>
                        </td>
                        <td className="px-3 py-2 text-xs text-[--muted-foreground]">{plan.cycleType}</td>
                        <td className="px-3 py-2 text-xs text-[--muted-foreground]">{summarizeShiftPlan(plan)}</td>
                        <td className="px-3 py-2">
                          <div className="flex gap-2">
                          <button type="button" onClick={() => setShiftPlanForm(shiftPlanToForm(plan, defaultShiftRange, defaultTimetableId, weeklyOffDays))} className="rounded border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f]">Edit</button>
                            <button type="button" onClick={async () => { if (plan.id === "default-shift-plan") return void setErr("The default shift cannot be deleted (you can edit it)."); if (shiftPlans.length <= 1) return void setErr("At least one shift must exist."); try { setErr(null); const saved = await saveShiftConfigToApi({ shiftPlans: shiftPlans.filter((x) => x.id !== plan.id) }); applyShiftConfig(saved); setMsg("Shift removed."); } catch (error) { setErr(error instanceof Error ? error.message : "Failed to remove shift."); } }} className="rounded border border-[#f2d0d0] bg-white px-2 py-1 text-xs text-[#b94141]">Delete</button>
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>

            <div className="rounded-lg border border-[--border] p-3">
              <div className="mb-2 flex items-center justify-between">
                <div className="text-sm font-semibold">{shiftPlanForm.id ? "Edit Shift" : "Add Shift"}</div>
              </div>
              <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-sm font-medium text-[#1f4f79]">Shift Name</span>
                  <input value={shiftPlanForm.name} onChange={(e) => setShiftPlanForm((p) => ({ ...p, name: e.target.value }))} placeholder="e.g. Sales Rotation" className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]" />
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-sm font-medium text-[#1f4f79]">Cycle Type</span>
                  <select value={shiftPlanForm.cycleType} onChange={(e) => setShiftPlanForm((p) => ({ ...p, cycleType: e.target.value as ShiftPlanCycleType }))} className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]">
                    <option value="daily">Daily</option>
                    <option value="weekly">Weekly</option>
                    <option value="monthly">Monthly</option>
                  </select>
                </label>
                <label className="grid gap-1 rounded-lg border border-[--input] px-3 py-2 text-xs text-[--muted-foreground]">
                  <span className="flex items-center gap-2 text-sm font-medium text-[#1f4f79]"><input type="checkbox" checked={shiftPlanForm.isCyclic} onChange={(e) => setShiftPlanForm((p) => ({ ...p, isCyclic: e.target.checked }))} />Cyclic</span>
                  <span>Repeat this cycle continuously.</span>
                </label>
              </div>

              {shiftPlanForm.cycleType === "daily" ? (
                <div className="mt-3 rounded-lg border border-[--input] bg-[#fbfdff] p-3">
                  <div className="mb-2 text-sm font-semibold text-[#1f4f79]">Daily Timetable</div>
                  <div className="overflow-x-auto">
                    <div className="grid min-w-[280px] grid-cols-1 gap-2">
                      <label className="grid gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-sm font-medium text-[#1f4f79]">Daily</span>
                        <select value={shiftPlanForm.dailyTimetableId} onChange={(e) => setShiftPlanForm((p) => ({ ...p, dailyTimetableId: e.target.value }))} className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]">
                          <option value="">Select Timetable</option>
                          {activeTimetables.map((tt) => <option key={tt.id} value={tt.id}>{tt.name}</option>)}
                        </select>
                      </label>
                    </div>
                  </div>
                </div>
              ) : null}

              {shiftPlanForm.cycleType === "weekly" ? (
                <div className="mt-3 rounded-lg border border-[--input] bg-[#fbfdff] p-3">
                  <div className="mb-2 text-sm font-semibold text-[#1f4f79]">Weekly Timetable Assignment</div>
                  <div className="overflow-x-auto">
                    <div className="grid min-w-[1050px] grid-cols-7 gap-2">
                    {shiftPlanForm.weeklyRules.slice().sort((a, b) => a.dayOfWeek - b.dayOfWeek).map((rule) => (
                      <label key={rule.dayOfWeek} className="grid gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-sm font-medium text-[#1f4f79]">{WEEKDAY_LABELS[rule.dayOfWeek]}</span>
                        <select value={rule.timetableId} onChange={(e) => setShiftPlanForm((p) => ({ ...p, weeklyRules: p.weeklyRules.map((x) => (x.dayOfWeek === rule.dayOfWeek ? { ...x, timetableId: e.target.value } : x)) }))} className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]">
                          {weeklyOffDays.has(rule.dayOfWeek) ? <option value="">Weekly Off (Blank)</option> : null}
                          <option value="">Select Timetable</option>
                          {activeTimetables.map((tt) => <option key={tt.id} value={tt.id}>{tt.name}</option>)}
                        </select>
                      </label>
                    ))}
                    </div>
                  </div>
                </div>
              ) : null}

              {shiftPlanForm.cycleType === "monthly" ? (
                <div className="mt-3 rounded-lg border border-[--input] bg-[#fbfdff] p-3">
                  <div className="mb-2 text-sm font-semibold text-[#1f4f79]">Monthly Timetable Assignment</div>
                  <div className="overflow-x-auto">
                    <div className="grid grid-flow-col auto-cols-[180px] gap-2">
                    {shiftPlanForm.monthlyRules.map((rule) => (
                      <label key={rule.dayOfMonth} className="grid gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-sm font-medium text-[#1f4f79]">Day {rule.dayOfMonth}</span>
                        <select value={rule.timetableId} onChange={(e) => setShiftPlanForm((p) => ({ ...p, monthlyRules: p.monthlyRules.map((x) => (x.dayOfMonth === rule.dayOfMonth ? { ...x, timetableId: e.target.value } : x)) }))} className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]">
                          <option value="">Select Timetable</option>
                          {activeTimetables.map((tt) => <option key={tt.id} value={tt.id}>{tt.name}</option>)}
                        </select>
                      </label>
                    ))}
                    </div>
                  </div>
                </div>
              ) : null}

              <div className="mt-3 flex justify-end gap-3">
                {shiftPlanForm.id ? (
                  <button
                    type="button"
                    onClick={() => setShiftPlanForm(shiftPlanBlank(defaultShiftRange, defaultTimetableId, weeklyOffDays))}
                    className="rounded-lg border border-[#d3e2f3] px-6 py-2 text-sm font-semibold text-[#5f7f9f] hover:bg-[#f8fbff]"
                  >
                    Cancel
                  </button>
                ) : null}
                <button
                  type="button"
                  onClick={async () => {
                    setErr(null);
                    setMsg(null);
                    const name = normalizeShiftName(shiftPlanForm.name);
                    if (!name) return void setErr("Shift name is required.");
                    const nameKey = name.toLowerCase();
                    const hasDuplicate = shiftPlans.some(
                      (x) => x.id !== shiftPlanForm.id && normalizeShiftName(x.name).toLowerCase() === nameKey,
                    );
                    if (hasDuplicate) return void setErr("Shift name already exists.");
                    if (activeTimetables.length === 0) return void setErr("Create at least one active timetable first.");
                    const normalized = normalizeShiftPlanForm(shiftPlanForm, defaultShiftRange, defaultTimetableId, weeklyOffDays);
                    if (normalized.effectiveTo && normalized.effectiveTo < normalized.effectiveFrom) return void setErr("Effective To must be on or after Effective From.");
                    if (normalized.cycleType === "daily" && !normalized.dailyTimetableId) return void setErr("Select timetable for daily cycle.");
                    if (normalized.cycleType === "weekly" && normalized.weeklyRules.some((x) => !weeklyOffDays.has(x.dayOfWeek) && !x.timetableId)) return void setErr("Assign timetable for all working days. Weekly off days can stay blank.");
                    if (normalized.cycleType === "monthly" && normalized.monthlyRules.some((x) => !x.timetableId)) return void setErr("Assign timetable for all days in monthly cycle.");

                    const now = new Date().toISOString();
                    if (normalized.id) {
                      const current = shiftPlans.find((x) => x.id === normalized.id);
                      const row: ShiftPlanDefinition = {
                        id: normalized.id,
                        name,
                        cycleType: normalized.cycleType,
                        effectiveFrom: normalized.effectiveFrom,
                        effectiveTo: normalized.effectiveTo || null,
                        isCyclic: normalized.isCyclic,
                        isActive: true,
                        dailyTimetableId: normalized.dailyTimetableId || defaultTimetableId || null,
                        weeklyRules: normalized.weeklyRules.map((x) => ({ dayOfWeek: x.dayOfWeek, timetableId: weeklyOffDays.has(x.dayOfWeek) ? x.timetableId : (x.timetableId || defaultTimetableId) })),
                        monthlyRules: normalized.monthlyRules.map((x) => ({ dayOfMonth: x.dayOfMonth, timetableId: x.timetableId || defaultTimetableId })),
                        createdAt: current?.createdAt ?? now,
                        updatedAt: now,
                      };
                      try {
                        const saved = await saveShiftConfigToApi({ shiftPlans: shiftPlans.map((x) => (x.id === row.id ? row : x)) });
                        setShiftPlans(saved.shiftPlans);
                      } catch (error) {
                        return void setErr(error instanceof Error ? error.message : "Failed to update shift.");
                      }
                      setMsg("Shift updated.");
                    } else {
                      const seed = slug(name) || `shift-plan-${Date.now()}`;
                      const id = shiftPlans.some((x) => x.id === seed) ? `${seed}-${Date.now()}` : seed;
                      const row: ShiftPlanDefinition = {
                        id,
                        name,
                        cycleType: normalized.cycleType,
                        effectiveFrom: normalized.effectiveFrom,
                        effectiveTo: normalized.effectiveTo || null,
                        isCyclic: normalized.isCyclic,
                        isActive: true,
                        dailyTimetableId: normalized.dailyTimetableId || defaultTimetableId || null,
                        weeklyRules: normalized.weeklyRules.map((x) => ({ dayOfWeek: x.dayOfWeek, timetableId: weeklyOffDays.has(x.dayOfWeek) ? x.timetableId : (x.timetableId || defaultTimetableId) })),
                        monthlyRules: normalized.monthlyRules.map((x) => ({ dayOfMonth: x.dayOfMonth, timetableId: x.timetableId || defaultTimetableId })),
                        createdAt: now,
                        updatedAt: now,
                      };
                      try {
                        const saved = await saveShiftConfigToApi({ shiftPlans: [...shiftPlans, row] });
                        setShiftPlans(saved.shiftPlans);
                      } catch (error) {
                        return void setErr(error instanceof Error ? error.message : "Failed to create shift.");
                      }
                      setMsg("Shift created.");
                      setShiftPlanForm(shiftPlanBlank(defaultShiftRange, defaultTimetableId, weeklyOffDays));
                    }
                  }}
                  className="rounded-lg bg-[#1f5c8f] px-6 py-2 text-sm font-bold text-white hover:bg-[#164a75]"
                >
                  {shiftPlanForm.id ? "Update Shift" : "Save Shift"}
                </button>
              </div>
            </div>
          </div>
        ) : tab === "timetable" ? (
          <div className="space-y-4">
            <div id="timetable-section" className="rounded-xl border border-[--border] bg-white p-4">
              <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                <div>
                  <div className="text-base font-semibold text-[#1f4f79]">Timetable Setup</div>
                  <p className="text-xs text-[--muted-foreground]">Configure unique attendance windows for each day of the week.</p>
                </div>
              </div>

              <div className="mb-4 rounded-lg border border-[--input] bg-[#fbfdff] p-3">
                <div className="mb-2 text-sm font-semibold text-[#1f4f79]">Basic Details</div>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-sm font-medium text-[#1f4f79]">Timetable Name</span>
                  <input value={ttForm.name} onChange={(e) => setTtForm((p) => ({ ...p, name: e.target.value }))} placeholder="e.g. Office Weekly" className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]" />
                  <span>Name used in timetable list and reports.</span>
                </label>
              </div>

              <div className="space-y-3">
                <div className="flex items-center justify-between">
                  <div className="text-sm font-semibold text-[#1f4f79]">Daily Configuration</div>
                  <button
                    type="button"
                    onClick={() => setExpandedDays((prev) => (prev.size > 0 ? new Set() : new Set(ttForm.days.map((d) => d.dayOfWeek))))}
                    className="rounded-lg border border-[#d3e2f3] bg-white px-3 py-1.5 text-xs font-semibold text-[#1f5c8f] hover:bg-[#f8fbff]"
                  >
                    {expandedDays.size > 0 ? "Collapse All" : "Expand All"}
                  </button>
                </div>
                <div className="grid gap-3">
                  {ttForm.days.map((d) => {
                    const isExpanded = expandedDays.has(d.dayOfWeek);
                    return (
                    <div key={d.dayOfWeek} className="rounded-lg border border-[--border] bg-white p-3">
                      <div className={`flex items-center justify-between ${isExpanded ? "mb-3 border-b border-[#f0f4f8] pb-2" : ""}`}>
                        <button
                          type="button"
                          onClick={() => toggleDayExpanded(d.dayOfWeek)}
                          className="flex items-center gap-2 text-left"
                          aria-expanded={isExpanded}
                        >
                          {isExpanded ? <ChevronDown className="h-4 w-4 text-[#5f7f9f]" /> : <ChevronRight className="h-4 w-4 text-[#5f7f9f]" />}
                          <span className="text-base font-bold text-[#1f4f79]">{WEEKDAY_LABELS[d.dayOfWeek]}</span>
                          {!isExpanded ? (
                            <span className="text-xs font-normal text-[--muted-foreground]">{summarizeTimetableDay(d)}</span>
                          ) : null}
                        </button>
                        <div className="flex items-center gap-2">
                          {!d.isWeeklyOff && isExpanded ? (
                            <button
                              type="button"
                              onClick={() =>
                                setTtForm((p) => ({
                                  ...p,
                                  days: p.days.map((x) =>
                                    x.isWeeklyOff
                                      ? x
                                      : {
                                          ...x,
                                          checkInTime: d.checkInTime,
                                          checkInStart: d.checkInStart,
                                          checkInEnd: d.checkInEnd,
                                          checkOutStart: d.checkOutStart,
                                          checkOutTime: d.checkOutTime,
                                          checkOutEnd: d.checkOutEnd,
                                          crossDay: d.crossDay,
                                          useBreakTime: d.useBreakTime,
                                          breakTimeIds: d.breakTimeIds,
                                          breakTimeId: d.breakTimeIds[0] ?? "",
                                        },
                                  ),
                                }))
                              }
                              className="rounded-lg border border-[#d3e2f3] bg-white px-3 py-1.5 text-xs font-semibold text-[#1f5c8f] hover:bg-[#f8fbff]"
                            >
                              Copy to all
                            </button>
                          ) : null}
                          <button
                            type="button"
                            onClick={() => {
                              const turningOn = d.isWeeklyOff;
                              setTtForm((p) => ({
                                ...p,
                                days: p.days.map((x) =>
                                  x.dayOfWeek !== d.dayOfWeek ? x : { ...x, isWeeklyOff: !x.isWeeklyOff },
                                ),
                              }));
                              // Auto-expand so the newly-working day's times are immediately visible to fill in.
                              if (turningOn) setExpandedDays((prev) => new Set(prev).add(d.dayOfWeek));
                            }}
                            className={`rounded-lg border px-4 py-1.5 text-sm font-semibold transition-colors ${
                              d.isWeeklyOff ? "border-[#d3e2f3] bg-white text-[#5f7f9f]" : "border-[#1f5c8f] bg-[#1f5c8f] text-white"
                            }`}
                          >
                            {d.isWeeklyOff ? "OFF" : "WORKING"}
                          </button>
                        </div>
                      </div>

                      {!d.isWeeklyOff && isExpanded && (
                        <div className="grid gap-4">
                          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-6">
                            <TimeField label="Check-In Time" value={d.checkInTime} onChange={(v) => setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, checkInTime: v } : x) }))} />
                            <TimeField label="Check-In Start" value={d.checkInStart} onChange={(v) => setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, checkInStart: v } : x) }))} />
                            <TimeField label="Check-In End" value={d.checkInEnd} onChange={(v) => setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, checkInEnd: v } : x) }))} />
                            <TimeField label="Check-Out Time" value={d.checkOutTime} onChange={(v) => setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, checkOutTime: v } : x) }))} />
                            <TimeField label="Check-Out Start" value={d.checkOutStart} onChange={(v) => setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, checkOutStart: v } : x) }))} />
                            <TimeField label="Check-Out End" value={d.checkOutEnd} onChange={(v) => setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, checkOutEnd: v } : x) }))} />
                          </div>

                          <div className="flex flex-wrap gap-4 pt-1 border-t border-[#f0f4f8]">
                            <label className="flex items-center gap-2 rounded-lg bg-[#f8fbff] px-3 py-1.5 text-xs font-medium text-[#1f4f79] cursor-pointer border border-[#e1eef9] hover:bg-[#eff6ff]">
                              <input type="checkbox" className="w-3.5 h-3.5" checked={d.crossDay} onChange={(e) => setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, crossDay: e.target.checked } : x) }))} />
                              Night Shift (Cross Day)
                            </label>
                            
                            <label className="flex items-center gap-2 rounded-lg bg-[#f8fbff] px-3 py-1.5 text-xs font-medium text-[#1f4f79] cursor-pointer border border-[#e1eef9] hover:bg-[#eff6ff]">
                              <input type="checkbox" className="w-3.5 h-3.5" checked={d.useBreakTime} onChange={(e) => setTtForm((p) => ({ ...p, days: p.days.map((x) => {
                                if (x.dayOfWeek !== d.dayOfWeek) return x;
                                const nextUseBreak = e.target.checked;
                                const nextBreakIds = nextUseBreak ? (x.breakTimeIds.length ? x.breakTimeIds : (activeBreakProfiles[0]?.id ? [activeBreakProfiles[0].id] : [])) : [];
                                return { ...x, useBreakTime: nextUseBreak, breakTimeIds: nextBreakIds, breakTimeId: nextBreakIds[0] ?? "" };
                              }) }))} />
                              Use Break
                            </label>

                            {d.useBreakTime && (
                              <div className="flex items-center gap-2">
                                <span className="text-[11px] font-bold text-[#5f7f9f] uppercase tracking-wider">Profile:</span>
                                <select
                                  multiple
                                  value={d.breakTimeIds}
                                  onChange={(e) => {
                                    const selected = Array.from(e.target.selectedOptions).map((o) => o.value).filter(Boolean);
                                    setTtForm((p) => ({ ...p, days: p.days.map((x) => x.dayOfWeek === d.dayOfWeek ? { ...x, breakTimeIds: selected, breakTimeId: selected[0] ?? "", useBreakTime: selected.length > 0 } : x) }));
                                  }}
                                  className="rounded border border-[#d3e2f3] bg-white px-2 py-1 text-[11px] font-semibold text-[#1f4f79] outline-none"
                                >
                                  {activeBreakProfiles.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
                                </select>
                              </div>
                            )}
                          </div>
                        </div>
                      )}

                      {d.isWeeklyOff && isExpanded && (
                        <div className="py-1 text-[11px] italic text-[#9db2c7]">This day is marked as a weekly holiday. No attendance records will be required.</div>
                      )}
                    </div>
                    );
                  })}
                </div>
              </div>

              <div className="mt-6 flex justify-end gap-3 border-t border-[#f0f4f8] pt-4">
                <button
                  type="button"
                  onClick={() => setTtForm(timetableBlank())}
                  className="rounded-lg border border-[#d3e2f3] px-6 py-2 text-sm font-semibold text-[#5f7f9f] hover:bg-[#f8fbff]"
                >
                  Reset
                </button>
                <button
                  type="button"
                  onClick={async () => {
                    setErr(null);
                    setMsg(null);
                    const name = ttForm.name.trim();
                    if (!name) return void setErr("Timetable name is required.");
                    
                    const workingDays = ttForm.days.filter(d => !d.isWeeklyOff);
                    if (workingDays.length === 0) return void setErr("At least one working day must be configured.");

                    for (const d of workingDays) {
                      if (!d.checkInTime || !d.checkInStart || !d.checkInEnd || !d.checkOutStart || !d.checkOutTime || !d.checkOutEnd) {
                        return void setErr(`All times are required for ${WEEKDAY_LABELS[d.dayOfWeek]}.`);
                      }
                      if (d.useBreakTime && d.breakTimeIds.length === 0) {
                        return void setErr(`Select break profile for ${WEEKDAY_LABELS[d.dayOfWeek]}.`);
                      }
                    }

                    const daysPayload = ttForm.days.map((d) => ({
                      ...d,
                      shiftId: d.isWeeklyOff ? null : defaultShiftId,
                    })).sort((a, b) => a.dayOfWeek - b.dayOfWeek);

                    const now = new Date().toISOString();
                    try {
                      if (ttForm.id) {
                        const saved = await saveShiftConfigToApi({
                          timetables: timetables.map((x) => (x.id === ttForm.id ? { ...x, name, isActive: true, days: daysPayload, updatedAt: now } : x))
                        });
                        setTimetables(saved.timetables);
                        setMsg("Timetable updated.");
                      } else {
                        const seed = slug(name) || `timetable-${Date.now()}`;
                        const id = timetables.some((x) => x.id === seed) ? `${seed}-${Date.now()}` : seed;
                        const saved = await saveShiftConfigToApi({
                          timetables: [...timetables, { id, name, isActive: true, days: daysPayload, createdAt: now, updatedAt: now }]
                        });
                        setTimetables(saved.timetables);
                        setMsg("Timetable created.");
                        setTtForm(timetableBlank());
                      }
                    } catch (error) {
                      setErr(error instanceof Error ? error.message : "Failed to save timetable.");
                    }
                  }}
                  className="rounded-lg bg-[#1f5c8f] px-8 py-2 text-sm font-bold text-white shadow-md hover:bg-[#164a75]"
                >
                  {ttForm.id ? "Update Timetable" : "Save Timetable"}
                </button>
              </div>
            </div>

            <div className="rounded-lg border border-[--border] p-3">
              <div className="mb-2 text-sm font-semibold">Timetable List</div>
              <div className="overflow-x-auto rounded-lg border border-[--border]">
              <table className="min-w-full text-left text-sm">
                <thead className="bg-[#f7fbff] text-xs uppercase text-[--muted-foreground]">
                  <tr>
                    <th className="px-3 py-2">Timetable</th>
                    <th className="px-3 py-2">Weekly Structure</th>
                    <th className="px-3 py-2">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {timetables.map((t) => (
                    <tr key={t.id} className="border-t border-[--border]">
                      <td className="px-3 py-2">
                        <div className="font-medium">{t.name}</div>
                      </td>
                      <td className="px-3 py-2 text-xs text-[--muted-foreground]">{t.days.slice().sort((a, b) => a.dayOfWeek - b.dayOfWeek).map((d) => {
                        const breakLabels = (d.breakTimeIds?.length ? d.breakTimeIds : d.breakTimeId ? [d.breakTimeId] : [])
                          .map((id) => breakById.get(id) ?? id)
                          .filter(Boolean)
                          .join(", ");
                        return d.isWeeklyOff ? `${WEEKDAY_LABELS[d.dayOfWeek]}: Weekly Off` : `${WEEKDAY_LABELS[d.dayOfWeek]}: Check-In ${d.checkInTime} (${d.checkInStart}-${d.checkInEnd}), Check-Out ${d.checkOutTime} (${d.checkOutStart}-${d.checkOutEnd})${d.crossDay ? " [Cross Day]" : ""}${d.useBreakTime && breakLabels ? ` [Break: ${breakLabels}]` : ""}`;
                      }).join(" | ")}</td>
                      <td className="px-3 py-2">
                        <div className="flex gap-2">
                          <button type="button" onClick={() => setTtForm(timetableToForm(t))} className="rounded border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f]">Edit</button>
                          <button type="button" onClick={async () => { if (t.id === "default-weekly") return void setErr("The default timetable cannot be deleted (you can edit it)."); if (timetables.length <= 1) return void setErr("At least one timetable must exist."); try { setErr(null); const saved = await saveShiftConfigToApi({ timetables: timetables.filter((x) => x.id !== t.id) }); applyShiftConfig(saved); setMsg("Timetable removed."); } catch (error) { setErr(error instanceof Error ? error.message : "Failed to remove timetable."); } }} className="rounded border border-[#f2d0d0] bg-white px-2 py-1 text-xs text-[#b94141]">Delete</button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            </div>

          </div>
        ) : tab === "overtime" ? (
          <div id="overtime-section" className="space-y-4">
            <div className="rounded-xl border border-[--border] bg-white p-4">
              <div className="mb-4">
                <div className="text-base font-semibold text-[#1f4f79]">OT Setup</div>
                <p className="text-xs text-[--muted-foreground]">Select which shifts allow overtime and when OT should start after the normal working duration.</p>
              </div>

              <div className="grid gap-3 md:grid-cols-3">
                <label className="flex items-center gap-2 rounded-lg border border-[--border] bg-[#f8fbff] px-3 py-2 text-sm font-medium text-[#1f4f79]">
                  <input
                    type="checkbox"
                    checked={calendar.overtimeEnabled}
                    onChange={(e) => setCalendar((p) => ({ ...p, overtimeEnabled: e.target.checked }))}
                  />
                  Enable OT in reports
                </label>
                <label className="flex items-center gap-2 rounded-lg border border-[--border] bg-[#f8fbff] px-3 py-2 text-sm font-medium text-[#1f4f79]">
                  <input
                    type="checkbox"
                    checked={calendar.weeklyOffWorkAsOvertime}
                    onChange={(e) => setCalendar((p) => ({ ...p, weeklyOffWorkAsOvertime: e.target.checked }))}
                  />
                  Weekly off work as OT
                </label>
                <label className="flex items-center gap-2 rounded-lg border border-[--border] bg-[#f8fbff] px-3 py-2 text-sm font-medium text-[#1f4f79]">
                  <input
                    type="checkbox"
                    checked={calendar.holidayWorkAsOvertime}
                    onChange={(e) => setCalendar((p) => ({ ...p, holidayWorkAsOvertime: e.target.checked }))}
                  />
                  Holiday work as OT
                </label>
              </div>
            </div>

            <div className="rounded-lg border border-[--border] p-3">
              <div className="mb-2 text-sm font-semibold">Shift OT Rules</div>
              <p className="mb-3 text-xs text-[--muted-foreground]">Configure overtime rules for each created shift.</p>
              <div className="overflow-x-auto rounded-lg border border-[--border]">
                <table className="min-w-full text-left text-sm">
                  <thead className="bg-[#f7fbff] text-xs uppercase text-[--muted-foreground]">
                    <tr>
                      <th className="px-3 py-2">Shift</th>
                      <th className="px-3 py-2">Cycle</th>
                      <th className="px-3 py-2">OT</th>
                      <th className="px-3 py-2">OT After</th>
                      <th className="px-3 py-2">Effective Rule</th>
                    </tr>
                  </thead>
                  <tbody>
                    {shiftPlans.length === 0 ? (
                      <tr>
                        <td colSpan={5} className="px-3 py-4 text-center text-sm text-[--muted-foreground]">
                          No shifts available. Create shifts in the Shifts tab first.
                        </td>
                      </tr>
                    ) : (
                      shiftPlans.map((plan) => (
                        <tr key={plan.id} className="border-t border-[--border]">
                          <td className="px-3 py-2">
                            <div className="font-medium">{plan.name}</div>
                            <div className="text-xs text-[--muted-foreground]">{plan.id}</div>
                          </td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground] capitalize">{plan.cycleType}</td>
                          <td className="px-3 py-2">
                            <label className="inline-flex items-center gap-2 text-sm text-[#1f4f79]">
                              <input
                                type="checkbox"
                                checked={plan.enableOvertime !== false}
                                onChange={(e) => updateShiftPlanOvertime(plan.id, { enableOvertime: e.target.checked })}
                              />
                              Selected
                            </label>
                          </td>
                          <td className="px-3 py-2">
                            <input
                              type="number"
                              min={0}
                              value={plan.overtimeAfterMinutes ?? 0}
                              onChange={(e) =>
                                updateShiftPlanOvertime(plan.id, {
                                  overtimeAfterMinutes: Math.max(0, Number.parseInt(e.target.value || "0", 10) || 0),
                                })
                              }
                              className="w-28 rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]"
                            />
                            <span className="ml-2 text-xs text-[--muted-foreground]">minutes</span>
                          </td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground]">
                            {calendar.overtimeEnabled && plan.enableOvertime !== false
                              ? `After shift duration + ${formatOtMinutes(plan.overtimeAfterMinutes ?? 0)}`
                              : "OT disabled"}
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
              <div className="mt-3 flex justify-end">
                <button
                  type="button"
                  onClick={saveOvertimeSettings}
                  disabled={overtimeSaving}
                  className="rounded-lg bg-[#1f5c8f] px-6 py-2 text-sm font-bold text-white hover:bg-[#164a75] disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {overtimeSaving ? "Saving..." : "Save OT Settings"}
                </button>
              </div>
            </div>
          </div>
        ) : (
          <div id="break-section" className="space-y-4">
            <div className="rounded-xl border border-[--border] bg-white p-4">
              <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                <div>
                  <div className="text-base font-semibold text-[#1f4f79]">Break Time Setup</div>
                  <p className="text-xs text-[--muted-foreground]">Create reusable break windows and attach them to timetable days.</p>
                </div>
              </div>

              <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-7">
                <label className="grid gap-1 text-xs text-[--muted-foreground] xl:col-span-2">
                  <span className="text-sm font-medium text-[#1f4f79]">Break Name</span>
                  <input value={breakForm.name} onChange={(e) => setBreakForm((p) => ({ ...p, name: e.target.value }))} placeholder="e.g. Lunch Break" className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]" />
                </label>
                <TimeField label="Start" value={breakForm.startTime} onChange={(v) => setBreakForm((p) => ({ ...p, startTime: v }))} />
                <TimeField label="End" value={breakForm.endTime} onChange={(v) => setBreakForm((p) => ({ ...p, endTime: v }))} />
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-sm font-medium text-[#1f4f79]">Duration (Minutes)</span>
                  <input type="number" min={1} value={breakForm.durationMinutes} onChange={(e) => setBreakForm((p) => ({ ...p, durationMinutes: Number.parseInt(e.target.value || "0", 10) || 0 }))} className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]" />
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-sm font-medium text-[#1f4f79]">Calculate Type</span>
                  <select value={breakForm.calculateType} onChange={(e) => setBreakForm((p) => ({ ...p, calculateType: e.target.value as BreakForm["calculateType"] }))} className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]">
                    <option value="auto_deduct">Auto deduct</option>
                    <option value="required_punch">Required punch</option>
                  </select>
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-sm font-medium text-[#1f4f79]">Minimum Break (m)</span>
                  <input type="number" min={0} value={breakForm.minimumBreakMinutes} onChange={(e) => setBreakForm((p) => ({ ...p, minimumBreakMinutes: Number.parseInt(e.target.value || "0", 10) || 0 }))} className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]" />
                </label>
              </div>
              <div className="mt-3 flex flex-wrap items-center justify-between gap-3">
                <div className="flex gap-2">
                  <button type="button" onClick={() => setBreakForm(breakBlank())} className="rounded-lg border border-[#d3e2f3] px-4 py-2 text-sm font-semibold text-[#5f7f9f] hover:bg-[#f8fbff]">Reset</button>
                  <button
                    type="button"
                    onClick={async () => {
                      setErr(null);
                      setMsg(null);
                      const name = breakForm.name.trim();
                      if (!name) return void setErr("Break name is required.");
                      if (!TIME_24H_REGEX.test(breakForm.startTime) || !TIME_24H_REGEX.test(breakForm.endTime)) {
                        return void setErr("Start and End must be valid HH:mm values.");
                      }
                      if (!Number.isFinite(breakForm.durationMinutes) || breakForm.durationMinutes <= 0) {
                        return void setErr("Duration must be greater than zero.");
                      }
                      const start = parseTimeToMinutes(breakForm.startTime);
                      const end = parseTimeToMinutes(breakForm.endTime);
                      if (start === null || end === null) return void setErr("Invalid break start/end time.");
                      const isDuplicate = breakProfiles.some((x) => x.id !== breakForm.id && x.name.trim().toLowerCase() === name.toLowerCase());
                      if (isDuplicate) return void setErr("Break profile with this name already exists.");
                      const id = breakForm.id ?? (slug(name) || `break-${Date.now()}`);
                      const row: BreakTimeDefinition = {
                        id,
                        name,
                        startTime: breakForm.startTime,
                        endTime: breakForm.endTime,
                        durationMinutes: breakForm.durationMinutes,
                        calculateType: breakForm.calculateType,
                        minimumBreakMinutes: breakForm.minimumBreakMinutes,
                        isActive: true,
                      };
                      const nextRows = breakForm.id ? breakProfiles.map((x) => (x.id === breakForm.id ? row : x)) : [...breakProfiles, row];
                      try {
                        const saved = await saveShiftConfigToApi({ breakDefinitions: nextRows });
                        applyShiftConfig(saved);
                        setBreakForm(breakBlank());
                        setMsg(breakForm.id ? "Break profile updated." : "Break profile created.");
                      } catch (error) {
                        setErr(error instanceof Error ? error.message : "Failed to save break profile.");
                      }
                    }}
                    className="rounded-lg bg-[#1f5c8f] px-6 py-2 text-sm font-bold text-white hover:bg-[#164a75]"
                  >
                    {breakForm.id ? "Update Break" : "Save Break"}
                  </button>
                </div>
              </div>
            </div>

            <div className="rounded-lg border border-[--border] p-3">
              <div className="mb-2 text-sm font-semibold">Break Profile List</div>
              <div className="overflow-x-auto rounded-lg border border-[--border]">
                <table className="min-w-full text-left text-sm">
                  <thead className="bg-[#f7fbff] text-xs uppercase text-[--muted-foreground]">
                    <tr>
                      <th className="px-3 py-2">Name</th>
                      <th className="px-3 py-2">Window</th>
                      <th className="px-3 py-2">Duration</th>
                      <th className="px-3 py-2">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {breakProfiles.map((row) => (
                      <tr key={row.id} className="border-t border-[--border]">
                        <td className="px-3 py-2">
                          <div className="font-medium">{row.name}</div>
                          <div className="text-xs text-[--muted-foreground]">{row.id}</div>
                        </td>
                        <td className="px-3 py-2 text-xs text-[--muted-foreground]">{row.startTime} - {row.endTime}</td>
                        <td className="px-3 py-2 text-xs text-[--muted-foreground]">{row.durationMinutes} mins</td>
                        <td className="px-3 py-2">
                          <div className="flex gap-2">
                            <button type="button" onClick={() => setBreakForm({ id: row.id, name: row.name, startTime: row.startTime, endTime: row.endTime, durationMinutes: row.durationMinutes, calculateType: row.calculateType ?? "auto_deduct", minimumBreakMinutes: row.minimumBreakMinutes ?? 0, isActive: true })} className="rounded border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f]">Edit</button>
                            <button
                              type="button"
                              onClick={async () => {
                                setErr(null);
                                setMsg(null);
                                const nextRows = breakProfiles.filter((x) => x.id !== row.id);
                                const nextTimetables = timetables.map((tt) => ({
                                  ...tt,
                                  days: tt.days.map((d) =>
                                    (d.breakTimeIds?.includes(row.id) || d.breakTimeId === row.id)
                                      ? (() => {
                                          const nextIds = (d.breakTimeIds ?? []).filter((id) => id !== row.id);
                                          return { ...d, breakTimeIds: nextIds, breakTimeId: nextIds[0] ?? null, useBreakTime: nextIds.length > 0 };
                                        })()
                                      : d,
                                  ),
                                }));
                                try {
                                  const saved = await saveShiftConfigToApi({ breakDefinitions: nextRows, timetables: nextTimetables });
                                  applyShiftConfig(saved);
                                  setBreakForm((prev) => (prev.id === row.id ? breakBlank() : prev));
                                  setMsg("Break profile removed.");
                                } catch (error) {
                                  setErr(error instanceof Error ? error.message : "Failed to remove break profile.");
                                }
                              }}
                              className="rounded border border-[#f2d0d0] bg-white px-2 py-1 text-xs text-[#b94141]"
                            >
                              Delete
                            </button>
                          </div>
                        </td>
                      </tr>
                    ))}
                    {breakProfiles.length === 0 ? (
                      <tr>
                        <td colSpan={4} className="px-3 py-4 text-center text-sm text-[--muted-foreground]">
                          No break profiles created yet.
                        </td>
                      </tr>
                    ) : null}
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        )}
      </section>
    </div>
  );
}
