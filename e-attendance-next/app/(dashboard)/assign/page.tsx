"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import { Building2, Check, Users, UsersRound } from "lucide-react";
import { resolveApiBases } from "@/lib/api-bases";
import { BsDateField } from "@/components/date/dual-date-input";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { resolveCalendarYearRange } from "@/components/date/calendar-range";
import { formatCalendarDate } from "@/components/date/date-display";
import {
  ensureDefaultDepartmentShiftAssignments,
  loadShiftConfigFromApi,
  readShiftConfigSnapshot,
  saveShiftConfigToApi,
  isHoliday,
  defaultShiftCalendarSettings,
  type ShiftAssignment,
  type ShiftAssignmentTarget,
  type ShiftCalendarSettings,
  type ShiftDefinition,
  type ShiftGroup,
  type ShiftPlanDefinition,
  type TimetableDefinition,
} from "@/components/data/shift-store";

type EmployeeRow = {
  id: string;
  employeeCode: string;
  fullName: string;
  department: string;
  isActive: boolean;
};

type DepartmentRow = {
  id: string;
  code: string;
  name: string;
  isDefault?: boolean;
};

type PagedResponse<T> = {
  items?: T[];
};

type EmployeeApiRow = Partial<EmployeeRow> & {
  id?: string;
  employeeCode?: string;
  fullName?: string;
  department?: string;
  isActive?: boolean;
};

type DepartmentApiRow = Partial<DepartmentRow> & {
  id?: string;
  code?: string;
  name?: string;
  isDefault?: boolean;
};

type AssignmentForm = {
  id: string | null;
  targetType: ShiftAssignmentTarget;
  department: string;
  employeeCode: string;
  groupId: string;
  shiftId: string;
  effectiveFrom: string;
  effectiveTo: string;
  isActive: boolean;
};

type GroupForm = {
  id: string | null;
  name: string;
  memberEmployeeCodes: string[];
  isActive: boolean;
};

function formatTargetTypeLabel(targetType: ShiftAssignmentTarget): string {
  if (targetType === "department") return "Department";
  if (targetType === "employee") return "Employee";
  return "Group";
}

function slug(value: string) {
  return value.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "");
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch.";
  for (const base of resolveApiBases({ includeCurrentOrigin: true })) {
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
        return (await response.json()) as T;
      }
      lastError = (await response.text()) || `Request failed (${response.status})`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : lastError;
    }
  }
  throw new Error(lastError);
}

async function requestItems<T>(path: string, init?: RequestInit): Promise<T[]> {
  const payload = await requestJson<T[] | PagedResponse<T>>(path, init);
  if (Array.isArray(payload)) return payload;
  if (payload && Array.isArray(payload.items)) return payload.items;
  return [];
}

