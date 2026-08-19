"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { MouseEvent } from "react";
import { createPortal } from "react-dom";
import {
  BrushCleaning,
  Clock3,
  Cpu,
  ChevronLeft,
  ChevronRight,
  ListChecks,
  MessageCircle,
  RotateCcw,
  Search,
  Send,
  Settings2,
  Trash2,
  UploadCloud,
  Wifi,
  WifiOff,
  Wrench,
  X,
} from "lucide-react";
import { format } from "date-fns";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDateTime } from "@/components/date/date-display";
import { resolveApiBases } from "@/lib/api-bases";

type DeviceState = "Online" | "Offline";
type DeviceItem = {
  id: string;
  serialNumber: string;
  name: string;
  model?: string;
  ipAddress: string;
  areaCode?: string;
  area?: string;
  location: string;
  department?: string;
  departmentCode?: string;
  state: DeviceState;
  isOnline: boolean;
  lastSeenUtc?: string;
  userQty: number;
  fpQty: number;
  faceQty: number;
  palmQty: number;
  transactionQty: number;
  maxUserQty?: number;
  maxFpQty?: number;
  maxFaceQty?: number;
  maxPalmQty?: number;
  maxTransactionQty?: number;
  cmdQty: number;
  isRegistrationDevice?: boolean;
};

// Columns the user can show/hide from the device table via the preferences (gear) menu.
// (The selection checkbox, Serial Number, Device Name and Action columns are always shown.)
const DEVICE_COLUMN_PREFS_KEY = "e_attendance_device_columns";
const DEVICE_TOGGLEABLE_COLUMNS: { key: string; label: string }[] = [
  { key: "area", label: "Area" },
  { key: "department", label: "Department" },
  { key: "ipAddress", label: "Device IP" },
  { key: "model", label: "Device Model" },
  { key: "state", label: "State" },
  { key: "syncMode", label: "Realtime Sync" },
  { key: "lastActivity", label: "Last Activity" },
  { key: "userQty", label: "User Qty" },
  { key: "fpQty", label: "FP Qty" },
  { key: "faceQty", label: "Face Qty" },
  { key: "palmQty", label: "Palm Qty" },
  { key: "transactionQty", label: "Transaction Qty" },
  { key: "cmdQty", label: "Cmd" },
];
const DEFAULT_DEVICE_COLUMN_PREFS: Record<string, boolean> = Object.fromEntries(
  DEVICE_TOGGLEABLE_COLUMNS.map((column) => [column.key, true]),
);

type CommandItem = {
  id: string;
  deviceSerialNumber: string;
  commandText: string;
  createdUtc: string;
  sentUtc?: string;
  replyText?: string;
  status: "pending" | "sent" | "replied";
};

type DiscoveredDevice = {
  serialNumber: string;
  name: string;
  model?: string;
  ipAddress: string;
  area: string;
  lastActivityUtc: string;
  isOnline: boolean;
};

type AreaItem = {
  id: string;
  code: string;
  name: string;
  remark?: string;
  isDefault?: boolean;
  canDelete?: boolean;
};

type DepartmentItem = {
  id: string;
  code: string;
  name: string;
  areaCode: string;
  areaName?: string;
  isDefault?: boolean;
};

type DeviceTab = "managed" | "commands";
type AddFlowTab = "search" | "details";
type DeviceControlAction =
  | ""
  | "read-info"
  | "device-capacity"
  | "reboot-device"
  | "fetch-all-data"
  | "fetch-all-log-data"
  | "fetch-users"
  | "sync-time"
  | "sync-data"
  | "upgrade-firmware";
type DeviceFetchAction =
  | ""
  | "fetch-all-data"
  | "fetch-all-log-data"
  | "fetch-users";
type DeviceContextMenuState = {
  serial: string;
  deviceId: string;
  x: number;
  y: number;
} | null;
type DeviceInfoSnapshot = {
  title: string;
  serial: string;
  capturedAt: string;
  fields: Array<{ key: string; value: string }>;
  raw: string;
} | null;
type EditDevice = {
  id: string;
  serialNumber: string;
  name: string;
  model: string;
  ipAddress: string;
  areaCode: string;
  departmentCode: string;
  registrationDevice: boolean;
};

type DeviceLimitNotice = {
  message: string;
  allowedDevices?: number;
  currentDeviceCount?: number;
  licenseType?: string;
};

type DeviceServerNotice = {
  host: string;
  port: string;
  base: string;
};

type DeviceSettingsResponse = {
  port?: number;
  frontendPort?: number;
  serverIp?: string;
};

const UPGRADE_WHATSAPP_URL = "https://wa.me/9779814891277";

function getApiBases(): string[] {
  return resolveApiBases({ includeCurrentOrigin: true });
}

function getDeviceServerNotice(): DeviceServerNotice {
  if (typeof window === "undefined") {
    return { host: "-", port: "-", base: "-" };
  }

  const parsedBases = ensureArray<string>(getApiBases())
    .map((base) => {
      try {
        return new URL(base);
      } catch {
        return null;
      }
    })
    .filter((value): value is URL => value !== null);

  const preferred =
    parsedBases.find((url) => !["localhost", "127.0.0.1", "::1", "[::1]"].includes(url.hostname)) ??
    parsedBases[0] ??
    new URL(window.location.origin);

  const host = preferred.hostname || window.location.hostname || "-";
  const port = preferred.port || (preferred.protocol === "https:" ? "443" : "80");

  return {
    host,
    port,
    base: `${host}:${port}`,
  };
}

function normalizeServerIp(value: string | undefined): string {
  const trimmed = value?.trim();
  if (!trimmed) {
    return "";
  }

  return ["::1", "[::1]"].includes(trimmed) ? "127.0.0.1" : trimmed;
}

const presetActions = [
  { action: "check", label: "Check" },
  { action: "info", label: "Info" },
  { action: "reboot", label: "Reboot" },
  { action: "unlock", label: "Unlock" },
  { action: "sync-time", label: "Sync Time" },
  { action: "clear-log", label: "Clear Log" },
  { action: "clear-data", label: "Data Clean" },
  { action: "fetch-all-data", label: "Fetch All" },
] as const;

function statusClass(state: DeviceState) {
  return state === "Online" ? "badge-online" : "badge-offline";
}

function syncModeLabel(isRegistrationDevice?: boolean) {
  return isRegistrationDevice ? "Full (Reg)" : "Logs Only (Non-Reg)";
}

function toDateTimeString(date: Date) {
  return format(date, "yyyy-MM-dd HH:mm:ss");
}

function pickDefaultAreaCode(areas: AreaItem[]): string {
  const explicit = areas.find((a) => a.isDefault || a.code === "1");
  if (explicit) {
    return explicit.code;
  }
  return areas[0]?.code ?? "1";
}

function pickDefaultDepartmentCode(areaCode: string, departments: DepartmentItem[]): string {
  const inArea = departments.filter((d) => d.areaCode === areaCode);
  if (inArea.length === 0) {
    return departments[0]?.code ?? "";
  }
  const explicit = inArea.find((d) => d.isDefault || d.code === "1");
  return explicit?.code ?? inArea[0].code;
}

function ensureArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? value : [];
}

function actualDeviceName(name: string | undefined, serialNumber: string) {
  return name?.trim() || serialNumber;
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? "GET").toUpperCase();
  const isMutation = method !== "GET" && method !== "HEAD";
  let lastError: string | null = null;

  for (const base of getApiBases()) {
    try {
      let response = await fetch(`${base}${path}`, {
        ...init,
        headers: {
          "Content-Type": "application/json",
          ...(init?.headers ?? {}),
        },
        cache: "no-store",
      });

      if (response.status === 503) {
        try {
          await fetch(`${base}/api/server/start`, { method: "POST", cache: "no-store" });
          response = await fetch(`${base}${path}`, {
            ...init,
            headers: {
              "Content-Type": "application/json",
              ...(init?.headers ?? {}),
            },
            cache: "no-store",
          });
        } catch {
          // Keep original 503 handling below.
        }
      }

      if (response.ok) {
        return (await response.json()) as T;
      }

      const text = await response.text();
      lastError = text || `Request failed (${response.status})`;

      // Server responded with an error. Do not fail over to another base for mutations —
      // a retry could duplicate the write (e.g. adding the same device twice).
      if (isMutation) break;
    } catch (error) {
      lastError = error instanceof Error ? error.message : "Failed to fetch";
    }
  }

  throw new Error(lastError ?? "Failed to fetch");
}

function parseDeviceLimitNotice(rawMessage: string): DeviceLimitNotice | null {
  if (!rawMessage) {
    return null;
  }

  try {
    const parsed = JSON.parse(rawMessage) as {
      code?: string;
      message?: string;
      allowedDevices?: number;
      currentDeviceCount?: number;
      licenseType?: string;
    };

    if (parsed?.code !== "device_limit_reached") {
      return null;
    }

    const allowed = typeof parsed.allowedDevices === "number" ? parsed.allowedDevices : undefined;
    const current = typeof parsed.currentDeviceCount === "number" ? parsed.currentDeviceCount : undefined;
    const licenseType = typeof parsed.licenseType === "string" ? parsed.licenseType : undefined;
    const base = parsed.message?.trim() || "Device limit reached for current license.";
    const upgrade = " Please upgrade your license.";
    return {
      message: base.endsWith(".") ? `${base}${upgrade}` : `${base}.${upgrade}`,
      allowedDevices: allowed,
      currentDeviceCount: current,
      licenseType,
    };
  } catch {
    return null;
  }
}

