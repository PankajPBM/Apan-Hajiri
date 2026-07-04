"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { CalendarDays, Check, ChevronLeft, ChevronRight, Pencil, Plus, RefreshCcw, Search, Trash2, X } from "lucide-react";
import { syncShiftCalendarHolidays, type HolidayItem } from "@/components/data/shift-store";
import { adToBs, bsToAd, type CalendarType as DateCalendarType } from "@/components/date/bs-converter";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDate } from "@/components/date/date-display";
import { BsDateField, defaultTodayAdString, shiftDateByDays } from "@/components/date/dual-date-input";
import { LeaveApplicationModal } from "@/components/modal/leave-application-modal";
import { readAuthSession } from "@/components/data/auth-store";
import { resolveApiBases } from "@/lib/api-bases";

type LeaveStatus = "all" | "pending" | "approved" | "rejected";

type HolidayRow = {
  id: number;
  holidayDate: string;
  holidayName: string;
  description: string;
  isRecurring: boolean;
  isActive: boolean;
  department: string;
  timeZoneId: string;
};

type HolidayForm = {
  date: string;
  name: string;
  description: string;
  isRecurring: boolean;
  isActive: boolean;
  timeZoneId: string;
};

type CalendarCell = {
  date: string;
  inCurrentMonth: boolean;
  primaryLabel: string;
  secondaryLabel: string;
  dayOfWeek: number;
};

const WEEK_LABELS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"] as const;
const BS_MONTHS = ["Baisakh", "Jestha", "Asar", "Shrawan", "Bhadra", "Aswin", "Kartik", "Mangsir", "Poush", "Magh", "Falgun", "Chaitra"] as const;
const EMPTY_HOLIDAY_FORM: HolidayForm = {
  date: defaultTodayAdString(),
  name: "",
  description: "",
  isRecurring: false,
  isActive: true,
  timeZoneId: "1",
};

let preferredApiBase = "";

function toInputDate(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

function monthStart(date: Date): string {
  return toInputDate(new Date(date.getFullYear(), date.getMonth(), 1));
}

function pad2(value: number): string {
  return String(value).padStart(2, "0");
}

function calendarHeaderLabel(value: string, calendarType: DateCalendarType): string {
  const base = new Date(`${value}T00:00:00`);
  if (Number.isNaN(base.getTime())) return value;
  if (calendarType === "ad") {
    return base.toLocaleString(undefined, { month: "long", year: "numeric" });
  }
  const bs = adToBs(base);
  return bs ? `${BS_MONTHS[bs.month - 1]} ${bs.year}` : value;
}

function shiftMonth(value: string, delta: number, calendarType: DateCalendarType): string {
  const base = new Date(`${value}T00:00:00`);
  if (Number.isNaN(base.getTime())) return value;
  if (calendarType === "ad") {
    return toInputDate(new Date(base.getFullYear(), base.getMonth() + delta, 1));
  }
  const bs = adToBs(base);
  if (!bs) return value;
  let year = bs.year;
  let month = bs.month + delta;
  while (month < 1) {
    year -= 1;
    month += 12;
  }
  while (month > 12) {
    year += 1;
    month -= 12;
  }
  const target = bsToAd(year, month, 1);
  return target ? toInputDate(target) : value;
}

function buildCalendar(value: string, calendarType: DateCalendarType): CalendarCell[] {
  const anchor = new Date(`${value}T00:00:00`);
  if (Number.isNaN(anchor.getTime())) return [];

  if (calendarType === "ad") {
    const first = new Date(anchor.getFullYear(), anchor.getMonth(), 1);
    const start = new Date(first);
    start.setDate(first.getDate() - first.getDay());
    return Array.from({ length: 42 }, (_, index) => {
      const current = new Date(start);
      current.setDate(start.getDate() + index);
      const bs = adToBs(current);
      return {
        date: toInputDate(current),
        inCurrentMonth: current.getMonth() === first.getMonth(),
        primaryLabel: pad2(current.getDate()),
        secondaryLabel: bs ? `${pad2(bs.month)}-${pad2(bs.day)}` : "--",
        dayOfWeek: current.getDay(),
      };
    });
  }

  const anchorBs = adToBs(anchor);
  if (!anchorBs) return [];
  const firstAd = bsToAd(anchorBs.year, anchorBs.month, 1);
  if (!firstAd) return [];
  const start = new Date(firstAd);
  start.setDate(firstAd.getDate() - firstAd.getDay());
  return Array.from({ length: 42 }, (_, index) => {
    const current = new Date(start);
    current.setDate(start.getDate() + index);
    const bs = adToBs(current);
    return {
      date: toInputDate(current),
      inCurrentMonth: Boolean(bs && bs.year === anchorBs.year && bs.month === anchorBs.month),
      primaryLabel: bs ? pad2(bs.day) : "--",
      secondaryLabel: `${pad2(current.getMonth() + 1)}-${pad2(current.getDate())}`,
      dayOfWeek: current.getDay(),
    };
  });
}

function matchesHolidayDate(row: HolidayRow, date: string): boolean {
  if (!row.isActive) return false;
  return row.holidayDate === date || (row.isRecurring && row.holidayDate.slice(5) === date.slice(5));
}

function primaryHoliday(rows: HolidayRow[], date: string): HolidayRow | null {
  return rows
    .filter((row) => matchesHolidayDate(row, date))
    .sort((a, b) => {
      const aExact = a.holidayDate === date ? 1 : 0;
      const bExact = b.holidayDate === date ? 1 : 0;
      if (aExact !== bExact) return bExact - aExact;
      return a.holidayName.localeCompare(b.holidayName);
    })[0] ?? null;
}

function leaveStatusClass(status: string): string {
  if (status === "approved") return "bg-[#e9f8ef] text-[#0f8a45]";
  if (status === "pending") return "bg-[#fff5e8] text-[#cc7a00]";
  return "bg-[#ffecec] text-[#be2f2f]";
}

function normalizeHolidayRows(payload: HolidayRow[]): HolidayRow[] {
  return (Array.isArray(payload) ? payload : [])
    .map((row, index) => ({
      id: Number.isFinite(row?.id) ? Number(row.id) : index + 1,
      holidayDate: (row?.holidayDate || "").trim(),
      holidayName: (row?.holidayName || "").trim() || "Holiday",
      description: (row?.description || "").trim(),
      isRecurring: Boolean(row?.isRecurring),
      isActive: row?.isActive !== false,
      department: (row?.department || "all").trim() || "all",
      timeZoneId: (row?.timeZoneId || "1").trim() || "1",
    }))
    .filter((row) => /^\d{4}-\d{2}-\d{2}$/.test(row.holidayDate))
    .sort((a, b) => a.holidayDate.localeCompare(b.holidayDate));
}

function toHolidayItem(row: HolidayRow): HolidayItem {
  return {
    id: String(row.id),
    date: row.holidayDate,
    name: row.holidayName,
    description: row.description,
    isRecurring: row.isRecurring,
    department: row.department || "all",
    timeZoneId: row.timeZoneId || "1",
    isActive: row.isActive,
  };
}

function apiBases(): string[] {
  return resolveApiBases({ preferredBase: preferredApiBase });
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch";
  for (const base of apiBases()) {
    try {
      const call = async () => {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 4500);
        try {
          return await fetch(`${base}${path}`, {
            ...init,
            headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
            cache: "no-store",
            signal: controller.signal,
          });
        } finally {
          clearTimeout(timeout);
        }
      };

      let response = await call();
      if (response.status === 503) {
        try {
          await fetch(`${base}/api/server/start`, { method: "POST", cache: "no-store" });
          response = await call();
        } catch {
          // keep original error
        }
      }

      if (response.ok) {
        preferredApiBase = base;
        return (await response.json()) as T;
      }
      lastError = (await response.text()) || `Request failed (${response.status})`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : lastError;
    }
  }
  throw new Error(lastError);
}