export default function AssignPage() {
  const searchParams = useSearchParams();
  const section = searchParams.get("section");
  const viewMode = section === "groups" ? "groups" : section === "preview" ? "preview" : "assignment";

  const { calendarType } = useCalendarPreference();
  const now = useMemo(() => new Date(), []);
  const yearRange = useMemo(() => resolveCalendarYearRange(now, calendarType), [now, calendarType]);
  const [loading, setLoading] = useState(true);
  const [msg, setMsg] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [assignmentSaving, setAssignmentSaving] = useState(false);
  const [groupSaving, setGroupSaving] = useState(false);

  const [employees, setEmployees] = useState<EmployeeRow[]>([]);
  const [departments, setDepartments] = useState<DepartmentRow[]>([]);
  const [shiftDefinitions, setShiftDefinitions] = useState<ShiftDefinition[]>([]);
  const [shiftAssignments, setShiftAssignments] = useState<ShiftAssignment[]>([]);
  const [shiftGroups, setShiftGroups] = useState<ShiftGroup[]>([]);
  const [timetables, setTimetables] = useState<TimetableDefinition[]>([]);
  const [shiftPlans, setShiftPlans] = useState<ShiftPlanDefinition[]>([]);
  const [shiftCalendar, setShiftCalendar] = useState<ShiftCalendarSettings>(defaultShiftCalendarSettings());
  const [previewDate, setPreviewDate] = useState(() => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  });
  const [previewView, setPreviewView] = useState<"department" | "employee" | "group">("department");
  const [configLoaded, setConfigLoaded] = useState(false);

  const [activeTab, setActiveTab] = useState<ShiftAssignmentTarget>(() => {
    if (viewMode === "groups") return "group";
    return searchParams.get("tab") === "employee"
      ? "employee"
      : searchParams.get("tab") === "group"
        ? "group"
        : "department";
  });
  const [formOpen, setFormOpen] = useState(false);
  const [formMode, setFormMode] = useState<"add" | "edit">("add");
  const deptOptions = useMemo(() => {
    const seen = new Set<string>();
    const ordered = departments
      .filter((d) => d.name?.trim())
      .slice()
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
    const unique: DepartmentRow[] = [];
    for (const dept of ordered) {
      const key = dept.name.trim().toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      unique.push(dept);
    }
    return unique;
  }, [departments]);
  const [assignmentForm, setAssignmentForm] = useState<AssignmentForm>({
    id: null,
    targetType: "department",
    department: "all",
    employeeCode: "",
    groupId: "",
    shiftId: "",
    effectiveFrom: yearRange.start,
    effectiveTo: yearRange.end,
    isActive: true,
  });
  const [groupForm, setGroupForm] = useState<GroupForm>({
    id: null,
    name: "",
    memberEmployeeCodes: [],
    isActive: true,
  });
  const [groupSearch, setGroupSearch] = useState("");
  const [groupMembersOpen, setGroupMembersOpen] = useState(false);

  useEffect(() => {
    if (viewMode === "groups") {
      setActiveTab("group");
    }
  }, [viewMode]);

  const shiftById = useMemo(() => new Map(shiftPlans.map((s) => [s.id, s])), [shiftPlans]);
  const groupById = useMemo(() => new Map(shiftGroups.map((g) => [g.id, g])), [shiftGroups]);
  const defaultShiftId = useMemo(
    () => shiftPlans.find((x) => x.isActive)?.id ?? shiftPlans[0]?.id ?? "",
    [shiftPlans],
  );

  const activeEmployees = useMemo(
    () => employees.filter((x) => x.isActive).sort((a, b) => a.fullName.localeCompare(b.fullName)),
    [employees],
  );
  const employeeByCode = useMemo(
    () => new Map(activeEmployees.map((e) => [e.employeeCode, e])),
    [activeEmployees],
  );

  const filteredGroupMembers = useMemo(() => {
    const search = groupSearch.trim().toLowerCase();
    if (!search) return activeEmployees;
    return activeEmployees.filter((e) => {
      const text = `${e.fullName} ${e.employeeCode} ${e.department}`.toLowerCase();
      return text.includes(search);
    });
  }, [activeEmployees, groupSearch]);

  const lastDefaultRangeRef = useRef(yearRange);
  const autoAssignedRef = useRef(false);
  useEffect(() => {
    const last = lastDefaultRangeRef.current;
    setAssignmentForm((prev) => {
      const isDefaultRange = prev.effectiveFrom === last.start && prev.effectiveTo === last.end;
      if (!isDefaultRange) return prev;
      return { ...prev, effectiveFrom: yearRange.start, effectiveTo: yearRange.end };
    });
    lastDefaultRangeRef.current = yearRange;
  }, [yearRange.end, yearRange.start]);

  useEffect(() => {
    setAssignmentForm((prev) => {
      if (prev.shiftId && prev.shiftId !== defaultShiftId) return prev;
      return { ...prev, shiftId: defaultShiftId };
    });
  }, [defaultShiftId]);

  useEffect(() => {
    const current = readShiftConfigSnapshot();
    setShiftDefinitions(current.shiftDefinitions);
    setShiftAssignments(current.shiftAssignments);
    setShiftGroups(current.shiftGroups);
    setTimetables(current.timetables);
    setShiftPlans(current.shiftPlans);
    setShiftCalendar(current.calendar);
    void (async () => {
      try {
        const remote = await loadShiftConfigFromApi();
        setShiftDefinitions(remote.shiftDefinitions);
        setShiftAssignments(remote.shiftAssignments);
        setShiftGroups(remote.shiftGroups);
        setTimetables(remote.timetables);
        setShiftPlans(remote.shiftPlans);
        setShiftCalendar(remote.calendar);
      } catch {
        // Keep local cache if the backend config endpoint is unavailable.
      } finally {
        setConfigLoaded(true);
      }
    })();
  }, []);

  useEffect(() => {
    if (!configLoaded || autoAssignedRef.current) return;
    if (shiftAssignments.length > 0) {
      autoAssignedRef.current = true;
      return;
    }
    if (!defaultShiftId) return;
    autoAssignedRef.current = true;
    const defaultAssignment: ShiftAssignment = {
      id: `assign-${Date.now()}`,
      targetType: "department",
      employeeCode: "",
      employeeName: "",
      department: "all",
      groupId: "",
      groupName: "",
      shiftId: defaultShiftId,
      effectiveFrom: yearRange.start,
      effectiveTo: yearRange.end,
      isActive: true,
    };
    void saveShiftConfigToApi({ shiftAssignments: [defaultAssignment], shiftGroups })
      .then((saved) => {
        setShiftAssignments(saved.shiftAssignments);
        setShiftGroups(saved.shiftGroups);
        setMsg("Default shift assigned to all departments.");
      })
      .catch((error: unknown) => {
        setErr(error instanceof Error ? error.message : "Failed to create default assignment.");
      });
  }, [configLoaded, defaultShiftId, shiftAssignments.length, shiftGroups, yearRange.end, yearRange.start]);

  useEffect(() => {
    void (async () => {
      setLoading(true);
      try {
        const [employeeRows, departmentRows] = await Promise.all([
          requestItems<EmployeeApiRow>("/api/personnel/employees"),
          requestItems<DepartmentApiRow>("/api/personnel/departments"),
        ]);
        setEmployees(
          employeeRows
            .map((row) => ({
              id: String(row.id ?? row.employeeCode ?? ""),
              employeeCode: String(row.employeeCode ?? ""),
              fullName: String(row.fullName ?? ""),
              department: String(row.department ?? ""),
              isActive: row.isActive ?? true,
            }))
            .filter((row) => row.employeeCode || row.fullName || row.id),
        );
        const depts = departmentRows
          .map((row) => ({
            id: String(row.id ?? row.code ?? ""),
            code: String(row.code ?? ""),
            name: String(row.name ?? ""),
            isDefault: row.isDefault,
          }))
          .filter((row) => row.code || row.name || row.id);
        setDepartments(depts);
        if (depts.length > 0) {
          void ensureDefaultDepartmentShiftAssignments(depts, readShiftConfigSnapshot()).then((updated) => {
            setShiftAssignments(updated.shiftAssignments);
          });
        }
      } catch (error) {
        setErr(error instanceof Error ? error.message : "Failed to load assignments data.");
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  useEffect(() => {
    const tab = searchParams.get("tab");
    if (tab === "employee") {
      setActiveTab("employee");
      return;
    }
    if (tab === "group") {
      setActiveTab("group");
      return;
    }
    setActiveTab("department");
  }, [searchParams]);

  useEffect(() => {
    if (typeof window === "undefined") return;
    const q = new URLSearchParams(window.location.search);
    q.set("tab", activeTab);
    const next = q.toString() ? `${window.location.pathname}?${q.toString()}` : window.location.pathname;
    window.history.replaceState(null, "", next);
  }, [activeTab]);

  useEffect(() => {
    setAssignmentForm((prev) => {
      if (prev.targetType === activeTab) return prev;
      return {
        id: null,
        targetType: activeTab,
        department: "all",
        employeeCode: "",
        groupId: "",
        shiftId: defaultShiftId,
        effectiveFrom: yearRange.start,
        effectiveTo: yearRange.end,
        isActive: true,
      };
    });
    setFormOpen(false);
    setFormMode("add");
  }, [activeTab, defaultShiftId, yearRange.end, yearRange.start]);

  const saveShiftConfig = async (nextAssignments: ShiftAssignment[], nextGroups: ShiftGroup[]) => {
    const saved = await saveShiftConfigToApi({
      shiftAssignments: nextAssignments,
      shiftGroups: nextGroups,
    });
    setShiftAssignments(saved.shiftAssignments);
    setShiftGroups(saved.shiftGroups);
    setShiftDefinitions(saved.shiftDefinitions);
  };

  const resetAssignmentForm = () => {
    setAssignmentForm({
      id: null,
      targetType: activeTab,
      department: "all",
      employeeCode: "",
      groupId: "",
      shiftId: defaultShiftId,
      effectiveFrom: yearRange.start,
      effectiveTo: yearRange.end,
      isActive: true,
    });
  };

  const openAddForm = () => {
    resetAssignmentForm();
    setFormMode("add");
    setFormOpen(true);
  };

  const closeForm = () => {
    resetAssignmentForm();
    setFormMode("add");
    setFormOpen(false);
  };

  const handleSaveAssignment = async (stayOpen = false) => {
    if (assignmentSaving) return;
    setMsg(null);
    setErr(null);
    const effectiveFrom = assignmentForm.effectiveFrom.trim();
    const effectiveTo = assignmentForm.effectiveTo.trim();
    if (!assignmentForm.shiftId) {
      setErr("Select a shift.");
      return;
    }
    if (!effectiveFrom) {
      setErr("Effective From is required.");
      return;
    }
    if (effectiveTo && effectiveTo < effectiveFrom) {
      setErr("Effective To must be on or after Effective From.");
      return;
    }

    let targetLabel = "";
    if (assignmentForm.targetType === "department") {
      targetLabel = assignmentForm.department.trim() || "all";
    } else if (assignmentForm.targetType === "employee") {
      targetLabel = assignmentForm.employeeCode.trim();
    } else {
      targetLabel = assignmentForm.groupId.trim();
    }
    if (!targetLabel) {
      setErr("Select a target.");
      return;
    }

    const selectedEmployee = assignmentForm.targetType === "employee"
      ? employeeByCode.get(assignmentForm.employeeCode.trim())
      : null;
    const selectedGroup = assignmentForm.targetType === "group"
      ? groupById.get(assignmentForm.groupId.trim())
      : null;
    if (assignmentForm.targetType === "employee" && !selectedEmployee) {
      setErr("Select a valid employee.");
      return;
    }
    if (assignmentForm.targetType === "group" && !selectedGroup) {
      setErr("Select a valid group.");
      return;
    }

    const newRow: ShiftAssignment = {
      id: assignmentForm.id ?? `assign-${Date.now()}`,
      targetType: assignmentForm.targetType,
      employeeCode: selectedEmployee?.employeeCode ?? "",
      employeeName: selectedEmployee?.fullName ?? "",
      department: assignmentForm.targetType === "department" ? (assignmentForm.department.trim() || "all") : "",
      groupId: selectedGroup?.id ?? "",
      groupName: selectedGroup?.name ?? "",
      shiftId: assignmentForm.shiftId,
      effectiveFrom,
      effectiveTo: effectiveTo || null,
      isActive: assignmentForm.isActive,
    };

    const nextAssignments = assignmentForm.id
      ? shiftAssignments.map((row) => (row.id === assignmentForm.id ? newRow : row))
      : [...shiftAssignments, newRow];

    setAssignmentSaving(true);
    try {
      await saveShiftConfig(nextAssignments, shiftGroups);
      setMsg(assignmentForm.id ? "Assignment updated." : "Assignment created.");
      resetAssignmentForm();
      if (!assignmentForm.id && stayOpen) {
        setFormOpen(true);
        setFormMode("add");
        return;
      }
      setFormOpen(false);
      setFormMode("add");
    } catch (error) {
      setErr(error instanceof Error ? error.message : "Failed to save assignment.");
    } finally {
      setAssignmentSaving(false);
    }
  };

  const handleDeleteAssignment = async (id: string) => {
    setMsg(null);
    setErr(null);
    const nextAssignments = shiftAssignments.filter((row) => row.id !== id);
    try {
      await saveShiftConfig(nextAssignments, shiftGroups);
      setMsg("Assignment removed.");
      if (assignmentForm.id === id) {
        resetAssignmentForm();
        setFormOpen(false);
        setFormMode("add");
      }
    } catch (error) {
      setErr(error instanceof Error ? error.message : "Failed to delete assignment.");
    }
  };

  const handleEditAssignment = (row: ShiftAssignment) => {
    setActiveTab(row.targetType);
    setAssignmentForm({
      id: row.id,
      targetType: row.targetType,
      department: row.targetType === "department" ? (row.department || "all") : "all",
      employeeCode: row.targetType === "employee" ? row.employeeCode : "",
      groupId: row.targetType === "group" ? row.groupId : "",
      shiftId: row.shiftId,
      effectiveFrom: row.effectiveFrom,
      effectiveTo: row.effectiveTo ?? "",
      isActive: row.isActive,
    });
    setFormMode("edit");
    setFormOpen(true);
  };

  const handleSaveGroup = async () => {
    if (groupSaving) return;
    setMsg(null);
    setErr(null);
    const name = groupForm.name.trim();
    if (!name) {
      setErr("Group name is required.");
      return;
    }
    const members = Array.from(new Set(groupForm.memberEmployeeCodes.map((x) => x.trim()).filter(Boolean)));
    const nowIso = new Date().toISOString();
    const id = groupForm.id ?? (slug(name) || `group-${Date.now()}`);

    const nextGroup: ShiftGroup = {
      id,
      name,
      memberEmployeeCodes: members,
      isActive: groupForm.isActive,
      createdAt: groupForm.id ? (groupById.get(id)?.createdAt ?? nowIso) : nowIso,
      updatedAt: nowIso,
    };

    const nextGroups = groupForm.id
      ? shiftGroups.map((g) => (g.id === id ? nextGroup : g))
      : [...shiftGroups, nextGroup];

    setGroupSaving(true);
    try {
      await saveShiftConfig(shiftAssignments, nextGroups);
      setMsg(groupForm.id ? "Group updated." : "Group created.");
      setGroupForm({ id: null, name: "", memberEmployeeCodes: [], isActive: true });
    } catch (error) {
      setErr(error instanceof Error ? error.message : "Failed to save group.");
    } finally {
      setGroupSaving(false);
    }
  };

  const handleEditGroup = (group: ShiftGroup) => {
    setGroupForm({
      id: group.id,
      name: group.name,
      memberEmployeeCodes: group.memberEmployeeCodes ?? [],
      isActive: group.isActive,
    });
  };

  const handleDeleteGroup = async (group: ShiftGroup) => {
    const nextGroups = shiftGroups.filter((g) => g.id !== group.id);
    const nextAssignments = shiftAssignments.filter((a) => a.groupId !== group.id);
    try {
      await saveShiftConfig(nextAssignments, nextGroups);
      setMsg("Group removed.");
      if (groupForm.id === group.id) {
        setGroupForm({ id: null, name: "", memberEmployeeCodes: [], isActive: true });
      }
    } catch (error) {
      setErr(error instanceof Error ? error.message : "Failed to delete group.");
    }
  };

  const assignmentRows = useMemo(
    () =>
      shiftAssignments
        .filter((row) => row.targetType === activeTab)
        .slice()
        .sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom)),
    [activeTab, shiftAssignments],
  );

  const previewRows = useMemo(() => {
    const shiftPlanMap = new Map(shiftPlans.map((s) => [s.id, s]));
    const timetableMap = new Map(timetables.map((t) => [t.id, t]));
    const dayOfWeek = new Date(`${previewDate}T00:00:00`).getDay();
    const dayOfMonth = new Date(`${previewDate}T00:00:00`).getDate();

    const resolveTimetableLabel = (plan: typeof shiftPlans[0] | null) => {
      if (!plan) return "-";
      let timetableId: string | null = null;
      if (plan.cycleType === "daily") timetableId = plan.dailyTimetableId ?? null;
      else if (plan.cycleType === "weekly") timetableId = plan.weeklyRules.find((r) => r.dayOfWeek === dayOfWeek)?.timetableId ?? null;
      else timetableId = plan.monthlyRules.find((r) => r.dayOfMonth === dayOfMonth)?.timetableId ?? null;
      const tt = timetableId ? timetableMap.get(timetableId) : null;
      if (!tt) return "-";
      const dayRule = tt.days.find((d) => d.dayOfWeek === dayOfWeek);
      if (dayRule?.isWeeklyOff) return `${tt.name} (Weekly Off)`;
      if (dayRule) return `${tt.name} | In: ${dayRule.checkInTime} Out: ${dayRule.checkOutTime}`;
      return tt.name;
    };

    return employees
      .filter((e) => e.isActive)
      .map((e) => {
        const code = e.employeeCode.trim().toLowerCase();
        const dept = (e.department || "").trim().toLowerCase();
        const active = shiftAssignments
          .filter((a) =>
            a.isActive &&
            a.effectiveFrom <= previewDate &&
            (!a.effectiveTo || previewDate <= a.effectiveTo),
          )
          .filter((a) => {
            if (a.targetType === "employee") return a.employeeCode.trim().toLowerCase() === code;
            if (a.targetType === "group") {
              const grp = shiftGroups.find((g) => g.id === a.groupId && g.isActive);
              return grp?.memberEmployeeCodes.map((x) => x.trim().toLowerCase()).includes(code) ?? false;
            }
            const d = (a.department || "").trim().toLowerCase();
            return !d || d === "all" || d === dept;
          })
          .sort((a, b) => {
            const pri = (x: typeof a) => x.targetType === "employee" ? 3 : x.targetType === "group" ? 2 : 1;
            return pri(b) - pri(a) || b.effectiveFrom.localeCompare(a.effectiveFrom);
          });
        const assignment = active[0];
        const plan = assignment ? (shiftPlanMap.get(assignment.shiftId) ?? null) : null;
        const holiday = isHoliday(previewDate, e.department, shiftCalendar);
        const label = holiday ? `Holiday${holiday.name ? ` (${holiday.name})` : ""}` : plan ? plan.name : "Not Assigned";
        return {
          employeeCode: e.employeeCode,
          fullName: e.fullName,
          department: e.department || "Default Department",
          groupIds: shiftGroups.filter((g) => g.isActive && g.memberEmployeeCodes.map((x) => x.trim().toLowerCase()).includes(code)).map((g) => g.id),
          timetableName: resolveTimetableLabel(plan),
          label,
          unassigned: !holiday && !plan,
        };
      })
      .sort((a, b) => a.employeeCode.localeCompare(b.employeeCode, undefined, { numeric: true }));
  }, [employees, previewDate, shiftAssignments, shiftPlans, timetables, shiftCalendar, shiftGroups]);

  const previewByDept = useMemo(() => {
    const shiftPlanMap = new Map(shiftPlans.map((s) => [s.id, s]));
    const timetableMap = new Map(timetables.map((t) => [t.id, t]));
    const dayOfWeek = new Date(`${previewDate}T00:00:00`).getDay();
    const dayOfMonth = new Date(`${previewDate}T00:00:00`).getDate();

    const resolveTimetableLabel = (planId: string) => {
      const plan = shiftPlanMap.get(planId);
      if (!plan) return "-";
      let timetableId: string | null = null;
      if (plan.cycleType === "daily") timetableId = plan.dailyTimetableId ?? null;
      else if (plan.cycleType === "weekly") timetableId = plan.weeklyRules.find((r) => r.dayOfWeek === dayOfWeek)?.timetableId ?? null;
      else timetableId = plan.monthlyRules.find((r) => r.dayOfMonth === dayOfMonth)?.timetableId ?? null;
      const tt = timetableId ? timetableMap.get(timetableId) : null;
      if (!tt) return "-";
      const dayRule = tt.days.find((d) => d.dayOfWeek === dayOfWeek);
      if (dayRule?.isWeeklyOff) return `${tt.name} (Weekly Off)`;
      if (dayRule) return `${tt.name} | In: ${dayRule.checkInTime} Out: ${dayRule.checkOutTime}`;
      return tt.name;
    };

    const deptAssignments = shiftAssignments
      .filter((a) =>
        a.targetType === "department" &&
        a.isActive &&
        a.effectiveFrom <= previewDate &&
        (!a.effectiveTo || previewDate <= a.effectiveTo),
      )
      .map((a) => ({
        department: a.department?.trim() || "All Departments",
        shiftName: shiftPlanMap.get(a.shiftId)?.name ?? a.shiftId,
        timetableLabel: resolveTimetableLabel(a.shiftId),
        effectiveFrom: a.effectiveFrom,
        effectiveTo: a.effectiveTo,
      }));

    // group by department name
    const map = new Map<string, typeof deptAssignments>();
    for (const row of deptAssignments) {
      const key = row.department.toLowerCase() === "all" ? "All Departments" : row.department;
      const list = map.get(key) ?? [];
      list.push(row);
      map.set(key, list);
    }
    return Array.from(map.entries()).sort((a, b) => a[0].localeCompare(b[0]));
  }, [shiftAssignments, shiftPlans, timetables, previewDate]);

  const previewByGroup = useMemo(() => {
    return shiftGroups
      .filter((g) => g.isActive)
      .map((g) => ({
        group: g,
        rows: previewRows.filter((r) => r.groupIds.includes(g.id)),
      }))
      .filter((x) => x.rows.length > 0);
  }, [shiftGroups, previewRows]);

  return (
    <div className="space-y-4">
      <div className="tab-header">
        <h1 className="text-2xl font-semibold">{viewMode === "groups" ? "Group Management" : viewMode === "preview" ? "Assigned Shift Preview" : "Schedule Assignment"}</h1>
        <p className="text-sm text-[--muted-foreground]">
          {viewMode === "groups"
            ? "Create, edit, and delete employee groups used for scheduling."
            : viewMode === "preview"
              ? "View each employee's resolved shift for any date — department-wise or employee-wise."
              : "Assign a shift to an employee, group, or department. This is what reports use."}
        </p>
      </div>

      {err ? <div className="rounded-lg border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-sm text-[#8d2f2f]">{err}</div> : null}
      {msg ? <div className="rounded-lg border border-[#c9e7d1] bg-[#eefcf1] px-3 py-2 text-sm text-[#22603a]">{msg}</div> : null}

      {viewMode === "preview" ? (
        <section className="surface p-4 shadow-sm">
          <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
            <div className="text-sm font-semibold text-[#1f4f79]">Resolved schedule for {formatCalendarDate(previewDate, calendarType, previewDate)}</div>
            <div className="flex flex-wrap items-center gap-2">
              <BsDateField value={previewDate} onChange={setPreviewDate} className="min-w-[160px]" />
              <div className="inline-flex rounded-lg border border-[--border] bg-white p-0.5 text-xs">
                <button type="button" onClick={() => setPreviewView("department")} className={`rounded-md px-3 py-1.5 font-medium ${previewView === "department" ? "bg-[#1f5c8f] text-white" : "text-[#355a7a]"}`}>By Department</button>
                <button type="button" onClick={() => setPreviewView("employee")} className={`rounded-md px-3 py-1.5 font-medium ${previewView === "employee" ? "bg-[#1f5c8f] text-white" : "text-[#355a7a]"}`}>By Employee</button>
                <button type="button" onClick={() => setPreviewView("group")} className={`rounded-md px-3 py-1.5 font-medium ${previewView === "group" ? "bg-[#1f5c8f] text-white" : "text-[#355a7a]"}`}>By Group</button>
              </div>
            </div>
          </div>

          {previewRows.length === 0 ? (
            <div className="rounded-lg border border-dashed border-[--border] px-3 py-6 text-center text-xs text-[--muted-foreground]">No active employees.</div>
          ) : previewView === "employee" ? (
            <div className="overflow-x-auto rounded-lg border border-[--border]">
              <table className="min-w-full text-left text-sm">
                <thead className="bg-[#f7fbff] text-xs font-semibold uppercase text-[--muted-foreground]">
                  <tr>
                    <th className="px-3 py-2">Employee</th>
                    <th className="px-3 py-2">Name</th>
                    <th className="px-3 py-2">Department</th>
                    <th className="px-3 py-2">Assigned Shift</th>
                    <th className="px-3 py-2">Timetable</th>
                  </tr>
                </thead>
                <tbody>
                  {previewRows.map((r) => (
                    <tr key={r.employeeCode} className="border-t border-[--border]">
                      <td className="px-3 py-2 font-medium text-[#1a4a73]">{r.employeeCode}</td>
                      <td className="px-3 py-2">{r.fullName}</td>
                      <td className="px-3 py-2 text-xs text-[--muted-foreground]">{r.department}</td>
                      <td className="px-3 py-2 text-xs"><span className={r.unassigned ? "text-[#be2f2f]" : /Holiday|Weekly Off/.test(r.label) ? "text-[#cc7a00]" : "text-[#0f8a45]"}>{r.label}</span></td>
                      <td className="px-3 py-2 text-xs text-[--muted-foreground]">{r.timetableName}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : previewView === "department" ? (
            <div className="overflow-x-auto rounded-lg border border-[--border]">
              <table className="min-w-full text-left text-sm">
                <thead className="bg-[#f7fbff] text-xs font-semibold uppercase text-[--muted-foreground]">
                  <tr>
                    <th className="px-3 py-2">Department</th>
                    <th className="px-3 py-2">Assigned Shift</th>
                    <th className="px-3 py-2">Timetable</th>
                    <th className="px-3 py-2">Effective Range</th>
                  </tr>
                </thead>
                <tbody>
                  {previewByDept.map(([dept, rows]) =>
                    rows.map((row, i) => (
                      <tr key={`${dept}-${i}`} className="border-t border-[--border]">
                        {i === 0 ? <td className="px-3 py-2 font-semibold text-[#1a4a73]" rowSpan={rows.length}>{dept}</td> : null}
                        <td className="px-3 py-2 text-[#0f8a45]">{row.shiftName}</td>
                        <td className="px-3 py-2 text-xs text-[--muted-foreground]">{row.timetableLabel}</td>
                        <td className="px-3 py-2 text-xs text-[--muted-foreground]">{row.effectiveFrom} – {row.effectiveTo ?? "Open"}</td>
                      </tr>
                    ))
                  )}
                  {previewByDept.length === 0 ? (
                    <tr><td colSpan={4} className="px-3 py-6 text-center text-xs text-[--muted-foreground]">No department assignments for this date.</td></tr>
                  ) : null}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="space-y-4">
              {previewByGroup.map(({ group, rows }) => (
                <div key={group.id} className="overflow-hidden rounded-lg border border-[--border]">
                  <div className="grid grid-cols-3 gap-2 bg-[#f2f8ff] px-3 py-2 text-xs font-semibold text-[#1f4f79]">
                    <div>
                      <div className="text-[10px] uppercase tracking-wider text-[#5f7f9f]">Group</div>
                      <div className="text-sm">{group.name}</div>
                    </div>
                    <div>
                      <div className="text-[10px] uppercase tracking-wider text-[#5f7f9f]">Assigned Shift</div>
                      <div className="text-sm">{Array.from(new Set(rows.map((r) => r.label).filter((l) => !/Not Assigned/.test(l)))).join(", ") || <span className="text-[#be2f2f]">Not Assigned</span>}</div>
                    </div>
                    <div>
                      <div className="text-[10px] uppercase tracking-wider text-[#5f7f9f]">Timetable</div>
                      <div className="text-sm">{Array.from(new Set(rows.map((r) => r.timetableName).filter((t) => t !== "-"))).join(", ") || "-"}</div>
                    </div>
                  </div>
                  <table className="min-w-full text-left text-sm">
                    <thead className="bg-[#fafcff] text-xs font-semibold uppercase text-[--muted-foreground]">
                      <tr>
                        <th className="px-3 py-1.5">Employee</th>
                        <th className="px-3 py-1.5">Name</th>
                        <th className="px-3 py-1.5">Department</th>
                        <th className="px-3 py-1.5">Assigned Shift</th>
                        <th className="px-3 py-1.5">Timetable</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((r) => (
                        <tr key={r.employeeCode} className="border-t border-[--border]">
                          <td className="px-3 py-2 font-medium text-[#1a4a73]">{r.employeeCode}</td>
                          <td className="px-3 py-2">{r.fullName}</td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground]">{r.department}</td>
                          <td className="px-3 py-2 text-xs"><span className={r.unassigned ? "text-[#be2f2f]" : /Holiday|Weekly Off/.test(r.label) ? "text-[#cc7a00]" : "text-[#0f8a45]"}>{r.label}</span></td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground]">{r.timetableName}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ))}
              {previewByGroup.length === 0 ? <div className="rounded-lg border border-dashed border-[--border] px-3 py-6 text-center text-xs text-[--muted-foreground]">No active groups with members found.</div> : null}
            </div>
          )}
        </section>
      ) : (
      <section className="surface p-4 shadow-sm">
        {viewMode === "assignment" ? (
          <div className="tab-toolbar tab-toolbar-controls mb-3">
            <button
              type="button"
              onClick={() => setActiveTab("department")}
              className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm ${activeTab === "department" ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[--border] bg-white text-[#355a7a]"}`}
            >
              <Building2 className="h-4 w-4" />Department
            </button>
            <button
              type="button"
              onClick={() => setActiveTab("employee")}
              className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm ${activeTab === "employee" ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[--border] bg-white text-[#355a7a]"}`}
            >
              <Users className="h-4 w-4" />Employee
            </button>
            <button
              type="button"
              onClick={() => setActiveTab("group")}
              className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm ${activeTab === "group" ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[--border] bg-white text-[#355a7a]"}`}
            >
              <UsersRound className="h-4 w-4" />Group
            </button>
          </div>
        ) : null}

        {viewMode === "groups" ? (
          <div className="space-y-4">
            <div className="rounded-lg border border-[--border] p-3">
              <div className="mb-2 text-sm font-semibold">Group Setup</div>
              <div className="grid gap-3 md:grid-cols-2">
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-sm font-medium text-[#1f4f79]">Group Name</span>
                  <input
                    value={groupForm.name}
                    onChange={(e) => setGroupForm((p) => ({ ...p, name: e.target.value }))}
                    placeholder="e.g. Night Security"
                    className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]"
                  />
                </label>
                <label className="grid gap-1 rounded-lg border border-[--input] px-3 py-2 text-xs text-[--muted-foreground]">
                  <span className="flex items-center gap-2 text-sm font-medium text-[#1f4f79]">
                    <input
                      type="checkbox"
                      checked={groupForm.isActive}
                      onChange={(e) => setGroupForm((p) => ({ ...p, isActive: e.target.checked }))}
                    />
                    Active Group
                  </span>
                  <span>Inactive groups will not assign shifts.</span>
                </label>
              </div>
              <div className="mt-3 rounded-lg border border-[--input] bg-[#fbfdff] p-3">
                <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
                  <div className="text-sm font-semibold text-[#1f4f79]">Group Members</div>
                  <button
                    type="button"
                    onClick={() => setGroupMembersOpen((prev) => !prev)}
                    className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-1 text-xs font-semibold text-[#1f5c8f]"
                  >
                    {groupMembersOpen ? "Hide Members" : "Select Members"}
                  </button>
                </div>
                <div className="text-xs text-[#5f7f9f]">
                  Selected: {groupForm.memberEmployeeCodes.length}
                </div>
                {groupMembersOpen ? (
                  <>
                    <div className="mt-2 flex flex-wrap items-end gap-2">
                      <label className="grid min-w-[220px] flex-1 gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-sm font-medium text-[#1f4f79]">Search</span>
                        <input
                          value={groupSearch}
                          onChange={(e) => setGroupSearch(e.target.value)}
                          placeholder="Search employee..."
                          className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]"
                        />
                      </label>
                      <div className="flex flex-wrap gap-2">
                        <button
                          type="button"
                          onClick={() => setGroupForm((p) => ({
                            ...p,
                            memberEmployeeCodes: activeEmployees.map((e) => e.employeeCode),
                          }))}
                          className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-2 text-xs text-[#1f5c8f]"
                        >
                          Select All
                        </button>
                        <button
                          type="button"
                          onClick={() => setGroupForm((p) => ({ ...p, memberEmployeeCodes: [] }))}
                          className="rounded-lg border border-[#e2a6a6] bg-white px-3 py-2 text-xs text-[#a33a3a]"
                        >
                          Clear
                        </button>
                      </div>
                    </div>
                    <div className="mt-2 grid gap-2 rounded-lg border border-[--input] bg-white p-2 sm:grid-cols-2 xl:grid-cols-3">
                      {filteredGroupMembers.map((emp) => {
                        const checked = groupForm.memberEmployeeCodes.includes(emp.employeeCode);
                        return (
                          <label key={emp.employeeCode} className="flex items-start gap-2 rounded-md px-2 py-1 text-xs text-[#355a7a] hover:bg-[#f3f8ff]">
                            <input
                              type="checkbox"
                              checked={checked}
                              onChange={(e) => {
                                setGroupForm((p) => {
                                  const next = new Set(p.memberEmployeeCodes);
                                  if (e.target.checked) {
                                    next.add(emp.employeeCode);
                                  } else {
                                    next.delete(emp.employeeCode);
                                  }
                                  return { ...p, memberEmployeeCodes: Array.from(next) };
                                });
                              }}
                            />
                            <span className="break-words">{emp.fullName} ({emp.employeeCode})</span>
                            <span className="ml-auto break-words text-[10px] text-[#7b94ad]">{emp.department}</span>
                          </label>
                        );
                      })}
                      {filteredGroupMembers.length === 0 ? (
                        <div className="px-2 py-3 text-xs text-[#7b94ad]">No employees match this search.</div>
                      ) : null}
                    </div>
                  </>
                ) : null}
              </div>
              <div className="mt-3 flex flex-wrap gap-2">
                <button
                  type="button"
                  disabled={groupSaving}
                  onClick={handleSaveGroup}
                  className="rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground] disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {groupSaving ? "Saving..." : groupForm.id ? "Update Group" : "Save Group"}
                </button>
                {groupForm.id ? (
                  <button
                    type="button"
                    onClick={() => setGroupForm({ id: null, name: "", memberEmployeeCodes: [], isActive: true })}
                    className="rounded-lg border border-[--border] bg-white px-4 py-2 text-sm text-[#355a7a]"
                  >
                    New Group
                  </button>
                ) : null}
              </div>
            </div>

            <div className="rounded-lg border border-[--border] p-3">
              <div className="mb-2 text-sm font-semibold">Group List</div>
              <div className="overflow-x-auto rounded-lg border border-[--border]">
                <table className="min-w-full text-left text-sm">
                  <thead className="bg-[#f7fbff] text-xs uppercase text-[--muted-foreground]">
                    <tr>
                      <th className="px-3 py-2">Group</th>
                      <th className="px-3 py-2">Members</th>
                      <th className="px-3 py-2">Status</th>
                      <th className="px-3 py-2">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {shiftGroups.map((group) => (
                      <tr key={group.id} className="border-t border-[--border]">
                        <td className="px-3 py-2">
                          <div className="font-medium">{group.name}</div>
                          <div className="text-xs text-[--muted-foreground]">{group.id}</div>
                        </td>
                        <td className="px-3 py-2 text-xs text-[--muted-foreground]">
                          {group.memberEmployeeCodes.length} members
                        </td>
                        <td className="px-3 py-2">
                          <span className={`rounded-full px-2 py-0.5 text-xs ${group.isActive ? "bg-[#e7f5ec] text-[#22603a]" : "bg-[#f5e7e7] text-[#8d2f2f]"}`}>
                            {group.isActive ? "Active" : "Inactive"}
                          </span>
                        </td>
                        <td className="px-3 py-2">
                          <div className="flex flex-wrap gap-2">
                            <button type="button" onClick={() => handleEditGroup(group)} className="rounded border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f]">Edit</button>
                            <button type="button" onClick={() => handleDeleteGroup(group)} className="rounded border border-[#f2d0d0] bg-white px-2 py-1 text-xs text-[#b94141]">Delete</button>
                          </div>
                        </td>
                      </tr>
                    ))}
                    {shiftGroups.length === 0 ? (
                      <tr>
                        <td colSpan={4} className="px-3 py-4 text-center text-sm text-[--muted-foreground]">
                          No groups created yet.
                        </td>
                      </tr>
                    ) : null}
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        ) : null}

        {viewMode === "assignment" && formOpen ? (
          <div className="mt-4 rounded-lg border border-[--border] p-3">
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <div className="text-sm font-semibold">{formMode === "edit" ? "Edit Assignment" : "Schedule Shift"}</div>
              {formMode === "edit" ? (
                <span className="rounded-full border border-[#c8dbf0] bg-[#f7fbff] px-2 py-0.5 text-xs text-[#1f4f79]">Editing</span>
              ) : null}
            </div>
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-5">
            {activeTab === "department" ? (
              <label className="grid gap-1 text-xs text-[--muted-foreground]">
                <span className="text-sm font-medium text-[#1f4f79]">Department</span>
                <select
                  value={assignmentForm.department}
                  onChange={(e) => setAssignmentForm((p) => ({ ...p, department: e.target.value }))}
                  className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]"
                >
                  <option value="all">All Departments</option>
                  {deptOptions.map((dept) => (
                    <option key={dept.id} value={dept.name}>{dept.name}</option>
                  ))}
                </select>
              </label>
            ) : null}
            {activeTab === "employee" ? (
              <label className="grid gap-1 text-xs text-[--muted-foreground]">
                <span className="text-sm font-medium text-[#1f4f79]">Employee</span>
                <select
                  value={assignmentForm.employeeCode}
                  onChange={(e) => setAssignmentForm((p) => ({ ...p, employeeCode: e.target.value }))}
                  className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]"
                >
                  <option value="">Select Employee</option>
                  {activeEmployees.map((emp) => (
                    <option key={emp.employeeCode} value={emp.employeeCode}>
                      {emp.fullName} ({emp.employeeCode})
                    </option>
                  ))}
                </select>
              </label>
            ) : null}
            {activeTab === "group" ? (
              <label className="grid gap-1 text-xs text-[--muted-foreground]">
                <span className="text-sm font-medium text-[#1f4f79]">Group</span>
                <select
                  value={assignmentForm.groupId}
                  onChange={(e) => setAssignmentForm((p) => ({ ...p, groupId: e.target.value }))}
                  className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]"
                >
                  <option value="">Select Group</option>
                  {shiftGroups.map((group) => (
                    <option key={group.id} value={group.id}>{group.name}</option>
                  ))}
                </select>
              </label>
            ) : null}

            <label className="grid gap-1 text-xs text-[--muted-foreground]">
              <span className="text-sm font-medium text-[#1f4f79]">Shift</span>
              <select
                value={assignmentForm.shiftId}
                onChange={(e) => setAssignmentForm((p) => ({ ...p, shiftId: e.target.value }))}
                className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm text-[--foreground]"
              >
                <option value="">Select Shift</option>
                {shiftPlans.map((s) => (
                  <option key={s.id} value={s.id}>{s.name}</option>
                ))}
              </select>
            </label>
            <label className="grid gap-1 text-xs text-[--muted-foreground]">
              <span className="text-sm font-medium text-[#1f4f79]">Effective From</span>
              <BsDateField
                value={assignmentForm.effectiveFrom}
                onChange={(next) => setAssignmentForm((p) => ({ ...p, effectiveFrom: next }))}
              />
            </label>
            <label className="grid gap-1 text-xs text-[--muted-foreground]">
              <span className="text-sm font-medium text-[#1f4f79]">Effective To (Optional)</span>
              <BsDateField
                value={assignmentForm.effectiveTo}
                onChange={(next) => setAssignmentForm((p) => ({ ...p, effectiveTo: next }))}
              />
            </label>
            <label className="grid gap-1 rounded-lg border border-[--input] px-3 py-2 text-xs text-[--muted-foreground]">
              <span className="flex items-center gap-2 text-sm font-medium text-[#1f4f79]">
                <input
                  type="checkbox"
                  checked={assignmentForm.isActive}
                  onChange={(e) => setAssignmentForm((p) => ({ ...p, isActive: e.target.checked }))}
                />
                Active Assignment
              </span>
              <span>Inactive assignments remain for history.</span>
            </label>
          </div>
            <div className="mt-3 flex flex-wrap gap-2">
              <button
                type="button"
                disabled={assignmentSaving}
                onClick={() => handleSaveAssignment(false)}
                className="rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground] disabled:cursor-not-allowed disabled:opacity-60"
              >
                {assignmentSaving ? "Saving..." : assignmentForm.id ? "Save Changes" : "Save Assignment"}
              </button>
              {!assignmentForm.id ? (
                <button
                  type="button"
                  disabled={assignmentSaving}
                  onClick={() => handleSaveAssignment(true)}
                  className="rounded-lg border border-[#c8dbf0] bg-white px-4 py-2 text-sm text-[#1f5c8f] disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {assignmentSaving ? "Saving..." : "Save & Add"}
                </button>
              ) : null}
              <button
                type="button"
                onClick={closeForm}
                className="rounded-lg border border-[--border] bg-white px-4 py-2 text-sm text-[#355a7a]"
              >
                Cancel
              </button>
            </div>
          </div>
        ) : null}

        {viewMode === "assignment" ? (
        <div className="mt-4 rounded-lg border border-[--border] p-3">
          <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
            <div className="text-sm font-semibold">Schedule List</div>
            <button
              type="button"
              onClick={openAddForm}
              className="rounded-lg border border-[#c8dbf0] bg-white px-3 py-1.5 text-xs font-semibold text-[#1f5c8f]"
            >
              Add Assignment
            </button>
          </div>
          <div className="overflow-x-auto rounded-lg border border-[--border]">
            <table className="min-w-full text-left text-sm">
              <thead className="bg-[#f7fbff] text-xs uppercase text-[--muted-foreground]">
                <tr>
                  <th className="px-3 py-2">Target</th>
                  <th className="px-3 py-2">Shift</th>
                  <th className="px-3 py-2">Effective Range</th>
                  <th className="px-3 py-2">Status</th>
                  <th className="px-3 py-2">Actions</th>
                </tr>
              </thead>
              <tbody>
                {assignmentRows.map((row) => {
                  const shiftName = shiftById.get(row.shiftId)?.name ?? row.shiftId;
                  const targetTypeLabel = formatTargetTypeLabel(row.targetType);
                  const targetLabel =
                    row.targetType === "department"
                      ? (!row.department || row.department === "all" ? "All Departments" : row.department)
                      : row.targetType === "employee"
                        ? row.employeeName || row.employeeCode
                        : row.groupName || row.groupId;
                  const effectiveLabel = `${formatCalendarDate(row.effectiveFrom, calendarType, row.effectiveFrom)} to ${row.effectiveTo ? formatCalendarDate(row.effectiveTo, calendarType, row.effectiveTo) : "Open"}`;
                  return (
                    <tr key={row.id} className="border-t border-[--border]">
                      <td className="px-3 py-2">
                        <div className="font-medium">{targetLabel}</div>
                        <div className="text-xs text-[--muted-foreground]">{targetTypeLabel}</div>
                      </td>
                      <td className="px-3 py-2">{shiftName}</td>
                      <td className="px-3 py-2 text-xs text-[--muted-foreground]">{effectiveLabel}</td>
                      <td className="px-3 py-2">
                        <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs ${row.isActive ? "bg-[#e7f5ec] text-[#22603a]" : "bg-[#f5e7e7] text-[#8d2f2f]"}`}>
                          {row.isActive ? <Check className="h-3 w-3" /> : null}
                          {row.isActive ? "Active" : "Inactive"}
                        </span>
                      </td>
                      <td className="px-3 py-2">
                        <div className="flex flex-wrap gap-2">
                          <button type="button" onClick={() => handleEditAssignment(row)} className="rounded border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f]">Edit</button>
                          <button type="button" onClick={() => handleDeleteAssignment(row.id)} className="rounded border border-[#f2d0d0] bg-white px-2 py-1 text-xs text-[#b94141]">Delete</button>
                        </div>
                      </td>
                    </tr>
                  );
                })}
                {assignmentRows.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="px-3 py-4 text-center text-sm text-[--muted-foreground]">
                      {loading ? "Loading assignments..." : "No assignments yet."}
                    </td>
                  </tr>
                ) : null}
              </tbody>
            </table>
          </div>
        </div>
        ) : null}
      </section>
      )}
    </div>
  );
}
