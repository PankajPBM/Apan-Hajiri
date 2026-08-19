"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronLeft, ChevronRight, CheckCircle2, AlertCircle } from "lucide-react";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDateTime } from "@/components/date/date-display";
import { BsDateField } from "@/components/date/dual-date-input";
import { resolveApiBases } from "@/lib/api-bases";

const PAGE_SIZE = 100;

type TransactionRow = {
  id: string;
  rawTimePc?: string | null;
  sourceTable?: string | null;
  employeeCode: string;
  employeeName: string;
  employeeDepartment: string;
  punchTime?: string | null;
  parsedOk?: number;
  statusCode?: number | null;
  verifyMode?: number | null;
  verifyModeDescription?: string | null;
  workcode?: string | null;
  deviceSerialNumber?: string | null;
  remarks?: string | null;
};

// Remarks shown in the table. Manual-punch approval state is surfaced here (instead of a
// separate Approval column): approved manual punches read "Manual punch approved", etc.
function displayRemarks(item: TransactionRow): string {
  const isManual = (item.sourceTable ?? "").toUpperCase() === "MANUAL";
  if (isManual) {
    if (item.parsedOk === 1) return "Manual punch approved";
    if (item.parsedOk === 3) return "Manual punch rejected";
    if (item.parsedOk === 2) {
      const note = item.remarks?.trim();
      return note ? `${note} (pending approval)` : "Manual punch (pending approval)";
    }
  }
  return item.remarks?.trim() || "—";
}

function describeStatusCode(value?: number | null): string {
  switch (value) {
    case 0: return "Check-in";
    case 1: return "Check-out";
    case 2: return "Overtime in";
    case 3: return "Overtime out";
    case 4: return "Break in";
    case 5: return "Break out";
    case 255: return "Auto";
    default: return value !== null ? `Status ${value}` : "-";
  }
}

// The server already computes the full label (DescribeVerifyMode in Program.cs) from the same
// verify-mode numbering used across the app; this is just the fallback for older cached rows
// that were fetched before verifyModeDescription was added to the API response.
function describeVerifyMode(item: TransactionRow): string {
  if (item.verifyModeDescription) return item.verifyModeDescription;
  if (item.verifyMode === 200) return "Manual/QR";
  return item.verifyMode !== null && item.verifyMode !== undefined ? `Mode ${item.verifyMode}` : "-";
}