export default function LeavesPage() {
  const { calendarType } = useCalendarPreference();
  const now = useMemo(() => new Date(), []);
  const today = useMemo(() => toInputDate(now), [now]);

  const [holidayRows, setHolidayRows] = useState<HolidayRow[]>([]);
  const [holidayLoading, setHolidayLoading] = useState(false);
  const [holidaySaving, setHolidaySaving] = useState(false);
  const [holidayError, setHolidayError] = useState<string | null>(null);
  const [holidaySearch, setHolidaySearch] = useState("");
  const [holidayModalOpen, setHolidayModalOpen] = useState(false);
  const [editingHolidayId, setEditingHolidayId] = useState<number | null>(null);
  const [calendarMonth, setCalendarMonth] = useState(today);
  const [holidayForm, setHolidayForm] = useState<HolidayForm>(EMPTY_HOLIDAY_FORM);

  const [employees, setEmployees] = useState<Array<{ id: string; employeeCode: string; fullName: string }>>([]);
  const [employeesLoading, setEmployeesLoading] = useState(false);
  const [leaveTypes, setLeaveTypes] = useState<Array<{ Id: number; LeaveName: string; MaxPerYear?: number; IsPaid?: boolean }>>([]);
  const [leaveSaving, setLeaveSaving] = useState(false);
  const [leaveError, setLeaveError] = useState<string | null>(null);
  const [leaveForm, setLeaveForm] = useState({ personnelId: "", leaveTypeId: "", fromDate: defaultTodayAdString(), toDate: shiftDateByDays(defaultTodayAdString(), 1), reason: "" });

  const [leaveTypeModalOpen, setLeaveTypeModalOpen] = useState(false);
  const [editingLeaveTypeId, setEditingLeaveTypeId] = useState<number | null>(null);
  const [leaveTypeForm, setLeaveTypeForm] = useState({ name: "", maxDays: 12, isPaid: true });
  const [leaveTypeSaving, setLeaveTypeSaving] = useState(false);

  const [leaveStatus, setLeaveStatus] = useState<LeaveStatus>("all");
  const [leaveSearch, setLeaveSearch] = useState("");
  const [leaveAreaFilter, setLeaveAreaFilter] = useState("");
  const [leaveDeptFilter, setLeaveDeptFilter] = useState("");
  const [leaveModalOpen, setLeaveModalOpen] = useState(false);
  const [leaveRequests, setLeaveRequests] = useState<any[]>([]);
  const [employeeSearch, setEmployeeSearch] = useState("");

  const loadLeaves = useCallback(async () => {
    try {
      const rows = await requestJson<any[]>("/api/leave");
      const mapped = rows.map(r => ({
        id: r.id ?? r.Id,
        employeeCode: r.personnelCode ?? r.PersonnelCode ?? r.employeeCode ?? "",
        employee: r.personnelName ?? r.PersonnelName ?? r.employeeName ?? r.employeeCode ?? "Unknown",
        area: r.personnelArea ?? r.PersonnelArea ?? "",
        department: r.personnelDepartment ?? r.PersonnelDepartment ?? "",
        type: r.leaveType ?? r.LeaveType ?? r.leaveTypeName ?? r.LeaveTypeName ?? "Leave",
        from: r.fromDate ?? r.FromDate,
        to: r.toDate ?? r.ToDate,
        status: r.status ?? r.Status ?? "pending"
      }));
      setLeaveRequests(mapped);
    } catch { /* ignore */ }
  }, []);

  const [leaveActionBusyId, setLeaveActionBusyId] = useState<number | null>(null);

  const decideLeave = async (id: number, decision: "approved" | "rejected") => {
    setLeaveActionBusyId(id);
    try {
      // ActorId on the backend is a Guid; software user ids here are plain integers/strings
      // (auth_users.Id is BIGSERIAL), so it can't be sent as ActorId without breaking JSON
      // binding. Record who acted via remarks (free text) instead.
      const session = readAuthSession();
      await requestJson(`/api/leave/${id}/${decision === "approved" ? "approve" : "reject"}`, {
        method: "PUT",
        body: JSON.stringify({
          status: decision,
          remarks: session?.username ? `${decision === "approved" ? "Approved" : "Rejected"} by ${session.username}` : undefined,
        }),
      });
      await loadLeaves();
    } catch (error) {
      window.alert(error instanceof Error ? error.message : `Failed to ${decision === "approved" ? "approve" : "reject"} leave request.`);
    } finally {
      setLeaveActionBusyId(null);
    }
  };

  const loadHolidays = useCallback(async () => {
    setHolidayLoading(true);
    setHolidayError(null);
    try {
      const rows = normalizeHolidayRows(await requestJson<HolidayRow[]>("/api/personnel/holidays"));
      setHolidayRows(rows);
      syncShiftCalendarHolidays(rows.map(toHolidayItem));
    } catch (error) {
      setHolidayError(error instanceof Error ? error.message : "Failed to load holidays.");
    } finally {
      setHolidayLoading(false);
    }
  }, []);

  const loadEmployees = useCallback(async () => {
    setEmployeesLoading(true);
    try {
      const data = await requestJson<any>("/api/personnel/employees?status=active");
      const rows: any[] = Array.isArray(data) ? data : (data?.items ?? []);
      const mapped = rows.map(r => {
        const id = r.Id ?? r.id;
        const code = r.PersonnelCode ?? r.personnelCode ?? r.EmployeeCode ?? r.userId;
        const firstName = r.FirstName ?? r.firstName ?? "";
        const lastName = r.LastName ?? r.lastName ?? "";
        const fullName = r.fullName ?? r.FullName ?? `${firstName} ${lastName}`.trim();
        
        return { 
          id: String(id ?? ""), 
          employeeCode: String(code ?? ""), 
          fullName: fullName || String(code ?? "Unknown")
        };
      }).filter(x => x.id && x.employeeCode);
      setEmployees(mapped);
    } catch { /* ignore */ } finally {
      setEmployeesLoading(false);
    }
  }, []);

  const seedDefaultLeaveTypes = async () => {
    const defaults = [
      { leaveName: "Annual Leave", maxPerYear: 15, isPaid: true },
      { leaveName: "Sick Leave", maxPerYear: 12, isPaid: true },
      { leaveName: "Casual Leave", maxPerYear: 6, isPaid: true },
    ];
    for (const item of defaults) {
      try {
        await requestJson("/api/leave/types", {
          method: "POST",
          body: JSON.stringify(item),
        });
      } catch { /* ignore duplicates */ }
    }
  };

  const loadLeaveTypes = useCallback(async () => {
    try {
      let rows = await requestJson<any[]>("/api/leave/types?activeOnly=true");
      if (!Array.isArray(rows) || rows.length === 0) {
        await seedDefaultLeaveTypes();
        rows = await requestJson<any[]>("/api/leave/types?activeOnly=true");
      }
      if (Array.isArray(rows)) {
        const mapped = rows.map(r => ({
          Id: r.Id ?? r.id,
          LeaveName: r.LeaveName ?? r.leaveName ?? "Unnamed",
          MaxPerYear: r.MaxPerYear ?? r.maxPerYear ?? 0,
          IsPaid: r.IsPaid ?? r.isPaid ?? true
        }));
        setLeaveTypes(mapped);
        if (mapped.length > 0) setLeaveForm(f => ({ ...f, leaveTypeId: String(mapped[0].Id) }));
      }
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    void loadHolidays();
    void loadEmployees();
    void loadLeaveTypes();
    void loadLeaves();
  }, [loadHolidays, loadEmployees, loadLeaveTypes, loadLeaves]);

  const saveLeaveType = async () => {
    if (!leaveTypeForm.name.trim()) return;
    setLeaveTypeSaving(true);
    try {
      const payload = {
        leaveName: leaveTypeForm.name.trim(),
        maxPerYear: leaveTypeForm.maxDays,
        isPaid: leaveTypeForm.isPaid,
        isActive: true
      };
      await requestJson(editingLeaveTypeId ? `/api/leave/types/${editingLeaveTypeId}` : "/api/leave/types", {
        method: editingLeaveTypeId ? "PUT" : "POST",
        body: JSON.stringify(payload),
      });
      setLeaveTypeModalOpen(false);
      void loadLeaveTypes();
    } catch (err) {
      alert(err instanceof Error ? err.message : "Failed to save leave type");
    } finally {
      setLeaveTypeSaving(false);
    }
  };

  const openLeaveTypeModal = (row?: any) => {
    if (row) {
      setEditingLeaveTypeId(row.Id);
      setLeaveTypeForm({ name: row.LeaveName, maxDays: row.MaxPerYear || 12, isPaid: row.IsPaid !== false });
    } else {
      setEditingLeaveTypeId(null);
      setLeaveTypeForm({ name: "", maxDays: 12, isPaid: true });
    }
    setLeaveTypeModalOpen(true);
  };

  const saveLeave = async () => {
    if (!leaveForm.personnelId || !leaveForm.leaveTypeId || !leaveForm.reason.trim()) {
      setLeaveError("Employee, leave type, and reason are required.");
      return;
    }
    setLeaveSaving(true);
    setLeaveError(null);
    try {
      await requestJson("/api/leave", {
        method: "POST",
        body: JSON.stringify({
          // Backend's LeaveCreateRequest binds this as "employeeId" — sending "personnelId" (the
          // form field's own name, kept as-is) made every submission fail with a 400, same root
          // cause already fixed on the QR self-service /apply-leave page.
          employeeId: leaveForm.personnelId,
          leaveTypeId: parseInt(leaveForm.leaveTypeId, 10),
          fromDate: leaveForm.fromDate,
          toDate: leaveForm.toDate,
          reason: leaveForm.reason.trim(),
        }),
      });
      setLeaveModalOpen(false);
      setEmployeeSearch("");
      setLeaveForm({ personnelId: "", leaveTypeId: String(leaveTypes[0]?.Id || ""), fromDate: defaultTodayAdString(), toDate: shiftDateByDays(defaultTodayAdString(), 1), reason: "" });
      void loadLeaves();
    } catch (err) {
      setLeaveError(err instanceof Error ? err.message : "Failed to submit leave.");
    } finally {
      setLeaveSaving(false);
    }
  };

  const calendarCells = useMemo(() => buildCalendar(calendarMonth, calendarType), [calendarMonth, calendarType]);
  const activeHolidays = useMemo(() => holidayRows.filter((row) => row.isActive), [holidayRows]);
  const filteredHolidayRows = useMemo(() => {
    const token = holidaySearch.trim().toLowerCase();
    return holidayRows.filter((row) => {
      if (!token) return true;
      return [row.holidayDate, row.holidayName, row.description].join(" ").toLowerCase().includes(token);
    });
  }, [holidayRows, holidaySearch]);

  const visibleMonthHolidayCount = useMemo(() => {
    const ids = new Set<number>();
    for (const cell of calendarCells) {
      if (!cell.inCurrentMonth) continue;
      for (const row of activeHolidays) {
        if (matchesHolidayDate(row, cell.date)) ids.add(row.id);
      }
    }
    return ids.size;
  }, [calendarCells, activeHolidays]);

  const recurringCount = useMemo(() => activeHolidays.filter((row) => row.isRecurring).length, [activeHolidays]);
  const leaveSummary = useMemo(
    () => ({
      pending: leaveRequests.filter((row) => row.status === "pending").length,
      approved: leaveRequests.filter((row) => row.status === "approved").length,
      rejected: leaveRequests.filter((row) => row.status === "rejected").length,
    }),
    [leaveRequests],
  );

  const leaveAreaOptions = useMemo(
    () => Array.from(new Set(leaveRequests.map((row) => row.area).filter(Boolean))).sort(),
    [leaveRequests],
  );
  const leaveDeptOptions = useMemo(
    () => Array.from(new Set(leaveRequests.map((row) => row.department).filter(Boolean))).sort(),
    [leaveRequests],
  );

  const filteredLeaves = useMemo(() => {
    const token = leaveSearch.trim().toLowerCase();
    return leaveRequests.filter((row) => {
      if (leaveStatus !== "all" && row.status !== leaveStatus) return false;
      if (leaveAreaFilter && row.area !== leaveAreaFilter) return false;
      if (leaveDeptFilter && row.department !== leaveDeptFilter) return false;
      if (!token) return true;
      return [row.employeeCode, row.employee, row.type, row.from, row.to].join(" ").toLowerCase().includes(token);
    });
  }, [leaveRequests, leaveSearch, leaveStatus, leaveAreaFilter, leaveDeptFilter]);

  const openHolidayModal = (date?: string, row?: HolidayRow) => {
    const nextDate = row?.holidayDate || date || today;
    setCalendarMonth(nextDate);
    setEditingHolidayId(row?.id ?? null);
    setHolidayForm(
      row
        ? { date: row.holidayDate, name: row.holidayName, description: row.description, isRecurring: row.isRecurring, isActive: row.isActive, timeZoneId: row.timeZoneId || "1" }
        : { ...EMPTY_HOLIDAY_FORM, date: nextDate },
    );
    setHolidayError(null);
    setHolidayModalOpen(true);
  };

  const closeHolidayModal = () => {
    setHolidayModalOpen(false);
    setEditingHolidayId(null);
    setHolidayForm(EMPTY_HOLIDAY_FORM);
  };

  const saveHoliday = async () => {
    const payload = {
      holidayDate: holidayForm.date.trim(),
      holidayName: holidayForm.name.trim(),
      description: holidayForm.description.trim(),
      isRecurring: holidayForm.isRecurring,
      isActive: holidayForm.isActive,
      timeZoneId: holidayForm.timeZoneId.trim() || "1",
    };
    if (!payload.holidayDate || !payload.holidayName) {
      setHolidayError("Holiday date and holiday name are required.");
      return;
    }

    setHolidaySaving(true);
    setHolidayError(null);
    try {
      await requestJson<HolidayRow>(editingHolidayId ? `/api/personnel/holidays/${editingHolidayId}` : "/api/personnel/holidays", {
        method: editingHolidayId ? "PUT" : "POST",
        body: JSON.stringify(payload),
      });
      await loadHolidays();
      closeHolidayModal();
    } catch (error) {
      setHolidayError(error instanceof Error ? error.message : "Failed to save holiday.");
    } finally {
      setHolidaySaving(false);
    }
  };

  const deleteHoliday = async (row: HolidayRow) => {
    if (!window.confirm(`Delete holiday "${row.holidayName}" on ${row.holidayDate}?`)) return;
    setHolidaySaving(true);
    setHolidayError(null);
    try {
      await requestJson<{ success: boolean }>(`/api/personnel/holidays/${row.id}`, { method: "DELETE" });
      await loadHolidays();
      if (editingHolidayId === row.id) closeHolidayModal();
    } catch (error) {
      setHolidayError(error instanceof Error ? error.message : "Failed to delete holiday.");
    } finally {
      setHolidaySaving(false);
    }
  };

  return (
    <div className="space-y-4">
      <div className="tab-header">
        <h1 className="text-xl font-bold text-[#123454]">Leaves and Holiday Management</h1>
        <p className="text-xs text-[--muted-foreground]">Save holidays by date with detail, show them red in calendar view, and keep the same holiday data ready for reports.</p>
      </div>

      <section className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <article className="rounded-xl border border-[#d8e6f4] bg-[linear-gradient(145deg,#ffffff,#f6fbff)] p-3 shadow-sm transition duration-300 hover:-translate-y-0.5"><p className="text-[10px] font-semibold uppercase tracking-wider text-[#6987a3]">Active Holidays</p><p className="mt-1 text-2xl font-bold text-[#123454]">{activeHolidays.length}</p></article>
        <article className="rounded-xl border border-[#f1d3d3] bg-[linear-gradient(145deg,#fff9f9,#fff0f0)] p-3 shadow-sm transition duration-300 hover:-translate-y-0.5"><p className="text-[10px] font-semibold uppercase tracking-wider text-[#ab6262]">This Month</p><p className="mt-1 text-2xl font-bold text-[#a12424]">{visibleMonthHolidayCount}</p></article>
        <article className="rounded-xl border border-[#dce8de] bg-[linear-gradient(145deg,#fcfffc,#eff8f1)] p-3 shadow-sm transition duration-300 hover:-translate-y-0.5"><p className="text-[10px] font-semibold uppercase tracking-wider text-[#608767]">Recurring</p><p className="mt-1 text-2xl font-bold text-[#1d7b38]">{recurringCount}</p></article>
        <article className="rounded-xl border border-[#dae3f4] bg-[linear-gradient(145deg,#ffffff,#f3f7ff)] p-3 shadow-sm transition duration-300 hover:-translate-y-0.5"><p className="text-[10px] font-semibold uppercase tracking-wider text-[#657ca7]">Pending Leaves</p><p className="mt-1 text-2xl font-bold text-[#214f95]">{leaveSummary.pending}</p></article>
      </section>

      <section className="rounded-2xl border border-[#d8e6f4] bg-white p-3 shadow-sm">
        <div className="flex flex-wrap items-center gap-2">
          <button type="button" onClick={() => openHolidayModal(today)} className="inline-flex items-center gap-1.5 rounded-lg bg-[#1f5c8f] px-3 py-1.5 text-xs font-semibold text-white transition duration-300 hover:bg-[#164a72]"><Plus className="h-3.5 w-3.5" />Add Holiday</button>
          <button type="button" onClick={() => setLeaveModalOpen(true)} className="inline-flex items-center gap-1.5 rounded-lg border border-[#d3e1ee] bg-[#f7fbff] px-3 py-1.5 text-xs font-semibold text-[#1f5c8f] transition duration-300 hover:bg-[#eef7ff]"><CalendarDays className="h-3.5 w-3.5" />Apply Leave</button>
          <button type="button" onClick={() => void loadHolidays()} className="inline-flex items-center gap-1.5 rounded-lg border border-[#d3e1ee] bg-white px-3 py-1.5 text-xs font-semibold text-[#355a7a] transition duration-300 hover:bg-[#f9fcff]"><RefreshCcw className={`h-3.5 w-3.5 ${holidayLoading ? "animate-spin" : ""}`} />Refresh</button>
          <div className="relative ml-auto min-w-[200px] flex-1 md:max-w-xs">
            <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-[#6d87a4]" />
            <input value={holidaySearch} onChange={(event) => setHolidaySearch(event.target.value)} placeholder="Search holiday..." className="w-full rounded-lg border border-[#d3e1ee] bg-[#fbfdff] py-1.5 pl-8 pr-3 text-xs text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6]" />
          </div>
        </div>
        {holidayError ? <div className="mt-2 rounded-lg border border-[#f3c7c7] bg-[#fff3f3] px-3 py-2 text-xs text-[#a53535]">{holidayError}</div> : null}
      </section>

      <section className="grid gap-4 xl:grid-cols-[minmax(0,1.7fr)_minmax(280px,1fr)]">
        <div className="rounded-2xl border border-[#d8e6f4] bg-[radial-gradient(circle_at_top_left,#ffffff,#f7fbff_48%,#edf6ff)] p-3 shadow-sm">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <div className="inline-flex items-center gap-1.5 rounded-full bg-white/80 px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider text-[#1f5c8f] shadow-sm">
                <CalendarDays className="h-3 w-3" />
                {calendarType === "bs" ? "BS Calendar" : "AD Calendar"}
              </div>
              <h2 className="mt-1 text-base font-bold text-[#123454]">Holiday Calendar</h2>
            </div>
            <div className="flex flex-wrap items-center gap-1.5">
              <button type="button" onClick={() => setCalendarMonth((prev) => shiftMonth(prev, -1, calendarType))} className="rounded-lg border border-[#d3e1ee] bg-white p-1.5 text-[#1f5c8f] hover:bg-[#eef7ff]"><ChevronLeft className="h-3.5 w-3.5" /></button>
              <div className="min-w-[140px] rounded-xl bg-white/75 px-3 py-1 text-center shadow-sm">
                <div className="text-xs font-bold text-[#123454]">{calendarHeaderLabel(calendarMonth, calendarType)}</div>
                <div className="text-[10px] text-[#6a86a1]">{calendarType === "bs" ? `Ref AD ${calendarMonth}` : formatCalendarDate(calendarMonth, "bs", calendarMonth)}</div>
              </div>
              <button type="button" onClick={() => setCalendarMonth((prev) => shiftMonth(prev, 1, calendarType))} className="rounded-lg border border-[#d3e1ee] bg-white p-1.5 text-[#1f5c8f] hover:bg-[#eef7ff]"><ChevronRight className="h-4 w-4" /></button>
              <button type="button" onClick={() => setCalendarMonth(today)} className="rounded-lg border border-[#c8dbf0] bg-white px-2 py-1 text-[10px] font-bold text-[#355a7a] hover:bg-[#eef7ff]">Today</button>
            </div>
          </div>

          <div className="mt-3 grid grid-cols-7 gap-1.5">
            {WEEK_LABELS.map((label, index) => <div key={label} className={`rounded-lg px-2 py-1 text-center text-[10px] font-bold uppercase tracking-wider ${index === 6 ? "bg-[#fff4f4] text-[#b54646]" : "bg-[#eef5fb] text-[#6987a3]"}`}>{label}</div>)}
            {calendarCells.map((cell) => {
              const matching = activeHolidays.filter((row) => matchesHolidayDate(row, cell.date));
              const picked = primaryHoliday(matching, cell.date);
              const isHoliday = matching.length > 0;
              const isWeeklyOff = cell.dayOfWeek === 6;
              const isToday = cell.date === today;
              
              const dayStyles = isToday 
                ? "border-emerald-200 bg-emerald-50/50 ring-2 ring-emerald-500/20 hover:bg-emerald-50 shadow-[0_12px_24px_-20px_rgba(16,185,129,0.5)]"
                : isHoliday 
                  ? "border-[#efb8b8] bg-[#fff8f8] shadow-sm hover:-translate-y-0.5" 
                  : isWeeklyOff 
                    ? "border-[#f0d8c9] bg-[#fffaf7] hover:-translate-y-0.5" 
                    : "border-[#dbe8f4] bg-white hover:-translate-y-0.5 hover:bg-[#f8fbff]";

              const labelStyles = isToday 
                ? "text-emerald-700" 
                : isHoliday || isWeeklyOff 
                  ? "text-[#a82424]" 
                  : "text-[#123454]";

              return (
                <button key={cell.date} type="button" onClick={() => openHolidayModal(cell.date, picked ?? undefined)} className={`group relative flex min-h-[76px] flex-col overflow-hidden rounded-xl border p-2 text-left transition-all duration-300 ${dayStyles} ${cell.inCurrentMonth ? "" : "opacity-30"}`}>
                  <div className="flex items-center justify-between">
                    <div className="leading-tight">
                      <div className={`text-sm font-bold ${labelStyles}`}>{cell.primaryLabel}{isToday && " •"}</div>
                      <div className="text-[9px] font-medium text-[#6a86a1]">{cell.secondaryLabel}</div>
                    </div>
                    {isToday ? <span className="rounded-full bg-emerald-600 px-1.5 py-0.5 text-[8px] font-bold uppercase text-white shadow-sm">Today</span> : isHoliday ? <span className="rounded-full bg-[#b82828] px-1.5 py-0.5 text-[8px] font-bold uppercase text-white">Holiday</span> : null}
                  </div>
                  <div className="mt-1 min-h-0 grow space-y-0.5">
                    {matching.slice(0, 1).map((row) => <div key={`${cell.date}-${row.id}`} className="truncate rounded-md bg-white/80 px-1 py-0.5 text-[10px] font-semibold text-[#992c2c]">{row.holidayName}</div>)}
                    {matching.length > 1 ? <div className="pl-1 text-[9px] font-medium text-[#8a4646]">+{matching.length - 1} more</div> : null}
                  </div>
                </button>
              );
            })}
          </div>
        </div>

        <div className="rounded-2xl border border-[#d8e6f4] bg-white p-3 shadow-sm">
          <div className="flex items-center justify-between gap-2">
            <div><h2 className="text-base font-bold text-[#123454]">Holiday List</h2></div>
            <div className="rounded-full bg-[#eef5fb] px-2 py-0.5 text-[10px] font-bold text-[#355a7a]">{filteredHolidayRows.length} items</div>
          </div>
          <div className="mt-3 space-y-2">
            {!holidayLoading && filteredHolidayRows.length === 0 ? <div className="rounded-xl border border-dashed border-[#c8d9eb] px-3 py-6 text-center text-xs text-[#5f7f9f]">No records found.</div> : null}
            {!holidayLoading && filteredHolidayRows.map((row) => (
              <article key={row.id} className={`rounded-xl border p-2 transition duration-300 hover:shadow-sm ${row.isActive ? "border-[#eed0d0] bg-[#fff8f8]" : "border-[#dbe8f4] bg-[#f8fbff]"}`}>
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <h3 className="truncate text-xs font-bold text-[#123454]">{row.holidayName}</h3>
                      {row.isRecurring ? <span className="rounded-full bg-[#ffe7c4] px-1.5 py-0.5 text-[8px] font-bold uppercase text-[#b76500]">Rep</span> : null}
                    </div>
                    <p className="mt-0.5 text-[10px] font-bold text-[#8f2e2e]">{formatCalendarDate(row.holidayDate, calendarType, row.holidayDate)}</p>
                    <p className="mt-1 line-clamp-2 text-[11px] text-[#4d6782]">{row.description}</p>
                  </div>
                  <div className="flex shrink-0 gap-1">
                    <button type="button" onClick={() => openHolidayModal(row.holidayDate, row)} className="rounded-md border border-[#d3e1ee] bg-white p-1 text-[#1f5c8f] hover:bg-[#f0f7ff]"><Pencil className="h-3.5 w-3.5" /></button>
                    <button type="button" onClick={() => void deleteHoliday(row)} className="rounded-md border border-[#efc8c8] bg-white p-1 text-[#b12f2f] hover:bg-[#fff5f5]"><Trash2 className="h-3.5 w-3.5" /></button>
                  </div>
                </div>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="rounded-2xl border border-[#d8e6f4] bg-white p-3 shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div><h2 className="text-base font-bold text-[#123454]">Leave Requests</h2></div>
          <div className="flex flex-wrap items-center gap-2">
            <div className="relative min-w-[180px]">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-[#6d87a4]" />
              <input value={leaveSearch} onChange={(event) => setLeaveSearch(event.target.value)} placeholder="Search..." className="w-full rounded-lg border border-[#d3e1ee] bg-[#fbfdff] py-1.5 pl-8 pr-3 text-xs text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6]" />
            </div>
            <select value={leaveAreaFilter} onChange={(event) => setLeaveAreaFilter(event.target.value)} className="rounded-lg border border-[#d3e1ee] bg-[#fbfdff] px-2 py-1.5 text-xs text-[#123454] outline-none">
              <option value="">All Areas</option>
              {leaveAreaOptions.map((area) => <option key={area} value={area}>{area}</option>)}
            </select>
            <select value={leaveDeptFilter} onChange={(event) => setLeaveDeptFilter(event.target.value)} className="rounded-lg border border-[#d3e1ee] bg-[#fbfdff] px-2 py-1.5 text-xs text-[#123454] outline-none">
              <option value="">All Departments</option>
              {leaveDeptOptions.map((dept) => <option key={dept} value={dept}>{dept}</option>)}
            </select>
            <select value={leaveStatus} onChange={(event) => setLeaveStatus(event.target.value as LeaveStatus)} className="rounded-lg border border-[#d3e1ee] bg-[#fbfdff] px-2 py-1.5 text-xs text-[#123454] outline-none">
              <option value="all">All</option>
              <option value="pending">Pending</option>
              <option value="approved">Approved</option>
              <option value="rejected">Rejected</option>
            </select>
          </div>
        </div>

        <div className="mt-3 overflow-x-auto">
          <table className="min-w-full text-left text-xs">
            <thead className="bg-[#f8fbff] text-[10px] uppercase tracking-wider text-[#6b88a6] font-bold"><tr><th className="px-3 py-2">Employee ID</th><th className="px-3 py-2">Employee</th><th className="px-3 py-2">Area</th><th className="px-3 py-2">Department</th><th className="px-3 py-2">Type</th><th className="px-3 py-2">From</th><th className="px-3 py-2">To</th><th className="px-3 py-2">Status</th><th className="px-3 py-2">Actions</th></tr></thead>
            <tbody className="divide-y divide-[#e5effa]">
              {filteredLeaves.length === 0 ? <tr><td colSpan={9} className="px-3 py-6 text-center text-xs text-[#5f7f9f]">No requests found.</td></tr> : null}
              {filteredLeaves.map((row) => (
                <tr key={row.id} className="hover:bg-[#fbfdff]">
                  <td className="px-3 py-2 text-[#355a7a]">{row.employeeCode || "-"}</td>
                  <td className="px-3 py-2 font-bold text-[#123454]">{row.employee}</td>
                  <td className="px-3 py-2 text-[#355a7a]">{row.area || "-"}</td>
                  <td className="px-3 py-2 text-[#355a7a]">{row.department || "-"}</td>
                  <td className="px-3 py-2 text-[#355a7a]">{row.type}</td>
                  <td className="px-3 py-2 text-[#355a7a]">{formatCalendarDate(row.from, calendarType, row.from)}</td>
                  <td className="px-3 py-2 text-[#355a7a]">{formatCalendarDate(row.to, calendarType, row.to)}</td>
                  <td className="px-3 py-2"><span className={`rounded-full px-2 py-0.5 text-[10px] font-bold capitalize ${leaveStatusClass(row.status)}`}>{row.status}</span></td>
                  <td className="px-3 py-2">
                    {row.status === "pending" ? (
                      <div className="flex gap-1.5">
                        <button
                          type="button"
                          disabled={leaveActionBusyId === row.id}
                          onClick={() => void decideLeave(row.id, "approved")}
                          className="rounded-md border border-[#bfe3c9] bg-[#eefbf1] px-2 py-1 text-[10px] font-bold text-[#0f8a45] transition duration-300 hover:bg-[#dff5e4] disabled:opacity-50"
                        >
                          Approve
                        </button>
                        <button
                          type="button"
                          disabled={leaveActionBusyId === row.id}
                          onClick={() => void decideLeave(row.id, "rejected")}
                          className="rounded-md border border-[#f3c7c7] bg-[#fff3f3] px-2 py-1 text-[10px] font-bold text-[#a53535] transition duration-300 hover:bg-[#ffe6e6] disabled:opacity-50"
                        >
                          Reject
                        </button>
                      </div>
                    ) : (
                      <span className="text-[10px] text-[#8fa5bd]">—</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className="rounded-2xl border border-[#d8e6f4] bg-white p-3 shadow-sm">
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-base font-bold text-[#123454]">Available Leave Types</h2>
          <button type="button" onClick={() => openLeaveTypeModal()} className="inline-flex items-center gap-1.5 rounded-lg bg-[#1f5c8f] px-3 py-1.5 text-xs font-semibold text-white transition duration-300 hover:bg-[#164a72]">
            <Plus className="h-3.5 w-3.5" />Add Leave Type
          </button>
        </div>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {leaveTypes.map((type) => (
            <div key={type.Id} className="group relative flex items-center justify-between rounded-xl border border-[#e2effb] bg-[#f8fbff] p-3 transition hover:shadow-md">
              <div>
                <p className="text-sm font-bold text-[#1e4c78]">{type.LeaveName}</p>
                <div className="mt-1 flex items-center gap-2">
                  <span className="rounded-full bg-blue-100 px-2 py-0.5 text-[10px] font-bold text-blue-700">{type.MaxPerYear || 0} Days/Year</span>
                  <span className={`rounded-full px-2 py-0.5 text-[10px] font-bold ${type.IsPaid !== false ? "bg-emerald-100 text-emerald-700" : "bg-orange-100 text-orange-700"}`}>
                    {type.IsPaid !== false ? "Paid" : "Unpaid"}
                  </span>
                </div>
              </div>
              <button onClick={() => openLeaveTypeModal(type)} className="rounded-lg p-1.5 text-[#355a7a] opacity-0 transition group-hover:opacity-100 hover:bg-white/50">
                <Pencil className="h-3.5 w-3.5" />
              </button>
            </div>
          ))}
          {leaveTypes.length === 0 && <p className="col-span-full py-8 text-center text-xs text-slate-400 italic">No leave types defined.</p>}
        </div>
      </section>

      {leaveTypeModalOpen ? (
        <div className="fixed inset-0 z-[60] grid place-items-center bg-black/40 p-4 backdrop-blur-sm">
          <div className="w-full max-w-sm rounded-2xl border border-[#d8e6f4] bg-white p-5 shadow-2xl animate-in zoom-in-95 duration-200">
            <div className="flex items-start justify-between">
              <div>
                <h2 className="text-lg font-bold text-[#123454]">{editingLeaveTypeId ? "Edit Leave Type" : "Add Leave Type"}</h2>
                <p className="text-xs text-slate-500">Configure leave limits and details.</p>
              </div>
              <button onClick={() => setLeaveTypeModalOpen(false)} className="rounded-lg p-1 text-slate-400 hover:bg-slate-50"><X className="h-4 w-4" /></button>
            </div>
            <div className="mt-4 space-y-4">
              <div className="space-y-1.5">
                <label className="text-xs font-bold text-[#123454]">Leave Name *</label>
                <input 
                  value={leaveTypeForm.name} 
                  onChange={(e) => setLeaveTypeForm(f => ({ ...f, name: e.target.value }))}
                  placeholder="e.g. Sick Leave" 
                  className="w-full rounded-xl border border-[#d3e1ee] bg-[#fbfdff] px-3 py-2 text-sm text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6]" 
                />
              </div>
              <div className="space-y-1.5">
                <label className="text-xs font-bold text-[#123454]">Max Days per Year *</label>
                <input 
                  type="number"
                  value={leaveTypeForm.maxDays} 
                  onChange={(e) => setLeaveTypeForm(f => ({ ...f, maxDays: parseInt(e.target.value) || 0 }))}
                  className="w-full rounded-xl border border-[#d3e1ee] bg-[#fbfdff] px-3 py-2 text-sm text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6]" 
                />
              </div>
              <label className="flex items-center gap-2 cursor-pointer">
                <input 
                  type="checkbox" 
                  checked={leaveTypeForm.isPaid} 
                  onChange={(e) => setLeaveTypeForm(f => ({ ...f, isPaid: e.target.checked }))}
                  className="h-4 w-4 rounded border-[#d3e1ee] text-[#1f5c8f] focus:ring-[#1f5c8f]" 
                />
                <span className="text-xs font-bold text-[#123454]">Is Paid Leave?</span>
              </label>
            </div>
            <div className="mt-6 flex justify-end gap-2">
              <button onClick={() => setLeaveTypeModalOpen(false)} className="rounded-xl border border-[#d3e1ee] px-4 py-2 text-sm font-bold text-[#355a7a] hover:bg-slate-50">Cancel</button>
              <button 
                onClick={saveLeaveType} 
                disabled={leaveTypeSaving}
                className="rounded-xl bg-[#1f5c8f] px-6 py-2 text-sm font-bold text-white shadow-lg shadow-blue-900/10 hover:bg-[#164a72] disabled:opacity-50"
              >
                {leaveTypeSaving ? "Saving..." : "Save Type"}
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {holidayModalOpen ? (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/35 p-4">
          <div className="w-full max-w-lg rounded-2xl border border-[#d8e6f4] bg-white p-4 shadow-2xl">
            <div className="flex items-start justify-between gap-4">
              <div><h2 className="text-lg font-bold text-[#123454]">{editingHolidayId ? "Edit Holiday" : "Add Holiday"}</h2><p className="mt-0.5 text-xs text-[#5f7f9f]">Save holiday date, title, and detail.</p></div>
              <button type="button" onClick={closeHolidayModal} className="rounded-lg border border-[#d3e1ee] p-1.5 text-[#355a7a]"><X className="h-4 w-4" /></button>
            </div>
            <div className="mt-4 grid gap-3 md:grid-cols-2">
              <div className="space-y-1.5"><label className="text-xs font-bold text-[#123454]">Holiday Date</label><BsDateField value={holidayForm.date} onChange={(value) => setHolidayForm((prev) => ({ ...prev, date: value }))} /></div>
              <div className="space-y-1.5"><label className="text-xs font-bold text-[#123454]">Holiday Name</label><input value={holidayForm.name} onChange={(event) => setHolidayForm((prev) => ({ ...prev, name: event.target.value }))} placeholder="Holiday name" className="w-full rounded-lg border border-[#d3e1ee] bg-[#fbfdff] px-2.5 py-1.5 text-xs text-[#123454] outline-none ring-[#b7d0e6] focus:ring-1" /></div>
              <div className="space-y-1.5"><label className="text-xs font-bold text-[#123454]">Time Zone ID</label><input value={holidayForm.timeZoneId} onChange={(event) => setHolidayForm((prev) => ({ ...prev, timeZoneId: event.target.value }))} placeholder="e.g. 1" className="w-full rounded-lg border border-[#d3e1ee] bg-[#fbfdff] px-2.5 py-1.5 text-xs text-[#123454] outline-none ring-[#b7d0e6] focus:ring-1" /></div>
              <div className="space-y-1.5 md:col-span-2"><label className="text-xs font-bold text-[#123454]">Holiday Detail</label><textarea value={holidayForm.description} onChange={(event) => setHolidayForm((prev) => ({ ...prev, description: event.target.value }))} rows={3} placeholder="Detail..." className="w-full rounded-xl border border-[#d3e1ee] bg-[#fbfdff] px-2.5 py-1.5 text-xs text-[#123454] outline-none ring-[#b7d0e6] focus:ring-1" /></div>
            </div>
            <div className="mt-3 flex flex-wrap items-center gap-4 rounded-xl bg-[#f8fbff] px-3 py-2">
              <label className="inline-flex items-center gap-1.5 text-xs font-medium text-[#355a7a]"><input type="checkbox" checked={holidayForm.isRecurring} onChange={(event) => setHolidayForm((prev) => ({ ...prev, isRecurring: event.target.checked }))} className="h-3.5 w-3.5 rounded accent-[#1f5c8f]" />Repeat every year</label>
              <label className="inline-flex items-center gap-1.5 text-xs font-medium text-[#355a7a]"><input type="checkbox" checked={holidayForm.isActive} onChange={(event) => setHolidayForm((prev) => ({ ...prev, isActive: event.target.checked }))} className="h-3.5 w-3.5 rounded accent-[#1f5c8f]" />Is active</label>
            </div>
            {holidayError ? <div className="mt-3 rounded-lg border border-[#f3c7c7] bg-[#fff3f3] px-3 py-2 text-xs text-[#a53535]">{holidayError}</div> : null}
            <div className="mt-4 flex flex-wrap items-center justify-end gap-2">
              {editingHolidayId ? <button type="button" onClick={() => { const row = holidayRows.find((item) => item.id === editingHolidayId); if (row) void deleteHoliday(row); }} className="inline-flex items-center gap-1.5 rounded-lg border border-[#efc8c8] bg-white px-3 py-1.5 text-xs font-bold text-[#b12f2f]"><Trash2 className="h-3.5 w-3.5" />Delete</button> : null}
              <button type="button" onClick={closeHolidayModal} className="rounded-lg border border-[#d3e1ee] bg-white px-3 py-1.5 text-xs font-bold text-[#355a7a]">Cancel</button>
              <button type="button" onClick={() => void saveHoliday()} disabled={holidaySaving} className="inline-flex items-center gap-1.5 rounded-lg bg-[#1f5c8f] px-3 py-1.5 text-xs font-bold text-white disabled:cursor-not-allowed disabled:opacity-60"><Check className="h-3.5 w-3.5" />{holidaySaving ? "Saving..." : editingHolidayId ? "Update" : "Save"}</button>
            </div>
          </div>
        </div>
      ) : null}

      {leaveModalOpen && (
        <LeaveApplicationModal 
          onClose={() => setLeaveModalOpen(false)} 
          onSuccess={() => void loadLeaves()} 
        />
      )}
    </div>
  );
}