export default function DevicesPage() {
  const { calendarType } = useCalendarPreference();
  const [deviceServerNotice, setDeviceServerNotice] = useState<DeviceServerNotice>(() => getDeviceServerNotice());
  const [tab, setTab] = useState<DeviceTab>("managed");
  const [error, setError] = useState<string | null>(null);
  const [deviceLimitNotice, setDeviceLimitNotice] = useState<DeviceLimitNotice | null>(null);
  const [status, setStatus] = useState<"all" | "online" | "offline">("all");
  const [searchInput, setSearchInput] = useState("");
  const [searchQuery, setSearchQuery] = useState("");
  const [controlAction, setControlAction] = useState<DeviceControlAction>("");
  const [fetchAction, setFetchAction] = useState<DeviceFetchAction>("");

  // Column visibility preferences (persisted in localStorage; gear menu top-right).
  const [colPrefs, setColPrefs] = useState<Record<string, boolean>>(DEFAULT_DEVICE_COLUMN_PREFS);
  const [colMenuOpen, setColMenuOpen] = useState(false);
  const [colMenuPos, setColMenuPos] = useState<{ right: number; top: number } | null>(null);
  const colMenuButtonRef = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(DEVICE_COLUMN_PREFS_KEY);
      if (raw) {
        setColPrefs({ ...DEFAULT_DEVICE_COLUMN_PREFS, ...(JSON.parse(raw) as Record<string, boolean>) });
      }
    } catch {
      // ignore — fall back to defaults
    }
  }, []);

  function updateColPref(key: string, value: boolean) {
    setColPrefs((prev) => {
      const next = { ...prev, [key]: value };
      try {
        window.localStorage.setItem(DEVICE_COLUMN_PREFS_KEY, JSON.stringify(next));
      } catch {
        // ignore persistence errors
      }
      return next;
    });
  }

  function resetColPrefs() {
    setColPrefs(DEFAULT_DEVICE_COLUMN_PREFS);
    try {
      window.localStorage.removeItem(DEVICE_COLUMN_PREFS_KEY);
    } catch {
      // ignore
    }
  }

  const [devices, setDevices] = useState<DeviceItem[]>([]);
  const [sortField, setSortField] = useState<string>("serialNumber");
  const [sortAsc, setSortAsc] = useState<boolean>(true);

  const updateSort = (field: string) => {
    if (sortField === field) {
      setSortAsc((prev) => !prev);
    } else {
      setSortField(field);
      setSortAsc(true);
    }
  };
  const [commands, setCommands] = useState<CommandItem[]>([]);
  const [selectedSerial, setSelectedSerial] = useState<string>("");
  const [selectedDeviceSerials, setSelectedDeviceSerials] = useState<string[]>([]);
  const [contextMenu, setContextMenu] = useState<DeviceContextMenuState>(null);
  const [deviceInfoSnapshot, setDeviceInfoSnapshot] = useState<DeviceInfoSnapshot>(null);

  const [addOpen, setAddOpen] = useState(false);
  const [addTab, setAddTab] = useState<AddFlowTab>("search");
  const [discoveredForAdd, setDiscoveredForAdd] = useState<DiscoveredDevice[]>([]);
  const [areaOptions, setAreaOptions] = useState<AreaItem[]>([]);
  const [departmentOptions, setDepartmentOptions] = useState<DepartmentItem[]>([]);
  const [addSubmitting, setAddSubmitting] = useState(false);
  const [pendingAdd, setPendingAdd] = useState<{
    mode: "discovered" | "manual";
    serialNumber: string;
    ipAddress: string;
    model: string;
    name: string;
    areaCode: string;
    departmentCode: string;
    timeZone: string;
    registrationDevice: boolean;
    clearAllDataOnAdd: boolean;
  } | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  // Shared in-app confirmation dialog — replaces window.confirm() browser popups for
  // reboot/sync/delete so confirmations look and behave consistently with the rest of the app.
  const [confirmState, setConfirmState] = useState<{ message: string; onConfirm: () => void; danger?: boolean } | null>(null);
  const askConfirm = (message: string, onConfirm: () => void, danger = false) => setConfirmState({ message, onConfirm, danger });
  const [dataCleanOpen, setDataCleanOpen] = useState(false);
  const [clearPendingOpen, setClearPendingOpen] = useState(false);
  const [commandQueueOpen, setCommandQueueOpen] = useState(false);
  const [dataCleanMode, setDataCleanMode] = useState<"attendance" | "captured-image" | "all">("attendance");
  const [editOpen, setEditOpen] = useState(false);
  const [firmwareUpgradeOpen, setFirmwareUpgradeOpen] = useState(false);
  const [firmwareFile, setFirmwareFile] = useState<File | null>(null);
  const [firmwareTargetSerials, setFirmwareTargetSerials] = useState<string[]>([]);
  const [firmwareUploading, setFirmwareUploading] = useState(false);
  const [firmwareResult, setFirmwareResult] = useState<{ message: string; command: string; queued: string[] } | null>(null);
  const [editDevice, setEditDevice] = useState<EditDevice | null>(null);
  const [capacityModalData, setCapacityModalData] = useState<{
    open: boolean;
    serialNumber: string;
    deviceName: string;
    model: string;
    loading: boolean;
    user: { current: number; max: number };
    fingerprint: { current: number; max: number };
    face: { current: number; max: number };
    palm: { current: number; max: number };
    attLog: { current: number; max: number };
    card: { current: number; max: number };
    attPhoto: { current: number; max: number };
    isOfflineFetched?: boolean;
  } | null>(null);

  const [customCommand, setCustomCommand] = useState("");
  const [commandForm, setCommandForm] = useState({
    action: "check",
    pin: "",
    startTime: "",
    endTime: "",
    optionKey: "",
    optionValue: "",
  });

  const [totalCount, setTotalCount] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(200);

  const setUiError = useCallback((err: unknown, fallback: string) => {
    const message = err instanceof Error ? err.message : fallback;
    const limit = parseDeviceLimitNotice(message);
    if (limit) {
      setDeviceLimitNotice(limit);
      setError(limit.message);
      return;
    }

    setDeviceLimitNotice(null);
    setError(message || fallback);
  }, []);

  const loadManaged = useCallback(async () => {
    const params = new URLSearchParams();
    if (searchQuery.trim()) params.set("search", searchQuery.trim());
    params.set("page", String(page));
    params.set("pageSize", String(pageSize));

    const result = await requestJson<{ totalCount: number; page: number; pageSize: number; items: DeviceItem[] }>(`/api/devices?${params.toString()}`);
    const rows = ensureArray<DeviceItem>(result.items);
    console.log("API /api/devices response:", result, "wrapped items:", rows);
    setTotalCount(result.totalCount || 0);

    const normalized = rows.map((item) => {
      const onlineFromState = String(item.state ?? "").toLowerCase() === "online";
      const lastSeenMs = item.lastSeenUtc ? new Date(item.lastSeenUtc).getTime() : Number.NaN;
      const stale = Number.isFinite(lastSeenMs) && Date.now() - lastSeenMs > 5 * 60 * 1000;
      const isOnline = (item.isOnline ?? onlineFromState) && !stale;
      return {
        ...item,
        isOnline,
        state: isOnline ? "Online" : "Offline",
      } as DeviceItem;
    });

    setDevices(normalized);
    setSelectedDeviceSerials((prev) => prev.filter((serial) => normalized.some((item) => item.serialNumber === serial)));
    if (selectedSerial && !normalized.some((x) => x.serialNumber === selectedSerial)) {
      setSelectedSerial("");
    }
  }, [searchQuery, selectedSerial, page, pageSize]);

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  function renderPagination() {
    if (totalPages <= 1) return null;

    const pages = [];
    const maxVisible = 5;
    let start = Math.max(1, page - Math.floor(maxVisible / 2));
    let end = Math.min(totalPages, start + maxVisible - 1);
    if (end - start + 1 < maxVisible) {
      start = Math.max(1, end - maxVisible + 1);
    }

    for (let i = start; i <= end; i++) {
      pages.push(i);
    }

    return (
      <div className="mt-4 flex items-center justify-between border-t border-[#e0ecf7] pt-3 text-sm">
        <div className="flex flex-1 justify-between sm:hidden">
          <button
            onClick={() => setPage(p => Math.max(1, p - 1))}
            disabled={page === 1}
            className="relative inline-flex items-center rounded-md border border-[#c5d9ed] bg-white px-4 py-2 text-sm font-medium text-[#1a4a73] hover:bg-[#f0f7ff] disabled:opacity-50"
          >
            Previous
          </button>
          <button
            onClick={() => setPage(p => Math.min(totalPages, p + 1))}
            disabled={page === totalPages}
            className="relative ml-3 inline-flex items-center rounded-md border border-[#c5d9ed] bg-white px-4 py-2 text-sm font-medium text-[#1a4a73] hover:bg-[#f0f7ff] disabled:opacity-50"
          >
            Next
          </button>
        </div>
        <div className="hidden sm:flex sm:flex-1 sm:items-center sm:justify-between">
          <div>
            <p className="text-sm text-[#5a8bb7]">
              Showing <span className="font-medium">{(page - 1) * pageSize + 1}</span> to <span className="font-medium">{Math.min(page * pageSize, totalCount)}</span> of{' '}
              <span className="font-medium">{totalCount}</span> results
            </p>
          </div>
          <div className="flex items-center gap-4">
            <select
              value={pageSize}
              onChange={(e) => {
                setPageSize(Number(e.target.value));
                setPage(1);
              }}
              className="rounded-lg border border-[#c5d9ed] bg-white px-2 py-1 text-xs text-[#1a4a73] outline-none focus:border-[#1a4a73]"
            >
              <option value={100}>100 / page</option>
              <option value={200}>200 / page</option>
              <option value={500}>500 / page</option>
            </select>
            <nav className="isolate inline-flex -space-x-px rounded-md shadow-sm" aria-label="Pagination">
              <button
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page === 1}
                className="relative inline-flex items-center rounded-l-md px-2 py-2 text-[#5a8bb7] ring-1 ring-inset ring-[#c5d9ed] hover:bg-[#f0f7ff] focus:z-20 focus:outline-offset-0 disabled:opacity-50"
              >
                <span className="sr-only">Previous</span>
                <ChevronLeft className="h-4 w-4" />
              </button>
              {pages.map(p => (
                <button
                  key={p}
                  onClick={() => setPage(p)}
                  className={`relative inline-flex items-center px-4 py-2 text-sm font-semibold focus:z-20 ${
                    p === page
                      ? "z-10 bg-[#1a4a73] text-white"
                      : "text-[#1a4a73] ring-1 ring-inset ring-[#c5d9ed] hover:bg-[#f0f7ff]"
                  }`}
                >
                  {p}
                </button>
              ))}
              <button
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page === totalPages}
                className="relative inline-flex items-center rounded-r-md px-2 py-2 text-[#5a8bb7] ring-1 ring-inset ring-[#c5d9ed] hover:bg-[#f0f7ff] focus:z-20 focus:outline-offset-0 disabled:opacity-50"
              >
                <span className="sr-only">Next</span>
                <ChevronRight className="h-4 w-4" />
              </button>
            </nav>
          </div>
        </div>
      </div>
    );
  }

  const loadCommands = useCallback(async () => {
    if (!selectedSerial) {
      setCommands([]);
      return;
    }
    const result = await requestJson<CommandItem[] | { totalCount: number; page: number; pageSize: number; items: CommandItem[] }>(`/api/devices/${encodeURIComponent(selectedSerial)}/commands?take=300`);
    console.log("API /api/devices/{serial}/commands response:", result, "type:", typeof result);
    const rows = Array.isArray(result) ? result : ensureArray<CommandItem>(result.items);
    console.log("Commands after unwrap:", rows);
    setCommands(rows);
  }, [selectedSerial]);

  const loadAll = useCallback(async () => {
    setError(null);
    try {
      await Promise.all([loadManaged(), loadCommands()]);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load device data.");
    }
  }, [loadManaged, loadCommands]);

  useEffect(() => {
    void loadAll();
  }, [loadAll]);

  useEffect(() => {
    let cancelled = false;
    let inFlight = false;

    const tick = async () => {
      if (cancelled || inFlight) {
        return;
      }
      if (typeof document !== "undefined" && document.hidden) {
        return;
      }

      inFlight = true;
      try {
        await loadAll();
      } finally {
        inFlight = false;
      }
    };

    const handle = window.setInterval(() => {
      void tick();
    }, 4000);

    return () => {
      cancelled = true;
      window.clearInterval(handle);
    };
  }, [loadAll]);

  useEffect(() => {
    void loadCommands();
  }, [loadCommands]);

  useEffect(() => {
    let disposed = false;

    const loadDeviceServerSettings = async () => {
      try {
        const fallback = getDeviceServerNotice();
        const settings = await requestJson<DeviceSettingsResponse>("/api/settings");
        if (disposed) {
          return;
        }

        const host = normalizeServerIp(settings.serverIp) || fallback.host || "-";
        const port =
          typeof settings.port === "number" && settings.port > 0
            ? String(settings.port)
            : fallback.port || "-";

        setDeviceServerNotice({
          host,
          port,
          base: `${host}:${port}`,
        });
      } catch {
        if (!disposed) {
          setDeviceServerNotice(getDeviceServerNotice());
        }
      }
    };

    void loadDeviceServerSettings();

    return () => {
      disposed = true;
    };
  }, []);

  useEffect(() => {
    if (!contextMenu) {
      return;
    }

    const close = () => setContextMenu(null);
    const onDown = (event: globalThis.MouseEvent) => {
      if (event.button === 2) return;
      close();
    };
    const onEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        close();
      }
    };

    window.addEventListener("mousedown", onDown);
    window.addEventListener("scroll", close, true);
    window.addEventListener("keydown", onEscape);
    return () => {
      window.removeEventListener("mousedown", onDown);
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("keydown", onEscape);
    };
  }, [contextMenu]);

  useEffect(() => {
    if (!pendingAdd) {
      return;
    }

    const normalizedArea = pendingAdd.areaCode?.trim();
    const validArea = normalizedArea && areaOptions.some((a) => a.code === normalizedArea)
      ? normalizedArea
      : pickDefaultAreaCode(areaOptions);
    const validDepartment = pendingAdd.departmentCode?.trim() && departmentOptions.some((d) => d.code === pendingAdd.departmentCode && d.areaCode === validArea)
      ? pendingAdd.departmentCode
      : pickDefaultDepartmentCode(validArea, departmentOptions);

    if (validArea !== pendingAdd.areaCode || validDepartment !== pendingAdd.departmentCode) {
      setPendingAdd((prev) => (prev ? { ...prev, areaCode: validArea, departmentCode: validDepartment } : prev));
    }
  }, [areaOptions, departmentOptions, pendingAdd]);

  const filteredDevices = useMemo(() => {
    if (status === "all") {
      return devices;
    }
    return devices.filter((d) => (status === "online" ? d.isOnline : !d.isOnline));
  }, [devices, status]);

  const sortedDevices = useMemo(() => {
    const data = [...filteredDevices];
    if (!sortField) return data;
    data.sort((a, b) => {
      const valA = a[sortField as keyof DeviceItem];
      const valB = b[sortField as keyof DeviceItem];

      if (typeof valA === "number" && typeof valB === "number") {
        return sortAsc ? valA - valB : valB - valA;
      }

      let strA = "";
      let strB = "";
      if (valA !== undefined && valA !== null) {
        if (typeof valA === "boolean") strA = valA ? "1" : "0";
        else strA = String(valA).toLowerCase();
      }
      if (valB !== undefined && valB !== null) {
        if (typeof valB === "boolean") strB = valB ? "1" : "0";
        else strB = String(valB).toLowerCase();
      }

      if (strA < strB) return sortAsc ? -1 : 1;
      if (strA > strB) return sortAsc ? 1 : -1;
      return 0;
    });
    return data;
  }, [filteredDevices, sortField, sortAsc]);

  const allFilteredSelected = useMemo(
    () => filteredDevices.length > 0 && filteredDevices.every((d) => selectedDeviceSerials.includes(d.serialNumber)),
    [filteredDevices, selectedDeviceSerials],
  );

  const activeDevice = useMemo(
    () => devices.find((d) => d.serialNumber === selectedSerial) ?? null,
    [devices, selectedSerial],
  );

  const hasTargets = useMemo(() => {
    const valid = selectedDeviceSerials.filter((serial) => devices.some((d) => d.serialNumber === serial));
    const serialValid = !!selectedSerial && devices.some((d) => d.serialNumber === selectedSerial);
    return valid.length > 0 || serialValid;
  }, [selectedDeviceSerials, devices, selectedSerial]);

  const loadAreaOptions = useCallback(async () => {
    try {
      const result = await requestJson<AreaItem[] | { totalCount: number; page: number; pageSize: number; items: AreaItem[] }>("/api/personnel/areas");
      console.log("API /api/personnel/areas response:", result, "type:", typeof result);
      const rows = Array.isArray(result) ? result : ensureArray<AreaItem>(result.items);
      console.log("Area options after unwrap:", rows);
      setAreaOptions(rows);
    } catch (e) {
      console.error("Error loading area options:", e);
      setAreaOptions([]);
    }
  }, []);

  const loadDepartmentOptions = useCallback(async () => {
    try {
      const result = await requestJson<{ totalCount: number; page: number; pageSize: number; items: DepartmentItem[] }>("/api/personnel/departments");
      const rows = ensureArray<DepartmentItem>(result.items);
      setDepartmentOptions(rows);
    } catch {
      setDepartmentOptions([]);
    }
  }, []);

  useEffect(() => {
    void loadAreaOptions();
    void loadDepartmentOptions();
  }, [loadAreaOptions, loadDepartmentOptions]);

  const loadDiscoveredForAdd = useCallback(async () => {
    const result = await requestJson<DiscoveredDevice[] | { totalCount: number; page: number; pageSize: number; items: DiscoveredDevice[] }>("/api/devices/discovered");
    console.log("API /api/devices/discovered response:", result, "type:", typeof result);
    const rows = Array.isArray(result) ? result : ensureArray<DiscoveredDevice>(result.items);
    console.log("Discovered devices after unwrap:", rows);
    setDiscoveredForAdd(rows);
  }, []);

  const openAddFlow = async () => {
    setAddOpen(true);
    setAddTab("search");
    setPendingAdd(null);
    setError(null);
    try {
      await Promise.all([loadAreaOptions(), loadDepartmentOptions(), loadDiscoveredForAdd()]);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load available devices.");
    }
  };

  const closeAddFlow = () => {
    setAddOpen(false);
    setAddTab("search");
    setPendingAdd(null);
  };

  const chooseDiscoveredDevice = (device: DiscoveredDevice) => {
    const matchedArea = areaOptions.find((a) => (device.area || "").trim().toLowerCase() === a.name.toLowerCase());
    const areaCode = matchedArea?.code ?? pickDefaultAreaCode(areaOptions);
    const departmentCode = pickDefaultDepartmentCode(areaCode, departmentOptions);
    setPendingAdd({
      mode: "discovered",
      serialNumber: device.serialNumber,
      ipAddress: device.ipAddress ?? "",
      model: device.model || "",
      name: device.name?.trim() || "",
      areaCode,
      departmentCode,
      timeZone: getDeviceTimeZoneValue(),
      registrationDevice: true,
      clearAllDataOnAdd: false,
    });
    setAddTab("details");
  };

  const startManualAdd = () => {
    const areaCode = pickDefaultAreaCode(areaOptions);
    const departmentCode = pickDefaultDepartmentCode(areaCode, departmentOptions);
    setPendingAdd({
      mode: "manual",
      serialNumber: "",
      ipAddress: "",
      model: "",
      name: "",
      areaCode,
      departmentCode,
      timeZone: getDeviceTimeZoneValue(),
      registrationDevice: false,
      clearAllDataOnAdd: false,
    });
    setAddTab("details");
  };

  const queueRawCommand = async (serial: string, command: string) => {
    await requestJson(`/api/devices/${encodeURIComponent(serial)}/commands`, {
      method: "POST",
      body: JSON.stringify({ command }),
    });
  };

  const queueRegistrationImportCommands = async (serial: string) => {
    await queuePreset(serial, "check");
    await queueRawCommand(serial, "DATA QUERY USERINFO");
    await queueRawCommand(serial, "DATA QUERY USERINFO PIN=*");
    await queueRawCommand(serial, "DATA QUERY BIODATA");
    await queueRawCommand(serial, "DATA QUERY FINGERTMP");
    await queueRawCommand(serial, "DATA QUERY FACE");
    await queueRawCommand(serial, "DATA QUERY BIOPHOTO");
    await queueRawCommand(serial, "DATA QUERY USERPIC");
  };

  const triggerFetchAllData = async (serial: string, includeAttendanceLogs: boolean) => {
    await requestJson(`/api/devices/${encodeURIComponent(serial)}/fetch-all`, {
      method: "POST",
      body: JSON.stringify({ includeAttendanceLogs }),
    });
  };

  const submitAddFromDiscovered = async () => {
    if (!pendingAdd) {
      return;
    }

    const serialNumber = pendingAdd.serialNumber.trim();
    if (!serialNumber) {
      setError("Device serial number is required.");
      return;
    }

    if (!pendingAdd.areaCode.trim()) {
      setError("Area is required.");
      return;
    }

    if (!pendingAdd.departmentCode.trim()) {
      setError("Department is required.");
      return;
    }

    const selectedArea = areaOptions.find((a) => a.code === pendingAdd.areaCode);
    const selectedDepartment = departmentOptions.find((d) => d.code === pendingAdd.departmentCode);
    if (!selectedArea) {
      setError("Selected area is invalid.");
      return;
    }
    if (!selectedDepartment) {
      setError("Selected department is invalid.");
      return;
    }

    setAddSubmitting(true);
    setError(null);
    try {
      let serialForPostCommand = serialNumber;

      if (pendingAdd.mode === "discovered") {
        const created = await requestJson<{ id: string; serialNumber: string; name: string }>(`/api/devices/discovered/${encodeURIComponent(serialNumber)}/add`, {
          method: "POST",
        });

        await requestJson(`/api/devices/${created.id}/info`, {
          method: "PUT",
          body: JSON.stringify({
            name: pendingAdd.name,
            model: pendingAdd.model,
            ipAddress: pendingAdd.ipAddress,
            location: selectedArea.name,
            areaCode: selectedArea.code,
            departmentCode: selectedDepartment.code,
            registrationDevice: pendingAdd.registrationDevice,
            clearAllDataOnAdd: pendingAdd.clearAllDataOnAdd,
          }),
        });
        serialForPostCommand = created.serialNumber || serialNumber;
      } else {
        const created = await requestJson<{ id: string; serialNumber: string; name: string }>("/api/devices", {
          method: "POST",
          body: JSON.stringify({
            serialNumber,
            name: pendingAdd.name.trim() || serialNumber,
            model: pendingAdd.model.trim() || "None",
            ipAddress: pendingAdd.ipAddress.trim(),
            location: selectedArea.name,
            areaCode: selectedArea.code,
            departmentCode: selectedDepartment.code,
            registrationDevice: pendingAdd.registrationDevice,
            clearAllDataOnAdd: pendingAdd.clearAllDataOnAdd,
          }),
        });
        serialForPostCommand = created.serialNumber || serialNumber;
      }

      await queuePreset(serialForPostCommand, "info");

      await requestJson(`/api/devices/${encodeURIComponent(serialForPostCommand)}/commands/preset`, {
        method: "POST",
        body: JSON.stringify({
          action: "sync-time",
          timeZone: pendingAdd.timeZone || getDeviceTimeZoneValue(),
        }),
      });

      if (pendingAdd.clearAllDataOnAdd) {
        // Clear all device data and do NOT fetch anything — even for a registration device.
        await queuePreset(serialForPostCommand, "clear-data");
      } else if (pendingAdd.registrationDevice) {
        await triggerFetchAllData(serialForPostCommand, true);
      }

      setAddOpen(false);
      setPendingAdd(null);
      setAddTab("search");
      await loadAll();
    } catch (err) {
      setUiError(err, "Failed to add device.");
    } finally {
      setAddSubmitting(false);
    }
  };

  const runDataClean = async (mode: "attendance" | "captured-image" | "all") => {
    if (!selectedSerial) {
      setError("Select a device first.");
      return;
    }

    const mapping = {
      attendance: { action: "clear-log", label: "Clear Attendance Data" },
      "captured-image": { action: "clear-photo", label: "Clear Captured Image" },
      all: { action: "clear-data", label: "Clear All Data" },
    } as const;

    const pick = mapping[mode];
    // No extra confirm here — the Data Clean modal (Cancel/Clean buttons) is itself the
    // confirmation step; a second native popup on top of it was a redundant double-confirmation.
    await queuePreset(selectedSerial, pick.action);
  };

  const syncUsersToDevice = async (serial: string) => {
    await requestJson(`/api/device/sync/${encodeURIComponent(serial)}`, {
      method: "POST",
    });
  };

  const executeDataClean = async () => {
    await runDataClean(dataCleanMode);
    setDataCleanOpen(false);
  };

  const executeClearPending = async () => {
    const targets = selectedDeviceSerials.length > 0
      ? selectedDeviceSerials
      : (selectedSerial ? [selectedSerial] : []);
    if (targets.length === 0) {
      setError("Select at least one device first.");
      return;
    }
    for (const serial of targets) {
      await clearPending(serial, false);
    }
    await loadManaged();
    if (selectedSerial) {
      await loadCommands();
    }
    setClearPendingOpen(false);
  };

  const openCommandQueue = async () => {
    setCommandQueueOpen(true);
    await loadCommands();
  };

  const submitEditDevice = async () => {
    if (!editDevice) {
      return;
    }

    if (!editDevice.areaCode.trim()) {
      setError("Area is required.");
      return;
    }
    if (!editDevice.departmentCode.trim()) {
      setError("Department is required.");
      return;
    }

    const selectedArea = areaOptions.find((a) => a.code === editDevice.areaCode);
    const selectedDepartment = departmentOptions.find((d) => d.code === editDevice.departmentCode);
    if (!selectedArea || !selectedDepartment) {
      setError("Selected area/department is invalid.");
      return;
    }

    try {
      await requestJson(`/api/devices/${editDevice.id}/info`, {
        method: "PUT",
        body: JSON.stringify({
          name: editDevice.name,
          model: editDevice.model,
          ipAddress: editDevice.ipAddress,
          location: selectedArea.name,
          areaCode: selectedArea.code,
          departmentCode: selectedDepartment.code,
          registrationDevice: editDevice.registrationDevice,
        }),
      });
      setEditOpen(false);
      setEditDevice(null);
      await loadManaged();
    } catch (err) {
      setUiError(err, "Failed to update device.");
    }
  };

  const connectDisconnect = async (device: DeviceItem, connect: boolean) => {
    try {
      if (connect) {
        try {
          await requestJson(`/api/devices/${encodeURIComponent(device.serialNumber)}/commands/force-poll`, { method: "POST" });
        } catch {
          // Ignore wake errors; online status check below is authoritative.
        }

        let isReachable = false;
        try {
          const rows = ensureArray<DeviceItem>(await requestJson<DeviceItem[]>(`/api/devices?search=${encodeURIComponent(device.serialNumber)}`));
          const probed = rows.find((item) => item.id === device.id || item.serialNumber === device.serialNumber);
          isReachable = Boolean(probed?.isOnline);
        } catch {
          isReachable = false;
        }

        if (!isReachable) {
          await loadManaged();
          setError(`Device ${device.serialNumber} is offline or unavailable. Connect failed.`);
          return;
        }
      }

      await requestJson(`/api/devices/${device.id}/${connect ? "connect" : "disconnect"}`, { method: "POST" });
      await loadManaged();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to change device state.");
    }
  };

  const deleteDevice = async (id: string) => {
    try {
      await requestJson(`/api/devices/${id}`, { method: "DELETE" });
      await loadAll();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete device.");
    }
  };

  const sendCustomCommand = async () => {
    if (!selectedSerial || !customCommand.trim()) {
      return;
    }

    try {
      await requestJson(`/api/devices/${encodeURIComponent(selectedSerial)}/commands`, {
        method: "POST",
        body: JSON.stringify({ command: customCommand.trim() }),
      });
      setCustomCommand("");
      await loadCommands();
      await loadManaged();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to queue command.");
    }
  };

  const queuePreset = async (
    serial: string,
    action: string,
    overrides?: {
      pin?: string | null;
      startTime?: string | null;
      endTime?: string | null;
      optionKey?: string | null;
      optionValue?: string | null;
      timeZone?: string | null;
    },
  ) => {
    const now = new Date();
    const dayBefore = new Date(now.getTime() - 24 * 60 * 60 * 1000);

    try {
      await requestJson(`/api/devices/${encodeURIComponent(serial)}/commands/preset`, {
        method: "POST",
        body: JSON.stringify({
          action,
          pin: (overrides?.pin ?? commandForm.pin) || null,
          startTime: (overrides?.startTime ?? commandForm.startTime) || toDateTimeString(dayBefore),
          endTime: (overrides?.endTime ?? commandForm.endTime) || toDateTimeString(now),
          optionKey: (overrides?.optionKey ?? commandForm.optionKey) || null,
          optionValue: (overrides?.optionValue ?? commandForm.optionValue) || null,
          timeZone: overrides?.timeZone || null,
        }),
      });
      if (serial === selectedSerial) {
        await loadCommands();
      }
      await loadManaged();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to queue preset command.");
    }
  };

  const getControlTargets = () => {
    const valid = selectedDeviceSerials.filter((serial) => devices.some((d) => d.serialNumber === serial));
    if (valid.length > 0) {
      return valid;
    }
    return selectedSerial ? [selectedSerial] : [];
  };

  const openFirmwareUpgrade = () => {
    const targets = getControlTargets();
    setFirmwareTargetSerials(targets);
    setFirmwareFile(null);
    setFirmwareResult(null);
    setFirmwareUpgradeOpen(true);
  };

  const submitFirmwareUpgrade = async () => {
    if (!firmwareFile) {
      setError("Please select a firmware file.");
      return;
    }
    setFirmwareUploading(true);
    setError(null);
    try {
      const formData = new FormData();
      formData.append("file", firmwareFile);
      if (firmwareTargetSerials.length > 0) {
        formData.append("serials", firmwareTargetSerials.join(","));
      }
      let lastError: string | null = null;
      for (const base of getApiBases()) {
        try {
          const res = await fetch(`${base}/api/devices/firmware-upgrade`, {
            method: "POST",
            body: formData,
            cache: "no-store",
          });
          if (res.ok) {
            const data = await res.json() as { message: string; command: string; queued: string[] };
            setFirmwareResult(data);
            await loadManaged();
            return;
          }
          const txt = await res.text();
          lastError = txt || `Request failed (${res.status})`;
          break;
        } catch (err) {
          lastError = err instanceof Error ? err.message : "Failed to fetch";
        }
      }
      setError(lastError ?? "Failed to push firmware upgrade.");
    } finally {
      setFirmwareUploading(false);
    }
  };

  const getDeviceTimeZoneValue = () => "345";


  const parseCommandFields = (replyText: string) => {
    const pairs: Array<{ key: string; value: string }> = [];
    const seen = new Set<string>();
    const ignored = new Set(["id", "return", "cmd"]);

    const pushPair = (rawKey: string, rawValue: string) => {
      const key = rawKey.trim();
      const value = rawValue.trim();
      if (!key || !value) {
        return;
      }

      const normalizedKey = key.toLowerCase();
      if (ignored.has(normalizedKey)) {
        return;
      }
      if (seen.has(`${normalizedKey}:${value}`)) {
        return;
      }

      seen.add(`${normalizedKey}:${value}`);
      pairs.push({ key, value });
    };

    const parseToken = (token: string) => {
      const text = token.trim();
      if (!text) {
        return;
      }

      const eqIndex = text.indexOf("=");
      const colonIndex = text.indexOf(":");
      let splitIndex = -1;

      if (eqIndex > 0 && colonIndex > 0) {
        splitIndex = Math.min(eqIndex, colonIndex);
      } else {
        splitIndex = Math.max(eqIndex, colonIndex);
      }

      if (splitIndex <= 0 || splitIndex >= text.length - 1) {
        return;
      }

      pushPair(text.slice(0, splitIndex), text.slice(splitIndex + 1));
    };

    const lines = replyText.split(/\r?\n/);
    for (const lineRaw of lines) {
      const line = lineRaw.trim();
      if (!line) {
        continue;
      }

      if (line.includes("&")) {
        line.split("&").forEach(parseToken);
        continue;
      }

      if (line.includes("\t")) {
        line.split("\t").forEach(parseToken);
        continue;
      }

      if (line.includes(",")) {
        line.split(",").forEach(parseToken);
        continue;
      }

      parseToken(line);
    }

    // After parsing all tokens, try to group Max vs Count fields for capacity display
    const finalPairs: Array<{ key: string; value: string }> = [];
    const processedKeys = new Set<string>();

    const findValue = (k: string) => pairs.find(p => p.key.toLowerCase() === k.toLowerCase())?.value;

    const labelMap: Record<string, string> = {
      user: "User",
      fingertmp: "Fingerprint",
      fp: "Fingerprint",
      face: "Face",
      palm: "Palm",
      attlog: "Attendance Log",
      transaction: "Transaction",
      password: "Password",
      card: "Card",
      photo: "Photo",
      fpphoto: "Finger Photo",
      userpic: "User Picture",
      admin: "Admin",
      group: "Group",
      timezone: "Timezone",
      holiday: "Holiday",
    };

    const getLabel = (base: string) => {
      const b = base.toLowerCase();
      if (labelMap[b]) return labelMap[b];
      // Try removing "count" suffix
      const noCount = b.endsWith("count") ? b.substring(0, b.length - 5) : b;
      if (labelMap[noCount]) return labelMap[noCount];
      return b.charAt(0).toUpperCase() + b.slice(1);
    };

    const pairsToProcess = [...pairs];
    for (const p of pairsToProcess) {
      const key = p.key.toLowerCase();
      if (processedKeys.has(key)) continue;

      let usedVal: string | undefined;
      let totalVal: string | undefined;
      let baseKey = "";

      // Pattern 1: Max[Base]Count vs [Base]Count
      if (key.startsWith("max") && key.endsWith("count")) {
        totalVal = p.value;
        baseKey = key.substring(3, key.length - 5);
        usedVal = findValue(baseKey + "count") || findValue(baseKey);
      } 
      // Pattern 2: [Base]Count vs Max[Base]Count
      else if (key.endsWith("count") && !key.startsWith("max")) {
        usedVal = p.value;
        baseKey = key.substring(0, key.length - 5);
        totalVal = findValue("max" + key) || findValue("max" + baseKey);
      }
      // Pattern 3: Max[Base] vs [Base]
      else if (key.startsWith("max")) {
        totalVal = p.value;
        baseKey = key.substring(3);
        usedVal = findValue(baseKey) || findValue(baseKey + "count");
      }

      if (usedVal !== undefined && totalVal !== undefined) {
        const label = getLabel(baseKey);
        finalPairs.push({ key: `${label} Capacity`, value: `${usedVal} / ${totalVal}` });
        processedKeys.add(key);
        // Find and mark the counterpart as processed
        const usedKey = (baseKey + "count").toLowerCase();
        const usedKeyShort = baseKey.toLowerCase();
        const totalKeyMax = ("max" + baseKey + "count").toLowerCase();
        const totalKeyShort = ("max" + baseKey).toLowerCase();
        
        pairs.forEach(pair => {
          const lk = pair.key.toLowerCase();
          if (lk === usedKey || lk === usedKeyShort || lk === totalKeyMax || lk === totalKeyShort) {
            processedKeys.add(lk);
          }
        });
        continue;
      }

      finalPairs.push(p);
      processedKeys.add(key);
    }

    return finalPairs;
  };

  const captureLatestCommandReply = async (
    serial: string,
    commandText: "INFO" | "CAPACITY",
    retries = 0,
    shouldSetSnapshot = true
  ) => {
    let attempt = 0;
    while (attempt <= retries) {
      if (attempt > 0) {
        await new Promise((r) => setTimeout(r, 1000));
      }
      const rows = ensureArray<CommandItem>(await requestJson<CommandItem[]>(`/api/devices/${encodeURIComponent(serial)}/commands?take=300`));
      const targetReply = rows.find((item) => item.commandText.trim().toUpperCase().startsWith(commandText) && item.replyText);
      if (targetReply?.replyText) {
        const fields = parseCommandFields(targetReply.replyText);
        if (shouldSetSnapshot) {
          setDeviceInfoSnapshot({
            title: commandText === "CAPACITY" ? "Device Capacity" : "Device Information",
            serial,
            capturedAt: new Date().toISOString(),
            fields,
            raw: targetReply.replyText,
          });
        }
        return { found: true as const, fields };
      }
      attempt++;
    }
    return { found: false as const, fields: undefined };
  };

  const runControlActionForSerial = async (serial: string, action: Exclude<DeviceControlAction, "">) => {
    if (action === "read-info") {
      await queuePreset(serial, "info");
      await loadCommands();
      const { found, fields } = await captureLatestCommandReply(serial, "INFO", 5);
      if (!found) {
        setError("INFO command queued. Device details will appear after device replies.");
      } else if (fields && fields.length > 0) {
        const findField = (...keys: string[]) => {
          for (const key of keys) {
            const f = fields.find((item) => item.key.toLowerCase() === key.toLowerCase());
            if (f?.value) return f.value;
          }
          return undefined;
        };
        const deviceName = findField("DeviceName", "~DeviceName", "DevName", "~DevName", "Name", "Alias", "TerminalName");
        const deviceModel = findField("DeviceModel", "~DeviceModel", "Model", "~Model", "DeviceType", "ProductType", "DevType", "TerminalType");
        if (deviceName || deviceModel) {
          const targetDevice = devices.find((d) => d.serialNumber === serial);
          if (targetDevice) {
            try {
              // Preserve the editable name; use the fetched name as model when firmware exposes one shared value.
              await requestJson(`/api/devices/${targetDevice.id}/info`, {
                method: "PUT",
                body: JSON.stringify({
                  name: targetDevice.name,
                  model: deviceModel || targetDevice.model || "",
                  ipAddress: targetDevice.ipAddress,
                  location: targetDevice.area || targetDevice.location,
                  areaCode: targetDevice.areaCode,
                  departmentCode: targetDevice.departmentCode,
                  registrationDevice: targetDevice.isRegistrationDevice,
                }),
              });
              await loadManaged();
            } catch {
              // Silent: best-effort update from INFO reply
            }
          }
        }
      }
    } else if (action === "device-capacity") {
      const targetDevice = devices.find((d) => d.serialNumber === serial);
      const initialUserMax = targetDevice?.maxUserQty ?? 10000;
      const initialFpMax = targetDevice?.maxFpQty ?? 10000;
      const initialFaceMax = targetDevice?.maxFaceQty ?? 6000;
      const initialPalmMax = targetDevice?.maxPalmQty ?? 3000;
      const initialAttMax = targetDevice?.maxTransactionQty ?? 200000;

      setCapacityModalData({
        open: true,
        serialNumber: serial,
        deviceName: targetDevice?.name ?? serial,
        model: targetDevice?.model ?? "",
        loading: true,
        user: { current: targetDevice?.userQty ?? 0, max: 10000 },
        fingerprint: { current: targetDevice?.fpQty ?? 0, max: 6000 },
        face: { current: targetDevice?.faceQty ?? 0, max: 6000 },
        palm: { current: targetDevice?.palmQty ?? 0, max: 3000 },
        attLog: { current: targetDevice?.transactionQty ?? 0, max: 200000 },
        card: { current: 0, max: 10000 },
        attPhoto: { current: 0, max: 7500 },
      });

      try {
        await queuePreset(serial, "info");
        await loadCommands();
        const { found, fields } = await captureLatestCommandReply(serial, "INFO", 8, false);
        if (found && fields && fields.length > 0) {
          const findField = (...keys: string[]) => {
            for (const key of keys) {
              const f = fields.find((item) => item.key.toLowerCase() === key.toLowerCase());
              if (f?.value) return f.value;
            }
            return undefined;
          };

          const parseVal = (str: string | undefined, fallback: number) => {
            if (!str) return fallback;
            const parsed = parseInt(str, 10);
            return isNaN(parsed) ? fallback : parsed;
          };

          const currentUser = parseVal(findField("UserCount", "UserQty"), targetDevice?.userQty ?? 0);
          const currentFp = parseVal(findField("FPCount", "FpQty", "FingerprintCount"), targetDevice?.fpQty ?? 0);
          const currentFace = parseVal(findField("FaceCount", "FaceQty"), targetDevice?.faceQty ?? 0);
          const currentPalm = parseVal(findField("PvCount", "PalmCount", "PalmQty"), targetDevice?.palmQty ?? 0);
          const currentAtt = parseVal(findField("AttLogCount", "TransactionQty", "TransactionCount"), targetDevice?.transactionQty ?? 0);
          const currentCard = parseVal(findField("UserCardCount", "CardCount"), 0);
          const currentPhoto = parseVal(findField("ATTPhotoCount", "AttPhotoCount"), 0);

          setCapacityModalData({
            open: true,
            serialNumber: serial,
            deviceName: targetDevice?.name ?? serial,
            model: targetDevice?.model ?? "",
            loading: false,
            user: { current: currentUser, max: 10000 },
            fingerprint: { current: currentFp, max: 6000 },
            face: { current: currentFace, max: 6000 },
            palm: { current: currentPalm, max: 3000 },
            card: { current: currentCard, max: 10000 },
            attLog: { current: currentAtt, max: 200000 },
            attPhoto: { current: currentPhoto, max: 7500 },
            isOfflineFetched: false,
          });
        } else {
          setCapacityModalData((old) => old ? { ...old, loading: false, isOfflineFetched: true } : null);
        }
      } catch (err) {
        setCapacityModalData((old) => old ? { ...old, loading: false, isOfflineFetched: true } : null);
      }
      await loadManaged();
    } else if (action === "reboot-device") {
      await queuePreset(serial, "reboot");
    } else if (action === "fetch-all-data" || action === "fetch-all-log-data" || action === "fetch-users") {
      if (action === "fetch-all-log-data") {
        await queuePreset(serial, "query-attlog", {
          startTime: "2000-01-01 00:00:00",
          endTime: toDateTimeString(new Date()),
        });
      }
      if (action === "fetch-all-data" || action === "fetch-users") {
        await triggerFetchAllData(serial, action === "fetch-all-data");
      }
    } else if (action === "sync-time") {
      const localNow = toDateTimeString(new Date());
      await queueRawCommand(serial, "SET OPTIONS TimeZone=345");
      await queueRawCommand(serial, `SHELL date -s "${localNow}"`);
      await queueRawCommand(serial, "SHELL hwclock -w");
      await queueRawCommand(serial, "RELOAD OPTIONS");
      await queuePreset(serial, "check");
    } else if (action === "sync-data") {
      await syncUsersToDevice(serial);
      const localNow = toDateTimeString(new Date());
      await queueRawCommand(serial, "SET OPTIONS TimeZone=345");
      await queueRawCommand(serial, `SHELL date -s "${localNow}"`);
      await queueRawCommand(serial, "SHELL hwclock -w");
      // Deliberately NOT queuing "RELOAD OPTIONS" here (unlike sync-time above): it reboots the
      // device, which wipes any not-yet-delivered commands from the queue. syncUsersToDevice can
      // queue a large batch (USERINFO + USERPIC + face template + fingerprint per employee) that
      // takes several poll cycles to fully deliver — a reload firing before that batch drains
      // wipes out whatever hadn't been sent yet, which is why photos/biometrics silently never
      // arrived even though the sync reported success.
      await queuePreset(serial, "check");
    }
  };

  const executeControlAction = async (actionInput: DeviceControlAction) => {
    if (!actionInput) {
      return;
    }

    const targets = getControlTargets();
    if (targets.length === 0) {
      setError("Select a device first.");
      return;
    }

    const runNow = async () => {
      try {
        for (const serial of targets) {
          await runControlActionForSerial(serial, actionInput);
        }
        setControlAction("");
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to execute device control action.");
      }
    };

    if (actionInput === "reboot-device") {
      askConfirm("Reboot selected device now?", () => void runNow(), true);
      return;
    }
    if (actionInput === "sync-data") {
      askConfirm("Sync software user data to selected device now?", () => void runNow());
      return;
    }

    await runNow();
  };

  const toggleDeviceSelection = (serial: string, checked: boolean) => {
    setSelectedDeviceSerials((prev) => {
      if (checked) {
        return prev.includes(serial) ? prev : [...prev, serial];
      }
      return prev.filter((item) => item !== serial);
    });
    if (checked && !selectedSerial) {
      setSelectedSerial(serial);
    }
  };

  const toggleAllFilteredSelection = (checked: boolean) => {
    setSelectedDeviceSerials((prev) => {
      const filteredSerials = filteredDevices.map((d) => d.serialNumber);
      if (checked) {
        return Array.from(new Set([...prev, ...filteredSerials]));
      }
      return prev.filter((serial) => !filteredSerials.includes(serial));
    });
    if (checked && !selectedSerial && filteredDevices.length > 0) {
      setSelectedSerial(filteredDevices[0].serialNumber);
    }
  };

  const onDeviceRightClick = (event: MouseEvent, device: DeviceItem) => {
    event.preventDefault();
    event.stopPropagation();
    const menuWidth = 220;
    const menuHeight = 360;
    const maxX = Math.max(8, window.innerWidth - menuWidth - 8);
    const maxY = Math.max(8, window.innerHeight - menuHeight - 8);
    setSelectedSerial(device.serialNumber);
    setContextMenu({
      serial: device.serialNumber,
      deviceId: device.id,
      x: Math.min(Math.max(event.clientX, 8), maxX),
      y: Math.min(Math.max(event.clientY, 8), maxY),
    });
  };

  const runContextAction = async (action: Exclude<DeviceControlAction, ""> | "clear-pending" | "delete-device") => {
    if (!contextMenu) {
      return;
    }

    const current = contextMenu;
    setContextMenu(null);

    const withErrorHandling = (fn: () => Promise<void>) => async () => {
      try {
        await fn();
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to run context action.");
      }
    };

    if (action === "clear-pending") {
      await withErrorHandling(() => clearPending(current.serial))();
      return;
    }

    if (action === "delete-device") {
      askConfirm(
        `Delete device ${current.serial}?`,
        () => void withErrorHandling(() => deleteDevice(current.deviceId))(),
        true
      );
      return;
    }

    if (action === "reboot-device") {
      askConfirm(
        `Reboot device ${current.serial}?`,
        () => void withErrorHandling(() => runControlActionForSerial(current.serial, action))(),
        true
      );
      return;
    }

    if (action === "sync-data") {
      askConfirm(
        `Sync software data to ${current.serial}?`,
        () => void withErrorHandling(() => runControlActionForSerial(current.serial, action))()
      );
      return;
    }

    await withErrorHandling(() => runControlActionForSerial(current.serial, action))();
  };

  const clearPending = async (serial: string, reload = true) => {
    try {
      await requestJson(`/api/devices/${encodeURIComponent(serial)}/commands/clear`, { method: "POST" });
      if (reload && serial === selectedSerial) {
        await loadCommands();
      }
      if (reload) {
        await loadManaged();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to clear pending commands.");
    }
  };

  const openEditForDevice = (device: DeviceItem) => {
    const fallbackAreaCode = pickDefaultAreaCode(areaOptions);
    const selectedAreaCode = device.areaCode || fallbackAreaCode;
    const selectedDepartmentCode = device.departmentCode || pickDefaultDepartmentCode(selectedAreaCode, departmentOptions);

    setSelectedSerial(device.serialNumber);
    setEditDevice({
      id: device.id,
      serialNumber: device.serialNumber,
      name: device.name || "",
      model: device.model || "",

      ipAddress: device.ipAddress ?? "",
      areaCode: selectedAreaCode,
      departmentCode: selectedDepartmentCode,
      registrationDevice: Boolean(device.isRegistrationDevice),
    });
    setEditOpen(true);
  };

  const openEditForSelected = () => {
    if (!activeDevice) {
      setError("Select a device first.");
      return;
    }

    openEditForDevice(activeDevice);
  };

  const applySearch = () => {
    setSearchQuery(searchInput.trim());
    setPage(1);
  };

  const resetManagedFilters = () => {
    setStatus("all");
    setSearchInput("");
    setSearchQuery("");
    setPage(1);
  };

  return (
    <div className="space-y-4 bg-[#ecf3fa] px-2 pb-2 pt-0 sm:px-4 sm:pb-4 sm:pt-1 lg:px-6 lg:pb-6 lg:pt-1">
      <section className="w-full rounded-[28px] border border-[#d8e6f4] bg-white p-4 shadow-[0_24px_40px_-22px_rgba(20,55,80,0.6)] sm:p-5 lg:p-5">
        <div className="tab-header mb-3 flex flex-wrap items-center justify-between gap-3">
          <h1 className="inline-flex items-center gap-2 text-2xl font-semibold text-[#103856]">
            <Cpu className="h-6 w-6 text-[#2f70a8]" />
            Device Manager
          </h1>
        </div>

        {error ? (
          <div className="mb-4 rounded-xl border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-sm text-[#8d2f2f]">
            <div>{error}</div>
            {deviceLimitNotice ? (
              <div className="mt-2 flex flex-wrap items-center gap-2 text-xs">
                <span>
                  {deviceLimitNotice.licenseType ? `License: ${deviceLimitNotice.licenseType}. ` : ""}
                  {typeof deviceLimitNotice.allowedDevices === "number" ? `Allowed: ${deviceLimitNotice.allowedDevices}. ` : ""}
                  {typeof deviceLimitNotice.currentDeviceCount === "number" ? `Current: ${deviceLimitNotice.currentDeviceCount}.` : ""}
                </span>
                <a
                  href={UPGRADE_WHATSAPP_URL}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center gap-1 rounded-full border border-[#ebb4b4] bg-white px-3 py-1 font-semibold text-[#8d2f2f] hover:bg-[#fff7f7]"
                >
                  <MessageCircle className="h-3.5 w-3.5 text-[#0b8f3a]" />
                  Upgrade on WhatsApp
                </a>
              </div>
            ) : null}
          </div>
        ) : null}

        {tab === "managed" ? (
          <>
            <div className="tab-toolbar mb-4 flex flex-wrap items-center justify-between gap-4">
              <div className="flex w-full min-w-0 items-center rounded-full border border-[#bfd9f2] bg-white px-4 py-1 shadow-[0_2px_8px_rgba(0,0,0,0.02)] sm:w-auto sm:min-w-[280px]">
                <Search className="h-4 w-4 text-[#7c9ec2]" />
                <input
                  value={searchInput}
                  onChange={(event) => setSearchInput(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") {
                      applySearch();
                    }
                  }}
                  placeholder="Search serial/name/ip..."
                  className="min-w-0 flex-1 border-0 bg-transparent px-2 py-2 text-sm outline-none sm:w-[220px]"
                />
                <button
                  type="button"
                  onClick={applySearch}
                  className="ml-1 rounded-full border border-[#4084c0] bg-[#1b5585] px-4 py-1.5 text-xs font-medium text-white"
                >
                  Search
                </button>
              </div>

              <div className="tab-toolbar-controls w-full lg:w-auto">
                <button type="button" onClick={() => void openAddFlow()} className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff]">
                  Add
                </button>
                <button
                  type="button"
                  disabled={!activeDevice}
                  onClick={openEditForSelected}
                  className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Wrench className="h-3.5 w-3.5 text-[#2b6392]" />
                  <span>Edit Device</span>
                </button>
                <button
                  type="button"
                  disabled={!hasTargets}
                  onClick={() => void executeControlAction("sync-data")}
                  className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Send className="h-3.5 w-3.5 text-[#2b6392]" />
                  <span>Sync Data</span>
                </button>
                <div className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-2 py-1">
                  <select
                    value={controlAction}
                    onChange={(event) => {
                      const next = event.target.value as DeviceControlAction;
                      setControlAction(next);
                      if (next && next !== "upgrade-firmware") {
                        void executeControlAction(next);
                      } else if (next === "upgrade-firmware") {
                        setControlAction("");
                        openFirmwareUpgrade();
                      }
                    }}
                    disabled={!hasTargets}
                    className="rounded-full border border-[#bbd6f5] bg-[#f4f9ff] px-3 py-1.5 text-xs font-medium text-[#12426b] disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <option value="">Control</option>
                    <option value="read-info">Read Information</option>
                    <option value="device-capacity">Device Capacity</option>
                    <option value="reboot-device">Reboot Device</option>
                    <option value="sync-time">Sync Time</option>
                    <option value="sync-data">Sync Data</option>
                    <option value="upgrade-firmware">Upgrade Firmware</option>
                  </select>
                </div>
                <div className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-2 py-1">
                  <select
                    value={fetchAction}
                    onChange={(event) => {
                      const next = event.target.value as DeviceFetchAction;
                      setFetchAction(next);
                      if (next) {
                        void executeControlAction(next);
                        setFetchAction("");
                      }
                    }}
                    disabled={!hasTargets}
                    className="rounded-full border border-[#bbd6f5] bg-[#f4f9ff] px-3 py-1.5 text-xs font-medium text-[#12426b] disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <option value="">Fetch Data</option>
                    <option value="fetch-all-data">Fetch All Data</option>
                    <option value="fetch-all-log-data">Fetch All Log Data</option>
                    <option value="fetch-users">Fetch Users</option>
                  </select>
                </div>
                <button
                  type="button"
                  disabled={!activeDevice}
                  onClick={() => setDeleteOpen(true)}
                  className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Trash2 className="h-3.5 w-3.5 text-[#2b6392]" />
                  <span>Delete</span>
                </button>
                <button
                  type="button"
                  onClick={() => setDataCleanOpen(true)}
                  disabled={!activeDevice}
                  className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <BrushCleaning className="h-3.5 w-3.5 text-[#2b6392]" />
                  <span>Data Clean</span>
                </button>
                <button
                  type="button"
                  disabled={!hasTargets}
                  onClick={() => setClearPendingOpen(true)}
                  className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Clock3 className="h-3.5 w-3.5 text-[#2b6392]" />
                  <span>Clear Pending Command</span>
                </button>
                <button type="button" onClick={() => void openCommandQueue()} className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff]">
                  <ListChecks className="h-3.5 w-3.5 text-[#2b6392]" />
                  <span>Command Queue</span>
                </button>
                <button type="button" onClick={() => void loadAll()} className="inline-flex items-center gap-2 rounded-full border border-[#bcd3f0] bg-white px-4 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff]">
                  <RotateCcw className="h-3.5 w-3.5 text-[#2b6392]" />
                  <span>Refresh</span>
                </button>
                <div className="relative">
                  <button
                    ref={colMenuButtonRef}
                    type="button"
                    onClick={() => {
                      setColMenuOpen((prev) => {
                        const next = !prev;
                        if (next) {
                          const rect = colMenuButtonRef.current?.getBoundingClientRect();
                          if (rect) {
                            setColMenuPos({ right: window.innerWidth - rect.right, top: rect.bottom + 6 });
                          }
                        } else {
                          setColMenuPos(null);
                        }
                        return next;
                      });
                    }}
                    title="Column preferences"
                    aria-label="Column preferences"
                    className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-[#bcd3f0] bg-white text-[#1a4a73] transition-colors hover:border-[#8eb9e3] hover:bg-[#e1efff]"
                  >
                    <Settings2 className="h-4 w-4" />
                  </button>
                  {colMenuOpen && colMenuPos && typeof document !== "undefined"
                    ? createPortal(
                        <>
                          <div
                            className="fixed inset-0 z-40"
                            onClick={() => {
                              setColMenuOpen(false);
                              setColMenuPos(null);
                            }}
                          />
                          <div
                            className="w-56 rounded-xl border border-[#d2e3f5] bg-white p-2 shadow-[0_20px_30px_-12px_rgba(10,40,70,0.45)]"
                            style={{ position: "fixed", right: colMenuPos.right, top: colMenuPos.top, zIndex: 9999 }}
                            onClick={(event) => event.stopPropagation()}
                          >
                            <div className="flex items-center justify-between px-2 py-1">
                              <span className="text-xs font-semibold uppercase text-[#5a7892]">Columns</span>
                              <button
                                type="button"
                                onClick={resetColPrefs}
                                className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-[11px] font-medium text-[#1a4a73] hover:bg-[#eef6ff]"
                              >
                                <RotateCcw className="h-3 w-3" />
                                Reset
                              </button>
                            </div>
                            <div className="max-h-64 overflow-auto">
                              {DEVICE_TOGGLEABLE_COLUMNS.map((column) => (
                                <label
                                  key={column.key}
                                  className="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm text-[#1e4162] hover:bg-[#f6fbff]"
                                >
                                  <input
                                    type="checkbox"
                                    checked={Boolean(colPrefs[column.key])}
                                    onChange={(event) => updateColPref(column.key, event.target.checked)}
                                  />
                                  {column.label}
                                </label>
                              ))}
                            </div>
                          </div>
                        </>,
                        document.body,
                      )
                    : null}
                </div>
              </div>
            </div>

            <div className="tab-toolbar-controls mb-4 flex flex-wrap items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                <span className="text-xs font-semibold text-[#3d6385]">State:</span>
                <select
                  value={status}
                  onChange={(event) => setStatus(event.target.value as "all" | "online" | "offline")}
                  className="rounded-full border border-[#bbd6f5] bg-[#eaf3fd] px-4 py-1.5 text-xs font-medium text-[#12426b]"
                >
                  <option value="all">All</option>
                  <option value="online">Online</option>
                  <option value="offline">Offline</option>
                </select>
              </div>
              <button type="button" onClick={resetManagedFilters} className="inline-flex items-center gap-1 text-xs text-[#3572a5] underline decoration-dashed underline-offset-2">
                <RotateCcw className="h-3.5 w-3.5" />
                reset
              </button>
            </div>



            <div className="overflow-x-auto rounded-[22px] border border-[#e0ecf7] bg-white" onContextMenu={(event) => event.preventDefault()}>
              <table className="min-w-[1080px] border-collapse text-left text-xs">
                <thead>
                  <tr className="border-b border-[#cfe1f5] bg-[#f2f8ff] text-[11.5px] font-semibold uppercase tracking-wider text-[#25567f]">
                    <th className="px-3 py-4 font-semibold">
                      <input
                        type="checkbox"
                        checked={allFilteredSelected}
                        onChange={(event) => toggleAllFilteredSelection(event.target.checked)}
                        aria-label="Select all devices"
                      />
                    </th>
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("serialNumber")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Serial Number
                        {sortField === "serialNumber" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("name")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Device Name
                        {sortField === "name" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    {colPrefs.area ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("area")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Area
                        {sortField === "area" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.department ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("department")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Department
                        {sortField === "department" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.ipAddress ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("ipAddress")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Device IP
                        {sortField === "ipAddress" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.model ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("model")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Device Model
                        {sortField === "model" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.state ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("state")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        State
                        {sortField === "state" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.syncMode ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("isRegistrationDevice")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Realtime Sync
                        {sortField === "isRegistrationDevice" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.lastActivity ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("lastSeenUtc")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Last Activity
                        {sortField === "lastSeenUtc" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.userQty ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("userQty")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        User Qty
                        {sortField === "userQty" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.fpQty ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("fpQty")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        FP Qty
                        {sortField === "fpQty" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.faceQty ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("faceQty")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Face Qty
                        {sortField === "faceQty" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.palmQty ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("palmQty")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Palm Qty
                        {sortField === "palmQty" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.transactionQty ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("transactionQty")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Transaction Qty
                        {sortField === "transactionQty" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    {colPrefs.cmdQty ? (
                    <th className="px-3 py-4 font-semibold">
                      <button
                        type="button"
                        onClick={() => updateSort("cmdQty")}
                        className="whitespace-nowrap font-semibold tracking-wide hover:text-[#0f2f4d] inline-flex items-center gap-1 transition-colors"
                      >
                        Cmd
                        {sortField === "cmdQty" && (
                          <span className="ml-0.5 text-[10px]">{sortAsc ? "▲" : "▼"}</span>
                        )}
                      </button>
                    </th>
                    ) : null}
                    <th className="px-3 py-4 text-right font-semibold">Action</th>
                  </tr>
                </thead>
                <tbody>
                  {sortedDevices.map((device) => (
                    <tr
                      key={device.id}
                      onClick={() => setSelectedSerial(device.serialNumber)}
                      onContextMenu={(event) => onDeviceRightClick(event, device)}
                      className={`cursor-pointer border-b border-[#e5effa] text-[#1e4162] last:border-b-0 ${selectedSerial === device.serialNumber ? "bg-[#fafdff]" : ""} ${selectedDeviceSerials.includes(device.serialNumber) ? "bg-[#f0f8ff]" : ""}`}
                    >
                      <td className="px-3 py-3">
                        <input
                          type="checkbox"
                          checked={selectedDeviceSerials.includes(device.serialNumber)}
                          onClick={(event) => event.stopPropagation()}
                          onChange={(event) => toggleDeviceSelection(device.serialNumber, event.target.checked)}
                          aria-label={`Select ${device.serialNumber}`}
                        />
                      </td>
                      <td className="px-3 py-3">
                        <button type="button" onClick={() => setSelectedSerial(device.serialNumber)} className="text-left hover:underline font-medium">
                          {device.serialNumber}
                        </button>
                      </td>
                      <td className="px-3 py-3">{device.name || device.serialNumber}</td>
                      {colPrefs.area ? <td className="px-3 py-3">{device.area || device.location || "None"}</td> : null}
                      {colPrefs.department ? <td className="px-3 py-3">{device.department || "None"}</td> : null}
                      {colPrefs.ipAddress ? <td className="px-3 py-3">{device.ipAddress || "None"}</td> : null}
                      {colPrefs.model ? <td className="px-3 py-3">{device.model || "None"}</td> : null}
                      {colPrefs.state ? (
                      <td className="px-3 py-3">
                        <span className={`rounded-full px-3 py-1 text-[11px] font-semibold ${statusClass(device.state)}`}>{device.state}</span>
                      </td>
                      ) : null}
                      {colPrefs.syncMode ? (
                      <td className="px-3 py-3">
                        <span
                          className={`rounded-full px-3 py-1 text-[11px] font-semibold ${
                            device.isRegistrationDevice
                              ? "bg-[#e7f6eb] text-[#1f6b36]"
                              : "bg-[#f5f7fa] text-[#4a5f75]"
                          }`}
                        >
                          {syncModeLabel(device.isRegistrationDevice)}
                        </span>
                      </td>
                      ) : null}
                      {colPrefs.lastActivity ? <td className="px-3 py-3">{formatCalendarDateTime(device.lastSeenUtc, calendarType, "None")}</td> : null}
                      {colPrefs.userQty ? <td className="px-3 py-3">{device.userQty}</td> : null}
                      {colPrefs.fpQty ? <td className="px-3 py-3">{device.fpQty}</td> : null}
                      {colPrefs.faceQty ? <td className="px-3 py-3">{device.faceQty}</td> : null}
                      {colPrefs.palmQty ? <td className="px-3 py-3">{device.palmQty}</td> : null}
                      {colPrefs.transactionQty ? <td className="px-3 py-3">{device.transactionQty}</td> : null}
                      {colPrefs.cmdQty ? <td className="px-3 py-3">{device.cmdQty > 0 ? device.cmdQty : "None"}</td> : null}
                      <td className="px-3 py-3 text-right">
                        <div className="flex items-center justify-end gap-1">
                          <button
                            type="button"
                            onClick={(event) => {
                              event.stopPropagation();
                              openEditForDevice(device);
                            }}
                            className="rounded-md border border-[#d8e6f4] p-1.5 text-[#4e7dab] hover:bg-[#f2f8ff]"
                            title="Edit"
                          >
                            <Wrench className="h-4 w-4" />
                          </button>
                          {device.isOnline ? (
                            <button type="button" onClick={() => void connectDisconnect(device, false)} className="rounded-md border border-[#d8e6f4] p-1.5 text-[#4e7dab] hover:bg-[#f2f8ff]" title="Disconnect">
                              <WifiOff className="h-4 w-4" />
                            </button>
                          ) : (
                            <button type="button" onClick={() => void connectDisconnect(device, true)} className="rounded-md border border-[#d8e6f4] p-1.5 text-[#4e7dab] hover:bg-[#f2f8ff]" title="Connect">
                              <Wifi className="h-4 w-4" />
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="mt-4 flex flex-wrap items-center justify-between gap-2 text-xs text-[#6584a3]">
              <span>
                {filteredDevices.length === 0 ? "0 devices" : `1-${filteredDevices.length} of ${filteredDevices.length} devices`}
              </span>
              <span>Checked: {selectedDeviceSerials.length}</span>
              {activeDevice ? <span>Selected: {activeDevice.serialNumber}</span> : null}
            </div>
          </>
        ) : null}

        {contextMenu && typeof document !== "undefined"
          ? createPortal(
              <div
                className="fixed z-[9999] min-w-[220px] rounded-xl border border-[#c8def4] bg-white p-1.5 shadow-[0_20px_30px_-12px_rgba(10,40,70,0.45)]"
                style={{ left: contextMenu.x, top: contextMenu.y }}
                onClick={(event) => event.stopPropagation()}
                onMouseDown={(event) => event.stopPropagation()}
                onContextMenu={(event) => event.preventDefault()}
              >
                <button type="button" onClick={() => void runContextAction("read-info")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Read Information</button>
                <button type="button" onClick={() => void runContextAction("device-capacity")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Device Capacity</button>
                <button type="button" onClick={() => void runContextAction("reboot-device")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Reboot Device</button>
                <button type="button" onClick={() => void runContextAction("fetch-all-data")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Fetch All Data</button>
                <button type="button" onClick={() => void runContextAction("fetch-all-log-data")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Fetch All Log data</button>
                <button type="button" onClick={() => void runContextAction("fetch-users")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Fetch Users</button>
                <button type="button" onClick={() => void runContextAction("sync-time")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Sync Time</button>
                <button type="button" onClick={() => void runContextAction("sync-data")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Sync Data</button>
                <button type="button" onClick={() => void runContextAction("clear-pending")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Clear Pending Command</button>
                <button type="button" onClick={() => void runContextAction("delete-device")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#8d2f2f] hover:bg-[#fff0f0]">Delete Device</button>
              </div>,
              document.body,
            )
          : null}

        {tab === "commands" ? (
          <div className="space-y-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-lg font-semibold text-[#103856]">Command Queue</h2>
              <button type="button" onClick={() => setTab("managed")} className="rounded-full border border-[#bcd3f0] bg-white px-3 py-1.5 text-xs font-medium text-[#1a4a73] hover:bg-[#e1efff]">
                Back to Device Manager
              </button>
            </div>

            <div className="grid gap-3 md:grid-cols-[1fr_1fr_auto]">
              <select value={selectedSerial} onChange={(event) => setSelectedSerial(event.target.value)} className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm">
                <option value="">Select device serial</option>
                {devices.map((d) => (
                  <option key={d.id} value={d.serialNumber}>
                    {d.serialNumber} - {d.name}
                  </option>
                ))}
              </select>
              <input value={customCommand} onChange={(event) => setCustomCommand(event.target.value)} placeholder="Custom command" className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm" />
              <button type="button" onClick={() => void sendCustomCommand()} className="inline-flex items-center justify-center gap-2 rounded-lg border border-[--border] bg-[--card] px-3 py-2 text-sm">
                <Send className="h-4 w-4" />
                Queue
              </button>
            </div>

            <div className="grid gap-3 md:grid-cols-5">
              <select value={commandForm.action} onChange={(event) => setCommandForm((prev) => ({ ...prev, action: event.target.value }))} className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm">
                {presetActions.map((item) => (
                  <option key={item.action} value={item.action}>
                    {item.label}
                  </option>
                ))}
              </select>
              <input value={commandForm.pin} onChange={(event) => setCommandForm((prev) => ({ ...prev, pin: event.target.value }))} placeholder="PIN" className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm" />
              <input value={commandForm.startTime} onChange={(event) => setCommandForm((prev) => ({ ...prev, startTime: event.target.value }))} placeholder="StartTime" className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm" />
              <input value={commandForm.endTime} onChange={(event) => setCommandForm((prev) => ({ ...prev, endTime: event.target.value }))} placeholder="EndTime" className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm" />
              <button
                type="button"
                onClick={() => {
                  if (!selectedSerial) {
                    setError("Select a device first.");
                    return;
                  }
                  if (commandForm.action === "fetch-all-data") {
                    void runControlActionForSerial(selectedSerial, "fetch-all-data");
                    return;
                  }
                  void queuePreset(selectedSerial, commandForm.action);
                }}
                className="rounded-lg bg-[--primary] px-3 py-2 text-sm font-medium text-[--primary-foreground]"
              >
                Queue Preset
              </button>
            </div>

            <div className="grid gap-3 md:grid-cols-[1fr_1fr_auto]">
              <input value={commandForm.optionKey} onChange={(event) => setCommandForm((prev) => ({ ...prev, optionKey: event.target.value }))} placeholder="Option key" className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm" />
              <input value={commandForm.optionValue} onChange={(event) => setCommandForm((prev) => ({ ...prev, optionValue: event.target.value }))} placeholder="Option value" className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm" />
              <button type="button" onClick={() => void clearPending(selectedSerial)} className="rounded-lg border border-[--border] bg-[--card] px-3 py-2 text-sm">
                Clear Pending Command
              </button>
            </div>

            {activeDevice ? (
              <p className="text-xs text-[--muted-foreground]">
                Selected: {activeDevice.serialNumber} ({activeDevice.name}) | State: {activeDevice.state} | Sync: {syncModeLabel(activeDevice.isRegistrationDevice)} | Cmd: {activeDevice.cmdQty || "None"}
              </p>
            ) : null}

            <div className="space-y-2">
              {commands.length === 0 ? <p className="text-sm text-[--muted-foreground]">Cmd: None</p> : null}
              {commands.map((item) => (
                <div key={item.id} className="rounded-lg border border-[--border] bg-[--muted] p-3 text-sm">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <p className="font-medium">{item.commandText}</p>
                    <span className={`rounded-full px-2 py-0.5 text-xs ${item.status === "pending" ? "badge-pending" : item.status === "sent" ? "badge-online" : "badge-offline"}`}>
                      {item.status}
                    </span>
                  </div>
                  <p className="mt-1 text-xs text-[--muted-foreground]">
                    {formatCalendarDateTime(item.createdUtc, calendarType)} {item.sentUtc ? `| sent: ${formatCalendarDateTime(item.sentUtc, calendarType, item.sentUtc)}` : ""}
                  </p>
                  {item.replyText ? <p className="mt-1 text-xs text-[--muted-foreground]">reply: {item.replyText}</p> : null}
                </div>
              ))}
            </div>
          </div>
        ) : null}
      </section>

      {addOpen ? (
        <div className="fixed inset-0 z-50 overflow-y-auto bg-black/55 p-2 sm:p-6" onClick={closeAddFlow}>
          <div className="grid min-h-full place-items-start sm:place-items-center">
          <div className="my-2 flex max-h-[calc(100vh-1rem)] w-full max-w-4xl flex-col overflow-hidden rounded-2xl border border-[#d8e6f4] bg-white shadow-2xl transition-all duration-300 sm:my-0 sm:max-h-[92vh]" onClick={(event) => event.stopPropagation()}>
            <div className="flex items-center justify-between border-b border-[--border] bg-gradient-to-r from-sky-50 via-blue-50 to-cyan-50 px-4 py-3 sm:px-5">
              <h2 className="text-lg font-semibold">Add Device</h2>
              <button type="button" onClick={closeAddFlow} className="rounded-md border border-[--border] p-1.5">
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="border-b border-[--border] px-4 py-3 sm:px-5">
              <div className="inline-flex rounded-lg border border-[--border] bg-[--muted] p-1">
                <button
                  type="button"
                  onClick={() => setAddTab("search")}
                  className={`rounded-md px-3 py-1.5 text-sm ${addTab === "search" ? "bg-[--primary] text-[--primary-foreground]" : ""}`}
                >
                  Available Device
                </button>
                <button
                  type="button"
                  onClick={() => pendingAdd && setAddTab("details")}
                  className={`rounded-md px-3 py-1.5 text-sm ${addTab === "details" ? "bg-[--primary] text-[--primary-foreground]" : ""} ${pendingAdd ? "" : "cursor-not-allowed opacity-50"}`}
                >
                  Device Information
                </button>
              </div>
            </div>

            <div className="flex-1 overflow-y-auto px-4 py-4 sm:px-5">
              {addTab === "search" ? (
                <div className="flex h-full min-h-0 flex-col gap-3">
                  <div className="flex flex-col gap-2 sm:flex-row">
                    <button type="button" onClick={() => void loadDiscoveredForAdd()} className="w-full rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm sm:w-auto">
                      Refresh Available Devices
                    </button>
                    <button type="button" onClick={startManualAdd} className="w-full rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground] sm:w-auto">
                      Manual Add Device
                    </button>
                  </div>

                  <div className="rounded-xl border border-sky-200 bg-[linear-gradient(135deg,#eff8ff,#f6fffb)] p-4 shadow-[0_18px_30px_-26px_rgba(37,99,235,0.65)]">
                    <p className="text-sm font-medium leading-6 text-[#1f4f7c]">
                      Notice: To add a device, please enter the Cloud Server Address and Port; in device cloud server setting.
                    </p>
                    <div className="mt-3 grid gap-2 text-sm font-semibold text-[#0f3f68]">
                      <div className="rounded-xl border border-sky-200 bg-white px-3 py-2 shadow-sm">
                        Serve IP: <span className="text-[#0b63ce]">{deviceServerNotice.host}</span>
                      </div>
                      <div className="rounded-xl border border-emerald-200 bg-white px-3 py-2 shadow-sm">
                        Port: <span className="text-[#047857]">{deviceServerNotice.port}</span>
                      </div>
                    </div>
                  </div>

                  <div className="min-h-[220px] flex-1 space-y-2 overflow-y-auto rounded-xl border border-sky-100 bg-gradient-to-b from-sky-50/60 to-cyan-50/40 p-2">
                    {discoveredForAdd.length === 0 ? <p className="p-2 text-sm text-[--muted-foreground]">No available device found.</p> : null}
                    {discoveredForAdd.map((item) => (
                      <div key={item.serialNumber} className="flex flex-col gap-2 rounded-xl border border-sky-200 bg-gradient-to-r from-white to-sky-50 p-3 text-sm shadow-sm transition-all duration-200 hover:-translate-y-0.5 hover:border-sky-300 hover:shadow-md sm:flex-row sm:items-center sm:justify-between">
                        <div className="min-w-0">
                          <p className="truncate font-medium">{item.name || item.serialNumber}</p>
                          <p className="text-xs text-[--muted-foreground]">
                            SN: {item.serialNumber} | IP: {item.ipAddress || "None"}{item.model && item.model !== "None" ? ` | Model: ${item.model}` : ""}
                          </p>
                        </div>
                        <button type="button" onClick={() => chooseDiscoveredDevice(item)} className="self-start rounded-lg bg-[--primary] px-3 py-1.5 text-xs font-medium text-[--primary-foreground] sm:self-auto">
                          Add
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}

              {addTab === "details" ? (
                <div className="grid gap-3 rounded-xl border border-sky-100 bg-gradient-to-b from-sky-50/65 to-white p-3 md:grid-cols-2">
                  {pendingAdd?.mode === "manual" ? (
                    <p className="text-xs text-[--muted-foreground] md:col-span-2">
                      Manual mode: enter device details, then add.
                    </p>
                  ) : null}
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Device Name</span>
                    <input
                      value={pendingAdd?.name ?? ""}
                      onChange={(event) => setPendingAdd((prev) => (prev ? { ...prev, name: event.target.value } : prev))}
                      placeholder="Enter device name"
                      className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                    />
                  </label>
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Serial Number</span>
                    <input
                      value={pendingAdd?.serialNumber ?? ""}
                      readOnly={pendingAdd?.mode !== "manual"}
                      onChange={(event) => setPendingAdd((prev) => (prev ? { ...prev, serialNumber: event.target.value } : prev))}
                      placeholder="Enter serial number"
                      className={`w-full rounded-lg border border-[--input] px-3 py-2 text-sm ${pendingAdd?.mode === "manual" ? "bg-[--card]" : "bg-[--muted]"}`}
                    />
                  </label>
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Device IP</span>
                    <input
                      value={pendingAdd?.ipAddress ?? ""}
                      readOnly={pendingAdd?.mode !== "manual"}
                      onChange={(event) => setPendingAdd((prev) => (prev ? { ...prev, ipAddress: event.target.value } : prev))}
                      placeholder="Enter device IP"
                      className={`w-full rounded-lg border border-[--input] px-3 py-2 text-sm ${pendingAdd?.mode === "manual" ? "bg-[--card]" : "bg-[--muted]"}`}
                    />
                  </label>
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Device Model</span>
                    <input
                      value={pendingAdd?.model ?? ""}
                      readOnly
                      placeholder="Enter device model"
                      className="w-full rounded-lg border border-[--input] bg-[--muted] px-3 py-2 text-sm"
                    />
                  </label>
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Time Zone</span>
                    <select
                      value={pendingAdd?.timeZone ?? getDeviceTimeZoneValue()}
                      onChange={(event) => setPendingAdd((prev) => (prev ? { ...prev, timeZone: event.target.value } : prev))}
                      className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                    >
                    <option value="-12">Time Zone: -12</option>
                    <option value="-11">Time Zone: -11</option>
                    <option value="-10">Time Zone: -10</option>
                    <option value="-9">Time Zone: -9</option>
                    <option value="-8">Time Zone: -8</option>
                    <option value="-7">Time Zone: -7</option>
                    <option value="-6">Time Zone: -6</option>
                    <option value="-5">Time Zone: -5</option>
                    <option value="-4">Time Zone: -4</option>
                    <option value="-3">Time Zone: -3</option>
                    <option value="-2">Time Zone: -2</option>
                    <option value="-1">Time Zone: -1</option>
                    <option value="0">Time Zone: 0</option>
                    <option value="1">Time Zone: +1</option>
                    <option value="2">Time Zone: +2</option>
                    <option value="3">Time Zone: +3</option>
                    <option value="4">Time Zone: +4</option>
                    <option value="5">Time Zone: +5</option>
                  <option value="330">Time Zone: +5:30</option>
                  <option value="345">Time Zone: +5:45</option>
                    <option value="6">Time Zone: +6</option>
                    <option value="7">Time Zone: +7</option>
                    <option value="8">Time Zone: +8</option>
                    <option value="9">Time Zone: +9</option>
                    <option value="10">Time Zone: +10</option>
                    <option value="11">Time Zone: +11</option>
                    <option value="12">Time Zone: +12</option>
                    <option value="13">Time Zone: +13</option>
                    <option value="14">Time Zone: +14</option>
                    </select>
                  </label>
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Registration Device</span>
                    <select
                      value={pendingAdd?.registrationDevice ? "yes" : "no"}
                      onChange={(event) => setPendingAdd((prev) => (prev ? { ...prev, registrationDevice: event.target.value === "yes" } : prev))}
                      className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                    >
                      <option value="yes">Yes</option>
                      <option value="no">No</option>
                    </select>
                  </label>
                  <label className="flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 md:col-span-2">
                    <input
                      type="checkbox"
                      checked={Boolean(pendingAdd?.clearAllDataOnAdd)}
                      onChange={(event) => setPendingAdd((prev) => (prev ? { ...prev, clearAllDataOnAdd: event.target.checked } : prev))}
                    />
                    <span className="text-sm">
                      Clear All Data after add
                    </span>
                  </label>
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Area</span>
                    <select
                      value={pendingAdd?.areaCode ?? pickDefaultAreaCode(areaOptions)}
                      onChange={(event) => {
                        const areaCode = event.target.value;
                        const departmentCode = pickDefaultDepartmentCode(areaCode, departmentOptions);
                        setPendingAdd((prev) => (prev ? { ...prev, areaCode, departmentCode } : prev));
                      }}
                      className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                    >
                      {areaOptions.map((a) => (
                        <option key={a.id} value={a.code}>
                          {a.code} - {a.name}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="space-y-1">
                    <span className="text-xs font-medium text-[--muted-foreground]">Department</span>
                    <select
                      value={pendingAdd?.departmentCode ?? ""}
                      onChange={(event) => setPendingAdd((prev) => (prev ? { ...prev, departmentCode: event.target.value } : prev))}
                      className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                    >
                      {(departmentOptions.filter((d) => d.areaCode === (pendingAdd?.areaCode ?? pickDefaultAreaCode(areaOptions))).length === 0
                        ? departmentOptions
                        : departmentOptions.filter((d) => d.areaCode === (pendingAdd?.areaCode ?? pickDefaultAreaCode(areaOptions))))
                        .map((d) => (
                          <option key={d.id} value={d.code}>
                            {d.code} - {d.name}
                          </option>
                        ))}
                    </select>
                  </label>
                  <p className="text-xs text-[--muted-foreground] md:col-span-2">
                    Area and Department are required. Devices in the same Area/Department share the same employee sync scope.
                  </p>
                  <button
                    type="button"
                    disabled={addSubmitting || !pendingAdd || !pendingAdd.areaCode.trim() || !pendingAdd.departmentCode.trim()}
                    onClick={() => void submitAddFromDiscovered()}
                    className="w-full rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground] disabled:opacity-50 md:col-span-2"
                  >
                    {addSubmitting ? "Adding..." : "Add Device"}
                  </button>
                </div>
              ) : null}
            </div>
          </div>
          </div>
        </div>
      ) : null}

      {capacityModalData?.open ? (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/45 backdrop-blur-sm p-4">
          <div className="surface w-full max-w-lg p-6 shadow-2xl rounded-2xl border border-[#bcd3f0] bg-white text-[#1a4a73] relative overflow-hidden">
            {/* Header */}
            <div className="mb-4 flex items-center justify-between border-b border-[#e1efff] pb-3">
              <div>
                <h2 className="text-base font-semibold text-[#0f3254]">Device Capacity</h2>
                <p className="text-xs text-[#5f7f9d]">
                  {capacityModalData.deviceName} ({capacityModalData.serialNumber}) {capacityModalData.model && `• ${capacityModalData.model}`}
                </p>
              </div>
              <button 
                type="button" 
                onClick={() => setCapacityModalData(null)} 
                className="rounded-lg p-1 hover:bg-[#eef6ff] text-[#5f7f9d] hover:text-[#1a4a73] transition-colors"
              >
                <span className="text-lg font-bold">×</span>
              </button>
            </div>

            {/* Content */}
            {capacityModalData.loading ? (
              <div className="flex flex-col items-center justify-center py-10 space-y-4">
                <div className="h-10 w-10 animate-spin rounded-full border-4 border-[#e1efff] border-t-[#3572a5]"></div>
                <div className="text-center">
                  <p className="text-sm font-medium text-[#1a4a73]">Querying Device Capacity...</p>
                  <p className="text-xs text-[#5f7f9d]">Waiting for ZKTeco device to reply (typically 2-5s)...</p>
                </div>
              </div>
            ) : (
              <div className="space-y-5">
                {/* Connection Status Indicator */}
                <div className="flex items-center justify-between">
                  <span className="text-xs font-semibold text-[#5f7f9d]">STATUS</span>
                  {capacityModalData.isOfflineFetched ? (
                    <span className="inline-flex items-center gap-1.5 rounded-full bg-amber-50 px-2 py-0.5 text-xs font-semibold text-amber-700 border border-amber-200">
                      <span className="h-1.5 w-1.5 rounded-full bg-amber-500"></span>
                      Last Known (Offline/Busy)
                    </span>
                  ) : (
                    <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-semibold text-emerald-700 border border-emerald-200">
                      <span className="h-1.5 w-1.5 rounded-full bg-emerald-500"></span>
                      Live Real-Time
                    </span>
                  )}
                </div>
                {/* Progress Indicators */}
                <div className="space-y-4">
                  {[
                    { label: "Users", data: capacityModalData.user, color: "from-blue-500 to-indigo-500" },
                    { label: "Fingerprints", data: capacityModalData.fingerprint, color: "from-teal-500 to-emerald-500" },
                    { label: "Faces", data: capacityModalData.face, color: "from-purple-500 to-pink-500" },
                    { label: "Cards", data: capacityModalData.card, color: "from-amber-500 to-yellow-600" },
                    { label: "Attendance Logs", data: capacityModalData.attLog, color: "from-cyan-500 to-blue-600" },
                    { label: "T&A Photos", data: capacityModalData.attPhoto, color: "from-rose-500 to-pink-600" },
                    { label: "Palms", data: capacityModalData.palm, color: "from-orange-500 to-red-500" },
                  ].map((metric) => {
                    const pct = metric.data.max > 0 ? Math.min(100, Math.round((metric.data.current / metric.data.max) * 100)) : 0;
                    return (
                      <div key={metric.label} className="space-y-1.5">
                        <div className="flex items-center justify-between text-xs">
                          <span className="font-semibold text-[#1e4162]">{metric.label}</span>
                          <span className="font-medium text-[#5f7f9d]">
                            {metric.data.current.toLocaleString()} / {metric.data.max.toLocaleString()} ({pct}%)
                          </span>
                        </div>
                        <div className="h-2.5 w-full rounded-full bg-[#f0f7ff] overflow-hidden border border-[#d7e7f8]">
                          <div 
                            className={`h-full rounded-full bg-gradient-to-r ${metric.color} transition-all duration-500`}
                            style={{ width: `${pct}%` }}
                          ></div>
                        </div>
                      </div>
                    );
                  })}
                </div>

                {/* Info Note */}
                <p className="text-[10px] leading-relaxed text-[#5f7f9d] border-t border-[#e1efff] pt-3">
                  * Live capacities are fetched directly from the device options handshake response. Make sure the device has a stable network connection.
                </p>

                {/* Footer Button */}
                <div className="mt-6 flex justify-end">
                  <button 
                    type="button" 
                    onClick={() => setCapacityModalData(null)} 
                    className="rounded-lg bg-[#3572a5] hover:bg-[#1a4a73] px-5 py-2 text-xs font-semibold text-white shadow-md hover:shadow-lg transition-all"
                  >
                    Close
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>
      ) : null}

      {deviceInfoSnapshot ? (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/45 backdrop-blur-sm p-4">
          <div className="surface w-full max-w-lg p-6 shadow-2xl rounded-2xl border border-[#bcd3f0] bg-white text-[#1a4a73] relative overflow-hidden">
            {/* Header */}
            <div className="mb-4 flex items-center justify-between border-b border-[#e1efff] pb-3">
              <div>
                <h2 className="text-base font-semibold text-[#0f3254]">{deviceInfoSnapshot.title}</h2>
                <p className="text-xs text-[#5f7f9d]">
                  Serial: {deviceInfoSnapshot.serial} • Captured {formatCalendarDateTime(deviceInfoSnapshot.capturedAt, calendarType, deviceInfoSnapshot.capturedAt)}
                </p>
              </div>
              <button 
                type="button" 
                onClick={() => setDeviceInfoSnapshot(null)} 
                className="rounded-lg p-1 hover:bg-[#eef6ff] text-[#5f7f9d] hover:text-[#1a4a73] transition-colors"
              >
                <span className="text-lg font-bold">×</span>
              </button>
            </div>

            {/* Content */}
            <div className="max-h-[60vh] overflow-y-auto pr-1">
              {deviceInfoSnapshot.fields.length > 0 ? (
                <div className="grid gap-x-4 gap-y-2 text-xs text-[#1e4162] grid-cols-2">
                  {deviceInfoSnapshot.fields.map((item) => (
                    <div key={`${item.key}-${item.value}`} className="border-b border-[#f0f7ff] pb-1.5">
                      <span className="font-semibold text-[#5f7f9d] block uppercase tracking-[0.02em] text-[10px]">{item.key}</span>
                      <span className="text-sm text-[#0f3254]">{item.value}</span>
                    </div>
                  ))}
                </div>
              ) : (
                <p className="text-xs text-[#5f7f9d] text-center py-10">Reply received. Raw response saved.</p>
              )}
            </div>

            {/* Footer Button */}
            <div className="mt-6 flex justify-end">
              <button 
                type="button" 
                onClick={() => setDeviceInfoSnapshot(null)} 
                className="rounded-lg bg-[#3572a5] hover:bg-[#1a4a73] px-5 py-2 text-xs font-semibold text-white shadow-md hover:shadow-lg transition-all"
              >
                Close
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {deleteOpen ? (
        <div className="fixed inset-0 z-40 grid place-items-center bg-black/35 p-4">
          <div className="surface w-full max-w-md p-5 shadow-2xl">
            <h2 className="mb-3 text-lg font-semibold">Delete Device</h2>
            <p className="text-sm text-[--muted-foreground]">
              Are you sure you want to delete <strong>{activeDevice?.serialNumber ?? "selected device"}</strong>?
            </p>
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setDeleteOpen(false)} className="rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm">
                Cancel
              </button>
              <button
                type="button"
                onClick={() => {
                  if (activeDevice) {
                    void deleteDevice(activeDevice.id);
                  }
                  setDeleteOpen(false);
                }}
                className="rounded-lg bg-[#aa3e3e] px-4 py-2 text-sm font-medium text-white"
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {dataCleanOpen ? (
        <div className="fixed inset-0 z-40 grid place-items-center bg-black/35 p-4">
          <div className="surface w-full max-w-md p-5 shadow-2xl">
            <h2 className="mb-3 text-lg font-semibold">Data Clean</h2>
            <div className="space-y-2 text-sm">
              <label className="flex items-center gap-2">
                <input type="radio" checked={dataCleanMode === "attendance"} onChange={() => setDataCleanMode("attendance")} />
                Clear Attendance Data
              </label>
              <label className="flex items-center gap-2">
                <input type="radio" checked={dataCleanMode === "captured-image"} onChange={() => setDataCleanMode("captured-image")} />
                Clear Captured Image
              </label>
              <label className="flex items-center gap-2">
                <input type="radio" checked={dataCleanMode === "all"} onChange={() => setDataCleanMode("all")} />
                Clear All Data
              </label>
            </div>
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setDataCleanOpen(false)} className="rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm">
                Cancel
              </button>
              <button type="button" onClick={() => void executeDataClean()} className="rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground]">
                Clean
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {confirmState ? (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/35 p-4">
          <div className="surface w-full max-w-sm p-5 shadow-2xl">
            <p className="text-sm">{confirmState.message}</p>
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setConfirmState(null)} className="rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm">
                Cancel
              </button>
              <button
                type="button"
                onClick={() => {
                  const run = confirmState.onConfirm;
                  setConfirmState(null);
                  run();
                }}
                className={`rounded-lg px-4 py-2 text-sm font-medium ${confirmState.danger ? "bg-red-600 text-white hover:bg-red-700" : "bg-[--primary] text-[--primary-foreground]"}`}
              >
                Confirm
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {clearPendingOpen ? (
        <div className="fixed inset-0 z-40 grid place-items-center bg-black/35 p-4">
          <div className="surface w-full max-w-md p-5 shadow-2xl">
            <h2 className="mb-3 text-lg font-semibold">Clear Pending Command</h2>
            <p className="text-sm text-[--muted-foreground]">
              {selectedDeviceSerials.length > 0
                ? `Clear all pending commands for ${selectedDeviceSerials.length} selected device(s)?`
                : "Clear all pending commands for selected device?"}
            </p>
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setClearPendingOpen(false)} className="rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm">
                Cancel
              </button>
              <button type="button" onClick={() => void executeClearPending()} className="rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground]">
                Clear
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {commandQueueOpen ? (
        <div className="fixed inset-0 z-40 grid place-items-center bg-black/35 p-4">
          <div className="surface w-full max-w-2xl p-5 shadow-2xl">
            <div className="mb-4 flex items-center justify-between">
              <h2 className="text-lg font-semibold">Command Queue</h2>
              <button type="button" onClick={() => setCommandQueueOpen(false)} className="rounded-md border border-[--border] p-1.5">
                <X className="h-4 w-4" />
              </button>
            </div>
            <div className="grid gap-3 md:grid-cols-[1fr_1fr_auto]">
              <select value={selectedSerial} onChange={(event) => setSelectedSerial(event.target.value)} className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm">
                <option value="">Select device serial</option>
                {devices.map((d) => (
                  <option key={d.id} value={d.serialNumber}>
                    {d.serialNumber} - {d.name}
                  </option>
                ))}
              </select>
              <input value={customCommand} onChange={(event) => setCustomCommand(event.target.value)} placeholder="Enter custom command" className="rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm" />
              <button type="button" onClick={() => void sendCustomCommand()} className="inline-flex items-center justify-center gap-2 rounded-lg border border-[--border] bg-[--card] px-3 py-2 text-sm">
                <Send className="h-4 w-4" />
                Send
              </button>
            </div>
            <div className="mt-4 max-h-[300px] space-y-2 overflow-auto rounded-lg border border-[--border] bg-[--muted] p-3">
              {commands.length === 0 ? <p className="text-sm text-[--muted-foreground]">No command history.</p> : null}
              {commands.map((item) => (
                <div key={item.id} className="border-b border-[--border] pb-2 text-xs last:border-b-0">
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-medium">{item.commandText}</span>
                    <span>{item.status}</span>
                  </div>
                  <div className="text-[--muted-foreground]">
                    {formatCalendarDateTime(item.createdUtc, calendarType)} {item.replyText ? `| Return: ${item.replyText}` : ""}
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      ) : null}

      {editOpen && editDevice ? (
        <div className="fixed inset-0 z-40 grid place-items-center bg-black/35 p-4">
          <div className="surface w-full max-w-lg p-5 shadow-2xl">
            <div className="mb-4 flex items-center justify-between">
              <h2 className="text-lg font-semibold">Edit Device</h2>
              <button type="button" onClick={() => setEditOpen(false)} className="rounded-md border border-[--border] p-1.5">
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="grid gap-3 md:grid-cols-2">
              <label className="space-y-1">
                <span className="text-xs font-medium text-[--muted-foreground]">Device Name</span>
                <input
                  value={editDevice.name}
                  onChange={(event) => setEditDevice((prev) => (prev ? { ...prev, name: event.target.value } : prev))}
                  placeholder="Device Name"
                  className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                />
              </label>
              <label className="space-y-1">
                <span className="text-xs font-medium text-[--muted-foreground]">Serial Number</span>
                <input
                  value={editDevice.serialNumber}
                  readOnly
                  className="w-full rounded-lg border border-[--input] bg-[--muted] px-3 py-2 text-sm"
                />
              </label>
              <label className="space-y-1">
                <span className="text-xs font-medium text-[--muted-foreground]">Area</span>
                <select
                  value={editDevice.areaCode}
                  onChange={(event) => {
                    const areaCode = event.target.value;
                    const departmentCode = pickDefaultDepartmentCode(areaCode, departmentOptions);
                    setEditDevice((prev) => (prev ? { ...prev, areaCode, departmentCode } : prev));
                  }}
                  className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                >
                  {areaOptions.map((a) => (
                    <option key={a.id} value={a.code}>
                      {a.code} - {a.name}
                    </option>
                  ))}
                </select>
              </label>
              <label className="space-y-1">
                <span className="text-xs font-medium text-[--muted-foreground]">Device IP</span>
                <input
                  value={editDevice.ipAddress}
                  onChange={(event) => setEditDevice((prev) => (prev ? { ...prev, ipAddress: event.target.value } : prev))}
                  placeholder="Device IP"
                  className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                />
              </label>
              <label className="space-y-1 md:col-span-2">
                <span className="text-xs font-medium text-[--muted-foreground]">Department</span>
                <select
                  value={editDevice.departmentCode}
                  onChange={(event) => setEditDevice((prev) => (prev ? { ...prev, departmentCode: event.target.value } : prev))}
                  className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                >
                  {(departmentOptions.filter((d) => d.areaCode === editDevice.areaCode).length === 0 ? departmentOptions : departmentOptions.filter((d) => d.areaCode === editDevice.areaCode)).map((d) => (
                    <option key={d.id} value={d.code}>
                      {d.code} - {d.name}
                    </option>
                  ))}
                </select>
              </label>
              <label className="space-y-1 md:col-span-2">
                <span className="text-xs font-medium text-[--muted-foreground]">Registration Device</span>
                <select
                  value={editDevice.registrationDevice ? "yes" : "no"}
                  onChange={(event) => setEditDevice((prev) => (prev ? { ...prev, registrationDevice: event.target.value === "yes" } : prev))}
                  className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                >
                  <option value="yes">Registration Device: Yes</option>
                  <option value="no">Registration Device: No</option>
                </select>
              </label>
              <label className="space-y-1 md:col-span-2">
                <span className="text-xs font-medium text-[--muted-foreground]">Device Model</span>
                <input
                  value={editDevice.model}
                  readOnly
                  placeholder="Device Model"
                  className="w-full rounded-lg border border-[--input] bg-[--muted] px-3 py-2 text-sm"
                />
              </label>
            </div>

            <div className="mt-4 flex flex-wrap justify-end gap-2">
              <button type="button" onClick={() => void queuePreset(editDevice.serialNumber, "clear-data")} className="inline-flex items-center gap-1 rounded-lg border border-[--border] bg-[--card] px-3 py-2 text-sm">
                <Wrench className="h-4 w-4" />
                Data Clean
              </button>
              <button type="button" onClick={() => void triggerFetchAllData(editDevice.serialNumber, true)} className="rounded-lg border border-[--border] bg-[--card] px-3 py-2 text-sm">
                Fetch All
              </button>
              <button type="button" onClick={() => void clearPending(editDevice.serialNumber)} className="rounded-lg border border-[--border] bg-[--card] px-3 py-2 text-sm">
                Clear Pending Command
              </button>
              <button type="button" onClick={() => void submitEditDevice()} className="rounded-lg bg-[--primary] px-3 py-2 text-sm font-medium text-[--primary-foreground]">
                Save
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {/* ── Firmware Upgrade Modal ── */}
      {firmwareUpgradeOpen ? (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/45 backdrop-blur-sm p-4">
          <div className="w-full max-w-lg rounded-2xl border border-[#bcd3f0] bg-white shadow-2xl overflow-hidden">
            {/* Header */}
            <div className="flex items-center justify-between border-b border-[#e1efff] bg-[#f5faff] px-5 py-4">
              <div className="flex items-center gap-2">
                <UploadCloud className="h-5 w-5 text-[#1a4a73]" />
                <h2 className="text-base font-semibold text-[#0f3254]">Upgrade Firmware</h2>
              </div>
              <button type="button" onClick={() => { setFirmwareUpgradeOpen(false); setFirmwareResult(null); }} className="rounded-lg p-1 hover:bg-[#eef6ff] text-[#5f7f9d]">
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="p-5 space-y-4">
              {/* Warning */}
              <div className="rounded-lg border border-orange-200 bg-orange-50 px-4 py-3 text-xs text-orange-800 leading-relaxed">
                <p className="font-semibold mb-1">⚠ Important — Read Before Proceeding</p>
                <p>Only use firmware files (<code className="font-mono bg-orange-100 px-1 rounded">emfw.cfg</code>) provided by ZKTeco for your exact device model. Wrong firmware can brick the terminal and require hardware recovery. The device will download and flash automatically after the next poll.</p>
              </div>

              {/* Result */}
              {firmwareResult && (
                <div className="rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-xs text-green-800 space-y-1">
                  <p className="font-semibold">{firmwareResult.message}</p>
                  <p className="font-mono text-[10px] break-all text-green-700">{firmwareResult.command}</p>
                  <p>Queued to: {firmwareResult.queued.join(", ")}</p>
                </div>
              )}

              {/* File picker */}
              <div className="space-y-1">
                <label className="text-xs font-semibold text-[#1a4a73]">Firmware File <span className="text-[#7a93ab] font-normal">(emfw.cfg or .bin)</span></label>
                <input
                  type="file"
                  accept=".cfg,.bin,.tar,.zip"
                  onChange={(e) => setFirmwareFile(e.target.files?.[0] ?? null)}
                  className="w-full rounded-lg border border-[#c8ddf0] bg-[#f8fbfe] px-3 py-2 text-sm text-[#0f3356] outline-none file:mr-3 file:rounded file:border-0 file:bg-[#dceeff] file:px-3 file:py-1 file:text-xs file:font-semibold file:text-[#1a4a73] hover:file:bg-[#c5e0f8]"
                />
                {firmwareFile && <p className="text-[11px] text-[#5a7a96]">Selected: {firmwareFile.name} ({(firmwareFile.size / 1024).toFixed(1)} KB)</p>}
              </div>

              {/* Target devices */}
              <div className="space-y-1">
                <label className="text-xs font-semibold text-[#1a4a73]">Target Devices</label>
                <div className="max-h-40 overflow-y-auto rounded-lg border border-[#d5e8f7] divide-y divide-[#eef5fc]">
                  {devices.map((dev) => (
                    <label key={dev.serialNumber} className="flex items-center gap-2.5 px-3 py-2 hover:bg-[#f4f9ff] cursor-pointer">
                      <input
                        type="checkbox"
                        checked={firmwareTargetSerials.length === 0 || firmwareTargetSerials.includes(dev.serialNumber)}
                        onChange={(e) => {
                          if (e.target.checked) {
                            setFirmwareTargetSerials((prev) => [...prev, dev.serialNumber]);
                          } else {
                            setFirmwareTargetSerials((prev) => prev.filter((s) => s !== dev.serialNumber));
                          }
                        }}
                        className="h-3.5 w-3.5 accent-[#1a4a73]"
                      />
                      <span className="text-xs font-medium text-[#0f3356]">{dev.name || dev.serialNumber}</span>
                      <span className="text-[10px] text-[#5a7a96] font-mono ml-auto">{dev.serialNumber}</span>
                      <span className={`h-1.5 w-1.5 rounded-full shrink-0 ${dev.isOnline ? "bg-green-500" : "bg-gray-300"}`} />
                    </label>
                  ))}
                </div>
                <p className="text-[10px] text-[#7a93ab]">
                  {firmwareTargetSerials.length === 0 ? "All devices selected" : `${firmwareTargetSerials.length} device(s) selected`}
                  {" · "}
                  <button type="button" onClick={() => setFirmwareTargetSerials([])} className="underline hover:text-[#1a4a73]">Select All</button>
                </p>
              </div>

              {/* Actions */}
              <div className="flex justify-end gap-2 pt-1">
                <button type="button" onClick={() => { setFirmwareUpgradeOpen(false); setFirmwareResult(null); }} className="rounded-lg border border-[#c8ddf0] bg-white px-4 py-2 text-sm font-medium text-[#1a4a73] hover:bg-[#eef6ff]">
                  Close
                </button>
                <button
                  type="button"
                  disabled={firmwareUploading || !firmwareFile}
                  onClick={() => void submitFirmwareUpgrade()}
                  className="inline-flex items-center gap-2 rounded-lg bg-[#1a4a73] px-5 py-2 text-sm font-semibold text-white hover:bg-[#15395c] disabled:opacity-50"
                >
                  <UploadCloud className="h-4 w-4" />
                  {firmwareUploading ? "Uploading..." : "Upload & Push"}
                </button>
              </div>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}