function toInputDate(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, "0");
  const d = String(date.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

function csvCell(value: string): string {
  return `"${value.replace(/"/g, '""')}"`;
}

function downloadCsv(filename: string, header: string[], rows: string[][]) {
  const lines = [header, ...rows].map((row) => row.map(csvCell).join(","));
  const blob = new Blob([lines.join("\r\n")], { type: "text/csv;charset=utf-8;" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

// Sticky-cache the first working API base so subsequent calls skip the
// port-discovery race on initial tab open (same pattern as dashboard page).
let preferredApiBase = "";

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError: string | null = null;
  const candidates = resolveApiBases({ preferredBase: preferredApiBase });
  for (const base of candidates) {
    try {
      const response = await fetch(`${base}${path}`, {
        ...init,
        headers: { "Content-Type": "application/json", ...(init?.headers ?? {}), },
        cache: "no-store",
      });
      if (response.ok) {
        preferredApiBase = base;
        return (response.status === 204 ? null : await response.json()) as T;
      }
      lastError = await response.text();
    } catch (error) { lastError = error instanceof Error ? error.message : "Failed to fetch"; }
  }
  throw new Error(lastError ?? "Failed to fetch");
}

export default function AttendancePage() {
  const { calendarType } = useCalendarPreference();
  const now = useMemo(() => new Date(), []);
  const today = useMemo(() => toInputDate(now), [now]);
  // Default view shows only today's transactions; presets/date pickers expand the range.
  const [fromDate, setFromDate] = useState(today);
  const [toDate, setToDate] = useState(today);
  const [rangePreset, setRangePreset] = useState<"day" | "week" | "month" | "year">("day");
  const [search, setSearch] = useState("");
  const [txPage, setTxPage] = useState(1);
  const [rows, setRows] = useState<TransactionRow[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [actionLoading, setActionLoading] = useState<string | null>(null);

  const [deviceMap, setDeviceMap] = useState<Record<string, string>>({});
  const [manualEmployees, setManualEmployees] = useState<Array<{ id: string; employeeCode: string; fullName: string }>>([]);
  const [manualOpen, setManualOpen] = useState(false);
  const [manualSaving, setManualSaving] = useState(false);
  const [manualMsg, setManualMsg] = useState<string | null>(null);
  const [manualForm, setManualForm] = useState({ employeeId: "", status: "In", date: today, time: "09:00", workcode: "", remarks: "" });

  const [areas, setAreas] = useState<Array<{ code: string; name: string }>>([]);
  const [depts, setDepts] = useState<Array<{ code: string; name: string }>>([]);
  const [devs, setDevs] = useState<Array<{ serialNumber: string; name: string }>>([]);

  const [selectedArea, setSelectedArea] = useState("");
  const [selectedDept, setSelectedDept] = useState("");
  const [selectedDevice, setSelectedDevice] = useState("");
  const [exporting, setExporting] = useState(false);

  const txTotalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const txCurrentPage = Math.min(txPage, txTotalPages);

  const [sortField, setSortField] = useState<string>("punchTime");
  const [sortAsc, setSortAsc] = useState<boolean>(false);

  const updateSort = (field: string) => {
    if (sortField === field) {
      setSortAsc((prev) => !prev);
    } else {
      setSortField(field);
      setSortAsc(true);
    }
  };

  const sortedRows = useMemo(() => {
    const data = [...rows];
    if (!sortField) return data;
    data.sort((a, b) => {
      const valA = a[sortField as keyof TransactionRow];
      const valB = b[sortField as keyof TransactionRow];

      if (typeof valA === "number" && typeof valB === "number") {
        return sortAsc ? valA - valB : valB - valA;
      }

      let strA = "";
      let strB = "";
      if (valA !== undefined && valA !== null) {
        strA = String(valA).toLowerCase();
      }
      if (valB !== undefined && valB !== null) {
        strB = String(valB).toLowerCase();
      }

      if (strA < strB) return sortAsc ? -1 : 1;
      if (strA > strB) return sortAsc ? 1 : -1;
      return 0;
    });
    return data;
  }, [rows, sortField, sortAsc]);

  const applyRangePreset = (preset: "day" | "week" | "month" | "year") => {
    setRangePreset(preset);
    const end = new Date();
    const start = new Date(end);
    // Each preset shows exactly that calendar period (up to today), not a rolling window:
    //   day   -> today only
    //   week  -> from the start of the current week (Sunday) to today
    //   month -> from the 1st of the current month to today
    //   year  -> from Jan 1 of the current year to today
    if (preset === "week") start.setDate(end.getDate() - end.getDay());
    else if (preset === "month") start.setDate(1);
    else if (preset === "year") start.setMonth(0, 1);
    setFromDate(toInputDate(start));
    setToDate(toInputDate(end));
  };

  const loadTransactions = async (pageNum?: number) => {
    setLoading(true); setError(null);
    const currentPage = pageNum ?? txPage;
    try {
      const qs = new URLSearchParams();
      qs.set("from", fromDate); qs.set("to", toDate); qs.set("page", String(currentPage)); qs.set("pageSize", String(PAGE_SIZE));
      // Filter by the actual punch time (when the punch happened), not ReceivedUtc (when the
      // server ingested it) — otherwise a "Day" filter shows every record fetched today.
      qs.set("usePunchTime", "true");
      if (search.trim()) qs.set("search", search.trim());
      if (selectedArea) qs.set("area", selectedArea);
      if (selectedDept) qs.set("department", selectedDept);
      if (selectedDevice) qs.set("deviceSerial", selectedDevice);
      const data = await requestJson<{ data: TransactionRow[]; total: number }>(`/api/transactions?${qs.toString()}`);
      setRows(Array.isArray(data?.data) ? data.data : []);
      setTotal(typeof data?.total === "number" ? data.total : 0);
    } catch (err) { setError(err instanceof Error ? err.message : "Failed to load transactions."); setRows([]); setTotal(0); }
    finally { setLoading(false); }
  };

  const filtersRef = useRef({ fromDate, toDate, search, selectedArea, selectedDept, selectedDevice });
  useEffect(() => {
    const prev = filtersRef.current;
    if (
      prev.fromDate !== fromDate ||
      prev.toDate !== toDate ||
      prev.search !== search ||
      prev.selectedArea !== selectedArea ||
      prev.selectedDept !== selectedDept ||
      prev.selectedDevice !== selectedDevice
    ) {
      setTxPage(1); void loadTransactions(1);
    } else { void loadTransactions(txPage); }
    filtersRef.current = { fromDate, toDate, search, selectedArea, selectedDept, selectedDevice };
  }, [fromDate, toDate, search, selectedArea, selectedDept, selectedDevice, txPage]);

  useEffect(() => {
    void (async () => {
      try {
        const data = await requestJson<unknown>("/api/personnel/employees?status=active");
        const items = Array.isArray(data) ? data : ((data as { items?: unknown[] })?.items ?? []);
        setManualEmployees((items as Array<Record<string, unknown>>).map((r) => ({ id: String(r.id ?? ""), employeeCode: String(r.employeeCode ?? ""), fullName: String(r.fullName ?? r.employeeCode ?? ""), })).filter((r) => r.id && r.employeeCode).sort((a, b) => a.employeeCode.localeCompare(b.employeeCode, undefined, { numeric: true })));
      } catch { setManualEmployees([]); }
    })();
    void (async () => {
      try {
        const data = await requestJson<{ areas?: any[]; departments?: any[] }>("/api/personnel/setup");
        const listAreas = Array.isArray(data?.areas) ? data.areas : [];
        const listDepts = Array.isArray(data?.departments) ? data.departments : [];
        setAreas(listAreas.map(a => ({ code: String(a.code || ""), name: String(a.name || "") })).filter(a => a.name));
        setDepts(listDepts.map(d => ({ code: String(d.code || ""), name: String(d.name || "") })).filter(d => d.name));
      } catch {
        setAreas([]);
        setDepts([]);
      }
    })();
    void (async () => {
      try {
        const data = await requestJson<{ items?: Array<Record<string, unknown>> }>("/api/devices");
        const list = (data?.items ?? []).map(d => ({
          serialNumber: String(d.serialNumber ?? "").trim(),
          name: String(d.name ?? "").trim()
        })).filter(d => d.serialNumber);
        setDevs(list);

        const map: Record<string, string> = {};
        for (const d of list) {
          map[d.serialNumber] = d.name || d.serialNumber;
        }
        setDeviceMap(map);
      } catch {
        setDeviceMap({});
        setDevs([]);
      }
    })();
  }, []);

  const submitManualPunch = async () => {
    if (!manualForm.employeeId) { setManualMsg("Select an employee."); return; }
    const statusCodeMap: Record<string, number> = { In: 0, Out: 1, BreakIn: 4, BreakOut: 5 };
    setManualSaving(true); setManualMsg(null);
    try {
      await requestJson("/api/attendance/manual", { method: "POST", body: JSON.stringify({ employeeId: manualForm.employeeId, status: manualForm.status, timestamp: `${manualForm.date}T${manualForm.time}:00`, statusCode: statusCodeMap[manualForm.status] ?? 0, workcode: manualForm.workcode.trim() || null, remarks: manualForm.remarks.trim() || null, }), });
      setManualOpen(false); setManualForm({ employeeId: "", status: "In", date: today, time: "09:00", workcode: "", remarks: "" }); await loadTransactions(1);
    } catch (err) { setManualMsg(err instanceof Error ? err.message : "Failed to add punch."); }
    finally { setManualSaving(false); }
  };

  const exportTransactions = async () => {
    setExporting(true);
    try {
      const qs = new URLSearchParams();
      qs.set("from", fromDate);
      qs.set("to", toDate);
      qs.set("usePunchTime", "true");
      qs.set("take", "20000");
      if (search.trim()) qs.set("search", search.trim());
      if (selectedArea) qs.set("area", selectedArea);
      if (selectedDept) qs.set("department", selectedDept);
      if (selectedDevice) qs.set("deviceSerial", selectedDevice);
      const data = await requestJson<{ data: TransactionRow[] }>(`/api/transactions?${qs.toString()}`);
      const items = Array.isArray(data?.data) ? data.data : [];
      const csvRows = items.map((item) => [
        item.employeeCode ?? "",
        item.employeeName ?? "",
        item.employeeDepartment ?? "",
        item.punchTime ? formatCalendarDateTime(item.punchTime, calendarType) : "",
        describeStatusCode(item.statusCode),
        describeVerifyMode(item),
        deviceMap[item.deviceSerialNumber ?? ""] ?? item.deviceSerialNumber ?? "",
        displayRemarks(item),
      ]);
      downloadCsv(
        `transactions_${fromDate}_to_${toDate}.csv`,
        ["Employee ID", "Employee Name", "Department", "Punch Time", "Status", "Verify Type", "Device", "Remarks"],
        csvRows,
      );
    } catch (err) {
      alert(err instanceof Error ? err.message : "Failed to export transactions.");
    } finally {
      setExporting(false);
    }
  };

  const handleDecision = async (id: string, decision: "approve" | "reject") => {
    setActionLoading(id);
    try {
      await requestJson(`/api/transactions/${id}/${decision}`, { method: "POST" });
      await loadTransactions();
    } catch (err) { alert(err instanceof Error ? err.message : `Failed to ${decision} transaction.`); }
    finally { setActionLoading(null); }
  };

  return (
    <div className="space-y-4">
      <div className="tab-header">
        <h1 className="text-xl font-bold text-[#1b4b73]">Transactions</h1>
        <p className="text-xs text-[--muted-foreground]">Biometric logs and manual punch requests for approval.</p>
      </div>

      <section className="surface p-4 shadow-sm rounded-2xl border border-[#e2eaf1]">
        <div className="tab-toolbar mb-4 space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex gap-1.5 p-1 bg-slate-50 rounded-xl border border-slate-100">
              {["day", "week", "month", "year"].map((p) => (
                <button key={p} onClick={() => applyRangePreset(p as any)} className={`rounded-lg px-3 py-1.5 text-xs font-bold transition-all ${rangePreset === p ? "bg-[#1b4b73] text-white shadow-md shadow-[#1b4b73]/20" : "text-[#1b4b73] hover:bg-white"}`}>{p.charAt(0).toUpperCase() + p.slice(1)}</button>
              ))}
            </div>
            <div className="flex items-center gap-2">
              <button onClick={() => { setFromDate(today); setToDate(today); setRangePreset("day"); }} className="rounded-xl border border-[#d3e1ee] bg-white px-3 py-2 text-xs font-bold text-[#1b4b73] hover:bg-slate-50 transition-colors">Today</button>
              <button onClick={() => { setManualMsg(null); setManualOpen(true); }} className="rounded-xl bg-[#1e7a4f] px-4 py-2 text-xs font-bold text-white shadow-lg shadow-emerald-900/10 hover:bg-[#165a3d] transition-all">+ Manual Punch</button>
              <button onClick={() => void exportTransactions()} disabled={exporting} className="rounded-xl border border-[#1b4b73] bg-white px-4 py-2 text-xs font-bold text-[#1b4b73] shadow-sm hover:bg-slate-50 transition-all disabled:opacity-50">{exporting ? "Exporting..." : "Export CSV"}</button>
              <button onClick={() => void loadTransactions(1)} className="rounded-xl bg-[#1b4b73] px-4 py-2 text-xs font-bold text-white shadow-lg shadow-blue-900/10 hover:bg-[#153a5c] transition-all">{loading ? "..." : "Refresh"}</button>
            </div>
          </div>

          <div className="grid gap-3 sm:grid-cols-2 md:grid-cols-3">
            <label className="grid gap-1.5"><span className="text-[10px] font-bold uppercase tracking-wider text-[#4a6d8c]">From Date</span><BsDateField value={fromDate} onChange={setFromDate} /></label>
            <label className="grid gap-1.5"><span className="text-[10px] font-bold uppercase tracking-wider text-[#4a6d8c]">To Date</span><BsDateField value={toDate} onChange={setToDate} /></label>
            <label className="grid gap-1.5"><span className="text-[10px] font-bold uppercase tracking-wider text-[#4a6d8c]">Quick Search</span><input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Employee ID or Name..." className="rounded-xl border border-[#cbd5e1] bg-slate-50 px-3 py-2 text-sm focus:border-blue-400 focus:ring-1 outline-none" /></label>
            <label className="grid gap-1.5">
              <span className="text-[10px] font-bold uppercase tracking-wider text-[#4a6d8c]">Area</span>
              <select value={selectedArea} onChange={e => setSelectedArea(e.target.value)} className="rounded-xl border border-[#cbd5e1] bg-slate-50 px-3 py-2 text-sm focus:border-blue-400 focus:ring-1 outline-none text-[#1b4b73] font-medium">
                <option value="">All Areas</option>
                {areas.map(a => <option key={a.code} value={a.name}>{a.name}</option>)}
              </select>
            </label>
            <label className="grid gap-1.5">
              <span className="text-[10px] font-bold uppercase tracking-wider text-[#4a6d8c]">Department</span>
              <select value={selectedDept} onChange={e => setSelectedDept(e.target.value)} className="rounded-xl border border-[#cbd5e1] bg-slate-50 px-3 py-2 text-sm focus:border-blue-400 focus:ring-1 outline-none text-[#1b4b73] font-medium">
                <option value="">All Departments</option>
                {depts.map(d => <option key={d.code} value={d.name}>{d.name}</option>)}
              </select>
            </label>
            <label className="grid gap-1.5">
              <span className="text-[10px] font-bold uppercase tracking-wider text-[#4a6d8c]">Device</span>
              <select value={selectedDevice} onChange={e => setSelectedDevice(e.target.value)} className="rounded-xl border border-[#cbd5e1] bg-slate-50 px-3 py-2 text-sm focus:border-blue-400 focus:ring-1 outline-none text-[#1b4b73] font-medium">
                <option value="">All Devices</option>
                {devs.map(d => <option key={d.serialNumber} value={d.serialNumber}>{d.name || d.serialNumber}</option>)}
              </select>
            </label>
          </div>
        </div>

        {error && <div className="mb-4 rounded-xl border border-red-200 bg-red-50 p-3 text-xs font-bold text-red-700">{error}</div>}

        <div className="mb-3 flex items-center justify-between text-[11px] font-bold text-slate-500 px-1">
          <p>Showing {total===0?0:(txCurrentPage-1)*PAGE_SIZE+1}–{Math.min(txCurrentPage*PAGE_SIZE,total)} of {total} records</p>
          <div className="flex items-center gap-2">
            <button onClick={() => setTxPage(p => Math.max(1, p-1))} disabled={txCurrentPage===1||loading} className="rounded-lg border border-slate-200 bg-white p-1 hover:bg-slate-50 disabled:opacity-30"><ChevronLeft className="h-4 w-4" /></button>
            <span>Page {txCurrentPage} / {txTotalPages}</span>
            <button onClick={() => setTxPage(p => Math.min(txTotalPages, p+1))} disabled={txCurrentPage===txTotalPages||loading} className="rounded-lg border border-slate-200 bg-white p-1 hover:bg-slate-50 disabled:opacity-30"><ChevronRight className="h-4 w-4" /></button>
          </div>
        </div>

        <div className="overflow-x-auto rounded-xl border border-[#e5effa]">
          <table className="min-w-full text-left text-xs">
            <thead className="bg-[#f8fbff] text-[10px] font-bold uppercase tracking-wider text-[#4a6d8d]">
              <tr>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("punchTime")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Punch Time
                    {sortField === "punchTime" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("employeeCode")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Employee ID
                    {sortField === "employeeCode" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("employeeName")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Employee Name
                    {sortField === "employeeName" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("employeeDepartment")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Dept
                    {sortField === "employeeDepartment" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("statusCode")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Status
                    {sortField === "statusCode" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("verifyMode")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Verify Type
                    {sortField === "verifyMode" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("deviceSerialNumber")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Source
                    {sortField === "deviceSerialNumber" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3">
                  <button
                    type="button"
                    onClick={() => updateSort("remarks")}
                    className="font-bold hover:text-[--foreground] inline-flex items-center gap-0.5"
                  >
                    Remarks
                    {sortField === "remarks" && (
                      <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                    )}
                  </button>
                </th>
                <th className="px-3 py-3 text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-[#e5effa]">
              {sortedRows.map((item) => (
                <tr key={item.id} className="hover:bg-[#fcfdfe] transition-colors">
                  <td className="whitespace-nowrap px-3 py-2.5">
                    <div className="font-bold text-[#1e293b]">{formatCalendarDateTime(item.punchTime, calendarType)}</div>
                    <div className="text-[9px] text-slate-400 font-medium">PC: {formatCalendarDateTime(item.rawTimePc, calendarType)}</div>
                  </td>
                  <td className="px-3 py-2.5 font-bold text-[#1e293b]">{item.employeeCode || "-"}</td>
                  <td className="px-3 py-2.5 font-bold text-[#1e293b]">{item.employeeName || "-"}</td>
                  <td className="px-3 py-2.5 text-[#1e293b] font-medium">{item.employeeDepartment || "-"}</td>
                  <td className="px-3 py-2.5">
                    <span className={`rounded-lg px-2 py-1 text-[9px] font-bold uppercase ${item.statusCode === 1 ? "bg-orange-50 text-orange-700 border border-orange-100" : "bg-blue-50 text-blue-700 border border-blue-100"}`}>
                      {describeStatusCode(item.statusCode)}
                    </span>
                  </td>
                  <td className="px-3 py-2.5 max-w-[140px] truncate" title={describeVerifyMode(item)}>
                    <span className="rounded-lg border border-slate-200 bg-slate-50 px-2 py-1 text-[9px] font-bold uppercase text-slate-600">
                      {describeVerifyMode(item)}
                    </span>
                  </td>
                  <td className="px-3 py-2.5">
                    {item.deviceSerialNumber ? (
                      <>
                        <div className="text-[11px] font-bold text-[#1e293b]">{deviceMap[item.deviceSerialNumber] || item.deviceSerialNumber}</div>
                        <div className="text-[10px] font-medium text-[#1e293b]">{item.deviceSerialNumber}</div>
                      </>
                    ) : (
                      <div className="text-[11px] font-bold text-[#1e293b]">{(item.sourceTable ?? "").toUpperCase() === "MANUAL" ? "Manual" : "System"}</div>
                    )}
                  </td>
                  <td className="px-3 py-2.5 text-[10px] italic text-slate-400 max-w-[160px] truncate" title={displayRemarks(item)}>{displayRemarks(item)}</td>
                  <td className="px-3 py-2.5 text-right">
                    {item.parsedOk === 2 ? (
                      <div className="flex justify-end gap-1.5">
                        <button disabled={!!actionLoading} onClick={() => handleDecision(item.id, "approve")} className="rounded-lg bg-emerald-600 px-2.5 py-1 text-[9px] font-bold text-white shadow-sm hover:bg-emerald-700 transition-colors disabled:opacity-50">Approve</button>
                        <button disabled={!!actionLoading} onClick={() => handleDecision(item.id, "reject")} className="rounded-lg bg-white border border-rose-200 px-2.5 py-1 text-[9px] font-bold text-rose-600 hover:bg-rose-50 transition-colors disabled:opacity-50">Reject</button>
                      </div>
                    ) : (
                      <span className="text-[10px] font-bold text-slate-300 uppercase tracking-tighter">— No Action —</span>
                    )}
                  </td>
                </tr>
              ))}
              {rows.length === 0 && (
                <tr>
                  <td colSpan={9} className="py-16 text-center">
                    <div className="mx-auto max-w-sm space-y-3">
                      <p className="text-sm font-medium text-slate-500">
                        {search.trim() ? "No matching transaction records found." : "No transactions found for the selected date range."}
                      </p>
                      {rangePreset === "day" && !search.trim() && (
                        <div className="flex justify-center gap-2">
                          <button
                            type="button"
                            onClick={() => applyRangePreset("month")}
                            className="rounded-xl border border-[#1b4b73] bg-blue-50 px-3 py-1.5 text-xs font-bold text-[#1b4b73] hover:bg-blue-100 transition-colors"
                          >
                            Show This Month
                          </button>
                          <button
                            type="button"
                            onClick={() => applyRangePreset("year")}
                            className="rounded-xl border border-slate-300 bg-white px-3 py-1.5 text-xs font-bold text-slate-700 hover:bg-slate-50 transition-colors"
                          >
                            Show All Time
                          </button>
                        </div>
                      )}
                    </div>
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      {manualOpen && (
        <div className="fixed inset-0 z-[70] grid place-items-center bg-black/40 p-4 backdrop-blur-sm" onClick={() => setManualOpen(false)}>
          <div className="w-full max-w-md rounded-[32px] bg-white p-8 shadow-2xl border border-[#d3e6fb] animate-in fade-in zoom-in duration-300" onClick={(e) => e.stopPropagation()}>
            <h3 className="text-xl font-bold text-[#0f3857]">Add Manual Punch</h3>
            <p className="mb-6 text-sm text-[#5f7f9f] font-medium">Record an immediate attendance entry.</p>
            <div className="space-y-4">
              <label className="grid gap-1.5"><span className="text-xs font-bold uppercase tracking-wider text-[#4a6d8c]">Employee *</span><select value={manualForm.employeeId} onChange={(e) => setManualForm((p) => ({ ...p, employeeId: e.target.value }))} className="rounded-2xl border border-[#cbd5e1] bg-slate-50 px-4 py-3 text-sm font-bold text-[#1e293b] outline-none">
                <option value="">Select employee</option>
                {manualEmployees.map((emp) => (<option key={emp.id} value={emp.id}>{emp.employeeCode} - {emp.fullName}</option>))}
              </select></label>
              <div className="grid grid-cols-2 gap-4">
                <label className="grid gap-1.5"><span className="text-xs font-bold uppercase tracking-wider text-[#4a6d8c]">Type *</span><select value={manualForm.status} onChange={(e) => setManualForm((p) => ({ ...p, status: e.target.value }))} className="rounded-2xl border border-[#cbd5e1] bg-slate-50 px-4 py-3 text-sm font-bold text-[#1e293b] outline-none"><option value="In">Check-In</option><option value="Out">Check-Out</option></select></label>
                <label className="grid gap-1.5"><span className="text-xs font-bold uppercase tracking-wider text-[#4a6d8c]">Time *</span><input type="time" value={manualForm.time} onChange={(e) => setManualForm((p) => ({ ...p, time: e.target.value }))} className="rounded-2xl border border-[#cbd5e1] bg-slate-50 px-4 py-3 text-sm font-bold text-[#1e293b] outline-none" /></label>
              </div>
              <label className="grid gap-1.5"><span className="text-xs font-bold uppercase tracking-wider text-[#4a6d8c]">Remarks</span><input value={manualForm.remarks} onChange={(e) => setManualForm((p) => ({ ...p, remarks: e.target.value }))} placeholder="Reason / note..." className="rounded-2xl border border-[#cbd5e1] bg-slate-50 px-4 py-3 text-sm font-medium text-[#1e293b] outline-none" /></label>
            </div>
            {manualMsg && <div className="mt-6 rounded-2xl bg-red-50 p-4 border border-red-100 text-sm font-bold text-red-600">{manualMsg}</div>}
            <div className="mt-8 flex gap-3">
              <button onClick={() => setManualOpen(false)} className="flex-1 rounded-2xl border border-[#cbd5e1] py-4 text-sm font-bold text-slate-600 transition hover:bg-slate-50">Cancel</button>
              <button disabled={manualSaving} onClick={() => void submitManualPunch()} className="flex-1 rounded-2xl bg-emerald-600 py-4 text-sm font-bold text-white shadow-xl shadow-emerald-900/10 hover:bg-emerald-700 transition-all disabled:opacity-50">{manualSaving ? "Saving..." : "Add Punch"}</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
