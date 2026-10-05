"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import type { Route } from "next";
import {
  Building2,
  Check,
  Layers3,
  MapPin,
  Pencil,
  Plus,
  Trash2,
  X,
  ChevronRight,
  ShieldCheck,
  FolderTree,
} from "lucide-react";
import { resolveApiBases } from "@/lib/api-bases";
import {
  ensureAccessControlDefaults,
  loadAccessControlFromApi,
  readAccessControlSnapshot,
} from "@/components/data/access-control-store";

type SectionKey = "areas" | "departments" | "positions";
type AreaRow = { id: string; code: string; name: string; remark?: string; isDefault?: boolean; canDelete?: boolean };
type DepartmentRow = {
  id: string;
  code: string;
  name: string;
  areaCode: string;
  areaName?: string;
  superior?: string;
  manager?: string;
  employeeQty?: number;
  resignedQty?: number;
  isDefault?: boolean;
  canDelete?: boolean;
};
type PositionRow = { id: string; code: string; name: string; department: string; employeeQty?: number };
type AreaForm = { code: string; name: string; remark: string };
type DepartmentForm = { code: string; name: string; areaCode: string; superior: string; manager: string };
type PositionForm = { code: string; name: string; department: string };
type SetupPayload = { areas: AreaRow[]; departments: DepartmentRow[]; positions: PositionRow[] };

function getApiBases(): string[] {
  return resolveApiBases();
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError: string | null = null;
  for (const base of getApiBases()) {
    try {
      let response = await fetch(`${base}${path}`, {
        ...init,
        headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
        cache: "no-store",
      });
      if (response.status === 503) {
        try {
          await fetch(`${base}/api/server/start`, { method: "POST", cache: "no-store" });
          response = await fetch(`${base}${path}`, {
            ...init,
            headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
            cache: "no-store",
          });
        } catch {
          // Keep current response
        }
      }
      if (response.ok) {
        if (response.status === 204) return null as T;
        return (await response.json()) as T;
      }
      const text = await response.text();
      lastError = text || `Request failed (${response.status})`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : "Failed to fetch";
    }
  }
  throw new Error(lastError ?? "Failed to fetch");
}

