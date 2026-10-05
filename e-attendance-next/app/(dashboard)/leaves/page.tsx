"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { CalendarDays, Check, ChevronLeft, ChevronRight, Pencil, Plus, RefreshCcw, Search, Sparkles, Trash2, X } from "lucide-react";
import { syncShiftCalendarHolidays, type HolidayItem } from "@/components/data/shift-store";
import { adToBs, bsToAd, fromAdString, toAdString, type CalendarType as DateCalendarType } from "@/components/date/bs-converter";
import { getResolvedNepalHolidays } from "@/components/data/nepal-holidays";
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
  const bs = adToBs(base);
  const adLabel = base.toLocaleString(undefined, { month: "short", year: "numeric" });
  const bsLabel = bs ? `${BS_MONTHS[bs.month - 1]} ${bs.year}` : "";
  if (calendarType === "ad") {
    return bs ? `${adLabel} (${bsLabel} BS)` : adLabel;
  }
  return bs ? `${bsLabel} (${adLabel} AD)` : value;
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

  const seedDefaultNepalHolidays = async () => {
    const catalog = getResolvedNepalHolidays();
    for (const h of catalog) {
      try {
        await requestJson("/api/personnel/holidays", {
          method: "POST",
          body: JSON.stringify({
            holidayDate: h.holidayDate,
            holidayName: h.holidayName,
            description: h.description,
            isRecurring: h.isRecurring,
            department: "all",
            timeZoneId: "1",
            isActive: true,
          }),
        });
      } catch { /* ignore duplicates */ }
    }
  };

  const [importingHolidays, setImportingHolidays] = useState(false);

  const importNepalHolidays = async () => {
    setImportingHolidays(true);
    try {
      const catalog = getResolvedNepalHolidays();
      const existing = await requestJson<HolidayRow[]>("/api/personnel/holidays").catch(() => []);
      const existingSet = new Set(existing.map((h) => `${h.holidayDate}_${h.holidayName.toLowerCase().trim()}`));

      let addedCount = 0;
      for (const h of catalog) {
        const key = `${h.holidayDate}_${h.holidayName.toLowerCase().trim()}`;
        if (existingSet.has(key)) continue;
        try {
          await requestJson("/api/personnel/holidays", {
            method: "POST",
            body: JSON.stringify({
              holidayDate: h.holidayDate,
              holidayName: h.holidayName,
              description: h.description,
              isRecurring: h.isRecurring,
              department: "all",
              timeZoneId: "1",
              isActive: true,
            }),
          });
          addedCount++;
        } catch { /* ignore */ }
      }
      await loadHolidays();
      window.alert(
        addedCount > 0
          ? `Successfully synchronized ${addedCount} Nepal Public Holidays into the system!`
          : "All Nepal Public Holidays are already up-to-date in the calendar."
      );
    } catch (err) {
      window.alert(err instanceof Error ? err.message : "Failed to sync Nepal public holidays.");
    } finally {
      setImportingHolidays(false);
    }
  };

  const loadHolidays = useCallback(async () => {
    setHolidayLoading(true);
    setHolidayError(null);
    try {
      let rows = normalizeHolidayRows(await requestJson<HolidayRow[]>("/api/personnel/holidays"));
      if (rows.length === 0) {
        await seedDefaultNepalHolidays();
        rows = normalizeHolidayRows(await requestJson<HolidayRow[]>("/api/personnel/holidays"));
      }
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

  const currentCalendarYear = useMemo(() => {
    const anchor = fromAdString(calendarMonth) ?? new Date();
    if (calendarType === "bs") {
      const bs = adToBs(anchor);
      return bs?.year ?? 2082;
    }
    return anchor.getFullYear();
  }, [calendarMonth, calendarType]);

  const availableYears = useMemo(() => {
    const set = new Set<number>();
    set.add(currentCalendarYear);
    for (const h of holidayRows) {
      const d = fromAdString(h.holidayDate);
      if (!d) continue;
      if (calendarType === "bs") {
        const bs = adToBs(d);
        if (bs) set.add(bs.year);
      } else {
        set.add(d.getFullYear());
      }
    }
    return Array.from(set).sort((a, b) => a - b);
  }, [holidayRows, calendarType, currentCalendarYear]);

  const handleSelectYear = (year: number) => {
    if (calendarType === "bs") {
      const targetAd = bsToAd(year, 1, 1);
      if (targetAd) setCalendarMonth(toInputDate(targetAd));
    } else {
      setCalendarMonth(`${year}-01-01`);
    }
  };

  const filteredHolidayRows = useMemo(() => {
    const token = holidaySearch.trim().toLowerCase();
    return holidayRows
      .filter((row) => {
        const d = fromAdString(row.holidayDate);
        if (!d) return false;
        if (calendarType === "bs") {
          const bs = adToBs(d);
          const matchesYear = bs ? bs.year === currentCalendarYear : false;
          if (!matchesYear && !row.isRecurring) return false;
        } else {
          const matchesYear = d.getFullYear() === currentCalendarYear;
          if (!matchesYear && !row.isRecurring) return false;
        }

        if (!token) return true;
        return [row.holidayDate, row.holidayName, row.description].join(" ").toLowerCase().includes(token);
      })
      .sort((a, b) => a.holidayDate.localeCompare(b.holidayDate));
  }, [holidayRows, holidaySearch, calendarType, currentCalendarYear]);

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

  const selectedYearActiveHolidays = useMemo(() => {
    return activeHolidays.filter((row) => {
      const d = fromAdString(row.holidayDate);
      if (!d) return false;
      if (calendarType === "bs") {
        const bs = adToBs(d);
        const matchesYear = bs ? bs.year === currentCalendarYear : false;
        return matchesYear || row.isRecurring;
      } else {
        const matchesYear = d.getFullYear() === currentCalendarYear;
        return matchesYear || row.isRecurring;
      }
    });
  }, [activeHolidays, calendarType, currentCalendarYear]);

  const recurringCount = useMemo(() => selectedYearActiveHolidays.filter((row) => row.isRecurring).length, [selectedYearActiveHolidays]);
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

  const [leavePage, setLeavePage] = useState(1);
  const [leavePageSize, setLeavePageSize] = useState(10);

  const leaveTotalPages = Math.max(1, Math.ceil(filteredLeaves.length / leavePageSize));
  const leaveCurrentPage = Math.min(leavePage, leaveTotalPages);

  const paginatedLeaves = useMemo(() => {
    const start = (leaveCurrentPage - 1) * leavePageSize;
    return filteredLeaves.slice(start, start + leavePageSize);
  }, [filteredLeaves, leaveCurrentPage, leavePageSize]);

  useEffect(() => {
    setLeavePage(1);
  }, [leaveSearch, leaveStatus, leaveAreaFilter, leaveDeptFilter, leavePageSize]);

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
      {/* 1. Header Overview & Summary */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 sm:p-6 shadow-2xs">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between border-b border-slate-100 pb-5 mb-4">
          <div>
            <div className="flex items-center gap-2">
              <h1 className="text-xl font-bold tracking-tight text-slate-900">
                Leaves & Holiday Management
              </h1>
              <span className="rounded-md bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700 border border-blue-100">
                Time-Off Hub
              </span>
            </div>
            <p className="mt-1 text-xs text-slate-500">
              Configure organizational calendar holidays, manage employee leave requests, and review approvals.
            </p>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => openHolidayModal(today)}
              className="inline-flex items-center gap-1.5 rounded-xl bg-blue-600 px-3.5 py-2 text-xs font-semibold text-white hover:bg-blue-700 shadow-xs transition"
            >
              <Plus className="h-4 w-4" />
              Add Holiday
            </button>
            <button
              type="button"
              onClick={() => void importNepalHolidays()}
              disabled={importingHolidays}
              className="inline-flex items-center gap-1.5 rounded-xl border border-rose-200 bg-rose-50 px-3.5 py-2 text-xs font-semibold text-rose-700 hover:bg-rose-100 shadow-xs transition"
            >
              <Sparkles className={`h-4 w-4 text-rose-600 ${importingHolidays ? "animate-spin" : ""}`} />
              {importingHolidays ? "Updating Holidays..." : "Load Nepal Public Holidays"}
            </button>
            <button
              type="button"
              onClick={() => setLeaveModalOpen(true)}
              className="inline-flex items-center gap-1.5 rounded-xl border border-slate-300 bg-white px-3.5 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 shadow-xs transition"
            >
              <CalendarDays className="h-4 w-4 text-slate-500" />
              Apply Leave
            </button>
            <button
              type="button"
              onClick={() => void loadHolidays()}
              className="inline-flex items-center gap-1.5 rounded-xl border border-slate-300 bg-white px-3.5 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 transition"
            >
              <RefreshCcw className={`h-4 w-4 text-slate-500 ${holidayLoading ? "animate-spin" : ""}`} />
              Refresh
            </button>
          </div>
        </div>

        {/* 4 Summary Cards */}
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div className="flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50/60 p-4">
            <div>
              <span className="text-xs font-bold uppercase tracking-wider text-slate-500">Active Holidays</span>
              <p className="text-2xl font-extrabold text-slate-900 leading-tight mt-1">{selectedYearActiveHolidays.length}</p>
              <p className="text-[11px] text-slate-400">In year {currentCalendarYear} {calendarType.toUpperCase()}</p>
            </div>
          </div>
          <div className="flex items-center justify-between rounded-xl border border-rose-100 bg-rose-50/40 p-4">
            <div>
              <span className="text-xs font-bold uppercase tracking-wider text-rose-700">This Month</span>
              <p className="text-2xl font-extrabold text-rose-700 leading-tight mt-1">{visibleMonthHolidayCount}</p>
              <p className="text-[11px] text-rose-500">Current calendar view</p>
            </div>
          </div>
          <div className="flex items-center justify-between rounded-xl border border-emerald-100 bg-emerald-50/40 p-4">
            <div>
              <span className="text-xs font-bold uppercase tracking-wider text-emerald-700">Recurring</span>
              <p className="text-2xl font-extrabold text-emerald-700 leading-tight mt-1">{recurringCount}</p>
              <p className="text-[11px] text-emerald-500">Annual repeat</p>
            </div>
          </div>
          <div className="flex items-center justify-between rounded-xl border border-blue-100 bg-blue-50/40 p-4">
            <div>
              <span className="text-xs font-bold uppercase tracking-wider text-blue-700">Pending Leaves</span>
              <p className="text-2xl font-extrabold text-blue-700 leading-tight mt-1">{leaveSummary.pending}</p>
              <p className="text-[11px] text-blue-500">Awaiting approval</p>
            </div>
          </div>
        </div>
      </section>

      {/* 2. Interactive Calendar & Holiday List */}
      <section className="grid gap-4 xl:grid-cols-[minmax(0,1.7fr)_minmax(300px,1fr)]">
        <div className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs">
          <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-100 pb-4">
            <div className="flex items-center gap-2">
              <span className="rounded-md bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-700">
                {calendarType === "bs" ? "BS Calendar" : "AD Calendar"}
              </span>
              <h2 className="text-sm font-bold text-slate-900">Holiday Calendar</h2>
            </div>
            <div className="flex items-center gap-1.5">
              <button
                type="button"
                onClick={() => setCalendarMonth((prev) => shiftMonth(prev, -1, calendarType))}
                className="rounded-lg border border-slate-300 bg-white p-1.5 text-slate-600 hover:bg-slate-50"
              >
                <ChevronLeft className="h-4 w-4" />
              </button>
              <div className="min-w-[140px] rounded-lg border border-slate-200 bg-slate-50 px-3 py-1 text-center text-xs font-bold text-slate-800">
                {calendarHeaderLabel(calendarMonth, calendarType)}
              </div>
              <button
                type="button"
                onClick={() => setCalendarMonth((prev) => shiftMonth(prev, 1, calendarType))}
                className="rounded-lg border border-slate-300 bg-white p-1.5 text-slate-600 hover:bg-slate-50"
              >
                <ChevronRight className="h-4 w-4" />
              </button>
              <button
                type="button"
                onClick={() => setCalendarMonth(today)}
                className="rounded-lg border border-slate-300 bg-white px-2.5 py-1 text-xs font-semibold text-slate-700 hover:bg-slate-50"
              >
                Today
              </button>
            </div>
          </div>

          <div className="mt-4 grid grid-cols-7 gap-1.5">
            {WEEK_LABELS.map((label, index) => (
              <div
                key={label}
                className={`rounded-md p-1.5 text-center text-[10px] font-bold uppercase tracking-wider ${
                  index === 6 ? "bg-rose-50 text-rose-700 font-bold" : "bg-slate-50 text-slate-500"
                }`}
              >
                {label}
              </div>
            ))}
            {calendarCells.map((cell, cellIndex) => {
              const matching = activeHolidays.filter((row) => matchesHolidayDate(row, cell.date));
              const picked = primaryHoliday(matching, cell.date);
              const isHoliday = matching.length > 0;
              const isWeeklyOff = cell.dayOfWeek === 6;
              const isToday = cell.date === today;

              const cellParsed = fromAdString(cell.date);
              const cellBs = cellParsed ? adToBs(cellParsed) : null;
              const cellBsFormatted = cellBs ? `${BS_MONTHS[cellBs.month - 1]} ${cellBs.day}, ${cellBs.year} BS` : "";

              const dayStyles = isToday
                ? "border-blue-600 bg-blue-50/50 ring-2 ring-blue-600/30 shadow-xs"
                : isHoliday
                ? "border-rose-200 bg-rose-50/50"
                : isWeeklyOff
                ? "border-slate-200 bg-slate-50/60"
                : "border-slate-200 bg-white hover:bg-slate-50";

              return (
                <button
                  key={cell.date}
                  type="button"
                  onClick={() => openHolidayModal(cell.date, picked ?? undefined)}
                  title={
                    isHoliday
                      ? `${matching.map((r) => r.holidayName).join(", ")}${cellBsFormatted ? ` (${cellBsFormatted})` : ""} - Click to edit`
                      : undefined
                  }
                  className={`group relative flex min-h-[76px] flex-col rounded-xl border p-1.5 sm:p-2 text-left transition overflow-hidden ${dayStyles} ${
                    cell.inCurrentMonth ? "" : "opacity-30"
                  }`}
                >
                  <div className="flex items-start justify-between gap-1">
                    <div className="flex flex-col min-w-0">
                      <span className={`text-xs font-bold leading-tight ${isHoliday || isWeeklyOff ? "text-rose-600" : "text-slate-800"}`}>
                        {cell.primaryLabel}
                      </span>
                      <span className="text-[9px] font-semibold text-slate-400 tabular-nums whitespace-nowrap leading-tight mt-0.5">
                        {cell.secondaryLabel}
                      </span>
                    </div>
                    {isToday ? (
                      <span className="shrink-0 rounded bg-emerald-600 px-1 py-0.5 text-[8px] font-bold text-white leading-none">TODAY</span>
                    ) : isHoliday ? (
                      <span className="shrink-0 rounded bg-rose-600 px-1 py-0.5 text-[8px] font-bold text-white leading-none">OFF</span>
                    ) : null}
                  </div>
                  <div className="mt-1 flex-1 min-w-0">
                    {matching.slice(0, 1).map((row) => (
                      <div key={`${cell.date}-${row.id}`} className="truncate rounded bg-white px-1 py-0.5 text-[9px] font-semibold text-rose-700 shadow-2xs border border-rose-100" title={row.holidayName}>
                        {row.holidayName}
                      </div>
                    ))}
                  </div>

                  {/* Holiday Hover Detail Popover */}
                  {isHoliday && (
                    <div
                      className={`pointer-events-none absolute z-50 hidden group-hover:flex flex-col w-72 rounded-2xl border border-rose-200/90 bg-white/95 backdrop-blur-md p-3.5 shadow-2xl text-xs text-slate-800 transition-all duration-150 ${
                        cellIndex < 7 ? "top-full mt-2" : "bottom-full mb-2"
                      } ${
                        cell.dayOfWeek <= 1 ? "left-0" : cell.dayOfWeek >= 5 ? "right-0" : "left-1/2 -translate-x-1/2"
                      }`}
                    >
                      <div className="flex items-center justify-between border-b border-rose-100 pb-2 mb-2">
                        <div className="flex items-center gap-1.5">
                          <span className="flex h-2 w-2 rounded-full bg-rose-500 animate-pulse" />
                          <span className="font-bold text-rose-700 uppercase tracking-wider text-[10px]">
                            Public Holiday
                          </span>
                        </div>
                        <span className="rounded-full bg-rose-50 px-2 py-0.5 text-[9px] font-bold text-rose-700 border border-rose-200">
                          Office Closed
                        </span>
                      </div>

                      <div className="space-y-2">
                        {matching.map((row) => (
                          <div key={row.id} className="space-y-0.5">
                            <div className="flex items-start justify-between gap-1">
                              <h4 className="font-extrabold text-slate-900 text-xs leading-snug">
                                {row.holidayName}
                              </h4>
                              {row.isRecurring && (
                                <span className="rounded bg-amber-50 px-1.5 py-0.2 text-[8.5px] font-bold text-amber-700 border border-amber-200 shrink-0">
                                  Annual
                                </span>
                              )}
                            </div>
                            {row.description && (
                              <p className="text-[11px] text-slate-600 leading-relaxed font-normal">
                                {row.description}
                              </p>
                            )}
                          </div>
                        ))}
                      </div>

                      <div className="mt-2.5 pt-2 border-t border-slate-100 grid grid-cols-2 gap-1.5 text-[10.5px]">
                        <div>
                          <span className="text-[9px] font-semibold text-slate-400 uppercase tracking-wider block">
                            Bikram Sambat
                          </span>
                          <span className="font-bold text-rose-700">
                            {cellBsFormatted || "—"}
                          </span>
                        </div>
                        <div>
                          <span className="text-[9px] font-semibold text-slate-400 uppercase tracking-wider block">
                            Gregorian (AD)
                          </span>
                          <span className="font-semibold text-slate-700">
                            {cell.date}
                          </span>
                        </div>
                      </div>

                      <div className="mt-2 pt-1.5 border-t border-slate-100 text-[9.5px] text-slate-400 italic text-center">
                        Click cell to edit or manage holiday
                      </div>
                    </div>
                  )}
                </button>
              );
            })}
          </div>
        </div>

        {/* Holiday Quick List */}
        <div className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs flex flex-col h-full min-h-[520px]">
          {/* Header */}
          <div className="border-b border-slate-100 pb-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <h2 className="text-sm font-bold text-slate-900">Configured Holidays</h2>
                <p className="text-[11px] text-slate-500 font-medium">
                  Showing year {currentCalendarYear} {calendarType.toUpperCase()}
                </p>
              </div>
              <div className="flex items-center gap-2">
                <select
                  value={currentCalendarYear}
                  onChange={(e) => handleSelectYear(Number(e.target.value))}
                  className="rounded-lg border border-slate-200 bg-slate-50 px-2 py-1 text-xs font-bold text-slate-700 outline-none focus:border-blue-500"
                  title="Switch holiday list year"
                >
                  {availableYears.map((yr) => (
                    <option key={yr} value={yr}>
                      {yr} {calendarType.toUpperCase()}
                    </option>
                  ))}
                </select>
                <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[10px] font-bold text-slate-600">
                  {filteredHolidayRows.length} total
                </span>
              </div>
            </div>

            {/* Quick Search */}
            <div className="relative mt-2.5">
              <Search className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-400" />
              <input
                value={holidaySearch}
                onChange={(e) => setHolidaySearch(e.target.value)}
                placeholder="Search holidays by name or date..."
                style={{ paddingLeft: "2.25rem" }}
                className="w-full rounded-lg border border-slate-200 bg-slate-50/60 pr-7 py-1.5 text-xs text-slate-900 placeholder:text-slate-400 focus:border-blue-600 focus:bg-white focus:outline-none focus:ring-2 focus:ring-blue-500/20 transition"
              />
              {holidaySearch ? (
                <button
                  type="button"
                  onClick={() => setHolidaySearch("")}
                  className="absolute right-2 top-1/2 -translate-y-1/2 rounded p-0.5 text-slate-400 hover:text-slate-600 hover:bg-slate-200/60"
                >
                  <X className="h-3 w-3" />
                </button>
              ) : null}
            </div>
          </div>

          {/* List Content: Fills container naturally without cutting off with blank space */}
          <div className="mt-3 flex-1 min-h-[360px] max-h-[500px] overflow-y-auto pr-1 space-y-2">
            {!holidayLoading && filteredHolidayRows.length === 0 ? (
              <div className="rounded-xl border border-dashed border-slate-200 p-8 text-center text-xs text-slate-400">
                {holidaySearch
                  ? `No holidays match "${holidaySearch}".`
                  : `No holidays found for year ${currentCalendarYear} ${calendarType.toUpperCase()}.`}
              </div>
            ) : null}
            {filteredHolidayRows.map((row, index) => {
              const parsed = fromAdString(row.holidayDate);
              const bs = parsed ? adToBs(parsed) : null;
              const bsFormatted = bs ? `${bs.year}-${pad2(bs.month)}-${pad2(bs.day)} BS` : "";
              return (
                <article
                  key={row.id}
                  className="rounded-xl border border-slate-200 bg-slate-50/50 p-3 hover:bg-white hover:shadow-2xs transition"
                >
                  <div className="flex items-start justify-between gap-2.5">
                    {/* Holiday Number Badge */}
                    <span className="flex h-6 min-w-6 items-center justify-center rounded-md bg-rose-50 text-[11px] font-bold text-rose-700 border border-rose-200 shrink-0 mt-0.5">
                      #{index + 1}
                    </span>
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <h3 className="text-xs font-bold text-slate-900 truncate">{row.holidayName}</h3>
                        {row.isRecurring && (
                          <span className="rounded bg-amber-50 px-1.5 py-0.5 text-[9px] font-bold text-amber-700 border border-amber-200 shrink-0">
                            Annual
                          </span>
                        )}
                      </div>
                      <p className="mt-0.5 text-[11px] font-semibold text-rose-600 flex items-center gap-1.5 flex-wrap">
                        {calendarType === "bs" ? (
                          <>
                            <span>{bsFormatted || `${row.holidayDate} AD`}</span>
                            <span className="text-slate-300 font-normal">•</span>
                            <span className="text-slate-500 font-normal">({row.holidayDate} AD)</span>
                          </>
                        ) : (
                          <>
                            <span>{row.holidayDate} AD</span>
                            {bsFormatted && (
                              <>
                                <span className="text-slate-300 font-normal">•</span>
                                <span className="text-slate-500 font-normal">({bsFormatted})</span>
                              </>
                            )}
                          </>
                        )}
                      </p>
                      {row.description && <p className="mt-1 text-[11px] text-slate-500 line-clamp-1">{row.description}</p>}
                    </div>
                    <div className="flex items-center gap-1 shrink-0">
                      <button
                        type="button"
                        onClick={() => openHolidayModal(row.holidayDate, row)}
                        className="rounded p-1 text-slate-500 hover:bg-slate-200"
                        title="Edit Holiday"
                      >
                        <Pencil className="h-3.5 w-3.5" />
                      </button>
                      <button
                        type="button"
                        onClick={() => void deleteHoliday(row)}
                        className="rounded p-1 text-rose-600 hover:bg-rose-50"
                        title="Delete Holiday"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  </div>
                </article>
              );
            })}
          </div>

          {/* Card Footer */}
          <div className="mt-3 pt-3 border-t border-slate-100 flex items-center justify-between text-[11px] text-slate-500">
            <span>
              {filteredHolidayRows.length} of {activeHolidays.length} active holidays
            </span>
            <button
              type="button"
              onClick={() => openHolidayModal(today)}
              className="inline-flex items-center gap-1 font-semibold text-blue-600 hover:text-blue-700 hover:underline"
            >
              <Plus className="h-3.5 w-3.5" /> Add Holiday
            </button>
          </div>
        </div>
      </section>

      {/* 3. Leave Requests Table Card */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-100 pb-4 mb-4">
          <h2 className="text-sm font-bold text-slate-900">Employee Leave Applications</h2>
          <div className="flex flex-wrap items-center gap-2">
            <div className="relative min-w-[180px]">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" />
              <input
                value={leaveSearch}
                onChange={(event) => setLeaveSearch(event.target.value)}
                placeholder="Search staff, leave type..."
                style={{ paddingLeft: "2.25rem" }}
                className="w-full rounded-lg border border-slate-300 bg-white py-1.5 pl-9 pr-3 text-xs text-slate-900 outline-none focus:border-blue-600"
              />
            </div>
            <select
              value={leaveStatus}
              onChange={(event) => setLeaveStatus(event.target.value as LeaveStatus)}
              className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 outline-none focus:border-blue-600"
            >
              <option value="all">All Status</option>
              <option value="pending">Pending</option>
              <option value="approved">Approved</option>
              <option value="rejected">Rejected</option>
            </select>
          </div>
        </div>

        <div className="overflow-x-auto rounded-xl border border-slate-200">
          <table className="w-full text-left text-xs">
            <thead className="bg-slate-50 text-[11px] font-bold uppercase tracking-wider text-slate-500 border-b border-slate-200">
              <tr>
                <th className="px-4 py-3">Employee Code</th>
                <th className="px-4 py-3">Staff Name</th>
                <th className="px-4 py-3">Leave Type</th>
                <th className="px-4 py-3">From Date</th>
                <th className="px-4 py-3">To Date</th>
                <th className="px-4 py-3">Status</th>
                <th className="px-4 py-3 text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {filteredLeaves.length === 0 ? (
                <tr>
                  <td colSpan={7} className="p-6 text-center text-xs text-slate-400">
                    No leave requests found.
                  </td>
                </tr>
              ) : (
                paginatedLeaves.map((row) => (
                  <tr key={row.id} className="hover:bg-slate-50 transition">
                    <td className="px-4 py-3 font-semibold text-slate-900">{row.employeeCode}</td>
                    <td className="px-4 py-3 font-medium text-slate-800">{row.employee}</td>
                    <td className="px-4 py-3 text-slate-600">{row.type}</td>
                    <td className="px-4 py-3 text-slate-600">
                      {(() => {
                        const parsed = row.from ? fromAdString(row.from) : null;
                        const bs = parsed ? adToBs(parsed) : null;
                        return bs ? (
                          <div>
                            <span className="font-semibold text-xs text-slate-800">{`${bs.year}-${pad2(bs.month)}-${pad2(bs.day)} BS`}</span>
                            <span className="block text-[10px] text-slate-400 tabular-nums">({row.from} AD)</span>
                          </div>
                        ) : (
                          <span>{row.from}</span>
                        );
                      })()}
                    </td>
                    <td className="px-4 py-3 text-slate-600">
                      {(() => {
                        const parsed = row.to ? fromAdString(row.to) : null;
                        const bs = parsed ? adToBs(parsed) : null;
                        return bs ? (
                          <div>
                            <span className="font-semibold text-xs text-slate-800">{`${bs.year}-${pad2(bs.month)}-${pad2(bs.day)} BS`}</span>
                            <span className="block text-[10px] text-slate-400 tabular-nums">({row.to} AD)</span>
                          </div>
                        ) : (
                          <span>{row.to}</span>
                        );
                      })()}
                    </td>
                    <td className="px-4 py-3">
                      <span
                        className={`rounded-full px-2.5 py-0.5 text-[10px] font-bold uppercase border ${
                          row.status === "approved"
                            ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                            : row.status === "pending"
                            ? "bg-amber-50 text-amber-700 border-amber-200"
                            : "bg-rose-50 text-rose-700 border-rose-200"
                        }`}
                      >
                        {row.status}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-right">
                      {row.status === "pending" ? (
                        <div className="inline-flex gap-1.5">
                          <button
                            type="button"
                            onClick={() => void decideLeave(row.id, "approved")}
                            className="rounded-md bg-emerald-600 px-2.5 py-1 text-[11px] font-semibold text-white hover:bg-emerald-700"
                          >
                            Approve
                          </button>
                          <button
                            type="button"
                            onClick={() => void decideLeave(row.id, "rejected")}
                            className="rounded-md border border-rose-300 bg-white px-2.5 py-1 text-[11px] font-semibold text-rose-700 hover:bg-rose-50"
                          >
                            Reject
                          </button>
                        </div>
                      ) : (
                        <span className="text-[11px] text-slate-400 font-medium">—</span>
                      )}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        {filteredLeaves.length > 0 ? (
          <div className="mt-4 flex flex-wrap items-center justify-between gap-3 text-xs text-slate-500 border-t border-slate-100 pt-3">
            <div className="flex items-center gap-2">
              <span>
                Showing <strong className="text-slate-800 font-semibold">{(leaveCurrentPage - 1) * leavePageSize + 1}</strong> to{" "}
                <strong className="text-slate-800 font-semibold">{Math.min(leaveCurrentPage * leavePageSize, filteredLeaves.length)}</strong> of{" "}
                <strong className="text-slate-800 font-semibold">{filteredLeaves.length}</strong> application(s)
              </span>
              <span className="text-slate-300">•</span>
              <div className="flex items-center gap-1.5">
                <span>Per page:</span>
                <select
                  value={leavePageSize}
                  onChange={(e) => setLeavePageSize(Number(e.target.value))}
                  className="rounded-md border border-slate-200 bg-white px-2 py-1 text-xs text-slate-700 font-medium outline-none focus:border-blue-600"
                >
                  <option value={10}>10</option>
                  <option value={25}>25</option>
                  <option value={50}>50</option>
                  <option value={100}>100</option>
                </select>
              </div>
            </div>

            <div className="inline-flex items-center gap-1">
              <button
                type="button"
                onClick={() => setLeavePage(1)}
                disabled={leaveCurrentPage === 1}
                className="rounded-lg border border-slate-200 bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                title="First Page"
              >
                « First
              </button>
              <button
                type="button"
                onClick={() => setLeavePage((p) => Math.max(1, p - 1))}
                disabled={leaveCurrentPage === 1}
                className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-white px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
              >
                <ChevronLeft className="h-3.5 w-3.5" />
                Prev
              </button>
              <span className="px-2 font-medium text-slate-700">
                Page {leaveCurrentPage} of {leaveTotalPages}
              </span>
              <button
                type="button"
                onClick={() => setLeavePage((p) => Math.min(leaveTotalPages, p + 1))}
                disabled={leaveCurrentPage >= leaveTotalPages}
                className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-white px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
              >
                Next
                <ChevronRight className="h-3.5 w-3.5" />
              </button>
              <button
                type="button"
                onClick={() => setLeavePage(leaveTotalPages)}
                disabled={leaveCurrentPage >= leaveTotalPages}
                className="rounded-lg border border-slate-200 bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                title="Last Page"
              >
                Last »
              </button>
            </div>
          </div>
        ) : null}
      </section>

      {/* 4. Available Leave Types */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs">
        <div className="flex items-center justify-between mb-4 border-b border-slate-100 pb-3">
          <h2 className="text-sm font-bold text-slate-900">Available Leave Types</h2>
          <button
            type="button"
            onClick={() => openLeaveTypeModal()}
            className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 transition"
          >
            <Plus className="h-3.5 w-3.5" /> Add Leave Type
          </button>
        </div>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {leaveTypes.map((type) => (
            <div
              key={type.Id}
              className="group relative flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50/50 p-3 hover:bg-white hover:shadow-2xs transition"
            >
              <div>
                <p className="text-xs font-bold text-slate-900">{type.LeaveName}</p>
                <div className="mt-1 flex items-center gap-2">
                  <span className="rounded-full bg-blue-50 px-2 py-0.5 text-[10px] font-bold text-blue-700 border border-blue-100">
                    {type.MaxPerYear || 0} Days/Year
                  </span>
                  <span
                    className={`rounded-full px-2 py-0.5 text-[10px] font-bold border ${
                      type.IsPaid !== false
                        ? "bg-emerald-50 text-emerald-700 border-emerald-100"
                        : "bg-amber-50 text-amber-700 border-amber-100"
                    }`}
                  >
                    {type.IsPaid !== false ? "Paid" : "Unpaid"}
                  </span>
                </div>
              </div>
              <button
                onClick={() => openLeaveTypeModal(type)}
                className="rounded p-1 text-slate-400 opacity-0 group-hover:opacity-100 hover:text-slate-600"
              >
                <Pencil className="h-3.5 w-3.5" />
              </button>
            </div>
          ))}
          {leaveTypes.length === 0 && <p className="col-span-full py-6 text-center text-xs text-slate-400 italic">No leave types defined.</p>}
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
              <div className="space-y-1.5">
                <label className="text-xs font-bold text-[#123454]">Holiday Date</label>
                <BsDateField value={holidayForm.date} onChange={(value) => setHolidayForm((prev) => ({ ...prev, date: value }))} />
                {(() => {
                  const parsed = fromAdString(holidayForm.date);
                  const bs = parsed ? adToBs(parsed) : null;
                  const bsStr = bs ? `${bs.year}-${pad2(bs.month)}-${pad2(bs.day)} BS` : "";
                  return bsStr ? <p className="text-[10.5px] text-blue-700 font-semibold">{bsStr} • {holidayForm.date} AD</p> : null;
                })()}
              </div>
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
