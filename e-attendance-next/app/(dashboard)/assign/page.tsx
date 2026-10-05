"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import Link from "next/link";
import {
  Building2,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ClipboardCheck,
  Filter,
  Pencil,
  Plus,
  Search,
  Trash2,
  UserCheck,
  Users,
  UsersRound,
  X,
} from "lucide-react";
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
  const [groupDeptFilter, setGroupDeptFilter] = useState("");
  const [groupMemberPage, setGroupMemberPage] = useState(1);
  const GROUP_MEMBER_PAGE_SIZE = 12;
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

  const resolveShiftPlan = useCallback(
    (shiftIdOrName: string): ShiftPlanDefinition | null => {
      const norm = (shiftIdOrName || "").trim().toLowerCase();
      if (!norm) return null;
      const byId = shiftPlans.find((p) => p.id.toLowerCase() === norm);
      if (byId) return byId;
      const byName = shiftPlans.find((p) => p.name.trim().toLowerCase() === norm);
      if (byName) return byName;
      if (norm === "general" || norm === "default") {
        return shiftPlans.find((p) => p.id === "default-shift-plan") ?? shiftPlans[0] ?? null;
      }
      return null;
    },
    [shiftPlans],
  );

  const resolveShiftName = useCallback(
    (shiftIdOrName: string): string => {
      const plan = resolveShiftPlan(shiftIdOrName);
      if (plan) return plan.name;
      const def = shiftDefinitions.find(
        (d) => d.id.toLowerCase() === shiftIdOrName.toLowerCase() || d.name.toLowerCase() === shiftIdOrName.toLowerCase()
      );
      if (def) return def.name;
      return shiftIdOrName || "Not Assigned";
    },
    [resolveShiftPlan, shiftDefinitions],
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
    const dept = groupDeptFilter.trim().toLowerCase();
    return activeEmployees.filter((e) => {
      if (dept && (e.department || "").toLowerCase() !== dept) return false;
      if (!search) return true;
      const text = `${e.fullName} ${e.employeeCode} ${e.department}`.toLowerCase();
      return text.includes(search);
    });
  }, [activeEmployees, groupSearch, groupDeptFilter]);

  const totalGroupMemberPages = Math.max(1, Math.ceil(filteredGroupMembers.length / GROUP_MEMBER_PAGE_SIZE));
  const currentGroupMemberPage = Math.min(groupMemberPage, totalGroupMemberPages);

  const paginatedGroupMembers = useMemo(() => {
    const start = (currentGroupMemberPage - 1) * GROUP_MEMBER_PAGE_SIZE;
    return filteredGroupMembers.slice(start, start + GROUP_MEMBER_PAGE_SIZE);
  }, [filteredGroupMembers, currentGroupMemberPage]);

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
    const resolvedPlanId = resolveShiftPlan(row.shiftId)?.id ?? row.shiftId;
    setAssignmentForm({
      id: row.id,
      targetType: row.targetType,
      department: row.targetType === "department" ? (row.department || "all") : "all",
      employeeCode: row.targetType === "employee" ? row.employeeCode : "",
      groupId: row.targetType === "group" ? row.groupId : "",
      shiftId: resolvedPlanId,
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
    const timetableMap = new Map(timetables.map((t) => [t.id, t]));
    const dayOfWeek = new Date(`${previewDate}T00:00:00`).getDay();
    const dayOfMonth = new Date(`${previewDate}T00:00:00`).getDate();

    const resolveTimetableLabel = (plan: ShiftPlanDefinition | null) => {
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
        const plan = assignment ? resolveShiftPlan(assignment.shiftId) : null;
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
  }, [employees, previewDate, shiftAssignments, shiftPlans, timetables, shiftCalendar, shiftGroups, resolveShiftPlan]);

  const previewByDept = useMemo(() => {
    const timetableMap = new Map(timetables.map((t) => [t.id, t]));
    const dayOfWeek = new Date(`${previewDate}T00:00:00`).getDay();
    const dayOfMonth = new Date(`${previewDate}T00:00:00`).getDate();

    const resolveTimetableLabel = (planId: string) => {
      const plan = resolveShiftPlan(planId);
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
        shiftName: resolveShiftName(a.shiftId),
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
  }, [shiftAssignments, shiftPlans, timetables, previewDate, resolveShiftPlan, resolveShiftName]);

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
      {/* 1. Header Overview */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 sm:p-6 shadow-2xs">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between border-b border-slate-100 pb-5 mb-4">
          <div>
            <div className="flex items-center gap-2">
              <h1 className="text-xl font-bold tracking-tight text-slate-900">
                {viewMode === "groups" ? "Group Management" : viewMode === "preview" ? "Schedule Shift Preview" : "Schedule Assignment"}
              </h1>
              <span className="rounded-md bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700 border border-blue-100">
                Roster Dispatcher
              </span>
            </div>
            <p className="mt-1 text-xs text-slate-500">
              {viewMode === "groups"
                ? "Create, edit, and manage custom staff groupings for streamlined shift assignment."
                : viewMode === "preview"
                ? "Inspect the resolved work schedule and assigned timetable rules for any chosen calendar date."
                : "Assign specific daily, weekly, or rotating shift plans to employees, departments, or custom groups."}
            </p>
          </div>
        </div>

        {/* Section Navigation Tabs matching Sidebar Dropdown */}
        <div className="flex flex-wrap items-center gap-1.5 rounded-xl border border-slate-200 bg-slate-50 p-1">
          <Link
            href="/assign"
            prefetch={false}
            className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-xs font-semibold transition ${
              viewMode === "assignment"
                ? "bg-white text-blue-700 shadow-xs"
                : "text-slate-600 hover:text-slate-900"
            }`}
          >
            <UserCheck className="h-3.5 w-3.5" /> Schedule Assignment
          </Link>
          <Link
            href="/assign?section=groups"
            prefetch={false}
            className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-xs font-semibold transition ${
              viewMode === "groups"
                ? "bg-white text-blue-700 shadow-xs"
                : "text-slate-600 hover:text-slate-900"
            }`}
          >
            <Users className="h-3.5 w-3.5" /> Group Management
          </Link>
          <Link
            href="/assign?section=preview"
            prefetch={false}
            className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-xs font-semibold transition ${
              viewMode === "preview"
                ? "bg-white text-blue-700 shadow-xs"
                : "text-slate-600 hover:text-slate-900"
            }`}
          >
            <ClipboardCheck className="h-3.5 w-3.5" /> Schedule Preview
          </Link>
        </div>

        {err && <div className="mt-3 rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs font-medium text-rose-700">{err}</div>}
        {msg && <div className="mt-3 rounded-lg border border-emerald-200 bg-emerald-50 p-3 text-xs font-medium text-emerald-700">{msg}</div>}
      </section>

      {viewMode === "preview" ? (
        <section className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs">
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
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs">
        {viewMode === "assignment" ? (
          <div className="flex flex-wrap items-center gap-1.5 rounded-xl border border-slate-200 bg-slate-50 p-1 mb-4">
            <button
              type="button"
              onClick={() => setActiveTab("department")}
              className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-xs font-semibold transition ${
                activeTab === "department"
                  ? "bg-white text-blue-700 shadow-xs"
                  : "text-slate-600 hover:text-slate-900"
              }`}
            >
              <Building2 className="h-3.5 w-3.5" /> Department
            </button>
            <button
              type="button"
              onClick={() => setActiveTab("employee")}
              className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-xs font-semibold transition ${
                activeTab === "employee"
                  ? "bg-white text-blue-700 shadow-xs"
                  : "text-slate-600 hover:text-slate-900"
              }`}
            >
              <Users className="h-3.5 w-3.5" /> Employee
            </button>
            <button
              type="button"
              onClick={() => setActiveTab("group")}
              className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-xs font-semibold transition ${
                activeTab === "group"
                  ? "bg-white text-blue-700 shadow-xs"
                  : "text-slate-600 hover:text-slate-900"
              }`}
            >
              <UsersRound className="h-3.5 w-3.5" /> Group
            </button>
          </div>
        ) : null}

        {viewMode === "groups" ? (
          <div className="space-y-5">
            {/* 1. Group Setup Card */}
            <div className="rounded-2xl border border-slate-200/90 bg-white p-5 sm:p-6 shadow-2xs">
              <div className="mb-4 flex flex-col gap-1 border-b border-slate-100 pb-4 sm:flex-row sm:items-center sm:justify-between">
                <div>
                  <h3 className="text-base font-bold text-slate-900">
                    {groupForm.id ? "Edit Custom Group" : "Create Custom Group"}
                  </h3>
                  <p className="text-xs text-slate-500">
                    Group employees across different branches or departments for flexible shift scheduling.
                  </p>
                </div>
                {groupForm.id ? (
                  <span className="inline-flex items-center gap-1.5 rounded-full bg-blue-50 px-3 py-1 text-xs font-semibold text-blue-700 border border-blue-200">
                    Editing ID: {groupForm.id}
                  </span>
                ) : null}
              </div>

              <div className="grid gap-4 sm:grid-cols-12 items-start">
                <div className="sm:col-span-8">
                  <label className="block text-xs font-semibold text-slate-700 mb-1">
                    Group Name *
                  </label>
                  <input
                    value={groupForm.name}
                    onChange={(e) => setGroupForm((p) => ({ ...p, name: e.target.value }))}
                    placeholder="e.g. Night Security Team"
                    className="w-full rounded-xl border border-slate-300 bg-white px-3.5 py-2 text-xs text-slate-900 placeholder:text-slate-400 focus:border-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                  />
                </div>

                <div className="sm:col-span-4">
                  <label className="block text-xs font-semibold text-slate-700 mb-1">
                    Status
                  </label>
                  <label className="flex items-center gap-2.5 rounded-xl border border-slate-200 bg-slate-50/60 p-2 cursor-pointer hover:bg-slate-100/60 transition">
                    <input
                      type="checkbox"
                      checked={groupForm.isActive}
                      onChange={(e) => setGroupForm((p) => ({ ...p, isActive: e.target.checked }))}
                      className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                    />
                    <div className="min-w-0">
                      <span className="text-xs font-bold text-slate-800">
                        {groupForm.isActive ? "Active Group" : "Inactive Group"}
                      </span>
                      <p className="text-[10px] text-slate-400 leading-tight">
                        {groupForm.isActive ? "Eligible for shift assignments" : "Disabled from shifts"}
                      </p>
                    </div>
                  </label>
                </div>
              </div>

              {/* Group Members Sub-Panel */}
              <div className="mt-4 rounded-xl border border-slate-200 bg-slate-50/50 p-4">
                <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-200/70 pb-3">
                  <div className="flex items-center gap-2.5">
                    <Users className="h-4 w-4 text-blue-600" />
                    <span className="text-xs font-bold uppercase tracking-wider text-slate-800">
                      Group Members
                    </span>
                    <span className={`rounded-full px-2.5 py-0.5 text-[11px] font-bold border transition ${
                      groupForm.memberEmployeeCodes.length > 0
                        ? "bg-blue-100 text-blue-800 border-blue-200"
                        : "bg-slate-100 text-slate-600 border-slate-200"
                    }`}>
                      {groupForm.memberEmployeeCodes.length} selected
                    </span>
                  </div>

                  <button
                    type="button"
                    onClick={() => setGroupMembersOpen((prev) => !prev)}
                    className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 shadow-2xs transition"
                  >
                    <ChevronDown className={`h-3.5 w-3.5 transition-transform duration-200 ${groupMembersOpen ? "rotate-180" : ""}`} />
                    {groupMembersOpen ? "Hide Member Picker" : "Select Members"}
                  </button>
                </div>

                {groupMembersOpen ? (
                  <div className="mt-3 space-y-3">
                    {/* Search & Department Filters Toolbar */}
                    <div className="grid gap-2 sm:grid-cols-12 items-center">
                      <div className="sm:col-span-5 relative">
                        <Search className="absolute left-3 top-2.5 h-3.5 w-3.5 text-slate-400 pointer-events-none" />
                        <input
                          value={groupSearch}
                          onChange={(e) => {
                            setGroupSearch(e.target.value);
                            setGroupMemberPage(1);
                          }}
                          placeholder="Search by name or code..."
                          className="w-full rounded-lg border border-slate-300 bg-white pl-8 pr-3 py-1.5 text-xs text-slate-900 placeholder:text-slate-400 focus:border-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                        />
                      </div>

                      <div className="sm:col-span-3">
                        <select
                          value={groupDeptFilter}
                          onChange={(e) => {
                            setGroupDeptFilter(e.target.value);
                            setGroupMemberPage(1);
                          }}
                          className="w-full rounded-lg border border-slate-300 bg-white px-2.5 py-1.5 text-xs text-slate-800 focus:border-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                        >
                          <option value="">All Departments ({activeEmployees.length})</option>
                          {deptOptions.map((d) => (
                            <option key={d.id} value={d.name.toLowerCase()}>
                              {d.name}
                            </option>
                          ))}
                        </select>
                      </div>

                      <div className="sm:col-span-4 flex justify-end gap-1.5">
                        <button
                          type="button"
                          onClick={() => {
                            const filteredCodes = filteredGroupMembers.map((e) => e.employeeCode);
                            setGroupForm((p) => ({
                              ...p,
                              memberEmployeeCodes: Array.from(new Set([...p.memberEmployeeCodes, ...filteredCodes])),
                            }));
                          }}
                          className="inline-flex items-center gap-1 rounded-lg border border-blue-200 bg-blue-50 px-2.5 py-1.5 text-xs font-semibold text-blue-700 hover:bg-blue-100 transition shadow-2xs"
                        >
                          <Check className="h-3 w-3" /> Select Filtered ({filteredGroupMembers.length})
                        </button>
                        <button
                          type="button"
                          onClick={() => setGroupForm((p) => ({ ...p, memberEmployeeCodes: [] }))}
                          className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-100 transition shadow-2xs"
                        >
                          <X className="h-3 w-3" /> Clear
                        </button>
                      </div>
                    </div>

                    {/* Member Cards Grid */}
                    <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3 max-h-80 overflow-y-auto pr-1">
                      {paginatedGroupMembers.map((emp) => {
                        const checked = groupForm.memberEmployeeCodes.includes(emp.employeeCode);
                        return (
                          <label
                            key={emp.employeeCode}
                            className={`flex items-center gap-2.5 rounded-xl border p-2.5 cursor-pointer transition select-none ${
                              checked
                                ? "border-blue-500 bg-blue-50/50 shadow-2xs ring-1 ring-blue-500/20"
                                : "border-slate-200 bg-white hover:border-slate-300 hover:bg-slate-50/50"
                            }`}
                          >
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
                              className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                            />
                            <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-slate-100 text-[11px] font-bold text-slate-600 border border-slate-200">
                              {emp.fullName.slice(0, 1).toUpperCase()}
                            </div>
                            <div className="min-w-0 flex-1">
                              <div className="text-xs font-semibold text-slate-900 truncate" title={`${emp.fullName} (#${emp.employeeCode})`}>
                                {emp.fullName}
                                <span className="ml-1 text-[10px] font-mono text-slate-500">#{emp.employeeCode}</span>
                              </div>
                              <div className="text-[10px] text-slate-400 truncate" title={emp.department}>
                                {emp.department || "No Department"}
                              </div>
                            </div>
                          </label>
                        );
                      })}
                      {filteredGroupMembers.length === 0 ? (
                        <div className="col-span-full py-8 text-center text-xs text-slate-500">
                          No employees match this search or department filter.
                        </div>
                      ) : null}
                    </div>

                    {/* Member Pagination Controls */}
                    {filteredGroupMembers.length > GROUP_MEMBER_PAGE_SIZE ? (
                      <div className="flex items-center justify-between border-t border-slate-200/70 pt-2 text-xs text-slate-500">
                        <span>
                          Showing {(currentGroupMemberPage - 1) * GROUP_MEMBER_PAGE_SIZE + 1}–{Math.min(currentGroupMemberPage * GROUP_MEMBER_PAGE_SIZE, filteredGroupMembers.length)} of {filteredGroupMembers.length} employees
                        </span>
                        <div className="flex items-center gap-1.5">
                          <button
                            type="button"
                            disabled={currentGroupMemberPage <= 1}
                            onClick={() => setGroupMemberPage((p) => Math.max(1, p - 1))}
                            className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                          >
                            <ChevronLeft className="h-3.5 w-3.5" /> Prev
                          </button>
                          <span className="px-2 font-semibold text-slate-800">
                            {currentGroupMemberPage} / {totalGroupMemberPages}
                          </span>
                          <button
                            type="button"
                            disabled={currentGroupMemberPage >= totalGroupMemberPages}
                            onClick={() => setGroupMemberPage((p) => Math.min(totalGroupMemberPages, p + 1))}
                            className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                          >
                            Next <ChevronRight className="h-3.5 w-3.5" />
                          </button>
                        </div>
                      </div>
                    ) : null}
                  </div>
                ) : null}
              </div>

              {/* Form Action Buttons */}
              <div className="mt-5 flex items-center justify-end gap-2.5">
                {groupForm.id ? (
                  <button
                    type="button"
                    onClick={() => setGroupForm({ id: null, name: "", memberEmployeeCodes: [], isActive: true })}
                    className="rounded-xl border border-slate-300 bg-white px-4 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 shadow-2xs transition"
                  >
                    Cancel Edit
                  </button>
                ) : null}
                <button
                  type="button"
                  disabled={groupSaving || !groupForm.name.trim()}
                  onClick={handleSaveGroup}
                  className="inline-flex items-center gap-1.5 rounded-xl bg-blue-600 px-5 py-2 text-xs font-bold text-white hover:bg-blue-700 shadow-xs transition disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  <Check className="h-4 w-4" />
                  {groupSaving ? "Saving Group..." : groupForm.id ? "Update Group" : "Save Group"}
                </button>
              </div>
            </div>

            {/* 2. Group List Table Card */}
            <div className="rounded-2xl border border-slate-200/90 bg-white p-5 sm:p-6 shadow-2xs">
              <div className="mb-4 flex items-center justify-between border-b border-slate-100 pb-3">
                <div className="flex items-center gap-2">
                  <h3 className="text-base font-bold text-slate-900">Custom Group List</h3>
                  <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-600 border border-slate-200">
                    {shiftGroups.length}
                  </span>
                </div>
              </div>

              <div className="overflow-x-auto rounded-xl border border-slate-200">
                <table className="w-full text-left text-xs">
                  <thead className="bg-slate-50 text-[11px] font-bold uppercase tracking-wider text-slate-500 border-b border-slate-200">
                    <tr>
                      <th className="px-4 py-3">Group Name</th>
                      <th className="px-4 py-3">Members</th>
                      <th className="px-4 py-3">Status</th>
                      <th className="px-4 py-3 text-right">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {shiftGroups.map((group) => (
                      <tr key={group.id} className="hover:bg-slate-50/70 transition">
                        <td className="px-4 py-3 font-semibold text-slate-900">
                          {group.name}
                        </td>
                        <td className="px-4 py-3">
                          <span className="inline-flex items-center gap-1 rounded-md bg-slate-100 px-2.5 py-0.5 font-bold text-slate-700 border border-slate-200">
                            <Users className="h-3 w-3 text-slate-500" />
                            {group.memberEmployeeCodes.length} members
                          </span>
                        </td>
                        <td className="px-4 py-3">
                          <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-[10px] font-bold uppercase tracking-wider border ${
                            group.isActive
                              ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                              : "bg-slate-100 text-slate-600 border-slate-200"
                          }`}>
                            {group.isActive ? "Active" : "Inactive"}
                          </span>
                        </td>
                        <td className="px-4 py-3 text-right">
                          <div className="inline-flex items-center gap-1.5">
                            <button
                              type="button"
                              onClick={() => handleEditGroup(group)}
                              className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-slate-700 hover:bg-slate-50 shadow-2xs transition"
                            >
                              <Pencil className="h-3 w-3 text-slate-500" /> Edit
                            </button>
                            <button
                              type="button"
                              onClick={() => handleDeleteGroup(group)}
                              className="inline-flex items-center gap-1 rounded-md border border-rose-200 bg-rose-50 px-2.5 py-1 text-xs font-semibold text-rose-700 hover:bg-rose-100 shadow-2xs transition"
                            >
                              <Trash2 className="h-3 w-3" /> Delete
                            </button>
                          </div>
                        </td>
                      </tr>
                    ))}
                    {shiftGroups.length === 0 ? (
                      <tr>
                        <td colSpan={4} className="py-10 text-center text-xs text-slate-400">
                          No groups created yet. Create a group above to assign shifts in bulk.
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
                  const shiftName = resolveShiftName(row.shiftId);
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
