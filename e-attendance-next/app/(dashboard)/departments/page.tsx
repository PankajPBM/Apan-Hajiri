"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import type { Route } from "next";
import { Building2, Check, Layers3, MapPin, Pencil, Plus, Trash2, X } from "lucide-react";
import { resolveApiBases } from "@/lib/api-bases";
import { 
  ensureAccessControlDefaults, 
  loadAccessControlFromApi, 
  readAccessControlSnapshot 
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

const inputClass = "w-full rounded-md border border-[#cadced] bg-white px-2 py-1 text-sm";
const btnBase = "inline-flex items-center gap-1 rounded-md border px-2 py-1 text-xs";

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
    [departments],
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
      const payload = await requestJson<SetupPayload>("/api/personnel/setup");
      setAreas(payload?.areas ?? []);
      setDepartments(payload?.departments ?? []);
      setPositions(payload?.positions ?? []);
      return payload;
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load setup data.");
      return null;
    } finally {
      clearTimeout(loadingTimer);
      setLoading(false);
      setShowLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadAll();
  }, [loadAll]);

  const clearEditors = () => {
    setEditingAreaId(null);
    setEditingDeptId(null);
    setEditingPositionId(null);
  };

  const seedAccessControl = useCallback(async () => {
    try {
      const payload = await requestJson<SetupPayload>("/api/personnel/setup");
      if (!payload) return;
      
      let ac = readAccessControlSnapshot();
      try { ac = await loadAccessControlFromApi(); } catch { /* ignore */ }
      
      const devicesPayload = await requestJson<any[]>("/api/devices").catch(() => []);
      const devices = Array.isArray(devicesPayload) ? devicesPayload : [];
      
      await ensureAccessControlDefaults(
        payload.areas.map(a => ({ name: a.name, code: a.code })),
        devices.map(d => ({ serialNumber: String(d.serialNumber || d.sn || ""), area: String(d.location || d.area || "") })),
        ac
      );
    } catch (e) {
      console.error("Failed to seed access control defaults during setup change", e);
    }
  }, []);

  const toggleAdd = (key: SectionKey) => {
    setOpenAdd((prev) => ({ ...prev, [key]: !prev[key] }));
    setError(null);
  };

  async function createArea() {
    if (creatingArea) return;
    const code = areaForm.code.trim();
    const name = areaForm.name.trim();
    if (!code || !name) return setError("Area ID and Area Name are required.");
    setCreatingArea(true);
    try {
      await requestJson("/api/personnel/areas", { method: "POST", body: JSON.stringify({ code, name, remark: areaForm.remark.trim() }) });
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
    // Area ID is fixed once created — only the name (and remark) can be edited.
    const code = item.code;
    const name = editingAreaForm.name.trim();
    if (!code || !name) return setError("Area ID and Area Name are required.");
    try {
      await requestJson(`/api/personnel/areas/${item.id}`, {
        method: "PUT",
        body: JSON.stringify({ code, name, remark: editingAreaForm.remark.trim() }),
      });
      setEditingAreaId(null);
      await loadAll();
      void seedAccessControl();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update area.");
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
          // Department code/ID is fixed once created — only the name (and other
          // details) can be edited.
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

  const sectionBtn = (active: boolean) =>
    `rounded-lg border px-3 py-2 text-sm font-medium ${active ? "border-[#1f5c8f] bg-[#1f5c8f] text-white" : "border-[#cfdfef] bg-white text-[#355a7a]"}`;
  const hasSetupData = areas.length > 0 || departments.length > 0 || positions.length > 0;

  return (
    <div className="space-y-5">
      <div className="tab-header rounded-2xl border border-[#d9e8f7] bg-[linear-gradient(120deg,#eef5ff,#f8fbff)] p-5">
        <h1 className="text-2xl font-semibold text-[#0f3b61]">Setup</h1>
        <p className="mt-1 text-sm text-[#587894]">Manage Area, Department, and Position masters.</p>
        <div className="metric-grid mt-4 md:grid-cols-3">
          <article className="metric-card"><p className="metric-card__label">Areas</p><p className="metric-card__value">{areas.length}</p></article>
          <article className="metric-card"><p className="metric-card__label">Departments</p><p className="metric-card__value">{departments.length}</p></article>
          <article className="metric-card"><p className="metric-card__label">Positions</p><p className="metric-card__value">{positions.length}</p></article>
        </div>
      </div>

      {error ? <div className="rounded-lg border border-[#f1cdcd] bg-[#fff2f2] px-3 py-2 text-sm text-[#9a2f2f]">{error}</div> : null}

      <div className="tab-toolbar tab-toolbar-controls">
        <button type="button" onClick={() => setSection("areas")} className={sectionBtn(section === "areas")}><MapPin className="mr-2 inline h-4 w-4" />Area</button>
        <button type="button" onClick={() => setSection("departments")} className={sectionBtn(section === "departments")}><Building2 className="mr-2 inline h-4 w-4" />Department</button>
        <button type="button" onClick={() => setSection("positions")} className={sectionBtn(section === "positions")}><Layers3 className="mr-2 inline h-4 w-4" />Position</button>
      </div>

      {section === "areas" ? (
        <section className="space-y-4 rounded-2xl border border-[#d9e8f7] bg-white p-4">
          <div className="overflow-x-auto rounded-xl border border-[#d9e8f7]">
            <table className="min-w-full text-left text-sm">
              <thead className="bg-[#f6faff] text-xs uppercase text-[#5a7892]"><tr><th className="px-3 py-2">Area ID</th><th className="px-3 py-2">Area Name</th><th className="px-3 py-2">Remark</th><th className="px-3 py-2 text-right">Action</th></tr></thead>
              <tbody>
                {areas.map((item) => {
                  const edit = editingAreaId === item.id;
                  const protectedArea = item.isDefault || item.canDelete === false;
                  return (
                    <tr key={item.id} className="border-t border-[#e5effa]">
                      <td className="px-3 py-2">{edit ? <input value={editingAreaForm.code} disabled title="Area ID cannot be changed after creation" onChange={(e) => setEditingAreaForm((p) => ({ ...p, code: e.target.value }))} className={`${inputClass} disabled:bg-[#f2f6fb] disabled:cursor-not-allowed`} /> : <span className="font-medium">{item.code}</span>}</td>
                      <td className="px-3 py-2">{edit ? <input value={editingAreaForm.name} onChange={(e) => setEditingAreaForm((p) => ({ ...p, name: e.target.value }))} className={inputClass} /> : <>{item.name}{item.isDefault ? <span className="ml-1 rounded-full border border-[#bfd5ee] bg-[#edf5ff] px-2 py-0.5 text-[10px] text-[#21537f]">Default</span> : null}</>}</td>
                      <td className="px-3 py-2">{edit ? <input value={editingAreaForm.remark} onChange={(e) => setEditingAreaForm((p) => ({ ...p, remark: e.target.value }))} className={inputClass} /> : item.remark || "-"}</td>
                      <td className="px-3 py-2 text-right">
                        {edit ? (
                          <div className="inline-flex gap-1">
                            <button type="button" onClick={() => void saveArea(item)} className={`${btnBase} border-[#c6e3cd] bg-[#eefcf1] text-[#22603a]`}><Check className="h-3.5 w-3.5" />Save</button>
                            <button type="button" onClick={() => setEditingAreaId(null)} className={`${btnBase} border-[#d6e3ef] bg-white text-[#355a7a]`}><X className="h-3.5 w-3.5" />Cancel</button>
                          </div>
                        ) : (
                          <div className="inline-flex gap-1">
                            <button type="button" onClick={() => { clearEditors(); setEditingAreaId(item.id); setEditingAreaForm({ code: item.code, name: item.name, remark: item.remark ?? "" }); }} className={`${btnBase} border-[#c5d8ed] bg-white text-[#1e4c78]`}><Pencil className="h-3.5 w-3.5" />Edit</button>
                            <button type="button" disabled={protectedArea} onClick={() => void (async () => { if (!(typeof window === "undefined" ? true : window.confirm(`Delete area ${item.name}? This will delete all departments, devices, employees, and positions under it.`))) return; await requestJson(`/api/personnel/areas/${item.id}`, { method: "DELETE" }); await loadAll(); })()} className={`${btnBase} border-[#e5c8c8] bg-white text-[#a33a3a] disabled:cursor-not-allowed disabled:opacity-50`}><Trash2 className="h-3.5 w-3.5" />Delete</button>
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className="flex justify-end"><button type="button" onClick={() => toggleAdd("areas")} className="inline-flex items-center gap-2 rounded-lg border border-[#c5d8ed] bg-white px-3 py-2 text-sm font-medium text-[#1e4c78]"><Plus className="h-4 w-4" />{openAdd.areas ? "Close Add Area" : "Add Area"}</button></div>
          {openAdd.areas ? (
            <div className="rounded-xl border border-[#d9e8f7] bg-[#f8fbff] p-4">
              <div className="mb-3 text-sm font-semibold text-[#0f3b61]">New Area</div>
              <div className="grid gap-2 md:grid-cols-4">
                <input value={areaForm.code} onChange={(e) => setAreaForm((p) => ({ ...p, code: e.target.value }))} placeholder="Area ID" className={inputClass} />
                <input value={areaForm.name} onChange={(e) => setAreaForm((p) => ({ ...p, name: e.target.value }))} placeholder="Area Name" className={inputClass} />
                <input value={areaForm.remark} onChange={(e) => setAreaForm((p) => ({ ...p, remark: e.target.value }))} placeholder="Remark" className={inputClass} />
                <button type="button" disabled={creatingArea} onClick={() => void createArea()} className="inline-flex items-center justify-center gap-2 rounded-lg bg-[#1f5c8f] px-3 py-2 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-60"><Plus className="h-4 w-4" />{creatingArea ? "Saving..." : "Save Area"}</button>
              </div>
            </div>
          ) : null}
        </section>
      ) : null}

      {section === "departments" ? (
        <section className="space-y-4 rounded-2xl border border-[#d9e8f7] bg-white p-4">
          <div className="overflow-x-auto rounded-xl border border-[#d9e8f7]">
            <table className="min-w-full text-left text-sm">
              <thead className="bg-[#f6faff] text-xs uppercase text-[#5a7892]"><tr><th className="px-3 py-2">Code</th><th className="px-3 py-2">Name</th><th className="px-3 py-2">Area</th><th className="px-3 py-2">Superior</th><th className="px-3 py-2">Manager</th><th className="px-3 py-2">Active</th><th className="px-3 py-2">Resigned</th><th className="px-3 py-2 text-right">Action</th></tr></thead>
              <tbody>
                {departments.map((item) => {
                  const edit = editingDeptId === item.id;
                  const protectedDepartment = item.isDefault || item.canDelete === false;
                  return (
                    <tr key={item.id} className="border-t border-[#e5effa]">
                      <td className="px-3 py-2">{edit ? <input value={editingDeptForm.code} disabled title="Department ID cannot be changed after creation" onChange={(e) => setEditingDeptForm((p) => ({ ...p, code: e.target.value }))} className={`${inputClass} disabled:bg-[#f2f6fb] disabled:cursor-not-allowed`} /> : <span className="font-medium">{item.code}</span>}</td>
                      <td className="px-3 py-2">{edit ? <input value={editingDeptForm.name} onChange={(e) => setEditingDeptForm((p) => ({ ...p, name: e.target.value }))} className={inputClass} /> : <>{item.name}{item.isDefault ? <span className="ml-1 rounded-full border border-[#bfd5ee] bg-[#edf5ff] px-2 py-0.5 text-[10px] text-[#21537f]">Default</span> : null}</>}</td>
                      <td className="px-3 py-2">{edit ? <select value={editingDeptForm.areaCode} disabled={protectedDepartment} onChange={(e) => setEditingDeptForm((p) => ({ ...p, areaCode: e.target.value }))} className={`${inputClass} disabled:bg-[#f2f6fb]`}>{areaOptions.map((a) => <option key={a.value} value={a.value}>{a.label}</option>)}</select> : (item.areaName || item.areaCode || "-")}</td>
                      <td className="px-3 py-2">{edit ? <input value={editingDeptForm.superior} disabled={protectedDepartment} onChange={(e) => setEditingDeptForm((p) => ({ ...p, superior: e.target.value }))} className={`${inputClass} disabled:bg-[#f2f6fb]`} /> : (item.superior || "-")}</td>
                      <td className="px-3 py-2">{edit ? <input value={editingDeptForm.manager} disabled={protectedDepartment} onChange={(e) => setEditingDeptForm((p) => ({ ...p, manager: e.target.value }))} className={`${inputClass} disabled:bg-[#f2f6fb]`} /> : (item.manager || "-")}</td>
                      <td className="px-3 py-2">{item.employeeQty ?? 0}</td>
                      <td className="px-3 py-2">{item.resignedQty ?? 0}</td>
                      <td className="px-3 py-2 text-right">
                        {edit ? (
                          <div className="inline-flex gap-1">
                            <button type="button" onClick={() => void saveDepartment(item)} className={`${btnBase} border-[#c6e3cd] bg-[#eefcf1] text-[#22603a]`}><Check className="h-3.5 w-3.5" />Save</button>
                            <button type="button" onClick={() => setEditingDeptId(null)} className={`${btnBase} border-[#d6e3ef] bg-white text-[#355a7a]`}><X className="h-3.5 w-3.5" />Cancel</button>
                          </div>
                        ) : (
                          <div className="inline-flex gap-1">
                            <button type="button" onClick={() => { clearEditors(); setEditingDeptId(item.id); setEditingDeptForm({ code: item.code, name: item.name, areaCode: item.areaCode || "1", superior: item.superior ?? "", manager: item.manager ?? "" }); }} className={`${btnBase} border-[#c5d8ed] bg-white text-[#1e4c78]`}><Pencil className="h-3.5 w-3.5" />Edit</button>
                            <button type="button" disabled={protectedDepartment} onClick={() => void (async () => { if (!(typeof window === "undefined" ? true : window.confirm(`Delete department ${item.name}?`))) return; await requestJson(`/api/personnel/departments/${item.id}`, { method: "DELETE" }); await loadAll(); })()} className={`${btnBase} border-[#e5c8c8] bg-white text-[#a33a3a] disabled:cursor-not-allowed disabled:opacity-50`}><Trash2 className="h-3.5 w-3.5" />Delete</button>
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className="flex justify-end"><button type="button" onClick={() => toggleAdd("departments")} className="inline-flex items-center gap-2 rounded-lg border border-[#c5d8ed] bg-white px-3 py-2 text-sm font-medium text-[#1e4c78]"><Plus className="h-4 w-4" />{openAdd.departments ? "Close Add Department" : "Add Department"}</button></div>
          {openAdd.departments ? (
            <div className="rounded-xl border border-[#d9e8f7] bg-[#f8fbff] p-4">
              <div className="mb-3 text-sm font-semibold text-[#0f3b61]">New Department</div>
              <div className="grid gap-2 md:grid-cols-6">
                <input value={deptForm.code} onChange={(e) => setDeptForm((p) => ({ ...p, code: e.target.value }))} placeholder="Dept Code" className={inputClass} />
                <input value={deptForm.name} onChange={(e) => setDeptForm((p) => ({ ...p, name: e.target.value }))} placeholder="Department Name" className={inputClass} />
                <select value={deptForm.areaCode} onChange={(e) => setDeptForm((p) => ({ ...p, areaCode: e.target.value }))} className={inputClass}>{areaOptions.map((a) => <option key={a.value} value={a.value}>{a.label}</option>)}</select>
                <input value={deptForm.superior} onChange={(e) => setDeptForm((p) => ({ ...p, superior: e.target.value }))} placeholder="Superior" className={inputClass} />
                <input value={deptForm.manager} onChange={(e) => setDeptForm((p) => ({ ...p, manager: e.target.value }))} placeholder="Manager" className={inputClass} />
                <button type="button" disabled={creatingDepartment} onClick={() => void createDepartment()} className="inline-flex items-center justify-center gap-2 rounded-lg bg-[#1f5c8f] px-3 py-2 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-60"><Plus className="h-4 w-4" />{creatingDepartment ? "Saving..." : "Save Department"}</button>
              </div>
            </div>
          ) : null}
        </section>
      ) : null}

      {section === "positions" ? (
        <section className="space-y-4 rounded-2xl border border-[#d9e8f7] bg-white p-4">
          <div className="overflow-x-auto rounded-xl border border-[#d9e8f7]">
            <table className="min-w-full text-left text-sm">
              <thead className="bg-[#f6faff] text-xs uppercase text-[#5a7892]"><tr><th className="px-3 py-2">Code</th><th className="px-3 py-2">Position</th><th className="px-3 py-2">Department</th><th className="px-3 py-2">Employee Qty</th><th className="px-3 py-2 text-right">Action</th></tr></thead>
              <tbody>
                {positions.map((item) => {
                  const edit = editingPositionId === item.id;
                  return (
                    <tr key={item.id} className="border-t border-[#e5effa]">
                      <td className="px-3 py-2">{edit ? <input value={editingPositionForm.code} onChange={(e) => setEditingPositionForm((p) => ({ ...p, code: e.target.value }))} className={inputClass} /> : <span className="font-medium">{item.code}</span>}</td>
                      <td className="px-3 py-2">{edit ? <input value={editingPositionForm.name} onChange={(e) => setEditingPositionForm((p) => ({ ...p, name: e.target.value }))} className={inputClass} /> : item.name}</td>
                      <td className="px-3 py-2">{edit ? <select value={editingPositionForm.department} onChange={(e) => setEditingPositionForm((p) => ({ ...p, department: e.target.value }))} className={inputClass}><option value="">Department</option>{departmentNameOptions.map((d) => <option key={d} value={d}>{d}</option>)}</select> : (item.department || "-")}</td>
                      <td className="px-3 py-2">{item.employeeQty ?? 0}</td>
                      <td className="px-3 py-2 text-right">
                        {edit ? (
                          <div className="inline-flex gap-1">
                            <button type="button" onClick={() => void savePosition(item)} className={`${btnBase} border-[#c6e3cd] bg-[#eefcf1] text-[#22603a]`}><Check className="h-3.5 w-3.5" />Save</button>
                            <button type="button" onClick={() => setEditingPositionId(null)} className={`${btnBase} border-[#d6e3ef] bg-white text-[#355a7a]`}><X className="h-3.5 w-3.5" />Cancel</button>
                          </div>
                        ) : (
                          <div className="inline-flex gap-1">
                            <button type="button" onClick={() => { clearEditors(); setEditingPositionId(item.id); setEditingPositionForm({ code: item.code, name: item.name, department: item.department || "" }); }} className={`${btnBase} border-[#c5d8ed] bg-white text-[#1e4c78]`}><Pencil className="h-3.5 w-3.5" />Edit</button>
                            <button type="button" onClick={() => void (async () => { if (!(typeof window === "undefined" ? true : window.confirm(`Delete position ${item.name}?`))) return; await requestJson(`/api/personnel/positions/${item.id}`, { method: "DELETE" }); await loadAll(); })()} className={`${btnBase} border-[#e5c8c8] bg-white text-[#a33a3a]`}><Trash2 className="h-3.5 w-3.5" />Delete</button>
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className="flex justify-end"><button type="button" onClick={() => toggleAdd("positions")} className="inline-flex items-center gap-2 rounded-lg border border-[#c5d8ed] bg-white px-3 py-2 text-sm font-medium text-[#1e4c78]"><Plus className="h-4 w-4" />{openAdd.positions ? "Close Add Position" : "Add Position"}</button></div>
          {openAdd.positions ? (
            <div className="rounded-xl border border-[#d9e8f7] bg-[#f8fbff] p-4">
              <div className="mb-3 text-sm font-semibold text-[#0f3b61]">New Position</div>
              <div className="grid gap-2 md:grid-cols-4">
                <input value={positionForm.code} onChange={(e) => setPositionForm((p) => ({ ...p, code: e.target.value }))} placeholder="Position Code" className={inputClass} />
                <input value={positionForm.name} onChange={(e) => setPositionForm((p) => ({ ...p, name: e.target.value }))} placeholder="Position Name" className={inputClass} />
                <select value={positionForm.department} onChange={(e) => setPositionForm((p) => ({ ...p, department: e.target.value }))} className={inputClass}><option value="">Department</option>{departmentNameOptions.map((d) => <option key={d} value={d}>{d}</option>)}</select>
                <button type="button" disabled={creatingPosition} onClick={() => void createPosition()} className="inline-flex items-center justify-center gap-2 rounded-lg bg-[#1f5c8f] px-3 py-2 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-60"><Plus className="h-4 w-4" />{creatingPosition ? "Saving..." : "Save Position"}</button>
              </div>
            </div>
          ) : null}
        </section>
      ) : null}

      {loading && showLoading && !hasSetupData ? <p className="text-sm text-[#6a8298]">Loading setup data...</p> : null}
    </div>
  );
}
