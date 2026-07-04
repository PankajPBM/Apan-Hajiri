"use client";

import { useCallback, useEffect, useState } from "react";
import { Check, Search, X } from "lucide-react";
import { BsDateField, defaultTodayAdString, shiftDateByDays } from "@/components/date/dual-date-input";
import { resolveApiBases } from "@/lib/api-bases";

let preferredApiBase = "";

function apiBases(): string[] {
  return resolveApiBases({ preferredBase: preferredApiBase });
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch";
  const bases = apiBases();
  
  // Ensure we try both localhost and 127.0.0.1 if they aren't already in the list
  const extendedBases = [...bases];
  const firstBase = bases[0];
  if (firstBase) {
    try {
      const url = new URL(firstBase);
      if (url.hostname === "localhost") {
        url.hostname = "127.0.0.1";
        const alt = url.toString().replace(/\/+$/, "");
        if (!extendedBases.includes(alt)) extendedBases.push(alt);
      } else if (url.hostname === "127.0.0.1") {
        url.hostname = "localhost";
        const alt = url.toString().replace(/\/+$/, "");
        if (!extendedBases.includes(alt)) extendedBases.push(alt);
      }
    } catch { /* ignore */ }
  }

  for (const base of extendedBases) {
    try {
      const call = async () => {
        const controller = new AbortController();
        // Increase timeout to 15s for POST requests which might take longer
        const timeoutMs = init?.method === "POST" ? 15000 : 8000;
        const timeout = setTimeout(() => controller.abort(), timeoutMs);
        try {
          const url = `${base}${path}`;
          return await fetch(url, {
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
        } catch { /* ignore */ }
      }

      if (response.ok) {
        preferredApiBase = base;
        return (await response.json()) as T;
      }
      const text = await response.text();
      lastError = `[${base}] ${text || `Request failed (${response.status})`}`;
      console.error(`[API Request Failed] ${base}${path}:`, { status: response.status, body: text });
    } catch (error) {
      const msg = error instanceof Error ? error.message : String(error);
      lastError = `[${base}${path}] ${msg}`;
      console.error(`[API Network/CORS Error] ${base}${path}:`, error);
    }
  }
  throw new Error(lastError);
}

interface LeaveApplicationModalProps {
  onClose: () => void;
  onSuccess?: () => void;
}

export function LeaveApplicationModal({ onClose, onSuccess }: LeaveApplicationModalProps) {
  const [employees, setEmployees] = useState<Array<{ id: string; employeeCode: string; fullName: string }>>([]);
  const [employeesLoading, setEmployeesLoading] = useState(false);
  const [employeeSearch, setEmployeeSearch] = useState("");
  
  const [leaveTypes, setLeaveTypes] = useState<Array<{ Id: number; LeaveName: string }>>([]);
  const [leaveSaving, setLeaveSaving] = useState(false);
  const [leaveError, setLeaveError] = useState<string | null>(null);
  const [leaveForm, setLeaveForm] = useState({ 
    personnelId: "", 
    leaveTypeId: "", 
    fromDate: defaultTodayAdString(), 
    toDate: shiftDateByDays(defaultTodayAdString(), 1), 
    reason: "" 
  });

  const loadEmployees = useCallback(async () => {
    setEmployeesLoading(true);
    try {
      const response = await requestJson<any>("/api/personnel/employees?status=active&pageSize=5000");
      const rows = Array.isArray(response) ? response : (response?.items ?? response?.Items ?? []);
      
      const mapped = (Array.isArray(rows) ? rows : []).map((r: any) => {
        const id = r.id ?? r.Id;
        const code = r.employeeCode ?? r.EmployeeCode ?? r.personnelCode ?? r.PersonnelCode ?? r.userId;
        const firstName = r.firstName ?? r.FirstName ?? "";
        const lastName = r.lastName ?? r.LastName ?? "";
        const fullName = r.fullName ?? r.FullName ?? `${firstName} ${lastName}`.trim();
        
        return { 
          id: String(id ?? ""), 
          employeeCode: String(code ?? ""), 
          fullName: fullName || String(code ?? "Unknown")
        };
      }).filter((x: any) => x.id && x.employeeCode);
      setEmployees(mapped);
    } catch (err) {
      console.error("[ApplyLeave] Failed to load employees:", err);
    } finally {
      setEmployeesLoading(false);
    }
  }, []);

  const loadLeaveTypes = useCallback(async () => {
    try {
      let rows = await requestJson<any[]>("/api/leave/types?activeOnly=true");
      if (!Array.isArray(rows) || rows.length === 0) {
        // Fallback to basic leave types if none defined
        const defaults = [
          { leaveName: "Annual Leave", maxPerYear: 15, isPaid: true },
          { leaveName: "Sick Leave", maxPerYear: 12, isPaid: true },
          { leaveName: "Casual Leave", maxPerYear: 6, isPaid: true },
        ];
        for (const item of defaults) {
          try {
            await requestJson("/api/leave/types", { method: "POST", body: JSON.stringify(item) });
          } catch { /* ignore */ }
        }
        rows = await requestJson<any[]>("/api/leave/types?activeOnly=true");
      }

      if (Array.isArray(rows)) {
        const mapped = rows.map(r => ({
          Id: r.Id ?? r.id,
          LeaveName: r.LeaveName ?? r.leaveName ?? "Unnamed"
        }));
        setLeaveTypes(mapped);
        if (mapped.length > 0) setLeaveForm(f => ({ ...f, leaveTypeId: String(mapped[0].Id) }));
      }
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    void loadEmployees();
    void loadLeaveTypes();
  }, [loadEmployees, loadLeaveTypes]);

  const saveLeave = async () => {
    if (!leaveForm.personnelId || !leaveForm.leaveTypeId) {
      setLeaveError("Please select employee and leave type.");
      return;
    }
    if (!leaveForm.reason.trim()) {
      setLeaveError("Please provide a reason for leave.");
      return;
    }

    setLeaveSaving(true);
    setLeaveError(null);
    try {
      // Backend's LeaveCreateRequest binds this as "employeeId" — the form field is named
      // "personnelId" internally, but the JSON key sent to the API must be "employeeId".
      await requestJson("/api/leave", {
        method: "POST",
        body: JSON.stringify({
          employeeId: leaveForm.personnelId, // Guid string, do NOT parseInt
          leaveTypeId: parseInt(leaveForm.leaveTypeId, 10),
          fromDate: leaveForm.fromDate,
          toDate: leaveForm.toDate,
          reason: leaveForm.reason.trim(),
        }),
      });
      onSuccess?.();
      onClose();
    } catch (err) {
      setLeaveError(err instanceof Error ? err.message : "Failed to submit leave.");
    } finally {
      setLeaveSaving(false);
    }
  };

  const filteredEmployees = employees.filter(e => {
    const s = employeeSearch.toLowerCase().trim();
    return !s || e.fullName.toLowerCase().includes(s) || e.employeeCode.toLowerCase().includes(s);
  });

  return (
    <div className="fixed inset-0 z-[100] grid place-items-center bg-black/35 p-4 backdrop-blur-sm" onClick={onClose}>
      <div className="w-full max-w-md rounded-2xl border border-[#d8e6f4] bg-white p-6 shadow-2xl animate-in fade-in zoom-in duration-300" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="text-lg font-bold text-[#123454]">Apply Leave</h2>
            <p className="mt-0.5 text-xs text-[#5f7f9f]">Submit leave request for an employee.</p>
          </div>
          <button type="button" onClick={onClose} className="rounded-lg border border-[#d3e1ee] p-1.5 text-[#355a7a] hover:bg-slate-50">
            <X className="h-4 w-4" />
          </button>
        </div>
        
        <div className="mt-4 space-y-4">
          <div className="space-y-1.5">
            <label className="text-xs font-bold text-[#123454]">Employee Name *</label>
            <div className="space-y-2">
              <div className="relative">
                <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-[#6d87a4]" />
                <input 
                  type="text" 
                  placeholder="Search by name or ID..."
                  value={employeeSearch}
                  onChange={(e) => setEmployeeSearch(e.target.value)}
                  className="w-full rounded-xl border border-[#d3e1ee] bg-[#fbfdff] py-1.5 pl-8 pr-3 text-xs text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6]"
                />
              </div>
              <select 
                value={leaveForm.personnelId} 
                onChange={(e) => setLeaveForm(f => ({ ...f, personnelId: e.target.value }))}
                className="w-full rounded-xl border border-[#d3e1ee] bg-[#fbfdff] px-3 py-2 text-sm text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6] font-medium"
                disabled={employeesLoading}
              >
                <option value="">{employeesLoading ? "Loading employees..." : "Select Employee"}</option>
                {filteredEmployees.map(e => (
                  <option key={e.id} value={e.id}>{e.employeeCode} - {e.fullName}</option>
                ))}
                {!employeesLoading && employees.length > 0 && filteredEmployees.length === 0 && (
                  <option disabled>No matches found</option>
                )}
              </select>
            </div>
          </div>

          <div className="space-y-1.5">
            <label className="text-xs font-bold text-[#123454]">Leave Type *</label>
            <select 
              value={leaveForm.leaveTypeId} 
              onChange={(e) => setLeaveForm(f => ({ ...f, leaveTypeId: e.target.value }))}
              className="w-full rounded-xl border border-[#d3e1ee] bg-[#fbfdff] px-3 py-2 text-sm text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6] font-medium"
            >
              {leaveTypes.map(t => <option key={t.Id} value={t.Id}>{t.LeaveName}</option>)}
            </select>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <div className="space-y-1.5">
              <label className="text-xs font-bold text-[#123454]">From Date *</label>
              <BsDateField value={leaveForm.fromDate} onChange={(v) => setLeaveForm(f => ({ ...f, fromDate: v }))} />
            </div>
            <div className="space-y-1.5">
              <label className="text-xs font-bold text-[#123454]">To Date *</label>
              <BsDateField value={leaveForm.toDate} onChange={(v) => setLeaveForm(f => ({ ...f, toDate: v }))} />
            </div>
          </div>

          <div className="space-y-1.5">
            <label className="text-xs font-bold text-[#123454]">Reason *</label>
            <textarea 
              rows={3} 
              value={leaveForm.reason}
              onChange={(e) => setLeaveForm(f => ({ ...f, reason: e.target.value }))}
              placeholder="Reason for leave request..." 
              className="w-full rounded-xl border border-[#d3e1ee] bg-[#fbfdff] px-3 py-2 text-sm text-[#123454] outline-none focus:ring-1 focus:ring-[#b7d0e6]" 
            />
          </div>
        </div>

        {leaveError && (
          <div className="mt-4 rounded-xl border border-red-200 bg-red-50 p-3 text-xs font-bold text-red-600">
            {leaveError}
          </div>
        )}

        <div className="mt-6 flex justify-end gap-3">
          <button 
            type="button" 
            onClick={onClose} 
            className="rounded-xl border border-[#d3e1ee] px-4 py-2 text-sm font-bold text-[#355a7a] hover:bg-slate-50 transition duration-300"
          >
            Cancel
          </button>
          <button 
            type="button" 
            onClick={() => void saveLeave()} 
            disabled={leaveSaving}
            className="inline-flex items-center gap-1.5 rounded-xl bg-[#1f5c8f] px-6 py-2 text-sm font-bold text-white transition duration-300 hover:bg-[#164a72] shadow-lg shadow-blue-900/10 disabled:opacity-50"
          >
            {leaveSaving ? "Saving..." : <><Check className="h-4 w-4" />Submit Request</>}
          </button>
        </div>
      </div>
    </div>
  );
}
