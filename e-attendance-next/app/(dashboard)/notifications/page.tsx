"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronDown, ChevronLeft, ChevronRight, ChevronUp, RefreshCw, Search, X } from "lucide-react";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDateTime } from "@/components/date/date-display";
import { ModuleShell } from "@/components/dashboard/module-shell";
import { BsDateField } from "@/components/date/dual-date-input";
import { loadAlertFeed, type AlertItem } from "@/lib/alerts";
import { resolveApiBases } from "@/lib/api-bases";

type AppLogItem = {
  timestampUtc: string;
  level: string;
  source: string;
  message: string;
  detail?: string | null;
};

function toInputDate(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, "0");
  const d = String(date.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

function compactText(value: string | null | undefined): string {
  return (value || "").replace(/\s+/g, " ").trim();
}

function shorten(value: string, maxLen: number): string {
  if (value.length <= maxLen) {
    return value;
  }
  return `${value.slice(0, maxLen).trimEnd()}...`;
}

function cleanLogs(items: AppLogItem[], showAllRawLogs: boolean): AppLogItem[] {
  if (showAllRawLogs) return items;
  return items.filter((item) => {
    const level = (item.level || "").toUpperCase();
    const src = (item.source || "").toLowerCase();
    const msg = (item.message || "").toLowerCase();
    const detail = (item.detail || "").toLowerCase();

    // Filter out repetitive historical false-positive Planned maintenance warnings from Health Monitor
    if (src.includes("health-monitor") && detail.includes("postgresql-maintenance=planned")) {
      return false;
    }

    // Always preserve warnings, errors, and fatal alerts
    if (level.includes("WARN") || level.includes("ERR") || level.includes("FATAL") || level.includes("CRIT")) {
      return true;
    }

    // Always preserve all device actions, operations, commands, attendance, enrollments, and sync
    if (
      src === "deviceaction" ||
      src === "device" ||
      src === "command" ||
      src === "attendance" ||
      src === "cardenrollment" ||
      src === "sync" ||
      src === "personnel"
    ) {
      return true;
    }

    // Filter only routine empty polling heartbeats that offer no operational value
    if (msg.includes("getrequest poll from") && msg.includes("no commands")) return false;
    if (msg.includes("options=all") && !item.detail?.includes("error")) return false;
    if (msg.startsWith("push server heartbeat")) return false;
    if (src === "devicepush" && msg.includes("heartbeat")) return false;

    return true;
  });
}

function csvCell(value: string): string {
  return `"${value.replace(/"/g, '""')}"`;
}

function downloadCsv(filename: string, header: string[], rows: string[][]) {
  const lines = [header, ...rows].map((row) => row.map(csvCell).join(","));
  const blob = new Blob(["\uFEFF" + lines.join("\r\n")], { type: "text/csv;charset=utf-8;" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

function renderLevelBadge(level: string | null | undefined) {
  const lvl = (level || "").toUpperCase().trim();
  if (lvl.includes("ERR") || lvl.includes("FATAL") || lvl.includes("CRIT")) {
    return (
      <span className="inline-flex items-center rounded-md border border-rose-200 bg-rose-50 px-2 py-0.5 text-[11px] font-bold text-rose-700">
        ERROR
      </span>
    );
  }
  if (lvl.includes("WARN")) {
    return (
      <span className="inline-flex items-center rounded-md border border-amber-200 bg-amber-50 px-2 py-0.5 text-[11px] font-bold text-amber-800">
        WARN
      </span>
    );
  }
  if (lvl.includes("INFO")) {
    return (
      <span className="inline-flex items-center rounded-md border border-sky-200 bg-sky-50 px-2 py-0.5 text-[11px] font-semibold text-sky-800">
        INFO
      </span>
    );
  }
  return (
    <span className="inline-flex items-center rounded-md border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] font-medium text-slate-700">
      {lvl || "DEBUG"}
    </span>
  );
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError: string | null = null;
  const candidates = resolveApiBases();

  for (const base of candidates) {
    try {
      const call = async () => {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 4500);
        try {
          return await fetch(`${base}${path}`, {
            ...init,
            headers: {
              "Content-Type": "application/json",
              ...(init?.headers ?? {}),
            },
            cache: "no-store",
            signal: controller.signal,
          });
        } finally {
          clearTimeout(timer);
        }
      };

      let response = await call();
      if (response.status === 503) {
        try {
          await fetch(`${base}/api/server/start`, { method: "POST", cache: "no-store" });
          response = await call();
        } catch {
          // Keep original 503 response for standard error handling below.
        }
      }

      if (response.ok) {
        if (response.status === 204) {
          return null as T;
        }
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

export default function LogsPage() {
  const { calendarType } = useCalendarPreference();
  const today = useMemo(() => toInputDate(new Date()), []);
  const [fromDate, setFromDate] = useState(today);
  const [toDate, setToDate] = useState(today);
  const [logs, setLogs] = useState<AppLogItem[]>([]);
  const [total, setTotal] = useState(0);
  const [logPage, setLogPage] = useState(1);
  const [pageSize, setPageSize] = useState(100);
  const [jumpPage, setJumpPage] = useState("");
  const [alerts, setAlerts] = useState<AlertItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expandedRows, setExpandedRows] = useState<Record<string, boolean>>({});
  const [exporting, setExporting] = useState(false);

  // Log rows have no structured device/area/department columns (AppLogEntry is just
  // timestamp/level/source/message/detail) — device serials just show up embedded in the free-text
  // message/detail. So Area/Department/Device filtering here works by matching a selected device's
  // serial number against that free text, using the device list (which does carry area/department)
  // to resolve which serials belong to a given area/department.
  const [devices, setDevices] = useState<Array<{ serialNumber: string; name: string; area: string; department: string }>>([]);
  // Area/Department dropdown options come from the actual Setup list (same source every other
  // page's Area/Department filter uses), not just areas/departments that happen to have a device
  // assigned yet — otherwise a newly-created area with no device in it never shows up as a filter
  // option here even though it's a real, selectable area everywhere else in the app.
  const [areaOptions, setAreaOptions] = useState<string[]>([]);
  const [deptOptions, setDeptOptions] = useState<string[]>([]);
  const [areaFilter, setAreaFilter] = useState("");
  const [deptFilter, setDeptFilter] = useState("");
  const [deviceFilter, setDeviceFilter] = useState("");
  const [levelFilter, setLevelFilter] = useState("");
  const [sourceFilter, setSourceFilter] = useState("");
  const [showAllRawLogs, setShowAllRawLogs] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [showAlerts, setShowAlerts] = useState(true);

  useEffect(() => {
    void (async () => {
      try {
        const payload = await requestJson<unknown>("/api/devices");
        const rows = Array.isArray(payload)
          ? payload
          : Array.isArray((payload as { items?: unknown })?.items)
            ? (payload as { items: unknown[] }).items
            : [];
        const mapped = rows
          .map((item) => {
            const s = item && typeof item === "object" ? (item as Record<string, unknown>) : {};
            const serialNumber = String(s.serialNumber ?? s.SerialNumber ?? "").trim();
            if (!serialNumber) return null;
            return {
              serialNumber,
              name: String(s.name ?? s.Name ?? serialNumber),
              area: String(s.area ?? s.Area ?? "").trim() || "Unassigned",
              department: String(s.department ?? s.Department ?? "").trim() || "Unassigned",
            };
          })
          .filter((x): x is { serialNumber: string; name: string; area: string; department: string } => x !== null);
        setDevices(mapped);
      } catch {
        setDevices([]);
      }
    })();
    void (async () => {
      try {
        const data = await requestJson<{ areas?: Array<{ name?: string }>; departments?: Array<{ name?: string }> }>("/api/personnel/setup");
        const areaNames = Array.isArray(data?.areas) ? data.areas.map((a) => String(a?.name || "").trim()).filter(Boolean) : [];
        const deptNames = Array.isArray(data?.departments) ? data.departments.map((d) => String(d?.name || "").trim()).filter(Boolean) : [];
        setAreaOptions(Array.from(new Set(areaNames)).sort());
        setDeptOptions(Array.from(new Set(deptNames)).sort());
      } catch {
        setAreaOptions([]);
        setDeptOptions([]);
      }
    })();
  }, []);

  const deviceOptions = useMemo(() => {
    return devices.filter((d) => (!areaFilter || d.area === areaFilter) && (!deptFilter || d.department === deptFilter));
  }, [devices, areaFilter, deptFilter]);

  useEffect(() => {
    if (deviceFilter && !deviceOptions.some((d) => d.serialNumber === deviceFilter)) {
      setDeviceFilter("");
    }
  }, [deviceOptions, deviceFilter]);

  const filteredLogs = useMemo(() => {
    let result = logs;

    if (levelFilter) {
      const target = levelFilter.toUpperCase();
      result = result.filter((item) => (item.level || "").toUpperCase().includes(target));
    }

    if (sourceFilter) {
      const target = sourceFilter.toLowerCase();
      result = result.filter((item) => (item.source || "").toLowerCase().includes(target));
    }

    if (searchQuery.trim()) {
      const q = searchQuery.toLowerCase().trim();
      result = result.filter((item) => {
        const haystack = `${item.level ?? ""} ${item.source ?? ""} ${item.message ?? ""} ${item.detail ?? ""}`.toLowerCase();
        return haystack.includes(q);
      });
    }

    if (!areaFilter && !deptFilter && !deviceFilter) {
      return result;
    }

    const matchingSerials = deviceFilter
      ? [deviceFilter]
      : devices
          .filter((d) => (!areaFilter || d.area === areaFilter) && (!deptFilter || d.department === deptFilter))
          .map((d) => d.serialNumber);
    if (matchingSerials.length === 0) {
      return [];
    }
    return result.filter((item) => {
      const haystack = `${item.message ?? ""} ${item.detail ?? ""}`;
      return matchingSerials.some((serial) => haystack.includes(serial));
    });
  }, [logs, devices, areaFilter, deptFilter, deviceFilter, levelFilter, sourceFilter, searchQuery]);

  const txTotalPages = Math.max(1, Math.ceil(total / pageSize));
  const txCurrentPage = Math.min(logPage, txTotalPages);

  const loadLogs = async (
    pageNum?: number,
    customSearch?: string,
    customLevel?: string,
    customSource?: string,
    customPageSize?: number
  ) => {
    setLoading(true);
    setError(null);
    const currentPage = pageNum ?? logPage;
    const currentSize = customPageSize ?? pageSize;
    const s = customSearch !== undefined ? customSearch : searchQuery;
    const l = customLevel !== undefined ? customLevel : levelFilter;
    const src = customSource !== undefined ? customSource : sourceFilter;
    try {
      const qs = new URLSearchParams();
      qs.set("from", fromDate);
      qs.set("to", toDate);
      qs.set("page", String(currentPage));
      qs.set("pageSize", String(currentSize));
      if (s.trim()) qs.set("search", s.trim());
      if (l) qs.set("level", l);
      if (src) qs.set("source", src);
      const data = await requestJson<{ data: AppLogItem[]; total: number }>(`/api/logs?${qs.toString()}`);
      const items = Array.isArray(data?.data) ? data.data : [];
      setLogs(cleanLogs(items, showAllRawLogs));
      setTotal(typeof data?.total === "number" ? data.total : 0);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load logs.");
    } finally {
      setLoading(false);
    }
  };

  const searchRef = useRef(searchQuery);
  searchRef.current = searchQuery;
  const levelRef = useRef(levelFilter);
  levelRef.current = levelFilter;
  const sourceRef = useRef(sourceFilter);
  sourceRef.current = sourceFilter;

  // Debounced search / filter reload
  const isFirstMount = useRef(true);
  useEffect(() => {
    if (isFirstMount.current) {
      isFirstMount.current = false;
      return;
    }
    const timer = setTimeout(() => {
      setLogPage(1);
      void loadLogs(1, searchRef.current, levelRef.current, sourceRef.current);
    }, 350);
    return () => clearTimeout(timer);
  }, [searchQuery, levelFilter, sourceFilter, showAllRawLogs]);

  const filtersRef = useRef({ fromDate, toDate });
  useEffect(() => {
    const prev = filtersRef.current;
    const filtersChanged = prev.fromDate !== fromDate || prev.toDate !== toDate;
    filtersRef.current = { fromDate, toDate };
    if (filtersChanged) {
      setLogPage(1);
      void loadLogs(1, searchRef.current, levelRef.current, sourceRef.current);
    } else {
      void loadLogs(logPage, searchRef.current, levelRef.current, sourceRef.current);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fromDate, toDate, logPage]);

  const loadAlerts = async () => {
    try {
      const rows = await loadAlertFeed();
      setAlerts(rows);
    } catch {
      setAlerts([]);
    }
  };

  useEffect(() => {
    void loadAlerts();
  }, []);

  useEffect(() => {
    if (!autoRefresh) return;
    const interval = setInterval(() => {
      void loadLogs(logPage);
      void loadAlerts();
    }, 15000);
    return () => clearInterval(interval);
  }, [autoRefresh, logPage, fromDate, toDate]);

  const exportLogs = async () => {
    setExporting(true);
    try {
      const exportPageSize = 500;
      const maxPages = 40; // caps export at 20,000 rows for the selected range
      let allItems: AppLogItem[] = [];
      let totalCount = Number.POSITIVE_INFINITY;
      for (let pageNum = 1; pageNum <= maxPages && allItems.length < totalCount; pageNum++) {
        const qs = new URLSearchParams();
        qs.set("from", fromDate);
        qs.set("to", toDate);
        qs.set("page", String(pageNum));
        qs.set("pageSize", String(exportPageSize));
        if (searchQuery.trim()) qs.set("search", searchQuery.trim());
        if (levelFilter) qs.set("level", levelFilter);
        if (sourceFilter) qs.set("source", sourceFilter);
        const data = await requestJson<{ data: AppLogItem[]; total: number }>(`/api/logs?${qs.toString()}`);
        const items = Array.isArray(data?.data) ? data.data : [];
        if (items.length === 0) break;
        allItems = allItems.concat(items);
        totalCount = typeof data?.total === "number" ? data.total : allItems.length;
      }

      let cleaned = cleanLogs(allItems, showAllRawLogs);

      if (levelFilter) {
        const target = levelFilter.toUpperCase();
        cleaned = cleaned.filter((item) => (item.level || "").toUpperCase().includes(target));
      }

      if (sourceFilter) {
        const target = sourceFilter.toLowerCase();
        cleaned = cleaned.filter((item) => (item.source || "").toLowerCase().includes(target));
      }

      if (searchQuery.trim()) {
        const q = searchQuery.toLowerCase().trim();
        cleaned = cleaned.filter((item) => {
          const haystack = `${item.level ?? ""} ${item.source ?? ""} ${item.message ?? ""} ${item.detail ?? ""}`.toLowerCase();
          return haystack.includes(q);
        });
      }

      const matchingSerials = deviceFilter
        ? [deviceFilter]
        : devices
            .filter((d) => (!areaFilter || d.area === areaFilter) && (!deptFilter || d.department === deptFilter))
            .map((d) => d.serialNumber);
      const finalRows = (!areaFilter && !deptFilter && !deviceFilter)
        ? cleaned
        : cleaned.filter((item) => {
            const haystack = `${item.message ?? ""} ${item.detail ?? ""}`;
            return matchingSerials.some((serial) => haystack.includes(serial));
          });

      const csvRows = finalRows.map((item) => [
        formatCalendarDateTime(item.timestampUtc, calendarType),
        item.level || "",
        item.source || "",
        compactText(item.message || ""),
        compactText(item.detail || ""),
      ]);
      downloadCsv(
        `logs_${fromDate}_to_${toDate}.csv`,
        ["Time", "Level", "Source", "Message", "Detail"],
        csvRows,
      );
    } catch (err) {
      alert(err instanceof Error ? err.message : "Failed to export logs.");
    } finally {
      setExporting(false);
    }
  };

  const toggleRow = (key: string) => {
    setExpandedRows((prev) => ({ ...prev, [key]: !prev[key] }));
  };

  return (
    <ModuleShell
      title="Notifications & Logs"
      description="Live alerts for manual punch, requests, device issues, and full application logs."
    >
      <div className="space-y-3">
        <div className="grid gap-3 md:grid-cols-3">
          <div className="rounded-2xl border border-sky-200 bg-[linear-gradient(135deg,#f8fdff,#eef7ff)] px-4 py-3">
            <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-[#5a7b98]">Live Alerts</p>
            <p className="mt-2 text-2xl font-semibold text-[#0d334d]">{alerts.length}</p>
            <p className="mt-1 text-xs text-[#5a7b98]">Recent actionable notices from the system.</p>
          </div>
          <div className="rounded-2xl border border-amber-200 bg-amber-50/80 px-4 py-3">
            <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-amber-700">Warnings</p>
            <p className="mt-2 text-2xl font-semibold text-amber-900">
              {alerts.filter((item) => item.level === "warning").length}
            </p>
            <p className="mt-1 text-xs text-amber-700">Pending requests, offline devices, and critical status changes.</p>
          </div>
          <div className="rounded-2xl border border-emerald-200 bg-emerald-50/80 px-4 py-3">
            <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-emerald-700">Activity</p>
            <p className="mt-2 text-2xl font-semibold text-emerald-900">
              {alerts.filter((item) => item.category === "attendance").length}
            </p>
            <p className="mt-1 text-xs text-emerald-700">Manual punch and attendance-related activity.</p>
          </div>
        </div>

        <div className="rounded-2xl border border-[#d9e8f7] bg-white p-3 shadow-sm">
          <div className="flex items-center justify-between gap-2">
            <div>
              <h3 className="text-sm font-semibold text-[#123a60]">Recent Alerts</h3>
              <p className="text-xs text-[#5b7a96]">This section feeds the top header bell icon.</p>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => setShowAlerts((v) => !v)}
                className="inline-flex items-center gap-1 rounded-lg border border-[#bfd4ea] bg-white px-2.5 py-1.5 text-xs font-semibold text-[#1e4f78] hover:bg-slate-50 transition"
              >
                {showAlerts ? (
                  <>
                    <ChevronUp className="h-3.5 w-3.5" />
                    <span>Hide Alerts</span>
                  </>
                ) : (
                  <>
                    <ChevronDown className="h-3.5 w-3.5" />
                    <span>Show Alerts ({alerts.length})</span>
                  </>
                )}
              </button>
              <button
                type="button"
                onClick={() => void loadAlerts()}
                className="rounded-lg border border-[#1e4f78] bg-[#1e4f78] px-3 py-1.5 text-xs font-semibold text-white hover:bg-[#163d5e] transition"
              >
                Refresh Alerts
              </button>
            </div>
          </div>

          {showAlerts ? (
            <div className="mt-3 space-y-2">
              {alerts.length === 0 ? (
                <div className="rounded-xl border border-[#d9e8f7] bg-[#f8fbff] px-3 py-8 text-center text-sm text-[#5f7f9f]">
                  No live alerts found.
                </div>
              ) : (
                alerts.map((item) => (
                  <div key={item.id} className="rounded-xl border border-[#e7f0fb] bg-[linear-gradient(120deg,#f8fbff,#eef6ff)] px-3 py-3">
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <div>
                        <p className="text-sm font-semibold text-[#123a60]">{item.title}</p>
                        <p className="mt-1 text-xs text-[#5b7a96]">{item.detail}</p>
                      </div>
                      <div className="text-right">
                        <span
                          className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase ${
                            item.level === "warning"
                              ? "bg-amber-100 text-amber-800"
                              : item.level === "success"
                                ? "bg-emerald-100 text-emerald-800"
                                : "bg-sky-100 text-sky-800"
                          }`}
                        >
                          {item.level}
                        </span>
                        <p className="mt-1 text-[11px] text-[#5f7f9f]">
                          {formatCalendarDateTime(item.timestampUtc, calendarType)}
                        </p>
                      </div>
                    </div>
                  </div>
                ))
              )}
            </div>
          ) : null}
        </div>

        <div className="tab-toolbar flex flex-wrap items-end gap-2">
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">From</span>
            <BsDateField value={fromDate} onChange={setFromDate} className="min-w-[160px]" />
          </label>
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">To</span>
            <BsDateField value={toDate} onChange={setToDate} className="min-w-[160px]" />
          </label>
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Level</span>
            <select
              value={levelFilter}
              onChange={(e) => setLevelFilter(e.target.value)}
              className="min-w-[130px] rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-sm text-[#1e4162]"
            >
              <option value="">All Levels</option>
              <option value="ERROR">ERROR / Fatal</option>
              <option value="WARN">WARN / Alert</option>
              <option value="INFO">INFO / Notice</option>
              <option value="DEBUG">DEBUG / Trace</option>
            </select>
          </label>
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Source</span>
            <select
              value={sourceFilter}
              onChange={(e) => setSourceFilter(e.target.value)}
              className="min-w-[150px] rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-sm text-[#1e4162]"
            >
              <option value="">All Sources</option>
              <option value="DeviceAction">Device Actions & Ops</option>
              <option value="DeviceEvent">Device Events & Push</option>
              <option value="Command">Device Commands</option>
              <option value="Attendance">Attendance Punches</option>
              <option value="CardEnrollment">Card & Biometrics</option>
              <option value="Sync">Real-Time Sync</option>
              <option value="Personnel">Personnel Changes</option>
              <option value="Device">Device Status</option>
              <option value="Server">System & Server</option>
            </select>
          </label>
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Search</span>
            <div className="relative min-w-[200px]">
              <Search className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-[#5f7f9f]" />
              <input
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Search text, serial, IP..."
                style={{ paddingLeft: "2.5rem", paddingRight: searchQuery ? "2.25rem" : "0.75rem" }}
                className="w-full rounded-lg border border-[#bfd4ea] bg-white py-2 pl-10 pr-8 text-sm text-[#1e4162] placeholder:text-[#8ea9c4] outline-none focus:border-[#1e4f78] focus:ring-1 focus:ring-[#1e4f78]"
              />
              {searchQuery ? (
                <button
                  type="button"
                  onClick={() => setSearchQuery("")}
                  className="absolute right-2.5 top-1/2 -translate-y-1/2 p-0.5 text-[#5f7f9f] hover:text-[#1e4162] rounded transition"
                  title="Clear search"
                >
                  <X className="h-4 w-4" />
                </button>
              ) : null}
            </div>
          </label>
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Area</span>
            <select
              value={areaFilter}
              onChange={(event) => setAreaFilter(event.target.value)}
              className="min-w-[130px] rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-sm text-[#1e4162]"
            >
              <option value="">All Areas</option>
              {areaOptions.map((area) => <option key={area} value={area}>{area}</option>)}
            </select>
          </label>
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Department</span>
            <select
              value={deptFilter}
              onChange={(event) => setDeptFilter(event.target.value)}
              className="min-w-[140px] rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-sm text-[#1e4162]"
            >
              <option value="">All Departments</option>
              {deptOptions.map((dept) => <option key={dept} value={dept}>{dept}</option>)}
            </select>
          </label>
          <label className="grid gap-1 text-xs text-[#1e4162]">
            <span className="font-semibold uppercase tracking-wide text-[#1e4f78]">Device</span>
            <select
              value={deviceFilter}
              onChange={(event) => setDeviceFilter(event.target.value)}
              className="min-w-[150px] rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-sm text-[#1e4162]"
            >
              <option value="">All Devices</option>
              {deviceOptions.map((d) => <option key={d.serialNumber} value={d.serialNumber}>{d.name}</option>)}
            </select>
          </label>
          <button
            type="button"
            onClick={() => {
              setFromDate(today);
              setToDate(today);
            }}
            className="rounded-lg border border-[#bfd4ea] bg-white px-3 py-2 text-sm font-medium text-[#1e4f78] hover:bg-slate-50 transition"
          >
            Today
          </button>
          <button
            type="button"
            onClick={() => setAutoRefresh((v) => !v)}
            className={`inline-flex items-center gap-1.5 rounded-lg border px-3 py-2 text-sm font-semibold transition ${
              autoRefresh
                ? "border-emerald-500 bg-emerald-50 text-emerald-800 shadow-xs"
                : "border-[#bfd4ea] bg-white text-[#1e4f78] hover:bg-slate-50"
            }`}
            title="Auto-refresh logs and alerts every 15 seconds"
          >
            <span className={`h-2 w-2 rounded-full ${autoRefresh ? "bg-emerald-500 animate-pulse" : "bg-slate-300"}`} />
            {autoRefresh ? "Live (15s)" : "Auto-Refresh"}
          </button>
          <button
            type="button"
            onClick={() => setShowAllRawLogs((v) => !v)}
            className={`inline-flex items-center gap-1.5 rounded-lg border px-3 py-2 text-sm font-semibold transition ${
              showAllRawLogs
                ? "border-indigo-500 bg-indigo-50 text-indigo-800 shadow-xs"
                : "border-[#bfd4ea] bg-white text-[#1e4f78] hover:bg-slate-50"
            }`}
            title="Toggle between Clean View (actionable events) and All Logs (including routine background device polling)"
          >
            {showAllRawLogs ? "All Logs (Raw Traffic)" : "Clean View"}
          </button>
          <button
            type="button"
            onClick={() => void exportLogs()}
            disabled={exporting}
            className="rounded-lg border border-[#1e4f78] bg-white px-3 py-2 text-sm font-semibold text-[#1e4f78] hover:bg-[#eaf3fa] transition disabled:opacity-50"
          >
            {exporting ? "Exporting..." : "Export CSV"}
          </button>
          <button
            type="button"
            onClick={() => {
              void loadLogs(1);
              void loadAlerts();
            }}
            className="inline-flex items-center gap-1.5 rounded-lg border border-[#1e4f78] bg-[#1e4f78] px-3.5 py-2 text-sm font-semibold text-white hover:bg-[#163d5e] transition"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? "animate-spin" : ""}`} />
            {loading ? "Loading..." : "Refresh"}
          </button>
        </div>

        {error ? (
          <div className="rounded-lg border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-sm text-[#8d2f2f]">{error}</div>
        ) : null}

        <div className="flex flex-wrap items-center justify-between gap-3 text-xs text-[#5f7f9f]">
          <div className="flex flex-wrap items-center gap-2">
            <span>
              Showing <strong className="text-slate-800 font-semibold">{total > 0 ? (txCurrentPage - 1) * pageSize + 1 : 0}</strong>–
              <strong className="text-slate-800 font-semibold">{Math.min(txCurrentPage * pageSize, total)}</strong> of{" "}
              <strong className="text-slate-800 font-semibold">{total.toLocaleString()}</strong> log(s)
            </span>
            <span className="text-slate-300">•</span>
            <div className="flex items-center gap-1.5">
              <span>Per page:</span>
              <select
                value={pageSize}
                onChange={(e) => {
                  const newSize = Number(e.target.value);
                  setPageSize(newSize);
                  setLogPage(1);
                  void loadLogs(1, searchQuery, levelFilter, sourceFilter, newSize);
                }}
                className="rounded-md border border-[#bfd4ea] bg-white px-2 py-1 text-xs text-[#1e4162] font-medium outline-none focus:border-[#1e4f78]"
              >
                <option value={50}>50</option>
                <option value={100}>100</option>
                <option value={200}>200</option>
                <option value={500}>500</option>
              </select>
            </div>
          </div>
          <div className="inline-flex items-center gap-1">
            <button
              type="button"
              onClick={() => setLogPage(1)}
              disabled={txCurrentPage === 1 || loading}
              className="rounded-lg border border-[#d0e0f0] bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
              title="First Page"
            >
              « First
            </button>
            <button
              type="button"
              onClick={() => setLogPage((p) => Math.max(1, p - 1))}
              disabled={txCurrentPage === 1 || loading}
              className="inline-flex items-center gap-1 rounded-lg border border-[#d0e0f0] bg-white px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
            >
              <ChevronLeft className="h-3.5 w-3.5" />
              Prev
            </button>
            <span className="px-2 font-medium text-slate-700">
              Page {txCurrentPage} of {txTotalPages}
            </span>
            <button
              type="button"
              onClick={() => setLogPage((p) => Math.min(txTotalPages, p + 1))}
              disabled={txCurrentPage === txTotalPages || loading}
              className="inline-flex items-center gap-1 rounded-lg border border-[#d0e0f0] bg-white px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
            >
              Next
              <ChevronRight className="h-3.5 w-3.5" />
            </button>
            <button
              type="button"
              onClick={() => setLogPage(txTotalPages)}
              disabled={txCurrentPage === txTotalPages || loading}
              className="rounded-lg border border-[#d0e0f0] bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
              title="Last Page"
            >
              Last »
            </button>
          </div>
        </div>

        <div className="overflow-x-auto rounded-xl border border-[#d9e8f7] bg-white">
          <table className="min-w-full text-left text-sm">
            <thead className="bg-[#f4f9ff] text-[#1f4a6e]">
              <tr>
                <th className="px-3 py-2">Time</th>
                <th className="px-3 py-2">Level</th>
                <th className="px-3 py-2">Source</th>
                <th className="px-3 py-2">Message</th>
                <th className="px-3 py-2">Detail</th>
              </tr>
            </thead>
            <tbody>
              {filteredLogs.map((item, index) => (
                (() => {
                  const rowKey = `${item.timestampUtc}-${index}`;
                  const expanded = Boolean(expandedRows[rowKey]);
                  const compactMessage = compactText(item.message || "-");
                  const compactDetail = compactText(item.detail || "-");
                  const hasDetail = compactDetail.length > 0 && compactDetail !== "-";
                  const canExpand = compactMessage.length > 50 || compactDetail.length > 50 || hasDetail;
                  const previewMessage = shorten(compactMessage, 60);
                  const previewDetail = shorten(compactDetail, 70);

                  return (
                    <tr key={rowKey} className="border-t border-[#e7f0fb] align-top hover:bg-slate-50/60 transition-colors">
                      <td className="whitespace-nowrap px-3 py-2 text-xs text-[#2d5579]">
                        {formatCalendarDateTime(item.timestampUtc, calendarType)}
                      </td>
                      <td className="px-3 py-2 text-xs font-semibold">{renderLevelBadge(item.level)}</td>
                      <td className="px-3 py-2 text-xs text-[#2d5579]">{item.source || "-"}</td>
                      <td className="max-w-[380px] px-3 py-2 text-xs text-[#1e4162]">
                        <div
                          title={compactMessage}
                          onClick={() => canExpand && toggleRow(rowKey)}
                          className={`${expanded ? "whitespace-pre-wrap break-words" : "truncate"} ${canExpand ? "cursor-pointer hover:text-blue-700" : ""}`}
                        >
                          {expanded ? compactMessage : previewMessage}
                        </div>
                      </td>
                      <td className="max-w-[560px] px-3 py-2 text-xs text-[#4f6f8d]">
                        <div
                          title={item.detail || compactDetail}
                          onClick={() => canExpand && toggleRow(rowKey)}
                          className={`${expanded ? "whitespace-pre-wrap break-words font-mono text-[11px] bg-slate-50 p-2 rounded-lg border border-slate-200/80 text-slate-800" : "truncate"} ${canExpand ? "cursor-pointer hover:text-blue-700" : ""}`}
                        >
                          {expanded ? item.detail || "-" : previewDetail}
                        </div>
                        {canExpand ? (
                          <button
                            type="button"
                            onClick={() => toggleRow(rowKey)}
                            className="mt-1 rounded-full border border-[#c8dbf0] bg-white px-2 py-0.5 text-[11px] font-semibold text-[#1e4f78] transition hover:bg-[#e9f2ff]"
                          >
                            {expanded ? "Show less" : "Read more"}
                          </button>
                        ) : null}
                      </td>
                    </tr>
                  );
                })()
              ))}
              {filteredLogs.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-3 py-10 text-center text-sm text-[#5f7f9f]">
                    No logs found for the selected range/filters.
                  </td>
                </tr>
              ) : null}
            </tbody>
          </table>
        </div>

        {total > 0 ? (
          <div className="flex flex-wrap items-center justify-between gap-3 text-xs text-[#5f7f9f] border-t border-[#d8e6f4] pt-3">
            <div className="flex items-center gap-2">
              <span>
                Showing <strong className="text-slate-800 font-semibold">{total > 0 ? (txCurrentPage - 1) * pageSize + 1 : 0}</strong>–
                <strong className="text-slate-800 font-semibold">{Math.min(txCurrentPage * pageSize, total)}</strong> of{" "}
                <strong className="text-slate-800 font-semibold">{total.toLocaleString()}</strong> log(s)
              </span>
            </div>

            <div className="inline-flex items-center gap-2">
              <div className="inline-flex items-center gap-1">
                <button
                  type="button"
                  onClick={() => setLogPage(1)}
                  disabled={txCurrentPage === 1 || loading}
                  className="rounded-lg border border-[#d0e0f0] bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                  title="First Page"
                >
                  « First
                </button>
                <button
                  type="button"
                  onClick={() => setLogPage((p) => Math.max(1, p - 1))}
                  disabled={txCurrentPage === 1 || loading}
                  className="inline-flex items-center gap-1 rounded-lg border border-[#d0e0f0] bg-white px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                >
                  <ChevronLeft className="h-3.5 w-3.5" />
                  Prev
                </button>
                <span className="px-2 font-medium text-slate-700">
                  Page {txCurrentPage} of {txTotalPages}
                </span>
                <button
                  type="button"
                  onClick={() => setLogPage((p) => Math.min(txTotalPages, p + 1))}
                  disabled={txCurrentPage === txTotalPages || loading}
                  className="inline-flex items-center gap-1 rounded-lg border border-[#d0e0f0] bg-white px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                >
                  Next
                  <ChevronRight className="h-3.5 w-3.5" />
                </button>
                <button
                  type="button"
                  onClick={() => setLogPage(txTotalPages)}
                  disabled={txCurrentPage === txTotalPages || loading}
                  className="rounded-lg border border-[#d0e0f0] bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40"
                  title="Last Page"
                >
                  Last »
                </button>
              </div>

              <div className="flex items-center gap-1 pl-2 border-l border-slate-200">
                <span className="text-[11px] text-slate-500">Go to:</span>
                <input
                  type="number"
                  min={1}
                  max={txTotalPages}
                  value={jumpPage}
                  onChange={(e) => setJumpPage(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" && jumpPage.trim()) {
                      const target = Math.max(1, Math.min(txTotalPages, parseInt(jumpPage, 10) || 1));
                      setLogPage(target);
                      setJumpPage("");
                    }
                  }}
                  placeholder="Page"
                  className="w-14 rounded-md border border-[#bfd4ea] bg-white px-2 py-1 text-xs text-center outline-none focus:border-[#1e4f78]"
                />
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </ModuleShell>
  );
}