export default function SetupPage() {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const sectionParam = searchParams.get("section");
  const section: SectionKey = sectionParam === "departments" || sectionParam === "positions" ? sectionParam : "areas";

  const [loading, setLoading] = useState(false);
  const [showLoading, setShowLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [areas, setAreas] = useState<AreaRow[]>([]);
  const [departments, setDepartments] = useState<DepartmentRow[]>([]);
  const [positions, setPositions] = useState<PositionRow[]>([]);
  const [creatingArea, setCreatingArea] = useState(false);
  const [creatingDepartment, setCreatingDepartment] = useState(false);
  const [creatingPosition, setCreatingPosition] = useState(false);

  const [openAdd, setOpenAdd] = useState<Record<SectionKey, boolean>>({ areas: false, departments: false, positions: false });
  const [areaForm, setAreaForm] = useState<AreaForm>({ code: "", name: "", remark: "" });
  const [deptForm, setDeptForm] = useState<DepartmentForm>({ code: "", name: "", areaCode: "1", superior: "", manager: "" });
  const [positionForm, setPositionForm] = useState<PositionForm>({ code: "", name: "", department: "" });

  const [editingAreaId, setEditingAreaId] = useState<string | null>(null);
  const [editingAreaForm, setEditingAreaForm] = useState<AreaForm>({ code: "", name: "", remark: "" });
  const [editingDeptId, setEditingDeptId] = useState<string | null>(null);
  const [editingDeptForm, setEditingDeptForm] = useState<DepartmentForm>({ code: "", name: "", areaCode: "1", superior: "", manager: "" });
  const [editingPositionId, setEditingPositionId] = useState<string | null>(null);
  const [editingPositionForm, setEditingPositionForm] = useState<PositionForm>({ code: "", name: "", department: "" });

  const areaOptions = useMemo(() => areas.map((a) => ({ value: a.code, label: `${a.code} - ${a.name}` })), [areas]);
  const departmentNameOptions = useMemo(
    () => Array.from(new Set(departments.map((d) => d.name.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b)),
    [departments]
  );

  const setSection = (next: SectionKey) => {
    const q = new URLSearchParams(searchParams.toString());
    q.set("section", next);
    router.replace(`${pathname}?${q.toString()}` as Route);
  };

  const loadAll = useCallback(async (): Promise<SetupPayload | null> => {
    setLoading(true);
    const loadingTimer = setTimeout(() => setShowLoading(true), 180);
    setError(null);
    try {
      const data = await requestJson<SetupPayload>("/api/personnel/setup");
      const normalizedAreas = Array.isArray(data?.areas) ? data.areas : [];
      const normalizedDepartments = Array.isArray(data?.departments) ? data.departments : [];
      const normalizedPositions = Array.isArray(data?.positions) ? data.positions : [];
      setAreas(normalizedAreas);
      setDepartments(normalizedDepartments);
      setPositions(normalizedPositions);
      return { areas: normalizedAreas, departments: normalizedDepartments, positions: normalizedPositions };
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load master configuration.");
      return null;
    } finally {
      clearTimeout(loadingTimer);
      setShowLoading(false);
      setLoading(false);
    }
  }, []);

  const seedAccessControl = async () => {
    try {
      const snap = await loadAccessControlFromApi();
      if (snap) {
        await ensureAccessControlDefaults(
          areas.map((a) => ({ name: a.name, code: a.code })),
          [],
          snap
        );
      }
    } catch {
      // Best-effort access control seeding
    }
  };

  useEffect(() => {
    void loadAll();
    void seedAccessControl();
  }, [loadAll]);

  async function createArea() {
    if (creatingArea) return;
    const code = areaForm.code.trim();
    const name = areaForm.name.trim();
    if (!code || !name) return setError("Area code and name are required.");
    setCreatingArea(true);
    try {
      await requestJson("/api/personnel/areas", {
        method: "POST",
        body: JSON.stringify({ code, name, remark: areaForm.remark.trim() }),
      });
      setAreaForm({ code: "", name: "", remark: "" });
      setOpenAdd((prev) => ({ ...prev, areas: false }));
      await loadAll();
      void seedAccessControl();
    } catch (err) {
      const refreshed = await loadAll();
      const created = refreshed?.areas?.some((area) => area.code.trim().toLowerCase() === code.toLowerCase());
      if (created) {
        setAreaForm({ code: "", name: "", remark: "" });
        setOpenAdd((prev) => ({ ...prev, areas: false }));
        void seedAccessControl();
        return;
      }
      setError(err instanceof Error ? err.message : "Failed to create area.");
    } finally {
      setCreatingArea(false);
    }
  }

  async function saveArea(item: AreaRow) {
    const name = editingAreaForm.name.trim();
    if (!name) return setError("Area name is required.");
    const payload = item.isDefault || item.canDelete === false
      ? { code: item.code, name, remark: item.remark ?? "" }
      : { code: item.code, name, remark: editingAreaForm.remark.trim() };
    try {
      await requestJson(`/api/personnel/areas/${item.id}`, { method: "PUT", body: JSON.stringify(payload) });
      setEditingAreaId(null);
      await loadAll();
      void seedAccessControl();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update area.");
    }
  }

  async function deleteArea(item: AreaRow) {
    if (item.isDefault || item.canDelete === false) {
      return setError("Default Area (Code: 1) is system-protected and cannot be deleted.");
    }
    if (!confirm(`Are you sure you want to delete area "${item.name}"?`)) return;
    try {
      await requestJson(`/api/personnel/areas/${item.id}`, { method: "DELETE" });
      await loadAll();
      void seedAccessControl();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete area.");
    }
  }

  async function createDepartment() {
    if (creatingDepartment) return;
    const code = deptForm.code.trim();
    const name = deptForm.name.trim();
    if (!code || !name) return setError("Department code and name are required.");
    setCreatingDepartment(true);
    try {
      await requestJson("/api/personnel/departments", {
        method: "POST",
        body: JSON.stringify({
          code,
          name,
          areaCode: deptForm.areaCode || "1",
          superior: deptForm.superior.trim(),
          manager: deptForm.manager.trim(),
        }),
      });
      setDeptForm({ code: "", name: "", areaCode: "1", superior: "", manager: "" });
      setOpenAdd((prev) => ({ ...prev, departments: false }));
      await loadAll();
      void seedAccessControl();
    } catch (err) {
      const refreshed = await loadAll();
      const created = refreshed?.departments?.some((department) => department.code.trim().toLowerCase() === code.toLowerCase());
      if (created) {
        setDeptForm({ code: "", name: "", areaCode: "1", superior: "", manager: "" });
        setOpenAdd((prev) => ({ ...prev, departments: false }));
        void seedAccessControl();
        return;
      }
      setError(err instanceof Error ? err.message : "Failed to create department.");
    } finally {
      setCreatingDepartment(false);
    }
  }

  async function saveDepartment(item: DepartmentRow) {
    const name = editingDeptForm.name.trim();
    if (!name) return setError("Department name is required.");
    const protectedDepartment = item.isDefault || item.canDelete === false;
    const payload = protectedDepartment
      ? { code: item.code, name, areaCode: item.areaCode || "1", superior: item.superior ?? "", manager: item.manager ?? "" }
      : {
          code: item.code,
          name,
          areaCode: editingDeptForm.areaCode || "1",
          superior: editingDeptForm.superior.trim(),
          manager: editingDeptForm.manager.trim(),
        };
    if (!payload.code) return setError("Department code is required.");
    try {
      await requestJson(`/api/personnel/departments/${item.id}`, { method: "PUT", body: JSON.stringify(payload) });
      setEditingDeptId(null);
      await loadAll();
      void seedAccessControl();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update department.");
    }
  }

  async function deleteDepartment(item: DepartmentRow) {
    if (item.isDefault || item.canDelete === false) {
      return setError("Default Department (Code: 1) is system-protected and cannot be deleted.");
    }
    if (!confirm(`Are you sure you want to delete department "${item.name}"?`)) return;
    try {
      await requestJson(`/api/personnel/departments/${item.id}`, { method: "DELETE" });
      await loadAll();
      void seedAccessControl();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete department.");
    }
  }

  async function createPosition() {
    if (creatingPosition) return;
    const code = positionForm.code.trim();
    const name = positionForm.name.trim();
    if (!code || !name) return setError("Position code and name are required.");
    setCreatingPosition(true);
    try {
      await requestJson("/api/personnel/positions", {
        method: "POST",
        body: JSON.stringify({ code, name, department: positionForm.department.trim() }),
      });
      setPositionForm({ code: "", name: "", department: "" });
      setOpenAdd((prev) => ({ ...prev, positions: false }));
      await loadAll();
    } catch (err) {
      const refreshed = await loadAll();
      const created = refreshed?.positions?.some((position) => position.code.trim().toLowerCase() === code.toLowerCase());
      if (created) {
        setPositionForm({ code: "", name: "", department: "" });
        setOpenAdd((prev) => ({ ...prev, positions: false }));
        return;
      }
      setError(err instanceof Error ? err.message : "Failed to create position.");
    } finally {
      setCreatingPosition(false);
    }
  }

  async function savePosition(item: PositionRow) {
    const code = editingPositionForm.code.trim();
    const name = editingPositionForm.name.trim();
    if (!code || !name) return setError("Position code and name are required.");
    try {
      await requestJson(`/api/personnel/positions/${item.id}`, {
        method: "PUT",
        body: JSON.stringify({ code, name, department: editingPositionForm.department.trim() }),
      });
      setEditingPositionId(null);
      await loadAll();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update position.");
    }
  }

  async function deletePosition(item: PositionRow) {
    if (!confirm(`Are you sure you want to delete position "${item.name}"?`)) return;
    try {
      await requestJson(`/api/personnel/positions/${item.id}`, { method: "DELETE" });
      await loadAll();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete position.");
    }
  }

  return (
    <div className="space-y-5">
      {/* 1. Header Overview & Master KPI Segment Control */}
      <section className="rounded-2xl border border-slate-200/90 bg-white p-5 sm:p-6 shadow-2xs">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between border-b border-slate-100 pb-5">
          <div>
            <div className="flex items-center gap-2">
              <h1 className="text-xl font-bold tracking-tight text-slate-900">
                Setup & Master Control
              </h1>
              <span className="rounded-md bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700 border border-blue-100">
                Personnel Architecture
              </span>
            </div>
            <p className="mt-1 text-xs text-slate-500">
              Manage physical branch areas, organizational departments, and staff position titles.
            </p>
          </div>

          <button
            type="button"
            onClick={() => setOpenAdd((prev) => ({ ...prev, [section]: !prev[section] }))}
            className="inline-flex items-center gap-1.5 rounded-xl bg-blue-600 px-4 py-2 text-xs font-semibold text-white hover:bg-blue-700 shadow-xs transition"
          >
            {openAdd[section] ? <X className="h-4 w-4" /> : <Plus className="h-4 w-4" />}
            {openAdd[section] ? "Close Form" : `Add New ${section.slice(0, -1)}`}
          </button>
        </div>

        {/* 3 Interactive Master Cards */}
        <div className="mt-5 grid gap-3 sm:grid-cols-3">
          {[
            {
              key: "areas" as SectionKey,
              label: "Areas",
              count: areas.length,
              icon: MapPin,
              color: "text-blue-600",
              bg: "bg-blue-50",
              desc: "Physical branch zones",
            },
            {
              key: "departments" as SectionKey,
              label: "Departments",
              count: departments.length,
              icon: Building2,
              color: "text-emerald-600",
              bg: "bg-emerald-50",
              desc: "Operational divisions",
            },
            {
              key: "positions" as SectionKey,
              label: "Positions",
              count: positions.length,
              icon: Layers3,
              color: "text-indigo-600",
              bg: "bg-indigo-50",
              desc: "Designation roles",
            },
          ].map((item) => {
            const isActive = section === item.key;
            return (
              <button
                key={item.key}
                type="button"
                onClick={() => setSection(item.key)}
                className={`relative flex items-center justify-between rounded-xl border p-4 text-left transition ${
                  isActive
                    ? "border-blue-600 bg-blue-50/50 shadow-xs ring-1 ring-blue-600"
                    : "border-slate-200 bg-white hover:border-slate-300 hover:bg-slate-50/50"
                }`}
              >
                <div className="flex items-center gap-3.5">
                  <div className={`flex h-11 w-11 shrink-0 items-center justify-center rounded-xl ${item.bg} ${item.color}`}>
                    <item.icon className="h-5 w-5" />
                  </div>
                  <div>
                    <span className="text-xs font-bold uppercase tracking-wider text-slate-500">{item.label}</span>
                    <p className="text-xl font-extrabold text-slate-900 leading-tight">{item.count}</p>
                    <p className="text-[11px] text-slate-400">{item.desc}</p>
                  </div>
                </div>

                <ChevronRight className={`h-4 w-4 transition ${isActive ? "text-blue-600 translate-x-0.5" : "text-slate-300"}`} />
              </button>
            );
          })}
        </div>
      </section>

      {error && (
        <div className="flex items-center gap-2 rounded-xl border border-rose-200 bg-rose-50 p-4 text-xs font-medium text-rose-700">
          <span>{error}</span>
        </div>
      )}

      {/* 2. Main Table Card & Inline Forms */}
      <div className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-2xs">
        {/* --- ADD NEW AREA FORM --- */}
        {section === "areas" && openAdd.areas && (
          <div className="mb-5 rounded-xl border border-blue-100 bg-blue-50/40 p-4">
            <h3 className="text-xs font-bold uppercase tracking-wider text-blue-900 mb-3">Add New Geographic Area</h3>
            <div className="grid gap-3 sm:grid-cols-3">
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Area Code / ID *</label>
                <input
                  value={areaForm.code}
                  onChange={(e) => setAreaForm((p) => ({ ...p, code: e.target.value }))}
                  placeholder="e.g. 2"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Area Name *</label>
                <input
                  value={areaForm.name}
                  onChange={(e) => setAreaForm((p) => ({ ...p, name: e.target.value }))}
                  placeholder="e.g. Branch Office"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Remark</label>
                <input
                  value={areaForm.remark}
                  onChange={(e) => setAreaForm((p) => ({ ...p, remark: e.target.value }))}
                  placeholder="Optional notes"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
            </div>
            <div className="mt-3 flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setOpenAdd((p) => ({ ...p, areas: false }))}
                className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={creatingArea}
                onClick={() => void createArea()}
                className="rounded-lg bg-blue-600 px-3.5 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-50"
              >
                {creatingArea ? "Saving..." : "Save Area"}
              </button>
            </div>
          </div>
        )}

        {/* --- ADD NEW DEPARTMENT FORM --- */}
        {section === "departments" && openAdd.departments && (
          <div className="mb-5 rounded-xl border border-blue-100 bg-blue-50/40 p-4">
            <h3 className="text-xs font-bold uppercase tracking-wider text-blue-900 mb-3">Add New Department</h3>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Dept Code *</label>
                <input
                  value={deptForm.code}
                  onChange={(e) => setDeptForm((p) => ({ ...p, code: e.target.value }))}
                  placeholder="e.g. 2"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Department Name *</label>
                <input
                  value={deptForm.name}
                  onChange={(e) => setDeptForm((p) => ({ ...p, name: e.target.value }))}
                  placeholder="e.g. Finance"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Assigned Area</label>
                <select
                  value={deptForm.areaCode}
                  onChange={(e) => setDeptForm((p) => ({ ...p, areaCode: e.target.value }))}
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                >
                  {areaOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Manager / Supervisor</label>
                <input
                  value={deptForm.manager}
                  onChange={(e) => setDeptForm((p) => ({ ...p, manager: e.target.value }))}
                  placeholder="Optional"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
            </div>
            <div className="mt-3 flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setOpenAdd((p) => ({ ...p, departments: false }))}
                className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={creatingDepartment}
                onClick={() => void createDepartment()}
                className="rounded-lg bg-blue-600 px-3.5 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-50"
              >
                {creatingDepartment ? "Saving..." : "Save Department"}
              </button>
            </div>
          </div>
        )}

        {/* --- ADD NEW POSITION FORM --- */}
        {section === "positions" && openAdd.positions && (
          <div className="mb-5 rounded-xl border border-blue-100 bg-blue-50/40 p-4">
            <h3 className="text-xs font-bold uppercase tracking-wider text-blue-900 mb-3">Add New Position / Designation</h3>
            <div className="grid gap-3 sm:grid-cols-3">
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Position Code *</label>
                <input
                  value={positionForm.code}
                  onChange={(e) => setPositionForm((p) => ({ ...p, code: e.target.value }))}
                  placeholder="e.g. ACC-01"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Position Title *</label>
                <input
                  value={positionForm.name}
                  onChange={(e) => setPositionForm((p) => ({ ...p, name: e.target.value }))}
                  placeholder="e.g. Senior Accountant"
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-slate-700 mb-1">Department</label>
                <select
                  value={positionForm.department}
                  onChange={(e) => setPositionForm((p) => ({ ...p, department: e.target.value }))}
                  className="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs text-slate-900 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
                >
                  <option value="">Select Department (Optional)</option>
                  {departmentNameOptions.map((name) => (
                    <option key={name} value={name}>
                      {name}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            <div className="mt-3 flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setOpenAdd((p) => ({ ...p, positions: false }))}
                className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={creatingPosition}
                onClick={() => void createPosition()}
                className="rounded-lg bg-blue-600 px-3.5 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-50"
              >
                {creatingPosition ? "Saving..." : "Save Position"}
              </button>
            </div>
          </div>
        )}

        {/* --- SECTION TABLE: AREAS --- */}
        {section === "areas" && (
          <div className="overflow-x-auto rounded-xl border border-slate-200">
            <table className="w-full text-left text-xs">
              <thead className="bg-slate-50 text-[11px] font-bold uppercase tracking-wider text-slate-500 border-b border-slate-200">
                <tr>
                  <th className="px-4 py-3">Area Code</th>
                  <th className="px-4 py-3">Area Name</th>
                  <th className="px-4 py-3">Remark</th>
                  <th className="px-4 py-3 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {areas.map((item) => {
                  const edit = editingAreaId === item.id;
                  const isProtected = item.isDefault || item.canDelete === false;
                  return (
                    <tr key={item.id} className="hover:bg-slate-50/70 transition">
                      <td className="px-4 py-3 font-semibold text-slate-900">{item.code}</td>
                      <td className="px-4 py-3">
                        {edit ? (
                          <input
                            value={editingAreaForm.name}
                            onChange={(e) => setEditingAreaForm((p) => ({ ...p, name: e.target.value }))}
                            className="rounded border border-slate-300 px-2 py-1 text-xs"
                          />
                        ) : (
                          <span className="font-medium text-slate-800">
                            {item.name}
                            {item.isDefault && (
                              <span className="ml-2 rounded-full bg-blue-50 px-2 py-0.5 text-[10px] font-bold text-blue-700 border border-blue-200">
                                System Default
                              </span>
                            )}
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-slate-500">
                        {edit ? (
                          <input
                            value={editingAreaForm.remark}
                            onChange={(e) => setEditingAreaForm((p) => ({ ...p, remark: e.target.value }))}
                            className="rounded border border-slate-300 px-2 py-1 text-xs"
                          />
                        ) : (
                          item.remark || "—"
                        )}
                      </td>
                      <td className="px-4 py-3 text-right">
                        {edit ? (
                          <div className="inline-flex gap-1.5">
                            <button
                              type="button"
                              onClick={() => void saveArea(item)}
                              className="inline-flex items-center gap-1 rounded-md bg-emerald-600 px-2 py-1 text-[11px] font-semibold text-white hover:bg-emerald-700"
                            >
                              <Check className="h-3 w-3" /> Save
                            </button>
                            <button
                              type="button"
                              onClick={() => setEditingAreaId(null)}
                              className="inline-flex items-center gap-1 rounded-md border border-slate-300 bg-white px-2 py-1 text-[11px] font-semibold text-slate-600 hover:bg-slate-50"
                            >
                              <X className="h-3 w-3" /> Cancel
                            </button>
                          </div>
                        ) : (
                          <div className="inline-flex gap-1.5">
                            <button
                              type="button"
                              onClick={() => {
                                setEditingAreaId(item.id);
                                setEditingAreaForm({ code: item.code, name: item.name, remark: item.remark || "" });
                              }}
                              className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2 py-1 text-[11px] font-semibold text-slate-700 hover:bg-slate-50"
                            >
                              <Pencil className="h-3 w-3 text-slate-500" /> Edit
                            </button>
                            {!isProtected && (
                              <button
                                type="button"
                                onClick={() => void deleteArea(item)}
                                className="inline-flex items-center gap-1 rounded-md border border-rose-200 bg-rose-50 px-2 py-1 text-[11px] font-semibold text-rose-700 hover:bg-rose-100"
                              >
                                <Trash2 className="h-3 w-3" /> Delete
                              </button>
                            )}
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {/* --- SECTION TABLE: DEPARTMENTS --- */}
        {section === "departments" && (
          <div className="overflow-x-auto rounded-xl border border-slate-200">
            <table className="w-full text-left text-xs">
              <thead className="bg-slate-50 text-[11px] font-bold uppercase tracking-wider text-slate-500 border-b border-slate-200">
                <tr>
                  <th className="px-4 py-3">Code</th>
                  <th className="px-4 py-3">Department Name</th>
                  <th className="px-4 py-3">Area</th>
                  <th className="px-4 py-3">Manager</th>
                  <th className="px-4 py-3">Staff Qty</th>
                  <th className="px-4 py-3 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {departments.map((item) => {
                  const edit = editingDeptId === item.id;
                  const isProtected = item.isDefault || item.canDelete === false;
                  return (
                    <tr key={item.id} className="hover:bg-slate-50/70 transition">
                      <td className="px-4 py-3 font-semibold text-slate-900">{item.code}</td>
                      <td className="px-4 py-3">
                        {edit ? (
                          <input
                            value={editingDeptForm.name}
                            onChange={(e) => setEditingDeptForm((p) => ({ ...p, name: e.target.value }))}
                            className="rounded border border-slate-300 px-2 py-1 text-xs"
                          />
                        ) : (
                          <span className="font-medium text-slate-800">
                            {item.name}
                            {item.isDefault && (
                              <span className="ml-2 rounded-full bg-blue-50 px-2 py-0.5 text-[10px] font-bold text-blue-700 border border-blue-200">
                                System Default
                              </span>
                            )}
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-slate-600">
                        {edit && !isProtected ? (
                          <select
                            value={editingDeptForm.areaCode}
                            onChange={(e) => setEditingDeptForm((p) => ({ ...p, areaCode: e.target.value }))}
                            className="rounded border border-slate-300 px-2 py-1 text-xs"
                          >
                            {areaOptions.map((opt) => (
                              <option key={opt.value} value={opt.value}>
                                {opt.label}
                              </option>
                            ))}
                          </select>
                        ) : (
                          item.areaName || item.areaCode || "—"
                        )}
                      </td>
                      <td className="px-4 py-3 text-slate-500">
                        {edit ? (
                          <input
                            value={editingDeptForm.manager}
                            onChange={(e) => setEditingDeptForm((p) => ({ ...p, manager: e.target.value }))}
                            className="rounded border border-slate-300 px-2 py-1 text-xs"
                          />
                        ) : (
                          item.manager || "—"
                        )}
                      </td>
                      <td className="px-4 py-3">
                        <span className="rounded-md bg-slate-100 px-2 py-0.5 font-bold text-slate-700">
                          {item.employeeQty ?? 0}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-right">
                        {edit ? (
                          <div className="inline-flex gap-1.5">
                            <button
                              type="button"
                              onClick={() => void saveDepartment(item)}
                              className="inline-flex items-center gap-1 rounded-md bg-emerald-600 px-2 py-1 text-[11px] font-semibold text-white hover:bg-emerald-700"
                            >
                              <Check className="h-3 w-3" /> Save
                            </button>
                            <button
                              type="button"
                              onClick={() => setEditingDeptId(null)}
                              className="inline-flex items-center gap-1 rounded-md border border-slate-300 bg-white px-2 py-1 text-[11px] font-semibold text-slate-600 hover:bg-slate-50"
                            >
                              <X className="h-3 w-3" /> Cancel
                            </button>
                          </div>
                        ) : (
                          <div className="inline-flex gap-1.5">
                            <button
                              type="button"
                              onClick={() => {
                                setEditingDeptId(item.id);
                                setEditingDeptForm({
                                  code: item.code,
                                  name: item.name,
                                  areaCode: item.areaCode || "1",
                                  superior: item.superior || "",
                                  manager: item.manager || "",
                                });
                              }}
                              className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2 py-1 text-[11px] font-semibold text-slate-700 hover:bg-slate-50"
                            >
                              <Pencil className="h-3 w-3 text-slate-500" /> Edit
                            </button>
                            {!isProtected && (
                              <button
                                type="button"
                                onClick={() => void deleteDepartment(item)}
                                className="inline-flex items-center gap-1 rounded-md border border-rose-200 bg-rose-50 px-2 py-1 text-[11px] font-semibold text-rose-700 hover:bg-rose-100"
                              >
                                <Trash2 className="h-3 w-3" /> Delete
                              </button>
                            )}
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {/* --- SECTION TABLE: POSITIONS --- */}
        {section === "positions" && (
          <div className="overflow-x-auto rounded-xl border border-slate-200">
            <table className="w-full text-left text-xs">
              <thead className="bg-slate-50 text-[11px] font-bold uppercase tracking-wider text-slate-500 border-b border-slate-200">
                <tr>
                  <th className="px-4 py-3">Position Code</th>
                  <th className="px-4 py-3">Position Title</th>
                  <th className="px-4 py-3">Department</th>
                  <th className="px-4 py-3">Active Staff</th>
                  <th className="px-4 py-3 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {positions.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="p-6 text-center text-xs text-slate-400">
                      No positions configured yet. Click "Add New Position" above to create one.
                    </td>
                  </tr>
                ) : (
                  positions.map((item) => {
                    const edit = editingPositionId === item.id;
                    return (
                      <tr key={item.id} className="hover:bg-slate-50/70 transition">
                        <td className="px-4 py-3 font-semibold text-slate-900">{item.code}</td>
                        <td className="px-4 py-3 font-medium text-slate-800">
                          {edit ? (
                            <input
                              value={editingPositionForm.name}
                              onChange={(e) => setEditingPositionForm((p) => ({ ...p, name: e.target.value }))}
                              className="rounded border border-slate-300 px-2 py-1 text-xs"
                            />
                          ) : (
                            item.name
                          )}
                        </td>
                        <td className="px-4 py-3 text-slate-600">
                          {edit ? (
                            <select
                              value={editingPositionForm.department}
                              onChange={(e) => setEditingPositionForm((p) => ({ ...p, department: e.target.value }))}
                              className="rounded border border-slate-300 px-2 py-1 text-xs"
                            >
                              <option value="">None</option>
                              {departmentNameOptions.map((name) => (
                                <option key={name} value={name}>
                                  {name}
                                </option>
                              ))}
                            </select>
                          ) : (
                            item.department || "—"
                          )}
                        </td>
                        <td className="px-4 py-3">
                          <span className="rounded-md bg-slate-100 px-2 py-0.5 font-bold text-slate-700">
                            {item.employeeQty ?? 0}
                          </span>
                        </td>
                        <td className="px-4 py-3 text-right">
                          {edit ? (
                            <div className="inline-flex gap-1.5">
                              <button
                                type="button"
                                onClick={() => void savePosition(item)}
                                className="inline-flex items-center gap-1 rounded-md bg-emerald-600 px-2 py-1 text-[11px] font-semibold text-white hover:bg-emerald-700"
                              >
                                <Check className="h-3 w-3" /> Save
                              </button>
                              <button
                                type="button"
                                onClick={() => setEditingPositionId(null)}
                                className="inline-flex items-center gap-1 rounded-md border border-slate-300 bg-white px-2 py-1 text-[11px] font-semibold text-slate-600 hover:bg-slate-50"
                              >
                                <X className="h-3 w-3" /> Cancel
                              </button>
                            </div>
                          ) : (
                            <div className="inline-flex gap-1.5">
                              <button
                                type="button"
                                onClick={() => {
                                  setEditingPositionId(item.id);
                                  setEditingPositionForm({
                                    code: item.code,
                                    name: item.name,
                                    department: item.department || "",
                                  });
                                }}
                                className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2 py-1 text-[11px] font-semibold text-slate-700 hover:bg-slate-50"
                              >
                                <Pencil className="h-3 w-3 text-slate-500" /> Edit
                              </button>
                              <button
                                type="button"
                                onClick={() => void deletePosition(item)}
                                className="inline-flex items-center gap-1 rounded-md border border-rose-200 bg-rose-50 px-2 py-1 text-[11px] font-semibold text-rose-700 hover:bg-rose-100"
                              >
                                <Trash2 className="h-3 w-3" /> Delete
                              </button>
                            </div>
                          )}
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
