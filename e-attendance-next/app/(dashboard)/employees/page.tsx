"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ArrowRightLeft, Camera, ChevronDown, ChevronLeft, ChevronRight, Eye, Fingerprint, FolderOpen, KeyRound, Pencil, Plus, RotateCcw, ScanFace, Search, Settings2, Trash2, User, UserPlus, X } from "lucide-react";
import { type Employee } from "@/components/data/mock-data";
import { BsDateField } from "@/components/date/dual-date-input";
import { resolveApiBases } from "@/lib/api-bases";

const PAGE_SIZE = 50;
function getApiBases(): string[] {
  return resolveApiBases({ includeCurrentOrigin: true });
}

type SortKey = "name" | "code" | "department" | "designation" | "area" | "cardNumber" | "biometric" | "phone" | "status";
type EmployeePrivilege = "User" | "Enroller" | "Admin";
type EmployeeAssignment = {
  areaCode: string;
  areaName: string;
  departmentCode: string;
  departmentName: string;
};
type PersonnelEmployeeRow = {
  id: string;
  userId?: string;
  employeeCode: string;
  fullName: string;
  department: string;
  departments?: string[];
  departmentCode?: string;
  departmentCodes?: string[];
  areas?: string[];
  areaCode?: string;
  areaCodes?: string[];
  assignments?: EmployeeAssignment[];
  position?: string;
  isActive: boolean;
  privilege?: string;
  privilegeRaw?: number;
  cardNumber?: string;
  pictureDataUrl?: string;
  faceTemplateVisible?: string;
  faceTemplateInfrared?: string;
  fingerprintTemplates?: string;
  fingerprintCount?: number;
  faceCount?: number;
  photoCount?: number;
  verifyMode?: number;
  groupId?: number;
  timezoneProfile?: string;
  validFrom?: string;
  validTo?: string;
  expiresEnabled?: boolean;
  idNumber?: string;
  secondaryCard?: string;
  mobileNumber?: string;
  gender?: string;
  hireDate?: string;
  password?: string;
};
type PersonnelDepartmentRow = {
  id: string;
  code: string;
  name: string;
  areaCode?: string;
  areaName?: string;
};
type PersonnelPositionRow = {
  id: string;
  code: string;
  name: string;
  department?: string;
};
type EmployeeRow = Employee & {
  position: string;
  departments: string[];
  departmentCodes: string[];
  areas: string[];
  areaCodes: string[];
  assignments: EmployeeAssignment[];
};
type EmployeeContextMenuState = { id: string; x: number; y: number } | null;
type EmployeeExtra = {
  privilege: string;
  privilegeRaw?: number;
  cardNumber: string;
  pictureDataUrl: string;
  faceTemplateVisible?: string;
  faceTemplateInfrared?: string;
  fingerprintTemplates?: string;
  fingerprintCount?: number;
  faceCount?: number;
  photoCount?: number;
  verifyMode?: number;
  groupId?: number;
  timezoneProfile?: string;
  validFrom?: string;
  validTo?: string;
  expiresEnabled?: boolean;
  idNumber?: string;
  secondaryCard?: string;
  mobileNumber: string;
  gender: "Male" | "Female" | "Other";
  dob: string;
  password: string;
};
type EmployeeExtraMap = Record<string, EmployeeExtra>;
type EmployeeImportRow = { userId: string; fullName: string; areaName: string; departmentName: string; isActive: boolean };
type EmployeePhotoImportRow = { employeeCode: string; pictureDataUrl: string };
type ManagedDeviceOption = {
  id: string;
  serialNumber: string;
  name: string;
  isOnline?: boolean;
  supportsVisibleLightFace?: boolean;
  supportsInfraredFace?: boolean;
  supportsFingerprint?: boolean;
  faceAlgorithmVersion?: string;
  fingerprintAlgorithmVersion?: string;
};
type DeviceCommandRow = {
  id: string;
  commandText: string;
  createdUtc: string;
  sentUtc?: string;
  replyText?: string;
  status?: "pending" | "sent" | "replied";
};
type EnrollLiveItem = {
  id: string;
  label: string;
  status: "pending" | "sent" | "replied";
  returnCode: string;
  returnMessage: string;
  updatedAt: string;
  rawReply: string;
};
type RemoteEnrollQueueResult = {
  success: boolean;
  mode: "direct" | "queued";
  code?: number;
  queued?: boolean;
  commandId?: string;
  syncLogIds?: Array<string | number>;
  message?: string;
  details?: string;
  preProvisionedUser?: boolean;
  autoCreatedUser?: boolean;
  queuedCount?: number;
  queuedSteps?: string[];
};

const VERIFY_MODE_OPTIONS = [
  { value: 0, label: "Auto identify" },
  { value: -1, label: "Inherit from group mode" },
  { value: 1, label: "Fingerprint only" },
  { value: 2, label: "User ID only (PIN)" },
  { value: 3, label: "Password only" },
  { value: 4, label: "Card only" },
  { value: 5, label: "Fingerprint OR password" },
  { value: 6, label: "Fingerprint OR card" },
  { value: 7, label: "Card OR password" },
  { value: 8, label: "User ID + fingerprint" },
  { value: 9, label: "Fingerprint + password" },
  { value: 10, label: "Card + fingerprint" },
  { value: 11, label: "Card + password" },
  { value: 12, label: "Fingerprint + password + card" },
  { value: 13, label: "User ID + fingerprint + password" },
  { value: 14, label: "(User ID + fingerprint) OR (card + fingerprint)" },
  { value: 15, label: "Face only" },
  { value: 16, label: "Face + fingerprint" },
  { value: 17, label: "Face + password" },
  { value: 18, label: "Face + card" },
  { value: 19, label: "Face + fingerprint + card" },
  { value: 20, label: "Face + fingerprint + password" },
  { value: 21, label: "Finger vein only" },
  { value: 22, label: "Finger vein + password" },
  { value: 23, label: "Finger vein + card" },
  { value: 24, label: "Finger vein + password + card" },
  { value: 25, label: "Palm print only" },
  { value: 26, label: "Palm print + card" },
  { value: 27, label: "Palm print + face" },
  { value: 28, label: "Palm print + fingerprint" },
  { value: 29, label: "Palm print + fingerprint + face" },
] as const;

function describeVerifyMode(value?: number) {
  if (typeof value !== "number") {
    return "-";
  }

  const match = VERIFY_MODE_OPTIONS.find((option) => option.value === value);
  return match ? match.label : "Unknown mode";
}

const PROFILE_CACHE_KEY = "pb.attendance.employeeProfileCache.v1";

function readProfileCache(): EmployeeExtraMap {
  if (typeof window === "undefined") {
    return {};
  }
  try {
    const raw = window.localStorage.getItem(PROFILE_CACHE_KEY);
    if (!raw) {
      return {};
    }
    const parsed = JSON.parse(raw) as unknown;
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return {};
    }
    return parsed as EmployeeExtraMap;
  } catch {
    return {};
  }
}

function writeProfileCache(map: EmployeeExtraMap) {
  if (typeof window === "undefined") {
    return;
  }
  try {
    window.localStorage.setItem(PROFILE_CACHE_KEY, JSON.stringify(map));
  } catch {
    // Ignore storage quota/private mode errors and keep app usable.
  }
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? "GET").toUpperCase();
  const isMutation = method !== "GET" && method !== "HEAD";
  let lastError: string | null = null;

  for (const base of getApiBases()) {
    try {
      const call = () =>
        fetch(`${base}${path}`, {
          ...init,
          headers: {
            "Content-Type": "application/json",
            ...(init?.headers ?? {}),
          },
          cache: "no-store",
        });

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
      if (text) {
        try {
          const parsed = JSON.parse(text) as { message?: string; error?: string };
          lastError = parsed.message || parsed.error || text;
        } catch {
          lastError = text;
        }
      } else {
        lastError = `Request failed (${response.status})`;
      }

      // The server responded (just with an error). For create/update/delete requests do NOT
      // fail over to another API base — the write may already have happened, and a retry can
      // duplicate it (e.g. a false "already exists" after the record was actually created).
      if (isMutation) break;
    } catch (error) {
      lastError = error instanceof Error ? error.message : "Failed to fetch";
    }
  }

  throw new Error(lastError ?? "Failed to fetch");
}

async function requestText(path: string, init?: RequestInit): Promise<string> {
  let lastError: string | null = null;

  for (const base of getApiBases()) {
    try {
      const call = () =>
        fetch(`${base}${path}`, {
          ...init,
          cache: "no-store",
        });

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
        return await response.text();
      }

      const text = await response.text();
      lastError = text || `Request failed (${response.status})`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : "Failed to fetch";
    }
  }

  throw new Error(lastError ?? "Failed to fetch");
}

async function requestBlob(path: string, init?: RequestInit): Promise<Blob> {
  let lastError: string | null = null;

  for (const base of getApiBases()) {
    try {
      const call = () => fetch(`${base}${path}`, { ...init, cache: "no-store" });
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
        return await response.blob();
      }
      lastError = (await response.text()) || `Request failed (${response.status})`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : "Failed to fetch";
    }
  }

  throw new Error(lastError ?? "Failed to fetch");
}

function sortRows(data: EmployeeRow[], key: SortKey, asc: boolean, extras: Record<string, EmployeeExtra>) {
  return [...data].sort((a, b) => {
    let result = 0;
    if (key === "area") {
      const areaA = (a.areas.length > 0 ? a.areas : [a.area || ""]).join(", ").toLowerCase();
      const areaB = (b.areas.length > 0 ? b.areas : [b.area || ""]).join(", ").toLowerCase();
      result = areaA.localeCompare(areaB);
    } else if (key === "cardNumber") {
      const cardA = (extras[a.id]?.cardNumber || "").toLowerCase();
      const cardB = (extras[b.id]?.cardNumber || "").toLowerCase();
      result = cardA.localeCompare(cardB);
    } else if (key === "biometric") {
      const extraA = extras[a.id];
      const extraB = extras[b.id];
      const countA = (extraA?.fingerprintCount ?? 0) + (extraA?.faceCount ?? 0) + (extraA?.photoCount ?? 0) + (extraA?.password ? 1 : 0);
      const countB = (extraB?.fingerprintCount ?? 0) + (extraB?.faceCount ?? 0) + (extraB?.photoCount ?? 0) + (extraB?.password ? 1 : 0);
      result = countA - countB;
    } else if (key === "phone" || key === "status" || key === "name" || key === "code" || key === "department" || key === "designation") {
      const valueA = String(a[key] ?? "").toLowerCase();
      const valueB = String(b[key] ?? "").toLowerCase();
      result = valueA.localeCompare(valueB);
    }
    return asc ? result : -result;
  });
}

function parseCsvLine(line: string): string[] {
  const result: string[] = [];
  let current = "";
  let inQuotes = false;

  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === '"') {
      if (inQuotes && line[i + 1] === '"') {
        current += '"';
        i++;
      } else {
        inQuotes = !inQuotes;
      }
      continue;
    }

    if (ch === "," && !inQuotes) {
      result.push(current.trim());
      current = "";
      continue;
    }

    current += ch;
  }

  result.push(current.trim());
  return result;
}

function toHeaderMap(headerRow: string[]) {
  const map = new Map<string, number>();
  headerRow.forEach((value, index) => {
    map.set(value.trim().toLowerCase(), index);
  });
  return map;
}

// Columns the user can show/hide from the employee table via the preferences (gear) menu.
// (The selection checkbox, User ID, Name and Actions columns are always shown.)
const EMP_COLUMN_PREFS_KEY = "e_attendance_employee_columns";
const TOGGLEABLE_COLUMNS: { key: string; label: string }[] = [
  { key: "photo", label: "Photo" },
  { key: "area", label: "Area" },
  { key: "department", label: "Department" },
  { key: "privilege", label: "Privilege" },
  { key: "card", label: "Card No" },
  { key: "biometric", label: "Biometric" },
  { key: "phone", label: "Phone" },
  { key: "status", label: "Status" },
];
const DEFAULT_COLUMN_PREFS: Record<string, boolean> = Object.fromEntries(
  TOGGLEABLE_COLUMNS.map((column) => [column.key, true]),
);

export default function EmployeesPage() {
  const [employeeRows, setEmployeeRows] = useState<EmployeeRow[]>([]);
  const [employeeExtras, setEmployeeExtras] = useState<Record<string, EmployeeExtra>>({});
  const [totalCount, setTotalCount] = useState(0);
  const [search, setSearch] = useState("");
  const [departmentFilter, setDepartmentFilter] = useState("all");
  const [areaFilter, setAreaFilter] = useState("all");
  const [status, setStatus] = useState("all");
  const [sortKey, setSortKey] = useState<SortKey>("name");
  const [sortAsc, setSortAsc] = useState(true);
  const [page, setPage] = useState(1);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editingEmployeeId, setEditingEmployeeId] = useState<string | null>(null);
  const [selectedEmployeeIds, setSelectedEmployeeIds] = useState<string[]>([]);
  const [contextMenu, setContextMenu] = useState<EmployeeContextMenuState>(null);
  const [addSaving, setAddSaving] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);
  const [assignmentAreaFilters, setAssignmentAreaFilters] = useState<string[]>([]);
  const [importMenuOpen, setImportMenuOpen] = useState(false);
  const [exportMenuOpen, setExportMenuOpen] = useState(false);
  const importMenuButtonRef = useRef<HTMLButtonElement | null>(null);
  const exportMenuButtonRef = useRef<HTMLButtonElement | null>(null);
  const [importMenuPos, setImportMenuPos] = useState<{ left: number; top: number } | null>(null);
  const [exportMenuPos, setExportMenuPos] = useState<{ left: number; top: number } | null>(null);
  const [sendUserModalOpen, setSendUserModalOpen] = useState(false);
  const [sendUserBusy, setSendUserBusy] = useState(false);
  const [selectedSendDeviceSerials, setSelectedSendDeviceSerials] = useState<string[]>([]);
  const [sendUserError, setSendUserError] = useState<string | null>(null);
  const [sendUserNotice, setSendUserNotice] = useState<string | null>(null);
  const [bulkVerifyMode, setBulkVerifyMode] = useState(0);
  const [bulkVerifyBusy, setBulkVerifyBusy] = useState(false);
  const [bulkVerifyError, setBulkVerifyError] = useState<string | null>(null);
  // Column visibility preferences (persisted in localStorage; gear menu top-right).
  const [colPrefs, setColPrefs] = useState<Record<string, boolean>>(DEFAULT_COLUMN_PREFS);
  const [colMenuOpen, setColMenuOpen] = useState(false);

  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(EMP_COLUMN_PREFS_KEY);
      if (raw) {
        setColPrefs({ ...DEFAULT_COLUMN_PREFS, ...(JSON.parse(raw) as Record<string, boolean>) });
      }
    } catch {
      // ignore — fall back to defaults
    }
  }, []);

  function updateColPref(key: string, value: boolean) {
    setColPrefs((prev) => {
      const next = { ...prev, [key]: value };
      try {
        window.localStorage.setItem(EMP_COLUMN_PREFS_KEY, JSON.stringify(next));
      } catch {
        // ignore persistence errors
      }
      return next;
    });
  }

  function resetColPrefs() {
    setColPrefs(DEFAULT_COLUMN_PREFS);
    try {
      window.localStorage.removeItem(EMP_COLUMN_PREFS_KEY);
    } catch {
      // ignore
    }
  }

  const visibleColCount = 4 + TOGGLEABLE_COLUMNS.filter((column) => colPrefs[column.key]).length;
  const [bulkVerifyNotice, setBulkVerifyNotice] = useState<string | null>(null);
  const [transferModalOpen, setTransferModalOpen] = useState(false);
  const [transferBusy, setTransferBusy] = useState(false);
  const [transferError, setTransferError] = useState<string | null>(null);
  const [transferAreaFilters, setTransferAreaFilters] = useState<string[]>([]);
  const [transferDepartmentCodes, setTransferDepartmentCodes] = useState<string[]>([]);
  const [transferPrimaryDepartmentCode, setTransferPrimaryDepartmentCode] = useState("");
  const [managedDevices, setManagedDevices] = useState<ManagedDeviceOption[]>([]);
  const [accessGroups, setAccessGroups] = useState<Array<{id:string;groupNo:string;name:string}>>([]);
  const [departmentRows, setDepartmentRows] = useState<PersonnelDepartmentRow[]>([]);
  const [positionRows, setPositionRows] = useState<PersonnelPositionRow[]>([]);
  const [remoteDevicePickerOpen, setRemoteDevicePickerOpen] = useState(false);
  const [pendingRemoteToggle, setPendingRemoteToggle] = useState<"face" | "fp" | null>(null);
  const [enrollSaving, setEnrollSaving] = useState(false);
  const [enrollNudging, setEnrollNudging] = useState(false);
  const [enrollSession, setEnrollSession] = useState<{ serial: string; pin: string; startedAtMs: number; running: boolean } | null>(null);
  const [enrollLiveItems, setEnrollLiveItems] = useState<EnrollLiveItem[]>([]);
  const [cameraPickerOpen, setCameraPickerOpen] = useState(false);
  const [cameraBusy, setCameraBusy] = useState(false);
  const [cameraError, setCameraError] = useState<string | null>(null);
  const [cameraDevices, setCameraDevices] = useState<Array<{ id: string; label: string }>>([]);
  const [activeCameraId, setActiveCameraId] = useState("");
  const [addForm, setAddForm] = useState({
    userId: "",
    firstName: "",
    lastName: "",
    department: "",
    departmentCodes: [] as string[],
    position: "",
    privilege: "User" as EmployeePrivilege,
    verifyMode: 0,
    groupId: 1,
    cardNumber: "",
    pictureDataUrl: "",
    mobileNumber: "",
    gender: "Male" as "Male" | "Female" | "Other",
    dob: "",
    password: "",
    remoteEnrollDeviceSerial: "",
    remoteEnrollFace: false,
    remoteEnrollFp: false,
    remoteFpIndex: "0",
    remoteFaceType: "9",
  });
  const browseInputRef = useRef<HTMLInputElement | null>(null);
  const cameraVideoRef = useRef<HTMLVideoElement | null>(null);
  const cameraCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const cameraStreamRef = useRef<MediaStream | null>(null);
  const importEmployeesInputRef = useRef<HTMLInputElement | null>(null);
  const importEmployeePhotosInputRef = useRef<HTMLInputElement | null>(null);
  const importEmployeePhotoFilesInputRef = useRef<HTMLInputElement | null>(null);

  const getAreaFilterKey = (areaCode?: string | null, areaLabel?: string | null) => {
    const normalizedCode = (areaCode ?? "").trim();
    if (normalizedCode) {
      return `code:${normalizedCode}`;
    }

    const normalizedLabel = (areaLabel ?? "").trim().toLowerCase();
    return `label:${normalizedLabel || "unassigned"}`;
  };

  const returnCodeMessage = (code: string) => {
    switch (code) {
      case "0":
        return "Success";
      case "-1":
        return "Parameter error";
      case "-3":
        return "Access error";
      case "2":
        return "User already has biometric data";
      case "4":
        return "Enrollment failed (poor quality)";
      case "5":
        return "Fingerprint already exists in DB";
      case "6":
        return "Enrollment cancelled";
      case "7":
        return "Device busy";
      case "-100":
        return "Face algorithm error";
      case "-1002":
        return "Not supported";
      case "-1003":
        return "Face enrollment timeout or poor capture quality";
      case "":
        return "";
      default:
        return "Device returned code " + code;
    }
  };

  const queueSummaryText = (result: RemoteEnrollQueueResult) => {
    const bits: string[] = [];
    if (typeof result.code === "number") {
      const codeText = String(result.code);
      const codeLabel = returnCodeMessage(codeText);
      bits.push(`Code: ${codeText}${codeLabel ? ` (${codeLabel})` : ""}.`);
    }
    if (result.message) {
      bits.push(result.message);
    }
    if (typeof result.queued === "boolean") {
      bits.push(result.queued ? "Execution mode: queued." : "Execution mode: direct.");
    }
    if (typeof result.preProvisionedUser === "boolean") {
      bits.push(result.preProvisionedUser ? "User provisioned before enrollment." : "User pre-provision skipped.");
    }
    if (typeof result.queuedCount === "number" && result.queuedCount > 0) {
      bits.push(`Queued commands: ${result.queuedCount}.`);
    }
    if (Array.isArray(result.queuedSteps) && result.queuedSteps.length > 0) {
      bits.push(`Steps: ${result.queuedSteps.join(" -> ")}`);
    }
    return bits.join(" ");
  };

  const parseReplyMeta = (replyText?: string) => {
    const text = (replyText ?? "").trim();
    if (!text) {
      return { returnCode: "", cmd: "", raw: "" };
    }
    const returnMatch = /(?:^|[&\s])Return=([-\d]+)/i.exec(text);
    const cmdMatch = /(?:^|[&\s])CMD=([A-Z_]+)/i.exec(text);
    return {
      returnCode: returnMatch?.[1] ?? "",
      cmd: cmdMatch?.[1] ?? "",
      raw: text,
    };
  };

  const detectEnrollLabel = (commandText: string) => {
    const cmd = commandText.trim().toUpperCase();
    if (cmd.includes("ENROLL_FP")) {
      return "Finger print enrolled";
    }
    if (cmd.includes("ENROLL_BIO")) {
      return "Face";
    }
    return "Enroll";
  };

  const returnCodeClass = (code: string) => {
    if (code === "0") {
      return "bg-[#e7f7ef] text-[#1f7a45] border-[#bfe7cf]";
    }
    if (code === "-1" || code === "-3" || code === "4" || code === "5") {
      return "bg-[#fff1f1] text-[#a33737] border-[#f3c6c6]";
    }
    if (code === "2" || code === "6" || code === "7") {
      return "bg-[#fff7e9] text-[#9a6a12] border-[#f2dfb2]";
    }
    return "bg-[#edf3fb] text-[#446384] border-[#cfe0f3]";
  };

  const loadEmployees = async (opts?: { page?: number; search?: string; status?: string }) => {
    const cached = readProfileCache();
    try {
      const p = opts?.page ?? 1;
      const s = opts?.search ?? "";
      const st = opts?.status ?? "all";
      const params = new URLSearchParams({ page: String(p), pageSize: String(PAGE_SIZE) });
      if (s.trim()) params.set("search", s.trim());
      if (st !== "all") params.set("status", st);
      const rowsResponse = await requestJson<any>(`/api/personnel/employees?${params.toString()}`);
      const rows: PersonnelEmployeeRow[] = Array.isArray(rowsResponse)
        ? rowsResponse
        : (rowsResponse?.items ?? rowsResponse?.Items ?? rowsResponse?.data ?? []);
      if (!Array.isArray(rowsResponse) && typeof rowsResponse?.totalCount === "number") {
        setTotalCount(rowsResponse.totalCount);
      } else {
        setTotalCount(rows.length);
      }
      const normalizedRows = rows.map((item) => {
        const raw = item as PersonnelEmployeeRow & {
          Id?: string;
          EmployeeCode?: string;
          FullName?: string;
          Department?: string;
          Departments?: string[];
          DepartmentCode?: string;
          DepartmentCodes?: string[];
          Areas?: string[];
          AreaCode?: string;
          AreaCodes?: string[];
          Assignments?: EmployeeAssignment[];
          Position?: string;
          IsActive?: boolean;
          Privilege?: string;
          PrivilegeRaw?: number;
          CardNumber?: string;
          PictureDataUrl?: string;
          FaceTemplateVisible?: string;
          FaceTemplateInfrared?: string;
          FingerprintTemplates?: string;
          FingerprintCount?: number;
          FaceCount?: number;
          PhotoCount?: number;
          VerifyMode?: number;
          GroupId?: number;
          TimezoneProfile?: string;
          ValidFrom?: string;
          ValidTo?: string;
          ExpiresEnabled?: boolean;
          IdNumber?: string;
          SecondaryCard?: string;
          MobileNumber?: string;
          Gender?: string;
          HireDate?: string;
          Password?: string;
        };
        return {
          id: raw.id ?? raw.Id ?? "",
          userId: raw.userId ?? "",
          employeeCode: raw.employeeCode ?? raw.EmployeeCode ?? "",
          fullName: raw.fullName ?? raw.FullName ?? "",
          department: raw.department ?? raw.Department ?? "",
          departments: raw.departments ?? raw.Departments ?? [],
          departmentCode: raw.departmentCode ?? raw.DepartmentCode ?? "",
          departmentCodes: raw.departmentCodes ?? raw.DepartmentCodes ?? [],
          areas: raw.areas ?? raw.Areas ?? [],
          areaCode: raw.areaCode ?? raw.AreaCode ?? "",
          areaCodes: raw.areaCodes ?? raw.AreaCodes ?? [],
          assignments: raw.assignments ?? raw.Assignments ?? [],
          position: raw.position ?? raw.Position ?? "",
          isActive: raw.isActive ?? raw.IsActive ?? true,
          privilege: raw.privilege ?? raw.Privilege ?? "",
          privilegeRaw: raw.privilegeRaw ?? raw.PrivilegeRaw,
          cardNumber: raw.cardNumber ?? raw.CardNumber ?? "",
          pictureDataUrl: raw.pictureDataUrl ?? raw.PictureDataUrl ?? "",
          faceTemplateVisible: raw.faceTemplateVisible ?? raw.FaceTemplateVisible ?? "",
          faceTemplateInfrared: raw.faceTemplateInfrared ?? raw.FaceTemplateInfrared ?? "",
          fingerprintTemplates: raw.fingerprintTemplates ?? raw.FingerprintTemplates ?? "",
          fingerprintCount: raw.fingerprintCount ?? raw.FingerprintCount ?? 0,
          faceCount: raw.faceCount ?? raw.FaceCount ?? 0,
          photoCount: raw.photoCount ?? raw.PhotoCount ?? 0,
          verifyMode: raw.verifyMode ?? raw.VerifyMode,
          groupId: raw.groupId ?? raw.GroupId,
          timezoneProfile: raw.timezoneProfile ?? raw.TimezoneProfile ?? "",
          validFrom: raw.validFrom ?? raw.ValidFrom ?? "",
          validTo: raw.validTo ?? raw.ValidTo ?? "",
          expiresEnabled: raw.expiresEnabled ?? raw.ExpiresEnabled,
          idNumber: raw.idNumber ?? raw.IdNumber ?? "",
          secondaryCard: raw.secondaryCard ?? raw.SecondaryCard ?? "",
          mobileNumber: raw.mobileNumber ?? raw.MobileNumber ?? "",
          gender: raw.gender ?? raw.Gender ?? "",
          hireDate: raw.hireDate ?? raw.HireDate ?? "",
          password: raw.password ?? raw.Password ?? "",
        } as PersonnelEmployeeRow;
      });
      setEmployeeRows(
        normalizedRows.map((item) => {
          const departments = Array.from(
            new Set(
              ((item.departments?.length ? item.departments : [item.department]) ?? [])
                .map((value) => value.trim())
                .filter((value) => value.length > 0),
            ),
          );
          const departmentCodes = Array.from(
            new Set(
              ((item.departmentCodes?.length ? item.departmentCodes : item.departmentCode ? [item.departmentCode] : []) ?? [])
                .map((value) => value.trim())
                .filter((value) => value.length > 0),
            ),
          );
          const areas = Array.from(
            new Set(
              ((item.areas?.length ? item.areas : item.areaCode ? [item.areaCode] : []) ?? [])
                .map((value) => value.trim())
                .filter((value) => value.length > 0),
            ),
          );
          const areaCodes = Array.from(
            new Set(
              ((item.areaCodes?.length ? item.areaCodes : item.areaCode ? [item.areaCode] : []) ?? [])
                .map((value) => value.trim())
                .filter((value) => value.length > 0),
            ),
          );
          const displayName = item.fullName?.trim() || item.employeeCode;
          return {
            id: item.id,
            code: item.employeeCode,
            name: displayName,
            department: item.department || departments[0] || "",
            departments,
            departmentCodes,
            areas,
            areaCodes,
            assignments: Array.isArray(item.assignments) ? item.assignments : [],
            position: item.position || "",
            designation: item.privilege || "User",
            phone: "-",
            status: item.isActive ? "active" : "inactive",
            avatar: item.pictureDataUrl || "",
          };
        }),
      );
      const mergedByCode = Object.fromEntries(
        normalizedRows.map((item) => {
          const local = cached[item.employeeCode];
          const merged: EmployeeExtra = {
            privilege: item.privilege ?? local?.privilege ?? "User",
            privilegeRaw: item.privilegeRaw ?? local?.privilegeRaw,
            cardNumber: item.cardNumber ?? local?.cardNumber ?? "",
            pictureDataUrl: item.pictureDataUrl ?? local?.pictureDataUrl ?? "",
            faceTemplateVisible: item.faceTemplateVisible ?? local?.faceTemplateVisible ?? "",
            faceTemplateInfrared: item.faceTemplateInfrared ?? local?.faceTemplateInfrared ?? "",
            fingerprintTemplates: item.fingerprintTemplates ?? local?.fingerprintTemplates ?? "",
            fingerprintCount: item.fingerprintCount ?? local?.fingerprintCount ?? 0,
            faceCount: item.faceCount ?? local?.faceCount ?? 0,
            photoCount: item.photoCount ?? local?.photoCount ?? 0,
            verifyMode: item.verifyMode ?? local?.verifyMode ?? 0,
            groupId: item.groupId ?? local?.groupId,
            timezoneProfile: item.timezoneProfile ?? local?.timezoneProfile ?? "",
            validFrom: item.validFrom ?? local?.validFrom ?? "",
            validTo: item.validTo ?? local?.validTo ?? "",
            expiresEnabled: item.expiresEnabled ?? local?.expiresEnabled,
            idNumber: item.idNumber ?? local?.idNumber ?? "",
            secondaryCard: item.secondaryCard ?? local?.secondaryCard ?? "",
            mobileNumber: item.mobileNumber ?? local?.mobileNumber ?? "",
            gender: item.gender === "Female" || item.gender === "Other" ? item.gender : local?.gender ?? "Male",
            dob: item.hireDate ?? local?.dob ?? "",
            password: item.password ?? local?.password ?? "",
          };
          return [item.employeeCode, merged];
        }),
      ) as EmployeeExtraMap;
      writeProfileCache(mergedByCode);
      setEmployeeExtras(
        Object.fromEntries(
          normalizedRows.map((item) => [item.id, mergedByCode[item.employeeCode] ?? {
            privilege: "User",
            cardNumber: "",
            pictureDataUrl: "",
            faceTemplateVisible: "",
            faceTemplateInfrared: "",
            fingerprintTemplates: "",
            fingerprintCount: 0,
            faceCount: 0,
            photoCount: 0,
            verifyMode: 0,
            timezoneProfile: "",
            validFrom: "",
            validTo: "",
            idNumber: "",
            secondaryCard: "",
            mobileNumber: "",
            gender: "Male",
            dob: "",
            password: "",
          }]),
        ),
      );
    } catch {
      const localRows = employeeRows.map((item) => ({
        ...item,
        avatar: cached[item.code]?.pictureDataUrl || item.avatar,
      }));
      setEmployeeRows(localRows);
    }
  };

  const loadManagedDevices = async () => {
    try {
      const res = await requestJson<{ items?: ManagedDeviceOption[] } | ManagedDeviceOption[]>("/api/devices");
      const rows = Array.isArray(res) ? res : ((res as { items?: ManagedDeviceOption[] }).items ?? []);
      setManagedDevices(rows);
    } catch {
      setManagedDevices([]);
    }
  };

  const loadDepartments = async () => {
    try {
      const res = await requestJson<{ items?: PersonnelDepartmentRow[] } | PersonnelDepartmentRow[]>("/api/personnel/departments");
      const rows = Array.isArray(res) ? res : ((res as { items?: PersonnelDepartmentRow[] }).items ?? []);
      setDepartmentRows(
        rows.map((item) => {
          const raw = item as PersonnelDepartmentRow & {
            Id?: string;
            Code?: string;
            Name?: string;
            AreaCode?: string;
            AreaName?: string;
          };
          return {
            id: raw.id ?? raw.Id ?? "",
            code: raw.code ?? raw.Code ?? "",
            name: raw.name ?? raw.Name ?? "",
            areaCode: raw.areaCode ?? raw.AreaCode ?? "",
            areaName: raw.areaName ?? raw.AreaName ?? "",
          } as PersonnelDepartmentRow;
        }),
      );
    } catch {
      setDepartmentRows([]);
    }
  };

  const loadPositions = async () => {
    try {
      const rows = await requestJson<PersonnelPositionRow[]>("/api/personnel/positions");
      setPositionRows(
        (rows ?? []).map((item) => {
          const raw = item as PersonnelPositionRow & {
            Id?: string;
            Code?: string;
            Name?: string;
            Department?: string;
          };
          return {
            id: raw.id ?? raw.Id ?? "",
            code: raw.code ?? raw.Code ?? "",
            name: raw.name ?? raw.Name ?? "",
            department: raw.department ?? raw.Department ?? "",
          } as PersonnelPositionRow;
        }),
      );
    } catch {
      setPositionRows([]);
    }
  };

  useEffect(() => {
    void loadEmployees({ page: 1, search: "", status: "all" });
    void loadManagedDevices();
    void loadDepartments();
    void loadPositions();
    // Load access groups for the Access Group dropdown
    void (async () => {
      try {
        const ac = await requestJson<{groups?: Array<{id:string;groupNo:string;name:string}>}>("/api/app-config/access-control");
        setAccessGroups((ac.groups ?? []).filter(g => g.groupNo && g.name));
      } catch { setAccessGroups([]); }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reload employees when search, status, or page changes (debounced for search)
  const searchRef = useRef(search);
  searchRef.current = search;
  useEffect(() => {
    const timer = setTimeout(() => {
      setPage(1);
      void loadEmployees({ page: 1, search: searchRef.current, status });
    }, 300);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, status]);

  useEffect(() => {
    void loadEmployees({ page, search: searchRef.current, status });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page]);

  useEffect(() => {
    setSelectedEmployeeIds((prev) => prev.filter((id) => employeeRows.some((row) => row.id === id)));
  }, [employeeRows]);

  useEffect(() => {
    if (!contextMenu) {
      return;
    }
    const close = () => setContextMenu(null);
    const onEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        close();
      }
    };

    window.addEventListener("click", close);
    window.addEventListener("scroll", close, true);
    window.addEventListener("keydown", onEscape);
    return () => {
      window.removeEventListener("click", close);
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("keydown", onEscape);
    };
  }, [contextMenu]);

  useEffect(() => {
    if (!importMenuOpen && !exportMenuOpen) {
      return;
    }
    const close = () => {
      setImportMenuOpen(false);
      setExportMenuOpen(false);
      setImportMenuPos(null);
      setExportMenuPos(null);
    };
    window.addEventListener("click", close);
    window.addEventListener("scroll", close, true);
    window.addEventListener("resize", close);
    return () => {
      window.removeEventListener("click", close);
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("resize", close);
    };
  }, [importMenuOpen, exportMenuOpen]);

  useEffect(() => {
    return () => {
      const stream = cameraStreamRef.current;
      if (!stream) {
        return;
      }
      stream.getTracks().forEach((track) => track.stop());
      cameraStreamRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (!drawerOpen) {
      stopCameraStream();
      setCameraPickerOpen(false);
      setCameraBusy(false);
      setCameraError(null);
    }
  }, [drawerOpen]);

  useEffect(() => {
    setSelectedSendDeviceSerials((prev) =>
      prev.filter((serial) => managedDevices.some((device) => device.serialNumber === serial)),
    );
  }, [managedDevices]);

  const loadEnrollLiveStatus = async (session: { serial: string; pin: string; startedAtMs: number; running: boolean }) => {
    const rows = await requestJson<DeviceCommandRow[]>(`/api/devices/${encodeURIComponent(session.serial)}/commands?take=200`);
    const startedAt = session.startedAtMs;
    const pinToken = `PIN=${session.pin}`.toUpperCase();
    const relevant = rows.filter((row) => {
      const createdMs = Date.parse(row.createdUtc);
      if (Number.isFinite(createdMs) && createdMs < startedAt - 5000) {
        return false;
      }
      const text = row.commandText.trim().toUpperCase();
      const enrollCommand =
        text.includes("ENROLL_FP") ||
        text.includes("ENROLL_BIO");
      if (!enrollCommand) {
        return false;
      }
      return text.includes(pinToken);
    });

    const normalized = relevant.map((row) => {
      const meta = parseReplyMeta(row.replyText);
      const status = row.status ?? (row.replyText ? "replied" : row.sentUtc ? "sent" : "pending");
      return {
        id: row.id,
        label: detectEnrollLabel(row.commandText),
        status,
        returnCode: meta.returnCode,
        returnMessage: returnCodeMessage(meta.returnCode),
        updatedAt: row.replyText ? new Date().toLocaleTimeString() : "",
        rawReply: meta.raw,
      } as EnrollLiveItem;
    });

    setEnrollLiveItems(normalized);

    if (normalized.length > 0 && normalized.every((item) => item.status === "replied")) {
      setEnrollSession((prev) => (prev ? { ...prev, running: false } : prev));
    }
  };

  const pollEnrollmentStatusByCommandId = async (commandId: string) => {
    const startedAt = Date.now();
    const timeoutMs = 30000;
    while (Date.now() - startedAt < timeoutMs) {
      try {
        const row = await requestJson<{
          completed: boolean;
          failed: boolean;
          status?: string;
          response?: string;
          code?: number | null;
          errorMessage?: string;
        }>(`/api/faceenrollment/status/${encodeURIComponent(commandId)}`);

        if (row.completed || row.failed) {
          const codeText = row.code == null ? "" : String(row.code);
          setEnrollLiveItems([
            {
              id: `status-${Date.now()}`,
              label: "Face",
              status: "replied",
              returnCode: codeText,
              returnMessage: row.failed ? (row.errorMessage || "Enrollment failed") : "Success",
              updatedAt: new Date().toLocaleTimeString(),
              rawReply: row.response ?? "",
            },
          ]);
          setEnrollSession((prev) => (prev ? { ...prev, running: false } : prev));
          return;
        }
      } catch {
        // Keep polling on transient API errors.
      }
      await new Promise((resolve) => setTimeout(resolve, 2000));
    }
    setEnrollSession((prev) => (prev ? { ...prev, running: false } : prev));
  };

  useEffect(() => {
    if (!drawerOpen || !enrollSession) {
      return;
    }

    let cancelled = false;
    let inFlight = false;

    const tick = async () => {
      if (cancelled || inFlight) {
        return;
      }
      inFlight = true;
      try {
        await loadEnrollLiveStatus(enrollSession);
      } catch {
        // Keep UI stable if polling fails temporarily.
      } finally {
        inFlight = false;
      }
    };

    void tick();
    const handle = window.setInterval(() => {
      void tick();
    }, enrollSession.running ? 2000 : 5000);

    return () => {
      cancelled = true;
      window.clearInterval(handle);
    };
  }, [drawerOpen, enrollSession]);

  const departmentDirectory = useMemo(() => {
    const map = new Map<string, { name: string; areaLabel: string }>();
    for (const row of departmentRows) {
      const name = row.name?.trim() ?? "";
      if (!name) {
        continue;
      }
      const areaCode = row.areaCode?.trim() ?? "";
      const areaName = row.areaName?.trim() ?? "";
      const areaLabel = areaName || areaCode || "Unassigned";
      const key = name.toLowerCase();
      if (!map.has(key)) {
        map.set(key, { name, areaLabel });
      }
    }
    return map;
  }, [departmentRows]);

  const departmentByCode = useMemo(() => {
    const map = new Map<string, { code: string; name: string; areaCode: string; areaLabel: string }>();
    for (const row of departmentRows) {
      const code = row.code?.trim() ?? "";
      const name = row.name?.trim() ?? "";
      if (!code || !name) {
        continue;
      }
      const areaCode = row.areaCode?.trim() ?? "";
      const areaName = row.areaName?.trim() ?? "";
      map.set(code, {
        code,
        name,
        areaCode,
        areaLabel: areaName || areaCode || "Unassigned",
      });
    }
    return map;
  }, [departmentRows]);

  const buildAssignmentsFromCodes = (codes: string[]): EmployeeAssignment[] =>
    codes
      .map((value) => {
        const item = departmentByCode.get(value);
        if (!item) {
          return null;
        }

        return {
          areaCode: item.areaCode,
          areaName: item.areaLabel,
          departmentCode: item.code,
          departmentName: item.name,
        };
      })
      .filter((item): item is EmployeeAssignment => item !== null);

  const resolveDepartmentCodesForEmployee = (employee: EmployeeRow) => {
    const fromCodes = employee.departmentCodes
      .map((value) => value.trim())
      .filter((value) => value.length > 0 && departmentByCode.has(value));
    if (fromCodes.length > 0) {
      return Array.from(new Set(fromCodes));
    }

    const fromAssignments = employee.assignments
      .map((item) => item.departmentCode?.trim() ?? "")
      .filter((value) => value.length > 0 && departmentByCode.has(value));
    if (fromAssignments.length > 0) {
      return Array.from(new Set(fromAssignments));
    }

    const names = [
      ...employee.departments,
      employee.department,
      ...employee.assignments.map((item) => item.departmentName ?? ""),
    ]
      .map((value) => value.trim().toLowerCase())
      .filter((value) => value.length > 0);

    return Array.from(
      new Set(
        Array.from(departmentByCode.values())
          .filter((item) => names.includes(item.name.trim().toLowerCase()))
          .map((item) => item.code),
      ),
    );
  };

  useEffect(() => {
    if (departmentByCode.size === 0) {
      return;
    }

    setEmployeeRows((currentRows) => {
      let changed = false;
      const nextRows = currentRows.map((employee) => {
        const departmentCodes = resolveDepartmentCodesForEmployee(employee);
        if (departmentCodes.length === 0) {
          return employee;
        }

        const departments = departmentCodes
          .map((value) => departmentByCode.get(value)?.name ?? "")
          .filter((value, index, array) => value.length > 0 && array.indexOf(value) === index);
        const areas = Array.from(
          new Set(
            departmentCodes
              .map((value) => departmentByCode.get(value)?.areaLabel ?? "")
              .filter((value) => value.length > 0),
          ),
        );
        const areaCodes = Array.from(
          new Set(
            departmentCodes
              .map((value) => departmentByCode.get(value)?.areaCode ?? "")
              .filter((value) => value.length > 0),
          ),
        );
        const assignments = buildAssignmentsFromCodes(departmentCodes);
        const primaryDepartment = departments[0] ?? employee.department;

        const same =
          employee.department === primaryDepartment &&
          employee.departments.join("|") === departments.join("|") &&
          employee.departmentCodes.join("|") === departmentCodes.join("|") &&
          employee.areas.join("|") === areas.join("|") &&
          employee.areaCodes.join("|") === areaCodes.join("|");

        if (same) {
          return employee;
        }

        changed = true;
        return {
          ...employee,
          department: primaryDepartment,
          departments,
          departmentCodes,
          areas,
          areaCodes,
          assignments,
        };
      });

      return changed ? nextRows : currentRows;
    });
    // Re-resolve employees only when setup department metadata changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [departmentByCode]);

  const getAreaFilterKeysForDepartments = (codes: string[]) =>
    Array.from(
      new Set(
        codes
          .map((value) => {
            const item = departmentByCode.get(value);
            return item ? getAreaFilterKey(item.areaCode, item.areaLabel) : "";
          })
          .filter((value) => value.length > 0),
      ),
    );

  const getAreaLabelsForFilterKeys = (filterKeys: string[]) =>
    areaSelectionOptions
      .filter((item) => filterKeys.includes(item.filterKey))
      .map((item) => item.label);

  const areaSelectionOptions = useMemo(() => {
    const map = new Map<string, { code: string; label: string; filterKey: string; departments: Array<{ code: string; name: string }> }>();
    for (const row of departmentRows) {
      const code = row.code?.trim() ?? "";
      const name = row.name?.trim() ?? "";
      if (!code || !name) {
        continue;
      }
      const areaCode = row.areaCode?.trim() ?? "";
      const areaName = row.areaName?.trim() ?? "";
      const label = areaName || areaCode || "Unassigned";
      const filterKey = getAreaFilterKey(areaCode, label);
      const key = filterKey;
      const existing = map.get(key);
      const nextDepartment = { code, name };
      if (existing) {
        existing.departments.push(nextDepartment);
        continue;
      }
      map.set(key, {
        code: areaCode,
        label,
        filterKey,
        departments: [nextDepartment],
      });
    }
    return Array.from(map.values())
      .map((item) => ({
        ...item,
        departments: [...item.departments].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" })),
      }))
      .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: "base" }));
  }, [departmentRows]);

  const areaOptions = useMemo(() => {
    return areaSelectionOptions.map((item) => item.label);
  }, [areaSelectionOptions]);

  const selectedAssignmentAreaFilter = assignmentAreaFilters[0] ?? "";
  const selectedTransferAreaFilter = transferAreaFilters[0] ?? "";

  const departmentOptions = useMemo(() => {
    const uniqueDepartments = new Map<string, string>();
    for (const row of employeeRows) {
      for (const value of row.departments.length > 0 ? row.departments : [row.department]) {
        const token = value.trim();
        if (token.length > 0) {
          uniqueDepartments.set(token.toLowerCase(), token);
        }
      }
    }
    for (const item of departmentDirectory.values()) {
      const value = item.name.trim();
      if (value.length > 0 && !uniqueDepartments.has(value.toLowerCase())) {
        uniqueDepartments.set(value.toLowerCase(), value);
      }
    }
    return Array.from(uniqueDepartments.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));
  }, [departmentDirectory, employeeRows]);

  const departmentSelectOptions = useMemo(
    () =>
      departmentOptions.map((name) => {
        const meta = departmentDirectory.get(name.toLowerCase());
        return { name, areaLabel: meta?.areaLabel ?? "" };
      }),
    [departmentDirectory, departmentOptions],
  );

  const defaultDepartment =
    departmentSelectOptions[0]?.name ??
    "";

  const availableDepartmentSelections = useMemo(() => {
    const activeFilter = selectedAssignmentAreaFilter || areaSelectionOptions[0]?.filterKey || "";
    const entries = Array.from(departmentByCode.values());
    const filtered = activeFilter
      ? entries.filter((item) => getAreaFilterKey(item.areaCode, item.areaLabel) === activeFilter)
      : entries;
    return [...filtered].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
  }, [areaSelectionOptions, departmentByCode, selectedAssignmentAreaFilter]);

  const availableTransferDepartmentSelections = useMemo(() => {
    const activeFilter = selectedTransferAreaFilter || areaSelectionOptions[0]?.filterKey || "";
    const entries = Array.from(departmentByCode.values());
    const filtered = activeFilter
      ? entries.filter((item) => getAreaFilterKey(item.areaCode, item.areaLabel) === activeFilter)
      : entries;
    return [...filtered].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
  }, [areaSelectionOptions, departmentByCode, selectedTransferAreaFilter]);

  const normalizeDepartmentSelection = (codes: string[], preferredPrimaryCode?: string) => {
    const normalizedCodes = Array.from(
      new Set(
        codes
          .map((value) => value.trim())
          .filter((value) => value.length > 0 && departmentByCode.has(value)),
      ),
    );
    const primaryCode =
      preferredPrimaryCode && normalizedCodes.includes(preferredPrimaryCode)
        ? preferredPrimaryCode
        : normalizedCodes[0] ?? "";
    const orderedCodes = primaryCode
      ? [primaryCode, ...normalizedCodes.filter((value) => value !== primaryCode)]
      : normalizedCodes;
    return {
      codes: orderedCodes,
      primaryCode,
      primaryName: primaryCode ? (departmentByCode.get(primaryCode)?.name ?? "") : "",
    };
  };

  const applyAddFormDepartmentSelection = (codes: string[], preferredPrimaryCode?: string) => {
    const next = normalizeDepartmentSelection(codes, preferredPrimaryCode);
    setAddForm((prev) => ({
      ...prev,
      departmentCodes: next.codes,
      department: next.primaryName,
    }));
  };

  const applyTransferDepartmentSelection = (codes: string[], preferredPrimaryCode?: string) => {
    const next = normalizeDepartmentSelection(codes, preferredPrimaryCode);
    setTransferDepartmentCodes(next.codes);
    setTransferPrimaryDepartmentCode(next.primaryCode);
  };

  const selectedAssignmentDepartments = useMemo(
    () =>
      addForm.departmentCodes
        .map((value) => departmentByCode.get(value))
        .filter((item): item is { code: string; name: string; areaCode: string; areaLabel: string } => Boolean(item)),
    [addForm.departmentCodes, departmentByCode],
  );

  const selectedTransferDepartments = useMemo(
    () =>
      transferDepartmentCodes
        .map((value) => departmentByCode.get(value))
        .filter((item): item is { code: string; name: string; areaCode: string; areaLabel: string } => Boolean(item)),
    [departmentByCode, transferDepartmentCodes],
  );

  const assignmentDepartmentSummary = useMemo(() => {
    const names = selectedAssignmentDepartments.map((item) => item.name);
    if (names.length === 0) {
      return "Select department";
    }
    if (names.length <= 2) {
      return names.join(", ");
    }
    return `${names.slice(0, 2).join(", ")} +${names.length - 2}`;
  }, [selectedAssignmentDepartments]);

  const transferDepartmentSummary = useMemo(() => {
    const names = selectedTransferDepartments.map((item) => item.name);
    if (names.length === 0) {
      return "Select department";
    }
    if (names.length <= 2) {
      return names.join(", ");
    }
    return `${names.slice(0, 2).join(", ")} +${names.length - 2}`;
  }, [selectedTransferDepartments]);

  const visibleAssignmentAreaSections = useMemo(
    () =>
      areaSelectionOptions.filter(
        (item) => assignmentAreaFilters.length === 0 || assignmentAreaFilters.includes(item.filterKey),
      ),
    [areaSelectionOptions, assignmentAreaFilters],
  );

  const visibleTransferAreaSections = useMemo(
    () =>
      areaSelectionOptions.filter(
        (item) => transferAreaFilters.length === 0 || transferAreaFilters.includes(item.filterKey),
      ),
    [areaSelectionOptions, transferAreaFilters],
  );

  const positionOptionsForDepartment = useMemo(() => {
    const selectedDepartment = addForm.department.trim().toLowerCase();
    const filtered = positionRows.filter((item) => {
      const department = (item.department ?? "").trim().toLowerCase();
      if (!selectedDepartment) {
        return true;
      }
      return department === selectedDepartment;
    });
    const unique = new Map<string, string>();
    for (const item of filtered) {
      const name = item.name?.trim() ?? "";
      if (!name) {
        continue;
      }
      unique.set(name.toLowerCase(), name);
    }
    return Array.from(unique.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));
  }, [addForm.department, positionRows]);

  const defaultPosition = positionOptionsForDepartment[0] ?? "";

  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase();
    const selectedDepartment = departmentFilter.trim().toLowerCase();
    const result = employeeRows.filter((item) => {
      const itemDepartments = item.departments.length > 0 ? item.departments : [item.department];
      const itemAreas = item.areas.length > 0
        ? item.areas
        : itemDepartments
            .map((value) => departmentDirectory.get(value.trim().toLowerCase())?.areaLabel ?? "")
            .filter((value) => value.length > 0);
      const departmentHaystack = itemDepartments.join(" ").toLowerCase();
      const areaHaystack = itemAreas.join(" ").toLowerCase();
      const matchesSearch =
        needle.length === 0 ||
        item.name.toLowerCase().includes(needle) ||
        item.code.toLowerCase().includes(needle) ||
        item.phone.toLowerCase().includes(needle) ||
        departmentHaystack.includes(needle) ||
        areaHaystack.includes(needle);
      const matchesDepartment =
        departmentFilter === "all" || itemDepartments.some((value) => value.trim().toLowerCase() === selectedDepartment);
      const matchesArea = areaFilter === "all" || itemAreas.includes(areaFilter);
      const matchesStatus = status === "all" || item.status === status;
      return matchesSearch && matchesDepartment && matchesArea && matchesStatus;
    });
    return sortRows(result, sortKey, sortAsc, employeeExtras);
  }, [areaFilter, departmentDirectory, departmentFilter, employeeRows, search, sortAsc, sortKey, status, employeeExtras]);

  useEffect(() => {
    if (departmentFilter !== "all" && !departmentOptions.some((item) => item.toLowerCase() === departmentFilter.toLowerCase())) {
      setDepartmentFilter("all");
    }
  }, [departmentFilter, departmentOptions]);

  useEffect(() => {
    if (areaFilter !== "all" && !areaOptions.includes(areaFilter)) {
      setAreaFilter("all");
    }
  }, [areaFilter, areaOptions]);

  useEffect(() => {
    const current = addForm.position.trim().toLowerCase();
    if (positionOptionsForDepartment.length === 0) {
      if (addForm.position) {
        setAddForm((prev) => ({ ...prev, position: "" }));
      }
      return;
    }

    if (!positionOptionsForDepartment.some((item) => item.toLowerCase() === current)) {
      setAddForm((prev) => ({ ...prev, position: positionOptionsForDepartment[0] ?? "" }));
    }
  }, [addForm.department, addForm.position, positionOptionsForDepartment]);

  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
  const currentPage = Math.min(page, totalPages);
  const rows = filtered;
  const allFilteredSelected = filtered.length > 0 && filtered.every((item) => selectedEmployeeIds.includes(item.id));
  const activeFilteredCount = filtered.filter((item) => item.status === "active").length;
  const inactiveFilteredCount = Math.max(0, filtered.length - activeFilteredCount);

  const rangeStart = totalCount === 0 ? 0 : (currentPage - 1) * PAGE_SIZE + 1;
  const rangeEnd = Math.min(currentPage * PAGE_SIZE, totalCount);

  function updateSort(key: SortKey) {
    if (sortKey === key) {
      setSortAsc((value) => !value);
      return;
    }
    setSortKey(key);
    setSortAsc(true);
  }

  function nextUserId() {
    const nums = employeeRows
      .map((item) => Number.parseInt(item.code, 10))
      .filter((value) => Number.isFinite(value));
    return String((nums.length > 0 ? Math.max(...nums) : 0) + 1);
  }

  function openCreateModal() {
    const fallbackAreaFilterKey = areaSelectionOptions[0]?.filterKey ?? "";
    const fallbackDepartmentCode =
      areaSelectionOptions.find((item) => item.filterKey === fallbackAreaFilterKey)?.departments[0]?.code ??
      availableDepartmentSelections[0]?.code ??
      Array.from(departmentByCode.keys())[0] ??
      "";
    const fallbackDepartmentName = fallbackDepartmentCode ? (departmentByCode.get(fallbackDepartmentCode)?.name ?? defaultDepartment) : defaultDepartment;
    const fallbackPosition = positionRows
      .filter((item) => (item.department ?? "").trim().toLowerCase() === fallbackDepartmentName.trim().toLowerCase())
      .map((item) => item.name?.trim() ?? "")
      .find((value) => value.length > 0) ?? "";
    setAddError(null);
    setEditingEmployeeId(null);
    setAssignmentAreaFilters(fallbackAreaFilterKey ? [fallbackAreaFilterKey] : []);
    setAddForm({
      userId: nextUserId(),
      firstName: "",
      lastName: "",
      department: fallbackDepartmentName,
      departmentCodes: fallbackDepartmentCode ? [fallbackDepartmentCode] : [],
      position: fallbackPosition,
      privilege: "User",
      verifyMode: 0,
      cardNumber: "",
      pictureDataUrl: "",
      mobileNumber: "",
      gender: "Male",
      dob: "",
      password: "",
      remoteEnrollDeviceSerial: "",
      remoteEnrollFace: false,
      remoteEnrollFp: false,
      remoteFpIndex: "0",
      remoteFaceType: "9",
    });
    setPendingRemoteToggle(null);
    setRemoteDevicePickerOpen(false);
    setEnrollSession(null);
    setEnrollLiveItems([]);
    setDrawerOpen(true);
  }

  function openTransferModal(ids: string[] = selectedEmployeeIds) {
    if (ids.length === 0) {
      return;
    }
    if (ids !== selectedEmployeeIds) {
      setSelectedEmployeeIds(ids);
    }
    setTransferError(null);
    const firstEmployee = employeeRows.find((item) => ids.includes(item.id));
    const initialDepartmentCode = firstEmployee?.departmentCodes[0] ?? "";
    const initialAreaFilterKey = initialDepartmentCode
      ? getAreaFilterKey(
          departmentByCode.get(initialDepartmentCode)?.areaCode ?? "",
          departmentByCode.get(initialDepartmentCode)?.areaLabel ?? "",
        )
      : areaSelectionOptions[0]?.filterKey ?? "";
    setTransferDepartmentCodes([]);
    setTransferPrimaryDepartmentCode("");
    setTransferAreaFilters(initialAreaFilterKey ? [initialAreaFilterKey] : []);
    setTransferModalOpen(true);
  }

  function toggleAssignmentAreaFilter(filterKey: string) {
    const nextFilterKey = filterKey.trim();
    setAssignmentAreaFilters(nextFilterKey ? [nextFilterKey] : []);
    const allowedCodes = new Set(
      Array.from(departmentByCode.values())
        .filter((item) => !nextFilterKey || getAreaFilterKey(item.areaCode, item.areaLabel) === nextFilterKey)
        .map((item) => item.code),
    );
    const filteredDepartmentCodes = addForm.departmentCodes.filter((value) => allowedCodes.has(value));
    const preferredPrimaryCode = filteredDepartmentCodes.includes(addForm.departmentCodes[0] ?? "")
      ? addForm.departmentCodes[0]
      : filteredDepartmentCodes[0] ?? "";
    applyAddFormDepartmentSelection(filteredDepartmentCodes, preferredPrimaryCode);
  }

  function toggleTransferAreaFilter(filterKey: string) {
    const nextFilterKey = filterKey.trim();
    setTransferAreaFilters(nextFilterKey ? [nextFilterKey] : []);
    const allowedCodes = new Set(
      Array.from(departmentByCode.values())
        .filter((item) => !nextFilterKey || getAreaFilterKey(item.areaCode, item.areaLabel) === nextFilterKey)
        .map((item) => item.code),
    );
    const filteredDepartmentCodes = transferDepartmentCodes.filter((value) => allowedCodes.has(value));
    const preferredPrimaryCode = filteredDepartmentCodes.includes(transferPrimaryDepartmentCode)
      ? transferPrimaryDepartmentCode
      : filteredDepartmentCodes[0] ?? "";
    applyTransferDepartmentSelection(filteredDepartmentCodes, preferredPrimaryCode);
  }

  function toggleAddDepartmentSelection(code: string, checked: boolean) {
    const nextCodes = checked
      ? [...addForm.departmentCodes, code]
      : addForm.departmentCodes.filter((value) => value !== code);
    const preferredPrimaryCode =
      !checked && addForm.departmentCodes[0] === code
        ? nextCodes[0] ?? ""
        : addForm.departmentCodes[0] || code;
    applyAddFormDepartmentSelection(nextCodes, preferredPrimaryCode);
  }

  function toggleTransferDepartmentSelection(code: string, checked: boolean) {
    const nextCodes = checked
      ? [...transferDepartmentCodes, code]
      : transferDepartmentCodes.filter((value) => value !== code);
    const preferredPrimaryCode =
      !checked && transferPrimaryDepartmentCode === code
        ? nextCodes[0] ?? ""
        : transferPrimaryDepartmentCode || code;
    applyTransferDepartmentSelection(nextCodes, preferredPrimaryCode);
  }

  function selectAllVisibleAddDepartments() {
    const visibleCodes = availableDepartmentSelections.map((department) => department.code);
    const preferredPrimaryCode = addForm.departmentCodes[0] ?? visibleCodes[0] ?? "";
    applyAddFormDepartmentSelection([...addForm.departmentCodes, ...visibleCodes], preferredPrimaryCode);
  }

  function clearAddDepartmentSelection() {
    applyAddFormDepartmentSelection([], "");
  }

  function selectAllVisibleTransferDepartments() {
    const visibleCodes = availableTransferDepartmentSelections.map((department) => department.code);
    const preferredPrimaryCode = transferPrimaryDepartmentCode || transferDepartmentCodes[0] || visibleCodes[0] || "";
    applyTransferDepartmentSelection([...transferDepartmentCodes, ...visibleCodes], preferredPrimaryCode);
  }

  function clearTransferDepartmentSelection() {
    applyTransferDepartmentSelection([], "");
  }

  function openSendUserModal() {
    if (selectedEmployeeIds.length === 0) {
      return;
    }
    const allSerials = managedDevices.map((device) => device.serialNumber).filter((x) => x.trim().length > 0);
    setSelectedSendDeviceSerials(allSerials);
    setSendUserError(null);
    setSendUserNotice(null);
    setSendUserModalOpen(true);
  }

  function toggleSendDevice(serial: string, checked: boolean) {
    setSelectedSendDeviceSerials((prev) => {
      if (checked) {
        return prev.includes(serial) ? prev : [...prev, serial];
      }
      return prev.filter((item) => item !== serial);
    });
  }

  async function sendSelectedUsersToDevices() {
    if (selectedEmployeeIds.length === 0) {
      setSendUserError("Select employee(s) first.");
      return;
    }
    if (selectedSendDeviceSerials.length === 0) {
      setSendUserError("Select at least one device.");
      return;
    }

    setSendUserBusy(true);
    setSendUserError(null);
    setSendUserNotice(null);

    let successCount = 0;
    let totalCommands = 0;
    const failed: string[] = [];

    for (const serial of selectedSendDeviceSerials) {
      try {
        const response = await requestJson<{ commandCount?: number }>(
          `/api/device/sync/${encodeURIComponent(serial)}/selected`,
          {
            method: "POST",
            body: JSON.stringify({ employeeIds: selectedEmployeeIds }),
          },
        );
        successCount += 1;
        totalCommands += response.commandCount ?? 0;
      } catch {
        failed.push(serial);
      }
    }

    if (failed.length > 0) {
      setSendUserError(
        `Sent to ${successCount}/${selectedSendDeviceSerials.length} device(s). Failed: ${failed.join(", ")}`,
      );
      if (successCount > 0) {
        setSendUserNotice(`Queued ${totalCommands} command(s) for selected users.`);
      }
    } else {
      setSendUserNotice(
        `Selected users sent successfully to ${successCount} device(s). Queued commands: ${totalCommands}.`,
      );
      setSendUserModalOpen(false);
    }

    setSendUserBusy(false);
  }

  function openEditModal(employee: EmployeeRow) {
    const extra = employeeExtras[employee.id];
    const splitName = employee.name.trim().split(/\s+/);
    const firstName = splitName.shift() ?? "";
    const lastName = splitName.join(" ");
    const privilegeOption = (() => {
      const token = (extra?.privilege ?? employee.designation ?? "").toLowerCase();
      if (token.includes("admin")) {
        return "Admin";
      }

      if (token.includes("enroll") || token.includes("registrar")) {
        return "Enroller";
      }

      return "User";
    })();
    const departmentCodes = resolveDepartmentCodesForEmployee(employee);
    const primaryAreaFilterKey = departmentCodes[0]
      ? getAreaFilterKey(
          departmentByCode.get(departmentCodes[0])?.areaCode ?? "",
          departmentByCode.get(departmentCodes[0])?.areaLabel ?? "",
        )
      : employee.areaCodes[0]
        ? (areaSelectionOptions.find((opt) => opt.code === employee.areaCodes[0])?.filterKey ?? areaSelectionOptions[0]?.filterKey ?? "")
        : areaSelectionOptions[0]?.filterKey ?? "";
    const selectedDepartmentCodes = departmentCodes.length > 0
      ? departmentCodes
      : availableDepartmentSelections[0]?.code
        ? [availableDepartmentSelections[0].code]
        : [];
    const primaryDepartmentName = selectedDepartmentCodes[0]
      ? (departmentByCode.get(selectedDepartmentCodes[0])?.name ?? (employee.department || defaultDepartment))
      : employee.department || defaultDepartment;
    setAddError(null);
    setEditingEmployeeId(employee.id);
    const effectiveAreaFilterKey = primaryAreaFilterKey || areaSelectionOptions[0]?.filterKey || "";
    toggleAssignmentAreaFilter(effectiveAreaFilterKey);
    setAddForm({
      userId: employee.code,
      firstName,
      lastName,
      department: primaryDepartmentName,
      departmentCodes: selectedDepartmentCodes,
      position: employee.position || "",
      privilege: privilegeOption,
      verifyMode: extra?.verifyMode ?? 0,
      groupId: extra?.groupId ?? 1,
      cardNumber: extra?.cardNumber ?? "",
      pictureDataUrl: extra?.pictureDataUrl ?? (employee.avatar.startsWith("data:") ? employee.avatar : ""),
      mobileNumber: extra?.mobileNumber ?? (employee.phone === "-" ? "" : employee.phone),
      gender: extra?.gender ?? "Male",
      dob: extra?.dob ?? "",
      password: extra?.password ?? "",
      remoteEnrollDeviceSerial: "",
      remoteEnrollFace: false,
      remoteEnrollFp: false,
      remoteFpIndex: "0",
      remoteFaceType: "9",
    });
    setPendingRemoteToggle(null);
    setRemoteDevicePickerOpen(false);
    setEnrollSession(null);
    setEnrollLiveItems([]);
    setDrawerOpen(true);
  }

  function sanitizeCommandValue(value: string) {
    return value.replace(/\t/g, " ").replace(/[\r\n]/g, " ").trim();
  }

  function normalizePasswordInput(value: string) {
    return value.replace(/\D/g, "");
  }

  function validatePassword(value: string, required: boolean) {
    const token = value.trim();
    if (!token) {
      return required ? "Password is required." : null;
    }
    if (!/^\d+$/.test(token)) {
      return "Password must contain only numbers.";
    }
    if (token.length < 6) {
      return "Password must be at least 6 digits.";
    }
    return null;
  }

  async function queueDeviceCommand(serial: string, command: string) {
    await requestJson(`/api/devices/${encodeURIComponent(serial)}/commands`, {
      method: "POST",
      body: JSON.stringify({ command }),
    });
  }

  async function clearPendingCommands(serial: string) {
    await requestJson(`/api/devices/${encodeURIComponent(serial)}/commands/clear`, {
      method: "POST",
    });
  }

  async function forcePollNow(serial: string) {
    await requestJson(`/api/devices/${encodeURIComponent(serial)}/commands/force-poll`, {
      method: "POST",
    });
  }

  async function enqueueRemoteEnrollment(
    employeeCode: string,
    fullName: string,
    mode: "face" | "fp",
    faceTypeOverride?: "2" | "9",
    cardNumberOverride?: string,
  ) {
    const serial = addForm.remoteEnrollDeviceSerial.trim();
    if (!serial) {
      return;
    }

    const pin = sanitizeCommandValue(employeeCode);
    const safeName = sanitizeCommandValue(fullName || employeeCode);
    const safeCard = sanitizeCommandValue(cardNumberOverride ?? addForm.cardNumber);
    const safePassword = sanitizeCommandValue(addForm.password);
    const pri = addForm.privilege === "Admin" ? "14" : addForm.privilege === "Enroller" ? "2" : "0";
    const verifyMode = Number.isFinite(addForm.verifyMode) ? String(addForm.verifyMode) : "0";
    const retry = 3;
    const overwrite = 1;
    const fpIndex = Number.parseInt(addForm.remoteFpIndex, 10);
    const fingerIndex = Number.isFinite(fpIndex) && fpIndex >= 0 && fpIndex <= 9 ? fpIndex : 0;
    const faceType = faceTypeOverride ?? (addForm.remoteFaceType === "2" ? "2" : addForm.remoteFaceType === "9" ? "9" : "");

    // Wake/prepare device command loop before enrollment commands.
    await queueDeviceCommand(serial, "CHECK");

    if (mode === "fp") {
      // Ensure user exists on device before triggering FP enrollment popup.
      await queueDeviceCommand(
        serial,
        `DATA UPDATE USERINFO PIN=${pin}\tName=${safeName}\tPri=${pri}\tPasswd=${safePassword}\tCard=${safeCard}\tGrp=1\tTZ=0001000000000000\tVerify=${verifyMode}`,
      );
      await queueDeviceCommand(
        serial,
        `ENROLL_FP\tPIN=${pin}\tFID=${fingerIndex}\tRETRY=${retry}\tOVERWRITE=${overwrite}`,
      );
    }

    if (mode === "face") {
      if (faceType !== "2" && faceType !== "9") {
        throw new Error("Select face type: Infrared (2) or Visible light (9).");
      }
      await queueDeviceCommand(
        serial,
        `ENROLL_BIO TYPE=${faceType} PIN=${pin} RETRY=${retry} OVERWRITE=${overwrite}`,
      );
    }
  }

  function pickRemoteDeviceFor(toggle: "face" | "fp") {
    setPendingRemoteToggle(toggle);
    setRemoteDevicePickerOpen(true);
  }

  function applyRemoteToggle(toggle: "face" | "fp", checked: boolean) {
    if (checked && !addForm.remoteEnrollDeviceSerial) {
      pickRemoteDeviceFor(toggle);
      return;
    }

    if (toggle === "face") {
      setAddForm((prev) => ({
        ...prev,
        remoteEnrollFace: checked,
        // Keep enrollment mode explicit: selecting face disables FP.
        remoteEnrollFp: checked ? false : prev.remoteEnrollFp,
      }));
      return;
    }
    if (toggle === "fp") {
      setAddForm((prev) => ({
        ...prev,
        remoteEnrollFp: checked,
        // Keep enrollment mode explicit: selecting FP disables face.
        remoteEnrollFace: checked ? false : prev.remoteEnrollFace,
      }));
      return;
    }
  }

  function chooseRemoteDevice(serial: string) {
    setAddForm((prev) => {
      const next = { ...prev, remoteEnrollDeviceSerial: serial };
      if (pendingRemoteToggle === "face") {
        next.remoteEnrollFace = true;
        next.remoteEnrollFp = false;
      } else if (pendingRemoteToggle === "fp") {
        next.remoteEnrollFp = true;
        next.remoteEnrollFace = false;
      }
      return next;
    });
    setPendingRemoteToggle(null);
    setRemoteDevicePickerOpen(false);
  }

  async function onEnrollInDevice() {
    const code = addForm.userId.trim();
    if (!code) {
      setAddError("User ID is required before enrollment.");
      return;
    }
    if (!addForm.remoteEnrollDeviceSerial.trim()) {
      setAddError("Select device for remote enrollment.");
      setRemoteDevicePickerOpen(true);
      return;
    }
    const selectedDevice = managedDevices.find((d) => d.serialNumber === addForm.remoteEnrollDeviceSerial.trim());
    if (selectedDevice?.isOnline === false) {
      setAddError("Selected device is offline. Bring it online, then retry enrollment.");
      return;
    }
    const faceSelected = addForm.remoteEnrollFace;
    const fpSelected = addForm.remoteEnrollFp;
    if (!(faceSelected || fpSelected)) {
      setAddError("Select at least one option: Face or FP.");
      return;
    }
    const mode: "face" | "fp" = faceSelected ? "face" : "fp";
    const effectiveFaceType = addForm.remoteFaceType === "2" ? "2" : addForm.remoteFaceType === "9" ? "9" : "";
    const selectedEmployee = employeeRows.find((item) => item.code.trim().toLowerCase() === code.toLowerCase());
    const selectedEmployeeExtra = selectedEmployee ? employeeExtras[selectedEmployee.id] : undefined;
    const isSavedUser = Boolean(selectedEmployee);
    const resolvedFullName = selectedEmployee?.name?.trim() || `${addForm.firstName.trim()} ${addForm.lastName.trim()}`.trim() || code;
    const resolvedCardNo = (selectedEmployeeExtra?.cardNumber ?? addForm.cardNumber ?? "").trim();
    if (mode === "face" && !effectiveFaceType) {
      setAddError("Select face type first: Visible light or Infrared.");
      return;
    }
    // Do not block by cached capability flags in UI; device response is authoritative.

    setAddError(null);
    setEnrollSaving(true);
    const session = {
      serial: addForm.remoteEnrollDeviceSerial.trim(),
      pin: code,
      startedAtMs: Date.now(),
      running: true,
    };
    setEnrollSession(session);
    setEnrollLiveItems([]);
    try {
      if (mode === "face") {
        try {
          const result = await requestJson<RemoteEnrollQueueResult>("/api/faceenrollment/enroll", {
            method: "POST",
            body: JSON.stringify({
              deviceSerial: session.serial,
              userId: code,
              userName: resolvedFullName,
              cardNo: resolvedCardNo,
              faceType: effectiveFaceType,
              retry: 3,
              overwrite: true,
              existingUserOnly: false,
              verifyMode: addForm.verifyMode,
            }),
          });

          if (result.mode === "direct") {
            const summary = queueSummaryText(result);
            setEnrollSession((prev) => (prev ? { ...prev, running: false } : prev));
            setEnrollLiveItems([
              {
                id: `direct-${Date.now()}`,
                label: "Face",
                status: "sent",
                returnCode: typeof result.code === "number" ? String(result.code) : "",
                returnMessage: summary || "Face enrollment started on device.",
                updatedAt: new Date().toLocaleTimeString(),
                rawReply: result.details ?? "",
              },
            ]);
          } else {
            const summary = queueSummaryText(result);
            if (summary) {
              setEnrollLiveItems([
                {
                  id: `queued-${Date.now()}`,
                  label: "Face",
                  status: "pending",
                  returnCode: "",
                  returnMessage: summary,
                  updatedAt: new Date().toLocaleTimeString(),
                  rawReply: "",
                },
              ]);
            }
            const firstQueuedId = result.commandId ?? result.syncLogIds?.[0];
            if (firstQueuedId) {
              await pollEnrollmentStatusByCommandId(String(firstQueuedId));
            } else {
              await loadEnrollLiveStatus(session);
            }
            if (!isSavedUser && result.autoCreatedUser) {
              await loadEmployees();
            }
          }
        } catch (err) {
          throw err;
        }
      } else {
        let result: RemoteEnrollQueueResult;
        try {
          result = await requestJson<RemoteEnrollQueueResult>("/api/fpenrollment/enroll", {
            method: "POST",
            body: JSON.stringify({
              deviceSerial: session.serial,
              userId: code,
              userName: resolvedFullName,
              cardNo: resolvedCardNo,
              fingerIndex: Number.parseInt(addForm.remoteFpIndex, 10) || 0,
              retry: 3,
              overwrite: true,
              verifyMode: addForm.verifyMode,
            }),
          });
        } catch {
          // Backward-compatible fallback for older API builds.
          await clearPendingCommands(session.serial);
          await enqueueRemoteEnrollment(code, resolvedFullName, mode, effectiveFaceType as "2" | "9", resolvedCardNo);
          result = {
            success: true,
            mode: "queued",
            message: "Fingerprint enrollment queued. Place finger on the device sensor.",
            preProvisionedUser: true,
            queuedCount: 3,
            queuedSteps: ["CHECK", "DATA UPDATE USERINFO", `ENROLL_FP(FID=${Number.parseInt(addForm.remoteFpIndex, 10) || 0})`],
          };
        }
        const summary = queueSummaryText(result);
        if (summary) {
          setEnrollLiveItems([
            {
              id: `queued-${Date.now()}`,
              label: "FP",
              status: "pending",
              returnCode: "",
              returnMessage: summary,
              updatedAt: new Date().toLocaleTimeString(),
              rawReply: "",
            },
          ]);
        }
        await loadEnrollLiveStatus(session);
      }
    } catch (err) {
      setAddError(err instanceof Error ? err.message : "Failed to queue remote enrollment.");
      setEnrollSession((prev) => (prev ? { ...prev, running: false } : prev));
    } finally {
      setEnrollSaving(false);
    }
  }

  function stopCameraStream() {
    const stream = cameraStreamRef.current;
    if (stream) {
      stream.getTracks().forEach((track) => track.stop());
      cameraStreamRef.current = null;
    }
    if (cameraVideoRef.current) {
      cameraVideoRef.current.srcObject = null;
    }
  }

  function closeCameraPicker() {
    stopCameraStream();
    setCameraPickerOpen(false);
    setCameraBusy(false);
    setCameraError(null);
  }

  async function startCameraStream(deviceId: string) {
    stopCameraStream();
    const stream = await navigator.mediaDevices.getUserMedia({
      video: { deviceId: { exact: deviceId } },
      audio: false,
    });

    cameraStreamRef.current = stream;
    const video = cameraVideoRef.current;
    if (video) {
      video.srcObject = stream;
      await video.play().catch(() => undefined);
    }
  }

  async function openCameraPicker() {
    if (typeof window === "undefined" || !navigator.mediaDevices?.getUserMedia) {
      setAddError("Camera API is not supported on this browser.");
      return;
    }

    setCameraPickerOpen(true);
    setCameraBusy(true);
    setCameraError(null);

    try {
      const warmup = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
      warmup.getTracks().forEach((track) => track.stop());

      const devices = (await navigator.mediaDevices.enumerateDevices())
        .filter((device) => device.kind === "videoinput")
        .map((device, index) => ({
          id: device.deviceId,
          label: device.label?.trim() || `Camera ${index + 1}`,
        }))
        .filter((device) => Boolean(device.id));

      setCameraDevices(devices);
      if (devices.length === 0) {
        setCameraError("No camera device found.");
        return;
      }

      const preferred = devices.find((device) => device.id === activeCameraId)?.id ?? devices[0].id;
      setActiveCameraId(preferred);
      await startCameraStream(preferred);
    } catch (err) {
      if (err instanceof DOMException && err.name === "NotAllowedError") {
        setCameraError("Camera permission denied. Please allow camera access.");
      } else {
        setCameraError(err instanceof Error ? err.message : "Failed to open camera.");
      }
    } finally {
      setCameraBusy(false);
    }
  }

  async function switchCamera(deviceId: string) {
    if (!deviceId) {
      return;
    }
    setActiveCameraId(deviceId);
    setCameraBusy(true);
    setCameraError(null);
    try {
      await startCameraStream(deviceId);
    } catch (err) {
      setCameraError(err instanceof Error ? err.message : "Failed to switch camera.");
    } finally {
      setCameraBusy(false);
    }
  }

  function captureFromCamera() {
    const video = cameraVideoRef.current;
    const canvas = cameraCanvasRef.current;
    if (!video || !canvas || video.videoWidth <= 0 || video.videoHeight <= 0) {
      setCameraError("Camera is not ready yet.");
      return;
    }

    canvas.width = video.videoWidth;
    canvas.height = video.videoHeight;
    const ctx = canvas.getContext("2d");
    if (!ctx) {
      setCameraError("Failed to capture frame.");
      return;
    }

    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
    const dataUrl = canvas.toDataURL("image/jpeg", 0.92);
    setAddForm((prev) => ({ ...prev, pictureDataUrl: dataUrl }));
    closeCameraPicker();
  }

  async function onPickImage(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (!file) {
      return;
    }
    if (file.size > 10 * 1024 * 1024) {
      setAddError("Image size must be 10 MB or less.");
      event.target.value = "";
      return;
    }

    try {
      const rawUrl = await readFileAsDataUrl(file);
      const jpegUrl = await ensureJpegDataUrl(rawUrl);
      setAddForm((prev) => ({ ...prev, pictureDataUrl: jpegUrl }));
    } catch (err) {
      setAddError(err instanceof Error ? err.message : "Failed to process image.");
    }
    event.target.value = "";
  }

  async function readTextFile(file: File): Promise<string> {
    return await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result ?? ""));
      reader.onerror = () => reject(new Error("Failed to read file."));
      reader.readAsText(file);
    });
  }

  function parseEmployeeImportRows(text: string): EmployeeImportRow[] {
    const trimmed = text.trim();
    if (!trimmed) {
      return [];
    }

    if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
      const parsed = JSON.parse(trimmed) as unknown;
      const items = Array.isArray(parsed) ? parsed : (parsed as { items?: unknown[] })?.items ?? [];
      return (Array.isArray(items) ? items : [])
        .map((x) =>
          x as {
            userId?: string;
            employeeCode?: string;
            fullName?: string;
            areaName?: string;
            area?: string;
            areaCode?: string;
            departmentName?: string;
            department?: string;
            departmentCode?: string;
            isActive?: boolean | string;
          },
        )
        .map((x) => ({
          userId: (x.userId ?? x.employeeCode ?? "").trim(),
          fullName: (x.fullName ?? "").trim(),
          areaName: (x.areaName ?? x.area ?? x.areaCode ?? "").trim(),
          departmentName: (x.departmentName ?? x.department ?? x.departmentCode ?? "").trim(),
          isActive: String(x.isActive ?? "true").toLowerCase() !== "false",
        }));
    }

    const lines = trimmed.split(/\r?\n/).filter((line) => line.trim().length > 0);
    if (lines.length < 2) {
      return [];
    }

    const headers = toHeaderMap(parseCsvLine(lines[0]));
    const userIdIndex = headers.get("userid") ?? headers.get("employeeid") ?? headers.get("employeecode");
    const nameIndex = headers.get("fullname");
    const areaIndex = headers.get("areaname") ?? headers.get("area") ?? headers.get("areacode");
    const deptIndex = headers.get("departmentname") ?? headers.get("department") ?? headers.get("departmentcode");
    const activeIndex = headers.get("isactive");
    if (userIdIndex === undefined || areaIndex === undefined || deptIndex === undefined) {
      throw new Error("Employee import file must contain headers: UserId, Area, Department, IsActive.");
    }

    const rows: EmployeeImportRow[] = [];
    for (let i = 1; i < lines.length; i++) {
      const cols = parseCsvLine(lines[i]);
      rows.push({
        userId: (cols[userIdIndex] ?? "").trim(),
        fullName: nameIndex !== undefined ? (cols[nameIndex] ?? "").trim() : "",
        areaName: (cols[areaIndex] ?? "").trim(),
        departmentName: (cols[deptIndex] ?? "").trim(),
        isActive: String(activeIndex !== undefined ? cols[activeIndex] : "true").toLowerCase() !== "false",
      });
    }

    return rows;
  }

  function parseEmployeePhotoImportRows(text: string): EmployeePhotoImportRow[] {
    const trimmed = text.trim();
    if (!trimmed) {
      return [];
    }

    if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
      const parsed = JSON.parse(trimmed) as unknown;
      const items = Array.isArray(parsed) ? parsed : (parsed as { items?: unknown[] })?.items ?? [];
      return (Array.isArray(items) ? items : [])
        .map((x) => x as { userId?: string; employeeCode?: string; pictureDataUrl?: string })
        .map((x) => ({
          employeeCode: (x.userId ?? x.employeeCode ?? "").trim(),
          pictureDataUrl: (x.pictureDataUrl ?? "").trim(),
        }));
    }

    const lines = trimmed.split(/\r?\n/).filter((line) => line.trim().length > 0);
    if (lines.length < 2) {
      return [];
    }

    const headers = toHeaderMap(parseCsvLine(lines[0]));
    const codeIndex = headers.get("userid") ?? headers.get("employeeid") ?? headers.get("employeecode");
    const pictureIndex = headers.get("picturedataurl") ?? headers.get("photo") ?? headers.get("photodataurl");
    if (codeIndex === undefined || pictureIndex === undefined) {
      throw new Error("Photo import file must contain headers: EmployeeCode, PictureDataUrl.");
    }

    const rows: EmployeePhotoImportRow[] = [];
    for (let i = 1; i < lines.length; i++) {
      const cols = parseCsvLine(lines[i]);
      rows.push({
        employeeCode: (cols[codeIndex] ?? "").trim(),
        pictureDataUrl: (cols[pictureIndex] ?? "").trim(),
      });
    }

    return rows;
  }

  async function readSheetRows(file: File): Promise<string[][]> {
    const XLSX = await import("xlsx");
    const buffer = await file.arrayBuffer();
    const wb = XLSX.read(buffer, { type: "array" });
    const firstSheet = wb.SheetNames[0];
    if (!firstSheet) {
      return [];
    }
    const ws = wb.Sheets[firstSheet];
    const matrix = XLSX.utils.sheet_to_json<(string | number | boolean | null)[]>(ws, {
      header: 1,
      raw: false,
      defval: "",
    });
    return matrix.map((row) => row.map((value) => String(value ?? "").trim()));
  }

  function parseEmployeeImportRowsFromSheet(matrix: string[][]): EmployeeImportRow[] {
    if (matrix.length < 2) {
      return [];
    }
    const headers = toHeaderMap(matrix[0]);
    const userIdIndex = headers.get("userid") ?? headers.get("employeeid") ?? headers.get("employeecode");
    const nameIndex = headers.get("fullname");
    const areaIndex = headers.get("areaname") ?? headers.get("area") ?? headers.get("areacode");
    const deptIndex = headers.get("departmentname") ?? headers.get("department") ?? headers.get("departmentcode");
    const activeIndex = headers.get("isactive");
    if (userIdIndex === undefined || areaIndex === undefined || deptIndex === undefined) {
      throw new Error("Employee import sheet must contain columns: UserId, Area, Department. (IsActive is optional)");
    }

    return matrix.slice(1).map((cols) => ({
      userId: (cols[userIdIndex] ?? "").trim(),
      fullName: nameIndex !== undefined ? (cols[nameIndex] ?? "").trim() : "",
      areaName: (cols[areaIndex] ?? "").trim(),
      departmentName: (cols[deptIndex] ?? "").trim(),
      isActive: String(activeIndex !== undefined ? cols[activeIndex] : "true").toLowerCase() !== "false",
    }));
  }

  function parseEmployeePhotoImportRowsFromSheet(matrix: string[][]): EmployeePhotoImportRow[] {
    if (matrix.length < 2) {
      return [];
    }
    const headers = toHeaderMap(matrix[0]);
    const codeIndex = headers.get("userid") ?? headers.get("employeeid") ?? headers.get("employeecode");
    const pictureIndex = headers.get("picturedataurl") ?? headers.get("photo") ?? headers.get("photodataurl");
    if (codeIndex === undefined || pictureIndex === undefined) {
      throw new Error("Photo import sheet must contain columns: EmployeeCode, PictureDataUrl.");
    }

    return matrix.slice(1).map((cols) => ({
      employeeCode: (cols[codeIndex] ?? "").trim(),
      pictureDataUrl: (cols[pictureIndex] ?? "").trim(),
    }));
  }

  async function parseEmployeeImportFile(file: File): Promise<EmployeeImportRow[]> {
    const name = file.name.toLowerCase();
    if (name.endsWith(".xlsx") || name.endsWith(".xls")) {
      const matrix = await readSheetRows(file);
      return parseEmployeeImportRowsFromSheet(matrix);
    }

    const text = await readTextFile(file);
    return parseEmployeeImportRows(text);
  }

  async function parseEmployeePhotoImportFile(file: File): Promise<EmployeePhotoImportRow[]> {
    const name = file.name.toLowerCase();
    if (name.endsWith(".xlsx") || name.endsWith(".xls")) {
      const matrix = await readSheetRows(file);
      return parseEmployeePhotoImportRowsFromSheet(matrix);
    }

    const text = await readTextFile(file);
    return parseEmployeePhotoImportRows(text);
  }

  type ImportSkippedItem = { line: number; userId: string; reason: string };

  function escapeCsv(value: string) {
    const s = String(value ?? "");
    if (/[",\n\r]/.test(s)) {
      return `"${s.replaceAll('"', '""')}"`;
    }
    return s;
  }

  function buildCsv(headers: string[], rows: Array<Array<string | number>>) {
    const head = headers.map(escapeCsv).join(",");
    const body = rows.map((row) => row.map((cell) => escapeCsv(String(cell ?? ""))).join(",")).join("\n");
    return `${head}\n${body}\n`;
  }

  async function downloadSkippedItems(baseName: string, skippedItems: ImportSkippedItem[]) {
    if (skippedItems.length === 0) {
      return;
    }
    const csv = buildCsv(
      ["Line", "UserId", "Reason"],
      skippedItems.map((x) => [x.line, x.userId ?? "", x.reason ?? ""]),
    );
    await downloadAsExcelOrCsv(baseName, csv);
  }

  function readFileAsDataUrl(file: File) {
    return new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result ?? ""));
      reader.onerror = () => reject(new Error("Failed to read image file."));
      reader.readAsDataURL(file);
    });
  }

  function ensureJpegDataUrl(dataUrl: string): Promise<string> {
    if (!dataUrl) return Promise.resolve("");
    return new Promise<string>((resolve, reject) => {
      const img = new Image();
      img.onload = () => {
        try {
          const canvas = document.createElement("canvas");
          const maxDim = 640;
          let w = img.width;
          let h = img.height;
          if (w > maxDim || h > maxDim) {
            if (w > h) {
              h = Math.round((h * maxDim) / w);
              w = maxDim;
            } else {
              w = Math.round((w * maxDim) / h);
              h = maxDim;
            }
          }
          canvas.width = w;
          canvas.height = h;
          const ctx = canvas.getContext("2d");
          if (!ctx) {
            reject(new Error("Failed to get canvas context."));
            return;
          }
          ctx.fillStyle = "#FFFFFF";
          ctx.fillRect(0, 0, w, h);
          ctx.drawImage(img, 0, 0, w, h);
          resolve(canvas.toDataURL("image/jpeg", 0.90));
        } catch (err) {
          reject(err);
        }
      };
      img.onerror = () => reject(new Error("Failed to load image."));
      img.src = dataUrl;
    });
  }

  async function onImportEmployeesFile(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) {
      return;
    }

    try {
      const items = await parseEmployeeImportFile(file);
      if (items.length === 0) {
        throw new Error("No employee rows found.");
      }

      const mode = await askImportMode("Import Employees");
      if (mode === "cancel") return;

      const result = await requestJson<{ added: number; updated: number; skipped: number; skippedItems?: ImportSkippedItem[] }>("/api/personnel/employees/import", {
        method: "POST",
        body: JSON.stringify({ items, mode }),
      });

      await loadEmployees();
      setPage(1);

      if (typeof window !== "undefined") {
        window.alert(`Import Employee completed. Added: ${result.added}, Updated: ${result.updated}, Skipped: ${result.skipped}`);
      }

      if (result.skippedItems?.length) {
        const stamp = new Date().toISOString().slice(0, 10);
        await downloadSkippedItems(`pb-employee-import-skipped-${stamp}`, result.skippedItems);
      }
    } catch (err) {
      if (typeof window !== "undefined") {
        window.alert(err instanceof Error ? err.message : "Employee import failed.");
      }
    }
  }

  async function onImportEmployeePhotosFile(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) {
      return;
    }

    try {
      const items = await parseEmployeePhotoImportFile(file);
      if (items.length === 0) {
        throw new Error("No photo rows found.");
      }

      const mode = await askImportMode("Import Employee Photos");
      if (mode === "cancel") return;

      // Convert all imported photos to standard JPEG in real-time
      for (const item of items) {
        if (item.pictureDataUrl) {
          item.pictureDataUrl = await ensureJpegDataUrl(item.pictureDataUrl);
        }
      }

      const result = await requestJson<{ updated: number; skipped: number; skippedItems?: ImportSkippedItem[] }>("/api/personnel/employees/import-photos", {
        method: "POST",
        body: JSON.stringify({ items, mode }),
      });

      await loadEmployees();
      setPage(1);

      if (typeof window !== "undefined") {
        window.alert(`Import Employee Photo completed. Updated: ${result.updated}, Skipped: ${result.skipped}`);
      }

      if (result.skippedItems?.length) {
        const stamp = new Date().toISOString().slice(0, 10);
        await downloadSkippedItems(`pb-employee-photo-import-skipped-${stamp}`, result.skippedItems);
      }
    } catch (err) {
      if (typeof window !== "undefined") {
        window.alert(err instanceof Error ? err.message : "Employee photo import failed.");
      }
    }
  }

  async function onImportEmployeePhotosFromFiles(event: React.ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? []);
    event.target.value = "";
    if (files.length === 0) {
      return;
    }

    try {
      const mode = await askImportMode("Import Employee Photos (by filename / ZIP)");
      if (mode === "cancel") return;

      const localSkipped: ImportSkippedItem[] = [];
      const items: EmployeePhotoImportRow[] = [];

      // Expand any .zip files (e.g. the exported photo archive) into individual images.
      const imageEntries: Array<{ name: string; dataUrl: string }> = [];
      for (const file of files) {
        if (/\.zip$/i.test(file.name)) {
          const JSZip = (await import("jszip")).default;
          const zip = await JSZip.loadAsync(file);
          for (const entry of Object.values(zip.files)) {
            if (entry.dir || /manifest\.csv$/i.test(entry.name) || !/\.(jpe?g|png|bmp|gif|webp)$/i.test(entry.name)) continue;
            const base64 = await entry.async("base64");
            const ext = entry.name.split(".").pop()?.toLowerCase() ?? "jpg";
            const mime = ext === "png" ? "image/png" : ext === "bmp" ? "image/bmp" : ext === "gif" ? "image/gif" : ext === "webp" ? "image/webp" : "image/jpeg";
            const rawUrl = `data:${mime};base64,${base64}`;
            try {
              const jpegUrl = await ensureJpegDataUrl(rawUrl);
              imageEntries.push({ name: entry.name.split("/").pop() ?? entry.name, dataUrl: jpegUrl });
            } catch (zipImgErr) {
              localSkipped.push({ line: imageEntries.length + 1, userId: entry.name, reason: `Failed to process zip image: ${zipImgErr instanceof Error ? zipImgErr.message : String(zipImgErr)}` });
            }
          }
        } else {
          try {
            const dataUrl = await readFileAsDataUrl(file);
            if (dataUrl) {
              const jpegUrl = await ensureJpegDataUrl(dataUrl);
              imageEntries.push({ name: file.name, dataUrl: jpegUrl });
            } else {
              localSkipped.push({ line: imageEntries.length + 1, userId: file.name, reason: "Failed to read image." });
            }
          } catch (imgErr) {
            localSkipped.push({ line: imageEntries.length + 1, userId: file.name, reason: `Failed to read image: ${imgErr instanceof Error ? imgErr.message : String(imgErr)}` });
          }
        }
      }

      for (let index = 0; index < imageEntries.length; index++) {
        const baseName = imageEntries[index].name.replace(/\.[^.]+$/, "").trim();
        if (!baseName) {
          localSkipped.push({ line: index + 1, userId: imageEntries[index].name, reason: "Filename must contain the UserId." });
          continue;
        }
        items.push({ employeeCode: baseName, pictureDataUrl: imageEntries[index].dataUrl });
      }

      if (items.length === 0) {
        throw new Error("No valid image files found. File name (without extension) must be the UserId.");
      }

      const result = await requestJson<{ updated: number; skipped: number; skippedItems?: ImportSkippedItem[] }>("/api/personnel/employees/import-photos", {
        method: "POST",
        body: JSON.stringify({ items, mode }),
      });

      await loadEmployees();
      setPage(1);

      if (typeof window !== "undefined") {
        const localSkippedCount = localSkipped.length;
        window.alert(`Import Employee Photo completed. Updated: ${result.updated}, Skipped: ${result.skipped + localSkippedCount}`);
      }

      const stamp = new Date().toISOString().slice(0, 10);
      const combinedSkipped = [...(result.skippedItems ?? []), ...localSkipped];
      if (combinedSkipped.length) {
        await downloadSkippedItems(`pb-employee-photo-import-skipped-${stamp}`, combinedSkipped);
      }
    } catch (err) {
      if (typeof window !== "undefined") {
        window.alert(err instanceof Error ? err.message : "Employee photo import failed.");
      }
    }
  }

  async function downloadAsExcelOrCsv(fileBaseName: string, csvText: string) {
    try {
      const XLSX = await import("xlsx");
      const wb = XLSX.read(csvText, { type: "string" });
      XLSX.writeFile(wb, `${fileBaseName}.xlsx`);
      return;
    } catch {
      // Fallback to CSV download.
    }

    const blob = new Blob([csvText], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${fileBaseName}.csv`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  }

  // Export modal state and helpers
  const [exportModalOpen, setExportModalOpen] = useState(false);
  const [exportModalKind, setExportModalKind] = useState<"employee" | "photo">("employee");
  const [exportModalScope, setExportModalScope] = useState<"all" | "selected" | "department" | "area">("all");
  const [exportModalToken, setExportModalToken] = useState("");

  // Import-mode chooser: Overwrite / Skip / Cancel
  const [importModeTitle, setImportModeTitle] = useState<string | null>(null);
  const importModeResolver = useRef<((value: "overwrite" | "skip" | "cancel") => void) | null>(null);
  function askImportMode(title: string) {
    return new Promise<"overwrite" | "skip" | "cancel">((resolve) => {
      importModeResolver.current = resolve;
      setImportModeTitle(title);
    });
  }
  function resolveImportMode(value: "overwrite" | "skip" | "cancel") {
    setImportModeTitle(null);
    importModeResolver.current?.(value);
    importModeResolver.current = null;
  }

  function openExportModal(kind: "employee" | "photo") {
    setExportModalKind(kind);
    setExportModalScope("all");
    setExportModalToken("");
    setExportModalOpen(true);
  }

  async function performExportFromModal(kind: "employee" | "photo") {
    let baseEndpoint = kind === "employee" ? "/api/personnel/employees/export" : "/api/personnel/employees/export/photos";
    let endpoint = baseEndpoint;

    if (exportModalScope === "selected") {
      if (selectedEmployeeIds.length === 0) {
        setExportModalOpen(false);
        window.alert("No employees selected.");
        return;
      }
      const params = new URLSearchParams();
      params.set("scope", "selected");
      params.set("ids", selectedEmployeeIds.join(","));
      endpoint = `${baseEndpoint}?${params.toString()}`;
    } else if (exportModalScope === "department") {
      const token = exportModalToken.trim();
      if (!token) {
        window.alert("Enter department code or name.");
        return;
      }
      const params = new URLSearchParams();
      params.set("scope", "department");
      params.set("department", token);
      endpoint = `${baseEndpoint}?${params.toString()}`;
    } else if (exportModalScope === "area") {
      const token = exportModalToken.trim();
      if (!token) {
        window.alert("Enter area code or name.");
        return;
      }
      const params = new URLSearchParams();
      params.set("scope", "area");
      params.set("area", token);
      endpoint = `${baseEndpoint}?${params.toString()}`;
    }

    try {
      setExportModalOpen(false);
      if (kind === "photo") {
        // Photo export returns a binary ZIP of actual image files (.jpg/.png) + manifest.csv.
        const blob = await requestBlob(endpoint);
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = `employee-photos-${new Date().toISOString().slice(0, 10)}.zip`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
        return;
      }
      const csvText = await requestText(endpoint);
      const blob = new Blob([csvText], { type: "text/csv;charset=utf-8;" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `employees.csv`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (err) {
      window.alert("Export failed.");
    }
  }

  async function exportEmployees(kind: "employee" | "photo" | "template" | "photo-template") {
    const baseEndpoint =
      kind === "employee"
        ? "/api/personnel/employees/export"
        : kind === "photo"
          ? "/api/personnel/employees/export/photos"
          : kind === "photo-template"
            ? "/api/personnel/employees/export/template/photos-filenames"
            : "/api/personnel/employees/export/template";

    try {
    let endpoint = baseEndpoint;
    if (kind === "employee" || kind === "photo") {
      // Open modal-driven export UI instead of prompt-based flow
      openExportModal(kind);
      return;
    }

    const csvText = await requestText(endpoint);
    const suffix = kind === "photo-template" ? "employee-photo-template" : "employee-template";
    const stamp = new Date().toISOString().slice(0, 10);
    await downloadAsExcelOrCsv(`pb-${suffix}-${stamp}`, csvText);
    } catch (err) {
    if (typeof window !== "undefined") {
      window.alert(err instanceof Error ? err.message : "Export failed.");
    }
    }
  }

  async function saveEmployee() {
    const code = addForm.userId.trim();
    const firstName = addForm.firstName.trim();
    const lastName = addForm.lastName.trim();
    const name = `${firstName} ${lastName}`.trim();
    const resolvedName = name || code;
    const selectedDepartmentCodes = Array.from(
      new Set(
        addForm.departmentCodes
          .map((value) => value.trim())
          .filter((value) => value.length > 0 && departmentByCode.has(value)),
      ),
    );
    const primaryDepartmentCode = selectedDepartmentCodes[0] ?? "";
    const fallbackDepartmentName = addForm.department.trim() || defaultDepartment;
    const departmentName = primaryDepartmentCode
      ? (departmentByCode.get(primaryDepartmentCode)?.name ?? fallbackDepartmentName)
      : fallbackDepartmentName;
    const selectedDepartmentNames = selectedDepartmentCodes
      .map((value) => departmentByCode.get(value)?.name ?? "")
      .filter((value, index, array) => value.length > 0 && array.indexOf(value) === index);
    const selectedAreaLabels = Array.from(
      new Set(
        selectedDepartmentCodes
          .map((value) => departmentByCode.get(value)?.areaLabel ?? "")
          .filter((value) => value.length > 0),
      ),
    );
    const selectedAreaCodes = Array.from(
      new Set(
        selectedDepartmentCodes
          .map((value) => departmentByCode.get(value)?.areaCode ?? "")
          .filter((value) => value.length > 0),
      ),
    );
    const selectedAssignments = buildAssignmentsFromCodes(selectedDepartmentCodes);
    const positionName = addForm.position.trim() || defaultPosition;
    if (!code) {
      setAddError("User ID is required.");
      return;
    }
    if (!selectedAssignmentAreaFilter) {
      setAddError("Select an area from Setup.");
      return;
    }
    if (selectedDepartmentCodes.length === 0 || !primaryDepartmentCode || !departmentName) {
      setAddError("Select at least one department from Setup.");
      return;
    }
    if (!selectedDepartmentCodes.every((value) => departmentByCode.has(value))) {
      setAddError("Department selection is invalid. Re-select department(s) from Setup.");
      return;
    }
    const existingByCode = employeeRows.find((item) => item.code.trim().toLowerCase() === code.toLowerCase());
    if (!editingEmployeeId && existingByCode) {
      setAddError("Employee already exists with User ID.");
      return;
    }
    const normalizedCardNumber = addForm.cardNumber.trim();
    if (normalizedCardNumber) {
      const cachedProfiles = readProfileCache();
      const duplicateCard = employeeRows.find((item) => {
        if (editingEmployeeId && item.id === editingEmployeeId) {
          return false;
        }
        const extra = employeeExtras[item.id];
        const cardValue = (extra?.cardNumber ?? cachedProfiles[item.code]?.cardNumber ?? "").trim();
        return cardValue.length > 0 && cardValue.toLowerCase() === normalizedCardNumber.toLowerCase();
      });
      if (duplicateCard) {
        setAddError("Card number already exists.");
        return;
      }
    }

    const targetEmployeeId = editingEmployeeId;
    const passwordError = validatePassword(addForm.password, false);
    if (passwordError) {
      setAddError(passwordError);
      return;
    }

    setAddError(null);
    setAddSaving(true);
    try {
      if (targetEmployeeId) {
        const current = employeeRows.find((item) => item.id === targetEmployeeId);
        await requestJson<{ success: boolean }>(`/api/personnel/employees/${targetEmployeeId}`, {
          method: "PUT",
          body: JSON.stringify({
            employeeCode: code,
            fullName: resolvedName,
            department: departmentName,
            departmentCodes: selectedDepartmentCodes,
            position: positionName,
            isActive: (current?.status ?? "active") === "active",
            privilege: addForm.privilege,
            verifyMode: addForm.verifyMode,
            groupId: addForm.groupId,
            cardNumber: addForm.cardNumber,
            pictureDataUrl: addForm.pictureDataUrl,
            mobileNumber: addForm.mobileNumber,
            gender: addForm.gender,
            hireDate: addForm.dob,
            password: addForm.password,
          }),
        });

        setEmployeeRows((prev) =>
          prev.map((item) =>
            item.id === targetEmployeeId
              ? {
                  ...item,
                  code,
                  name: resolvedName,
                  department: departmentName,
                  departments: selectedDepartmentNames,
                  departmentCodes: selectedDepartmentCodes,
                  areas: selectedAreaLabels,
                  areaCodes: selectedAreaCodes,
                  assignments: selectedAssignments,
                  position: positionName,
                  designation: addForm.privilege,
                  phone: addForm.mobileNumber.trim() || "-",
                  avatar: addForm.pictureDataUrl || item.avatar,
                }
              : item,
          ),
        );
        setEmployeeExtras((prev) => ({
          ...prev,
          [targetEmployeeId]: {
            privilege: addForm.privilege,
            verifyMode: addForm.verifyMode,
            groupId: addForm.groupId,
            cardNumber: addForm.cardNumber.trim(),
            pictureDataUrl: addForm.pictureDataUrl,
            mobileNumber: addForm.mobileNumber.trim(),
            gender: addForm.gender,
            dob: addForm.dob,
            password: addForm.password,
          },
        }));
        const cache = readProfileCache();
        cache[code] = {
          privilege: addForm.privilege,
          verifyMode: addForm.verifyMode,
          groupId: addForm.groupId,
          cardNumber: addForm.cardNumber.trim(),
          pictureDataUrl: addForm.pictureDataUrl,
          mobileNumber: addForm.mobileNumber.trim(),
          gender: addForm.gender,
          dob: addForm.dob,
          password: addForm.password,
        };
        writeProfileCache(cache);
      } else {
        const created = await requestJson<{
          id: string;
          employeeCode: string;
          fullName: string;
          department?: string;
          departments?: string[];
          departmentCodes?: string[];
          areas?: string[];
          areaCodes?: string[];
          position?: string;
        }>(
          "/api/personnel/employees",
          {
            method: "POST",
            body: JSON.stringify({
              employeeCode: code,
          fullName: resolvedName,
              department: departmentName,
              departmentCodes: selectedDepartmentCodes,
              position: positionName,
              privilege: addForm.privilege,
              verifyMode: addForm.verifyMode,
              groupId: addForm.groupId ?? 1,
              cardNumber: addForm.cardNumber,
              pictureDataUrl: addForm.pictureDataUrl,
              mobileNumber: addForm.mobileNumber,
              gender: addForm.gender,
              hireDate: addForm.dob,
              password: addForm.password,
            }),
          },
        );

        const newId = created.id || `${Date.now()}`;
        const newItem: EmployeeRow = {
          id: newId,
          code: created.employeeCode || code,
          name: created.fullName || resolvedName,
          department: created.department || departmentName,
          departments: created.departments?.length ? created.departments : selectedDepartmentNames,
          departmentCodes: created.departmentCodes?.length ? created.departmentCodes : selectedDepartmentCodes,
          areas: created.areas?.length ? created.areas : selectedAreaLabels,
          areaCodes: created.areaCodes?.length ? created.areaCodes : selectedAreaCodes,
          assignments: selectedAssignments,
          position: created.position || positionName,
          designation: addForm.privilege,
          phone: addForm.mobileNumber.trim() || "-",
          status: "active",
          avatar: addForm.pictureDataUrl || "",
        };

        setEmployeeRows((prev) => [newItem, ...prev]);
        setEmployeeExtras((prev) => ({
          ...prev,
          [newId]: {
            privilege: addForm.privilege,
            verifyMode: addForm.verifyMode,
            cardNumber: addForm.cardNumber.trim(),
            pictureDataUrl: addForm.pictureDataUrl,
            mobileNumber: addForm.mobileNumber.trim(),
            gender: addForm.gender,
            dob: addForm.dob,
            password: addForm.password,
          },
        }));
        const cache = readProfileCache();
        cache[newItem.code] = {
          privilege: addForm.privilege,
          verifyMode: addForm.verifyMode,
          cardNumber: addForm.cardNumber.trim(),
          pictureDataUrl: addForm.pictureDataUrl,
          mobileNumber: addForm.mobileNumber.trim(),
          gender: addForm.gender,
          dob: addForm.dob,
          password: addForm.password,
        };
        writeProfileCache(cache);
      }

      setDrawerOpen(false);
      setPage(1);
    } catch (err) {
      setAddError(err instanceof Error ? err.message : "Failed to save employee.");
    } finally {
      setAddSaving(false);
    }
  }

  async function applyActiveState(ids: string[], isActive: boolean) {
    if (ids.length === 0) {
      return;
    }
    await requestJson<{ success: boolean; updated: number }>("/api/personnel/employees/status", {
      method: "POST",
      body: JSON.stringify({ ids, isActive }),
    });
    setEmployeeRows((prev) => prev.map((item) => (ids.includes(item.id) ? { ...item, status: isActive ? "active" : "inactive" } : item)));
  }

  async function applyVerifyMode(ids: string[], verifyMode: number) {
    if (ids.length === 0) {
      return;
    }

    const uniqueIds = Array.from(new Set(ids));
    const normalizedVerifyMode = Number.isFinite(verifyMode) ? verifyMode : 0;
    setBulkVerifyBusy(true);
    setBulkVerifyError(null);
    setBulkVerifyNotice(null);
    try {
      await requestJson<{ success: boolean; updated: number; verifyMode: number }>("/api/personnel/employees/verify-mode", {
        method: "POST",
        body: JSON.stringify({ ids: uniqueIds, verifyMode: normalizedVerifyMode }),
      });

      setEmployeeExtras((prev) => {
        const next = { ...prev };
        for (const id of uniqueIds) {
          const current = next[id];
          if (!current) {
            continue;
          }

          next[id] = { ...current, verifyMode: normalizedVerifyMode };
        }
        return next;
      });

      const cache = readProfileCache();
      for (const employee of employeeRows) {
        if (!uniqueIds.includes(employee.id)) {
          continue;
        }

        const current = cache[employee.code];
        cache[employee.code] = {
          privilege: current?.privilege ?? employee.designation ?? "User",
          privilegeRaw: current?.privilegeRaw,
          cardNumber: current?.cardNumber ?? "",
          pictureDataUrl: current?.pictureDataUrl ?? "",
          faceTemplateVisible: current?.faceTemplateVisible ?? "",
          faceTemplateInfrared: current?.faceTemplateInfrared ?? "",
          fingerprintTemplates: current?.fingerprintTemplates ?? "",
          fingerprintCount: current?.fingerprintCount ?? 0,
          faceCount: current?.faceCount ?? 0,
          photoCount: current?.photoCount ?? 0,
          verifyMode: normalizedVerifyMode,
          groupId: current?.groupId,
          timezoneProfile: current?.timezoneProfile ?? "",
          validFrom: current?.validFrom ?? "",
          validTo: current?.validTo ?? "",
          expiresEnabled: current?.expiresEnabled,
          idNumber: current?.idNumber ?? "",
          secondaryCard: current?.secondaryCard ?? "",
          mobileNumber: current?.mobileNumber ?? "",
          gender: current?.gender ?? "Male",
          dob: current?.dob ?? "",
          password: current?.password ?? "",
        };
      }
      writeProfileCache(cache);
      setBulkVerifyNotice(`Verification mode updated for ${uniqueIds.length} employee(s).`);
    } catch (err) {
      setBulkVerifyError(err instanceof Error ? err.message : "Failed to update verification mode.");
    } finally {
      setBulkVerifyBusy(false);
    }
  }

  async function applyDepartmentTransfer() {
    const departmentCodes = Array.from(
      new Set(
        transferDepartmentCodes
          .map((value) => value.trim())
          .filter((value) => value.length > 0 && departmentByCode.has(value)),
      ),
    );
    if (selectedEmployeeIds.length === 0) {
      setTransferError("Select employee(s) first.");
      return;
    }
    if (departmentCodes.length === 0) {
      setTransferError("Select target department(s) first.");
      return;
    }

    setTransferBusy(true);
    setTransferError(null);
    try {
      await requestJson<{ success: boolean; updated: number }>("/api/personnel/employees/transfer", {
        method: "POST",
        body: JSON.stringify({ ids: selectedEmployeeIds, departmentCodes }),
      });

      const orderedDepartmentCodes = transferPrimaryDepartmentCode && departmentCodes.includes(transferPrimaryDepartmentCode)
        ? [transferPrimaryDepartmentCode, ...departmentCodes.filter((value) => value !== transferPrimaryDepartmentCode)]
        : departmentCodes;
      const primaryDepartmentName = orderedDepartmentCodes[0] ? (departmentByCode.get(orderedDepartmentCodes[0])?.name ?? "") : "";
      const areaLabels = Array.from(
        new Set(
          orderedDepartmentCodes
            .map((value) => departmentByCode.get(value)?.areaLabel ?? "")
            .filter((value) => value.length > 0),
        ),
      );
      const areaCodes = Array.from(
        new Set(
          orderedDepartmentCodes
            .map((value) => departmentByCode.get(value)?.areaCode ?? "")
            .filter((value) => value.length > 0),
        ),
      );
      const departmentNames = orderedDepartmentCodes
        .map((value) => departmentByCode.get(value)?.name ?? "")
        .filter((value, index, array) => value.length > 0 && array.indexOf(value) === index);
      const assignments = buildAssignmentsFromCodes(orderedDepartmentCodes);

      setEmployeeRows((prev) =>
        prev.map((item) =>
          selectedEmployeeIds.includes(item.id)
            ? {
                ...item,
                department: primaryDepartmentName || item.department,
                departments: departmentNames,
                departmentCodes: orderedDepartmentCodes,
                areas: areaLabels,
                areaCodes,
                assignments,
              }
            : item,
        ),
      );
      setTransferModalOpen(false);
    } catch (err) {
      setTransferError(err instanceof Error ? err.message : "Failed to transfer employee(s).");
    } finally {
      setTransferBusy(false);
    }
  }

  async function deleteEmployees(ids: string[]) {
    if (ids.length === 0) {
      return;
    }
    const uniqueIds = Array.from(new Set(ids));
    await requestJson<{ success: boolean; removed: number }>("/api/personnel/employees/delete", {
      method: "POST",
      body: JSON.stringify({ ids: uniqueIds }),
    });
    await loadEmployees();
    setSelectedEmployeeIds((prev) => prev.filter((id) => !uniqueIds.includes(id)));
  }

  async function deleteSingleEmployee(employee: EmployeeRow) {
    try {
      const ok = typeof window !== "undefined" ? window.confirm(`Delete ${employee.name}?`) : true;
      if (!ok) {
        return;
      }
      await deleteEmployees([employee.id]);
    } catch (err) {
      setAddError(err instanceof Error ? err.message : "Failed to delete employee.");
    }
  }

  function toggleEmployeeSelection(id: string, checked: boolean) {
    setSelectedEmployeeIds((prev) => {
      if (checked) {
        return prev.includes(id) ? prev : [...prev, id];
      }
      return prev.filter((item) => item !== id);
    });
  }

  function toggleAllFilteredSelection(checked: boolean) {
    setSelectedEmployeeIds((prev) => {
      const ids = filtered.map((item) => item.id);
      if (checked) {
        return Array.from(new Set([...prev, ...ids]));
      }
      return prev.filter((id) => !ids.includes(id));
    });
  }

  async function runContextAction(action: "edit" | "activate" | "deactivate" | "transfer" | "delete") {
    if (!contextMenu) {
      return;
    }
    const current = contextMenu;
    setContextMenu(null);
    const employee = employeeRows.find((item) => item.id === current.id);
    if (!employee) {
      return;
    }

    try {
      if (action === "edit") {
        openEditModal(employee);
        return;
      }
      if (action === "activate") {
        await applyActiveState([employee.id], true);
        return;
      }
      if (action === "deactivate") {
        await applyActiveState([employee.id], false);
        return;
      }
      if (action === "transfer") {
        openTransferModal([employee.id]);
        return;
      }
      const ok = typeof window !== "undefined" ? window.confirm(`Delete ${employee.name}?`) : true;
      if (!ok) {
        return;
      }
      await deleteEmployees([employee.id]);
    } catch (err) {
      setAddError(err instanceof Error ? err.message : "Action failed.");
    }
  }

  const editingExtra = editingEmployeeId ? employeeExtras[editingEmployeeId] : undefined;

  return (
    <div className="employee-page space-y-4 px-2 pb-2 pt-0 sm:px-4 sm:pb-4 sm:pt-1 lg:px-6 lg:pb-6 lg:pt-1">
      <div className="tab-header w-full p-4">
        <h1 className="text-2xl font-semibold text-[#103856]">Employees</h1>
        <p className="text-sm text-[#5f7f9d]">Clean personnel directory with quick filters, import templates, and bulk actions.</p>
        <div className="metric-grid mt-3 sm:grid-cols-3">
          <div className="metric-card"><p className="metric-card__label">Total</p><p className="metric-card__value">{filtered.length}</p></div>
          <div className="metric-card"><p className="metric-card__label">Active</p><p className="metric-card__value">{activeFilteredCount}</p></div>
          <div className="metric-card"><p className="metric-card__label">Inactive</p><p className="metric-card__value">{inactiveFilteredCount}</p></div>
        </div>
      </div>

      <section className="employee-main-shell w-full p-4 sm:p-5 lg:p-5">
        <div className="tab-toolbar mb-4 space-y-3 rounded-2xl border border-[#dbe9f7] bg-gradient-to-br from-[#f7fbff] to-[#eef6ff] p-3.5">
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={openCreateModal}
              className="inline-flex h-9 items-center gap-2 rounded-full border border-[#3f7fbd] bg-gradient-to-r from-[#1d4ed8] to-[#0369a1] px-4 text-xs font-semibold text-white shadow-[0_10px_16px_-12px_rgba(29,78,216,0.9)] transition-colors hover:from-[#1e40af] hover:to-[#075985] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[#8eb9e3] focus-visible:ring-offset-2 focus-visible:ring-offset-[#f7fbff] sm:text-sm"
            >
              <Plus className="h-4 w-4" />
              Add Employee
            </button>
            <div className="flex items-center gap-2">
              <div className="relative" style={{ position: "relative" }}>
                <button
                  ref={importMenuButtonRef}
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation();
                    setExportMenuOpen(false);
                    setExportMenuPos(null);
                    setImportMenuOpen((prev) => {
                      const next = !prev;
                      if (next) {
                        const rect = importMenuButtonRef.current?.getBoundingClientRect();
                        if (rect) {
                          setImportMenuPos({ left: rect.left, top: rect.bottom + 6 });
                        }
                      } else {
                        setImportMenuPos(null);
                      }
                      return next;
                    });
                  }}
                  className="inline-flex h-9 items-center gap-1 rounded-full border border-[#bcd3f0] bg-white px-3 text-xs font-semibold text-[#1a4a73] transition-colors hover:border-[#8eb9e3] hover:bg-[#e1efff] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[#8eb9e3] focus-visible:ring-offset-2 focus-visible:ring-offset-[#f7fbff] sm:px-4 sm:text-sm"
                >
                  Import
                  <ChevronDown className="h-4 w-4" />
                </button>
                {importMenuOpen && importMenuPos && typeof document !== "undefined"
                  ? createPortal(
                      <div
                        className="min-w-[220px] rounded-xl border border-[#c8def4] bg-white p-1.5 shadow-[0_20px_30px_-12px_rgba(10,40,70,0.45)]"
                        style={{ position: "fixed", left: importMenuPos.left, top: importMenuPos.top, zIndex: 9999 }}
                        onClick={(event) => event.stopPropagation()}
                      >
                        <button
                          type="button"
                          onClick={() => {
                            setImportMenuOpen(false);
                            setImportMenuPos(null);
                            importEmployeesInputRef.current?.click();
                          }}
                          className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Import Employee (UserId + Area + Department required)
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setImportMenuOpen(false);
                            setImportMenuPos(null);
                            importEmployeePhotoFilesInputRef.current?.click();
                          }}
                          className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Import Employee Photo (Image files: filename = UserId)
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setImportMenuOpen(false);
                            setImportMenuPos(null);
                            importEmployeePhotosInputRef.current?.click();
                          }}
                          className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Import Employee Photo (Template: PictureDataUrl)
                        </button>
                      </div>,
                      document.body,
                    )
                  : null}
              </div>
              <input ref={importEmployeesInputRef} type="file" accept=".xlsx,.xls,.csv,.json,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,application/vnd.ms-excel,text/csv,application/json" className="hidden" onChange={(event) => void onImportEmployeesFile(event)} />
              <input ref={importEmployeePhotosInputRef} type="file" accept=".xlsx,.xls,.csv,.json,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,application/vnd.ms-excel,text/csv,application/json" className="hidden" onChange={(event) => void onImportEmployeePhotosFile(event)} />
              <input ref={importEmployeePhotoFilesInputRef} type="file" accept="image/*,.zip,application/zip" multiple className="hidden" onChange={(event) => void onImportEmployeePhotosFromFiles(event)} />
              <div className="relative" style={{ position: "relative" }}>
                <button
                  ref={exportMenuButtonRef}
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation();
                    setImportMenuOpen(false);
                    setImportMenuPos(null);
                    setExportMenuOpen((prev) => {
                      const next = !prev;
                      if (next) {
                        const rect = exportMenuButtonRef.current?.getBoundingClientRect();
                        if (rect) {
                          setExportMenuPos({ left: rect.left, top: rect.bottom + 6 });
                        }
                      } else {
                        setExportMenuPos(null);
                      }
                      return next;
                    });
                  }}
                  className="inline-flex h-9 items-center gap-1 rounded-full border border-[#bcd3f0] bg-white px-3 text-xs font-semibold text-[#1a4a73] transition-colors hover:border-[#8eb9e3] hover:bg-[#e1efff] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[#8eb9e3] focus-visible:ring-offset-2 focus-visible:ring-offset-[#f7fbff] sm:px-4 sm:text-sm"
                >
                  Export
                  <ChevronDown className="h-4 w-4" />
                </button>
                {exportMenuOpen && exportMenuPos && typeof document !== "undefined"
                  ? createPortal(
                      <div
                        className="min-w-[220px] rounded-xl border border-[#c8def4] bg-white p-1.5 shadow-[0_20px_30px_-12px_rgba(10,40,70,0.45)]"
                        style={{ position: "fixed", left: exportMenuPos.left, top: exportMenuPos.top, zIndex: 9999 }}
                        onClick={(event) => event.stopPropagation()}
                      >
                        <button
                          type="button"
                          onClick={() => {
                            setExportMenuOpen(false);
                            setExportMenuPos(null);
                            void openExportModal("employee");
                          }}
                          className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Export Employee
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setExportMenuOpen(false);
                            setExportMenuPos(null);
                            void openExportModal("photo");
                          }}
                          className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Export Employee Photo
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setExportMenuOpen(false);
                            setExportMenuPos(null);
                            void exportEmployees("template");
                          }}
                          className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Export Employee Template (Updated)
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setExportMenuOpen(false);
                            setExportMenuPos(null);
                            void exportEmployees("photo-template");
                          }}
                          className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Export Employee Photo Template
                        </button>
                      </div>,
                      document.body,
                    )
                  : null}
              </div>
            </div>

            <div className="w-full flex flex-wrap items-center justify-end gap-2">
              <button
                type="button"
                disabled={selectedEmployeeIds.length === 0}
                onClick={openSendUserModal}
                className="inline-flex h-9 items-center rounded-full border border-[#bcd3f0] bg-white px-3 text-xs font-semibold text-[#1a4a73] transition-colors hover:border-[#8eb9e3] hover:bg-[#e1efff] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[#8eb9e3] focus-visible:ring-offset-2 focus-visible:ring-offset-[#f7fbff] disabled:cursor-not-allowed disabled:opacity-50 sm:px-4 sm:text-sm"
              >
                Send User
              </button>
              <button
                type="button"
                disabled={selectedEmployeeIds.length === 0}
                onClick={() => openTransferModal()}
                className="inline-flex h-9 items-center rounded-full border border-[#bcd3f0] bg-white px-3 text-xs font-semibold text-[#1a4a73] transition-colors hover:border-[#8eb9e3] hover:bg-[#e1efff] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[#8eb9e3] focus-visible:ring-offset-2 focus-visible:ring-offset-[#f7fbff] disabled:cursor-not-allowed disabled:opacity-50 sm:px-4 sm:text-sm"
              >
                <span className="inline-flex items-center gap-1.5">
                  <ArrowRightLeft className="h-4 w-4" />
                  Transfer Selected
                </span>
              </button>
              <div className="inline-flex h-9 items-center rounded-full border border-[#bcd3f0] bg-white text-xs font-semibold text-[#1a4a73] overflow-hidden">
                <span className="pl-4 pr-2 shrink-0">Verify</span>
                <div className="relative flex items-center border-l border-[#bcd3f0]">
                  <select
                    value={String(bulkVerifyMode)}
                    onChange={(event) => setBulkVerifyMode(Number.parseInt(event.target.value, 10) || 0)}
                    className="h-9 cursor-pointer appearance-none bg-transparent pl-3 pr-7 text-xs font-semibold text-[#1a4a73] outline-none"
                  >
                    {VERIFY_MODE_OPTIONS.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                  <ChevronDown className="pointer-events-none absolute right-2 h-3.5 w-3.5 text-[#6b8fb0]" />
                </div>
                <button
                  type="button"
                  disabled={bulkVerifyBusy || selectedEmployeeIds.length === 0}
                  onClick={() => void applyVerifyMode(selectedEmployeeIds, bulkVerifyMode)}
                  className="inline-flex h-9 items-center border-l border-[#bcd3f0] bg-[#1b5585] px-4 text-xs font-semibold text-white transition hover:bg-[#15466e] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {bulkVerifyBusy ? "…" : "Apply"}
                </button>
              </div>
              <button
                type="button"
                disabled={selectedEmployeeIds.length === 0}
                onClick={() => void applyActiveState(selectedEmployeeIds, true)}
                className="rounded-full border border-[#bcd3f0] bg-white px-3 py-2 text-xs font-semibold text-[#1a4a73] transition duration-200 hover:-translate-y-0.5 hover:border-[#8eb9e3] hover:bg-[#e1efff] disabled:cursor-not-allowed disabled:opacity-50 sm:px-4 sm:text-sm"
              >
                Activate Selected
              </button>
              <button
                type="button"
                disabled={selectedEmployeeIds.length === 0}
                onClick={() => void applyActiveState(selectedEmployeeIds, false)}
                className="rounded-full border border-[#bcd3f0] bg-white px-3 py-2 text-xs font-semibold text-[#1a4a73] transition duration-200 hover:-translate-y-0.5 hover:border-[#8eb9e3] hover:bg-[#e1efff] disabled:cursor-not-allowed disabled:opacity-50 sm:px-4 sm:text-sm"
              >
                Deactivate Selected
              </button>
              <button
                type="button"
                disabled={selectedEmployeeIds.length === 0}
                onClick={() => void deleteEmployees(selectedEmployeeIds)}
                className="rounded-full border border-[#f0caca] bg-white px-3 py-2 text-xs font-semibold text-[#8d2f2f] transition duration-200 hover:-translate-y-0.5 hover:border-[#e8a3a3] hover:bg-[#fff0f0] disabled:cursor-not-allowed disabled:opacity-50 sm:px-4 sm:text-sm"
              >
                Delete Selected
              </button>
              <div className="relative">
                <button
                  type="button"
                  onClick={() => setColMenuOpen((value) => !value)}
                  title="Column preferences"
                  aria-label="Column preferences"
                  className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-[#bcd3f0] bg-white text-[#1a4a73] transition-colors hover:border-[#8eb9e3] hover:bg-[#e1efff]"
                >
                  <Settings2 className="h-4 w-4" />
                </button>
                {colMenuOpen ? (
                  <>
                    <div className="fixed inset-0 z-40" onClick={() => setColMenuOpen(false)} />
                    <div className="absolute right-0 z-50 mt-2 w-56 rounded-xl border border-[#d2e3f5] bg-white p-2 shadow-lg">
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
                        {TOGGLEABLE_COLUMNS.map((column) => (
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
                  </>
                ) : null}
              </div>
            </div>
          </div>

          <div className="grid w-full gap-2 sm:grid-cols-2 xl:grid-cols-[minmax(300px,1.5fr)_minmax(160px,0.9fr)_minmax(190px,1fr)_minmax(140px,0.8fr)]">
            <div className="relative min-w-0">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-[--muted-foreground]" />
              <input
                value={search}
                onChange={(event) => {
                  setPage(1);
                  setSearch(event.target.value);
                }}
                placeholder="Search name, user ID, phone..."
                className="h-10 w-full rounded-full border border-[#c4daf1] bg-white py-2 pl-9 pr-3 text-[14px] text-[#1e4162] outline-none ring-[#8db6df] transition duration-200 hover:border-[#95bee6] focus:ring-2"
              />
            </div>

            <select
              value={areaFilter}
              onChange={(event) => {
                setPage(1);
                setAreaFilter(event.target.value);
              }}
              className="h-10 rounded-full border border-[#c4daf1] bg-white px-4 py-2 text-[14px] font-medium text-[#1e4162] transition duration-200 hover:border-[#95bee6]"
            >
              <option value="all">All Areas</option>
              {areaOptions.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>

            <select
              value={departmentFilter}
              onChange={(event) => {
                setPage(1);
                setDepartmentFilter(event.target.value);
              }}
              className="h-10 rounded-full border border-[#c4daf1] bg-white px-4 py-2 text-[14px] font-medium text-[#1e4162] transition duration-200 hover:border-[#95bee6]"
            >
              <option value="all">All Departments</option>
              {departmentOptions.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>

            <select
              value={status}
              onChange={(event) => {
                setPage(1);
                setStatus(event.target.value);
              }}
              className="h-10 rounded-full border border-[#c4daf1] bg-white px-4 py-2 text-[14px] font-medium text-[#1e4162] transition duration-200 hover:border-[#95bee6]"
            >
              <option value="all">All Status</option>
              <option value="active">Active</option>
              <option value="inactive">Inactive</option>
            </select>
          </div>
          {sendUserError ? (
            <div className="rounded-xl border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-xs text-[#8d2f2f]">
              {sendUserError}
            </div>
          ) : null}
          {sendUserNotice ? (
            <div className="rounded-xl border border-[#cbe6d5] bg-[#effaf3] px-3 py-2 text-xs text-[#24613a]">
              {sendUserNotice}
            </div>
          ) : null}
          {bulkVerifyError ? (
            <div className="rounded-xl border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-xs text-[#8d2f2f]">
              {bulkVerifyError}
            </div>
          ) : null}
          {bulkVerifyNotice ? (
            <div className="rounded-xl border border-[#cbe6d5] bg-[#effaf3] px-3 py-2 text-xs text-[#24613a]">
              {bulkVerifyNotice}
            </div>
          ) : null}
        </div>

        <div className="overflow-x-auto rounded-[22px] border border-[#e0ecf7] bg-white">
          <table className="min-w-full text-left text-sm [&_td]:align-middle [&_th]:align-middle">
            <thead className="bg-[#f2f8ff] text-xs uppercase text-[#1b4970]">
              <tr>
                <th className="px-3 py-2">
                  <input type="checkbox" checked={allFilteredSelected} onChange={(event) => toggleAllFilteredSelection(event.target.checked)} aria-label="Select all employees" />
                </th>
                {colPrefs.photo ? <th className="px-3 py-2">Photo</th> : null}
                <th className="px-3 py-2">
                  <button type="button" onClick={() => updateSort("code")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                    User ID
                    {sortKey === "code" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                  </button>
                </th>
                <th className="px-3 py-2">
                  <button type="button" onClick={() => updateSort("name")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                    Name
                    {sortKey === "name" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                  </button>
                </th>
                {colPrefs.area ? (
                  <th className="px-3 py-2">
                    <button type="button" onClick={() => updateSort("area")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Area
                      {sortKey === "area" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                ) : null}
                {colPrefs.department ? (
                  <th className="px-3 py-2">
                    <button type="button" onClick={() => updateSort("department")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Department
                      {sortKey === "department" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                ) : null}
                {colPrefs.privilege ? (
                  <th className="px-3 py-2">
                    <button type="button" onClick={() => updateSort("designation")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Privilege
                      {sortKey === "designation" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                ) : null}
                {colPrefs.card ? (
                  <th className="px-3 py-2">
                    <button type="button" onClick={() => updateSort("cardNumber")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Card No
                      {sortKey === "cardNumber" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                ) : null}
                {colPrefs.biometric ? (
                  <th className="px-3 py-2">
                    <button type="button" onClick={() => updateSort("biometric")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Biometric
                      {sortKey === "biometric" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                ) : null}
                {colPrefs.phone ? (
                  <th className="px-3 py-2">
                    <button type="button" onClick={() => updateSort("phone")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Phone
                      {sortKey === "phone" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                ) : null}
                {colPrefs.status ? (
                  <th className="px-3 py-2">
                    <button type="button" onClick={() => updateSort("status")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Status
                      {sortKey === "status" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                ) : null}
                <th className="px-3 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((employee, rowIndex) => (
                (() => {
                  const extra = employeeExtras[employee.id];
                  const fpCount = extra?.fingerprintCount ?? 0;
                  const faceCount = extra?.faceCount ?? 0;
                  const photoCount = extra?.photoCount ?? 0;
                  const hasPassword = Boolean(extra?.password && extra.password.trim().length > 0);
                  const hasAnyBiometric = fpCount > 0 || faceCount > 0 || photoCount > 0 || hasPassword;
                  return (
                <tr
                  key={employee.id}
                  onContextMenu={(event) => {
                    event.preventDefault();
                    setContextMenu({ id: employee.id, x: event.clientX, y: event.clientY });
                  }}
                  className={`border-t border-[#e5effa] text-[#1e4162] transition-colors ${selectedEmployeeIds.includes(employee.id) ? "bg-[#f0f8ff]" : rowIndex % 2 === 0 ? "bg-white" : "bg-[#fbfdff]"} hover:bg-[#eef6ff]`}
                >
                  <td className="px-3 py-2">
                    <input type="checkbox" checked={selectedEmployeeIds.includes(employee.id)} onClick={(event) => event.stopPropagation()} onChange={(event) => toggleEmployeeSelection(employee.id, event.target.checked)} aria-label={`Select ${employee.name}`} />
                  </td>
                  {colPrefs.photo ? (
                    <td className="px-3 py-2">
                      {employee.avatar ? (
                        <img src={employee.avatar} alt={employee.name} className="h-9 w-9 rounded-full border border-[--border] object-cover" />
                      ) : (
                        <div className="grid h-9 w-9 place-items-center rounded-full border border-[--border] bg-[#f2f8ff] text-[10px] font-semibold text-[#5f7f9d]">
                          N/A
                        </div>
                      )}
                    </td>
                  ) : null}
                  <td className="px-3 py-2 font-medium">{employee.code}</td>
                  <td className="px-3 py-2">{employee.name}</td>
                  {colPrefs.area ? (
                    <td className="px-3 py-2" title={(employee.areas.length > 0 ? employee.areas : ["-"]).join(", ")}>
                      {(employee.areas.length > 0 ? employee.areas : ["-"]).join(", ")}
                    </td>
                  ) : null}
                  {colPrefs.department ? (
                    <td className="px-3 py-2" title={(employee.departments.length > 0 ? employee.departments : [employee.department]).join(", ")}>
                      {(employee.departments.length > 0 ? employee.departments : [employee.department]).join(", ")}
                    </td>
                  ) : null}
                  {colPrefs.privilege ? <td className="px-3 py-2">{employee.designation}</td> : null}
                  {colPrefs.card ? <td className="px-3 py-2">{extra?.cardNumber || "-"}</td> : null}
                  {colPrefs.biometric ? (
                  <td className="px-3 py-2">
                    {hasAnyBiometric ? (
                      <div className="inline-flex min-h-7 items-center gap-2 rounded-full border border-[#c9dcf2] bg-[#f4f9ff] px-2.5 py-1 text-[#1a4a73]">
                        {fpCount > 0 ? (
                          <span className="inline-flex" title={`Fingerprint enrolled: ${fpCount}`}>
                            <Fingerprint className="h-4 w-4" />
                          </span>
                        ) : null}
                        {faceCount > 0 ? (
                          <span className="inline-flex" title={`Face enrolled: ${faceCount}`}>
                            <ScanFace className="h-4 w-4" />
                          </span>
                        ) : null}
                        {photoCount > 0 ? (
                          <span className="inline-flex" title={`Photo enrolled: ${photoCount}`}>
                            <Camera className="h-4 w-4" />
                          </span>
                        ) : null}
                        {hasPassword ? (
                          <span className="inline-flex" title="Password set">
                            <KeyRound className="h-4 w-4" />
                          </span>
                        ) : null}
                      </div>
                    ) : (
                      <span className="text-[#9bb0c6]">-</span>
                    )}
                  </td>
                  ) : null}
                  {colPrefs.phone ? <td className="px-3 py-2">{employee.phone}</td> : null}
                  {colPrefs.status ? (
                  <td className="px-3 py-2">
                    <span className={employee.status === "active" ? "badge-online rounded-full px-2 py-0.5 text-xs" : "badge-offline rounded-full px-2 py-0.5 text-xs"}>
                      {employee.status}
                    </span>
                  </td>
                  ) : null}
                  <td className="px-3 py-2 text-right">
                    <div className="inline-flex gap-1">
                      <button type="button" onClick={() => openEditModal(employee)} className="rounded-md border border-[#c9dcf2] bg-white p-1.5 text-[#1a4a73] hover:bg-[#eef6ff]" title="View">
                        <Eye className="h-4 w-4" />
                      </button>
                      <button type="button" onClick={() => openEditModal(employee)} className="rounded-md border border-[#c9dcf2] bg-white p-1.5 text-[#1a4a73] hover:bg-[#eef6ff]" title="Edit">
                        <Pencil className="h-4 w-4" />
                      </button>
                      <button type="button" onClick={() => openTransferModal([employee.id])} className="rounded-md border border-[#c9dcf2] bg-white p-1.5 text-[#1a4a73] hover:bg-[#eef6ff]" title="Transfer">
                        <ArrowRightLeft className="h-4 w-4" />
                      </button>
                      <button type="button" onClick={() => void deleteSingleEmployee(employee)} className="rounded-md border border-[#f0caca] bg-white p-1.5 text-[#8d2f2f] hover:bg-[#fff0f0]" title="Delete">
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
                  );
                })()
              ))}
              {rows.length === 0 ? (
                <tr>
                  <td colSpan={visibleColCount} className="px-3 py-8 text-center text-sm text-[--muted-foreground]">
                    No records found for selected filters.
                  </td>
                </tr>
              ) : null}
            </tbody>
          </table>
        </div>

        <div className="mt-4 flex items-center justify-between border-t border-[#e0ecf7] pt-3 text-sm">
          <p className="text-[#6584a3]">
            Showing {rangeStart} to {rangeEnd} of {totalCount}
          </p>
          <p className="text-[#6584a3]">Checked: {selectedEmployeeIds.length}</p>
          <div className="inline-flex items-center gap-2">
            <button
              type="button"
              onClick={() => setPage((value) => Math.max(1, value - 1))}
              disabled={currentPage === 1}
              className="inline-flex items-center gap-1 rounded-lg border border-[--border] bg-[--card] px-3 py-1.5 disabled:opacity-50"
            >
              <ChevronLeft className="h-4 w-4" />
              Prev
            </button>
            <span>
              Page {currentPage} / {totalPages}
            </span>
            <button
              type="button"
              onClick={() => setPage((value) => Math.min(totalPages, value + 1))}
              disabled={currentPage === totalPages}
              className="inline-flex items-center gap-1 rounded-lg border border-[--border] bg-[--card] px-3 py-1.5 disabled:opacity-50"
            >
              Next
              <ChevronRight className="h-4 w-4" />
            </button>
          </div>
        </div>
      </section>

      {contextMenu ? (
        <div
          className="fixed z-[70] min-w-[180px] rounded-xl border border-[#c8def4] bg-white p-1.5 shadow-[0_20px_30px_-12px_rgba(10,40,70,0.45)]"
          style={{ left: contextMenu.x, top: contextMenu.y }}
          onClick={(event) => event.stopPropagation()}
        >
          <button type="button" onClick={() => void runContextAction("edit")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Edit</button>
          <button type="button" onClick={() => void runContextAction("activate")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Activate</button>
          <button type="button" onClick={() => void runContextAction("deactivate")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Deactivate</button>
          <button type="button" onClick={() => void runContextAction("transfer")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Transfer</button>
          <button type="button" onClick={() => void runContextAction("delete")} className="block w-full rounded-md px-3 py-2 text-left text-xs text-[#8d2f2f] hover:bg-[#fff0f0]">Delete</button>
        </div>
      ) : null}

      {drawerOpen ? (
        <div style={{ maxHeight: 'calc(100vh - 80px)', overflowY: 'auto' }}>

        <div className="fixed inset-0 z-50 overflow-hidden bg-[rgba(12,33,56,0.45)] p-0 sm:p-2" onClick={() => setDrawerOpen(false)}>
          <div className="flex h-full items-stretch justify-center">
            <div className="employee-drawer flex min-h-0 h-full max-h-full w-[min(100vw,1180px)] flex-col overflow-hidden overscroll-contain rounded-none border border-[#cfe2f8] bg-[linear-gradient(135deg,#ffffff_0%,#f7fbff_52%,#f2f8ff_100%)] p-4 shadow-[0_30px_50px_-20px_rgba(16,56,86,0.85)] sm:h-full sm:max-h-full sm:w-[min(98vw,1180px)] sm:rounded-[30px] sm:p-6 lg:p-6" onClick={(event) => event.stopPropagation()}>
            <div className="mb-4 flex shrink-0 items-center justify-between border-b border-[#d2e3f5] pb-3">
              <div className="flex items-center gap-3">
                <UserPlus className="h-8 w-8 text-[#2f70a8]" />
                <div>
                  <h2 className="text-2xl font-semibold text-[#0f3857]">{editingEmployeeId ? "Edit Employee" : "New Employee"}</h2>
                  <p className="text-xs text-[#5f7f9f]">Use clean sections below to fill employee profile and enrollment options.</p>
                </div>
              </div>
              <button type="button" onClick={() => setDrawerOpen(false)} className="rounded-full border border-[#d8e6f4] p-2 text-[#4f7697] hover:bg-[#f2f8ff]">
                <X className="h-4 w-4" />
              </button>
            </div>
            {addError ? <div className="mb-3 rounded-lg border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-sm text-[#8d2f2f]">{addError}</div> : null}
            <div className="min-h-0 flex-1 overflow-y-auto pr-1">
            <div className="grid items-start gap-5 pb-8 xl:grid-cols-[minmax(0,1fr)_320px]">
              <div className="min-w-0">
                <div className="rounded-2xl border border-[#d5e4f4] bg-[linear-gradient(140deg,#f7fbff_0%,#ffffff_42%,#e9f4ff_100%)] p-4">
                  <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Basic Details</p>
                  <div className="grid gap-3 lg:grid-cols-3">
                    <label className="grid gap-1 text-sm text-[#1e4162] lg:col-span-1">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">
                        User ID <span className="text-[#d54c4c]">*</span>
                      </span>
                      <input
                        value={addForm.userId}
                        readOnly={Boolean(editingEmployeeId)}
                        onChange={(event) => setAddForm((prev) => ({ ...prev, userId: event.target.value }))}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-[#e9f0fa] px-4 py-2.5 text-sm font-semibold text-[#1f4a6e] outline-none ring-[#8db6df] focus:ring-2"
                      />
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162] lg:col-span-1">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">First Name</span>
                      <input value={addForm.firstName} onChange={(event) => setAddForm((prev) => ({ ...prev, firstName: event.target.value }))} placeholder="Pankaj" className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2" />
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162] lg:col-span-1">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Last Name</span>
                      <input value={addForm.lastName} onChange={(event) => setAddForm((prev) => ({ ...prev, lastName: event.target.value }))} placeholder="Kushwaha" className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2" />
                    </label>
                  </div>
                  <p className="mt-1 text-[11px] text-[#5f7f9f]">Auto-increment on create, editable before save.</p>
                </div>

                <div className="mt-3 rounded-2xl border border-[#d5e4f4] bg-[linear-gradient(140deg,#f5fff9_0%,#ffffff_45%,#ebfff3_100%)] p-4">
                  <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Work Assignment</p>
                  <div className="grid gap-3 items-start lg:grid-cols-2">
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">
                        Area <span className="text-[#d54c4c]">*</span>
                      </span>
                      <select
                        value={selectedAssignmentAreaFilter || areaSelectionOptions[0]?.filterKey || ""}
                        onChange={(event) => toggleAssignmentAreaFilter(event.target.value)}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                      >
                        {areaSelectionOptions.length === 0 && <option value="">Select area</option>}
                        {areaSelectionOptions.map((item) => (
                          <option key={item.filterKey} value={item.filterKey}>
                            {item.label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">
                        Department <span className="text-[#d54c4c]">*</span>
                      </span>
                      <select
                        value={addForm.departmentCodes[0] ?? ""}
                        onChange={(event) => {
                          const code = event.target.value;
                          if (code) toggleAddDepartmentSelection(code, true);
                          addForm.departmentCodes.filter(c => c !== code).forEach(c => toggleAddDepartmentSelection(c, false));
                        }}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                      >
                        <option value="">Select department</option>
                        {availableDepartmentSelections.map((dept) => (
                          <option key={dept.code} value={dept.code}>{dept.name}</option>
                        ))}
                      </select>
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Position</span>
                      <select
                        value={addForm.position}
                        onChange={(event) => setAddForm((prev) => ({ ...prev, position: event.target.value }))}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                      >
                        <option value="">Select Position</option>
                        {positionOptionsForDepartment.map((item) => (
                          <option key={item} value={item}>
                            {item}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Privilege</span>
                      <select
                        value={addForm.privilege}
                        onChange={(event) => setAddForm((prev) => ({ ...prev, privilege: event.target.value as EmployeePrivilege }))}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                      >
                        <option value="User">User</option>
                        <option value="Enroller">Enroller</option>
                        <option value="Admin">Admin</option>
                      </select>
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Verification Mode</span>
                      <select
                        value={String(addForm.verifyMode)}
                        onChange={(event) => setAddForm((prev) => ({ ...prev, verifyMode: Number.parseInt(event.target.value, 10) || 0 }))}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                      >
                        {VERIFY_MODE_OPTIONS.map((option) => (
                          <option key={option.value} value={option.value}>
                            {option.label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Access Group</span>
                      <select
                        value={addForm.groupId}
                        onChange={(event) => setAddForm((prev) => ({ ...prev, groupId: Number.parseInt(event.target.value, 10) || 1 }))}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                      >
                        {accessGroups.length > 0 ? (
                          <>
                            {/* Ensure current value is always an option */}
                            {!accessGroups.some(g => Number(g.groupNo) === addForm.groupId) && (
                              <option value={addForm.groupId}>{addForm.groupId} - Group {addForm.groupId}</option>
                            )}
                            {accessGroups.map(g => (
                              <option key={g.id} value={Number(g.groupNo)}>{g.groupNo} - {g.name}</option>
                            ))}
                          </>
                        ) : (
                          <option value={addForm.groupId}>{addForm.groupId} - Group {addForm.groupId}</option>
                        )}
                      </select>
                    </label>
                  </div>
                </div>

                <div className="mt-3 rounded-2xl border border-[#d5e4f4] bg-[linear-gradient(140deg,#fffaf3_0%,#ffffff_45%,#fff3e8_100%)] p-4">
                  <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Access & Contact</p>
                  <div className="grid gap-3 lg:grid-cols-2">
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Card No</span>
                      <input value={addForm.cardNumber} onChange={(event) => setAddForm((prev) => ({ ...prev, cardNumber: event.target.value }))} className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2" />
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Password</span>
                      <input
                        type="password"
                        inputMode="numeric"
                        pattern="[0-9]*"
                        minLength={6}
                        value={addForm.password}
                        onChange={(event) => {
                          const next = normalizePasswordInput(event.target.value);
                          setAddForm((prev) => ({ ...prev, password: next }));
                        }}
                        className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                      />
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Mobile Number</span>
                      <input value={addForm.mobileNumber} onChange={(event) => setAddForm((prev) => ({ ...prev, mobileNumber: event.target.value }))} className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2" />
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Gender</span>
                      <select value={addForm.gender} onChange={(event) => setAddForm((prev) => ({ ...prev, gender: event.target.value as "Male" | "Female" | "Other" }))} className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2">
                        <option value="Male">Male</option>
                        <option value="Female">Female</option>
                        <option value="Other">Other</option>
                      </select>
                    </label>
                    <label className="grid gap-1 text-sm text-[#1e4162]">
                      <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">DOB</span>
                  <BsDateField value={addForm.dob} onChange={(nextDob) => setAddForm((prev) => ({ ...prev, dob: nextDob }))} className="w-full" />
                    </label>
                  </div>
                </div>
              </div>

              <div className="flex min-w-0 w-full flex-col gap-3">
                <div className="mx-auto grid h-[160px] w-[160px] max-w-full place-items-center overflow-hidden rounded-[24px] border-2 border-dashed border-[#7fa9d6] bg-[#f0f6fe] shadow-[0_8px_16px_rgba(193,214,240,0.9)] sm:h-[186px] sm:w-[186px]">
                  {addForm.pictureDataUrl ? (
                    <img src={addForm.pictureDataUrl} alt="Employee preview" className="h-full w-full object-cover" />
                  ) : (
                    <div className="grid place-items-center gap-1 text-[#779fcb]">
                      <Camera className="h-10 w-10" />
                      <span className="rounded-full bg-white/80 px-3 py-1 text-[11px] font-medium text-[#31679e]">Face Template</span>
                    </div>
                  )}
                </div>
                <input ref={browseInputRef} type="file" accept="image/*" className="hidden" onChange={(event) => void onPickImage(event)} />
                <div className="grid grid-cols-2 gap-2">
                  <button type="button" onClick={() => browseInputRef.current?.click()} className="inline-flex items-center justify-center gap-1.5 rounded-full border border-[#b0c9ed] bg-white px-3 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#ddedff]">
                    <FolderOpen className="h-4 w-4 text-[#2f70a8]" />
                    Browse
                  </button>
                  <button type="button" onClick={() => void openCameraPicker()} className="inline-flex items-center justify-center gap-1.5 rounded-full border border-[#b0c9ed] bg-white px-3 py-2 text-xs font-medium text-[#1a4a73] hover:bg-[#ddedff]">
                    <Camera className="h-4 w-4 text-[#2f70a8]" />
                    Capture
                  </button>
                </div>

                <div className="mt-2 w-full min-w-0 rounded-2xl border border-[#c9dcf2] bg-[linear-gradient(140deg,#f4f9ff_0%,#ffffff_50%,#eef7ff_100%)] p-3">
                  {editingEmployeeId ? (
                    <details className="mb-3 rounded-xl border border-[#c7dbf2] bg-white p-3">
                      <summary className="cursor-pointer text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">
                        Fetched Details
                      </summary>
                      <div className="mt-2 grid gap-1.5 text-[11px] text-[#1e4162]">
                        <p><span className="font-semibold">Privilege:</span> {editingExtra?.privilege || "-"}</p>
                        <p><span className="font-semibold">Secondary Card:</span> {editingExtra?.secondaryCard || "-"}</p>
                        <p><span className="font-semibold">ID Number:</span> {editingExtra?.idNumber || "-"}</p>
                        <p><span className="font-semibold">Verify Mode:</span> {describeVerifyMode(editingExtra?.verifyMode)}</p>
                        <p><span className="font-semibold">Time Zone:</span> {editingExtra?.timezoneProfile || "-"}</p>
                        <p><span className="font-semibold">Valid:</span> {(editingExtra?.validFrom || "-")} to {(editingExtra?.validTo || "-")}</p>
                        <p><span className="font-semibold">Fingerprint Templates:</span> {editingExtra?.fingerprintCount ?? 0}</p>
                        <p><span className="font-semibold">Photos:</span> {editingExtra?.photoCount ?? 0}</p>
                      </div>
                    </details>
                  ) : null}
                  <details className="group">
                    <summary className="mb-2 cursor-pointer text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">
                      Remote Enroll
                    </summary>
                    <p className="mb-2 text-[11px] text-[#5f7f9f]">Select option to choose device</p>
                  <div className="mb-2 flex items-center justify-between gap-2">
                    <p className="min-w-0 break-all text-[11px] text-[#5f7f9f]">Device: {addForm.remoteEnrollDeviceSerial || "Not selected"}</p>
                    {addForm.remoteEnrollDeviceSerial ? (
                      <button
                        type="button"
                        onClick={() => setAddForm((prev) => ({ ...prev, remoteEnrollDeviceSerial: "" }))}
                        className="rounded-full border border-[#b9d2f0] bg-white px-2 py-0.5 text-[10px] text-[#1a4a73]"
                      >
                        Clear
                      </button>
                    ) : null}
                  </div>
                  <div className="grid gap-2">
                    <label className="inline-flex items-center gap-2 rounded-full border border-[#c7dbf2] bg-white px-3 py-2 text-xs text-[#1a4a73]">
                      <input type="checkbox" checked={addForm.remoteEnrollFace} onChange={(event) => applyRemoteToggle("face", event.target.checked)} />
                      Face
                    </label>
                    {addForm.remoteEnrollFace ? (
                      <div className="grid gap-2">
                        <label className="grid gap-1 text-xs text-[#1a4a73]">
                          <span className="font-semibold text-[#1e4f78]">Device Type</span>
                          <select
                            value={addForm.remoteFaceType}
                            onChange={(event) => setAddForm((prev) => ({ ...prev, remoteFaceType: event.target.value }))}
                            className="rounded-full border border-[#b9d2f0] bg-white px-3 py-2 text-xs text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                          >
                            <option value="">Select face type</option>
                            <option value="9">Latest Device (Visible light face)</option>
                            <option value="2">Old Device (Near-infrared face)</option>
                          </select>
                        </label>
                      </div>
                    ) : null}
                    <label className="inline-flex items-center gap-2 rounded-full border border-[#c7dbf2] bg-white px-3 py-2 text-xs text-[#1a4a73]">
                      <input type="checkbox" checked={addForm.remoteEnrollFp} onChange={(event) => applyRemoteToggle("fp", event.target.checked)} />
                      FP
                    </label>
                    {addForm.remoteEnrollFp ? (
                      <label className="grid gap-1 text-xs text-[#1a4a73]">
                        <span className="font-semibold text-[#1e4f78]">Finger Index</span>
                        <select
                          value={addForm.remoteFpIndex}
                          onChange={(event) => setAddForm((prev) => ({ ...prev, remoteFpIndex: event.target.value }))}
                          className="rounded-full border border-[#b9d2f0] bg-white px-3 py-2 text-xs text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                        >
                          <option value="0">0 - Left Little</option>
                          <option value="1">1 - Left Ring</option>
                          <option value="2">2 - Left Middle</option>
                          <option value="3">3 - Left Index</option>
                          <option value="4">4 - Left Thumb</option>
                          <option value="5">5 - Right Thumb</option>
                          <option value="6">6 - Right Index</option>
                          <option value="7">7 - Right Middle</option>
                          <option value="8">8 - Right Ring</option>
                          <option value="9">9 - Right Little</option>
                        </select>
                      </label>
                    ) : null}
                    <button
                      type="button"
                      disabled={enrollSaving || !(addForm.remoteEnrollFace || addForm.remoteEnrollFp)}
                      onClick={() => void onEnrollInDevice()}
                      className="mt-1 rounded-full bg-[#1b5585] px-4 py-2 text-xs font-medium text-white disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      {enrollSaving ? "Enrolling..." : "Enroll in Device"}
                    </button>
                    {(enrollSession || enrollLiveItems.length > 0) ? (
                      <div className="mt-2 rounded-xl border border-[#c7dbf2] bg-white p-2">
                        <div className="mb-1 flex items-center justify-between">
                          <p className="text-[11px] font-semibold text-[#1e4f78]">Real-time Enrollment</p>
                          <div className="flex items-center gap-2">
                            {enrollSession ? (
                              <button
                                type="button"
                                disabled={enrollNudging || enrollSaving}
                                onClick={async () => {
                                  if (!enrollSession) {
                                    return;
                                  }
                                  setEnrollNudging(true);
                                  try {
                                    await forcePollNow(enrollSession.serial);
                                    await loadEnrollLiveStatus(enrollSession);
                                  } catch (err) {
                                    setAddError(err instanceof Error ? err.message : "Failed to nudge device polling.");
                                  } finally {
                                    setEnrollNudging(false);
                                  }
                                }}
                                className="rounded-full border border-[#b9d2f0] bg-white px-2 py-1 text-[10px] text-[#1a4a73] disabled:cursor-not-allowed disabled:opacity-50"
                              >
                                {enrollNudging ? "Nudging..." : "Force Poll Now"}
                              </button>
                            ) : null}
                            <span className={`text-[10px] ${enrollSession?.running ? "text-[#2b7a48]" : "text-[#5f7f9f]"}`}>
                              {enrollSession?.running ? "Live" : "Idle"}
                            </span>
                          </div>
                        </div>
                        {enrollLiveItems.length === 0 ? (
                          <p className="text-[11px] text-[#5f7f9f]">Waiting for device response...</p>
                        ) : (
                          <div className="max-h-[220px] space-y-1 overflow-y-auto pr-1">
                            {enrollLiveItems.map((item) => (
                              <div key={item.id} className="rounded-lg border border-[#e1ecf8] bg-[#f9fcff] px-2 py-1.5">
                                <div className="flex items-center justify-between text-[11px]">
                                  <span className="font-semibold text-[#1e4162]">{item.label}</span>
                                  <span className={item.status === "replied" ? "text-[#2b7a48]" : item.status === "sent" ? "text-[#a36b19]" : "text-[#5f7f9f]"}>
                                    {item.status}
                                  </span>
                                </div>
                                {item.returnCode ? (
                                  <div className="mt-1 flex items-center justify-between gap-2">
                                    <span className={`rounded-full border px-2 py-0.5 text-[10px] font-medium ${returnCodeClass(item.returnCode)}`}>
                                      {item.returnMessage || "Device replied"}
                                    </span>
                                  </div>
                                ) : null}
                                {!item.returnCode && item.returnMessage ? (
                                  <p className="mt-1 text-[10px] text-[#1a4a73]">{item.returnMessage}</p>
                                ) : null}
                              </div>
                            ))}
                          </div>
                        )}
                      </div>
                    ) : null}
                  </div>
                  </details>
                </div>
              </div>
            </div>
            </div>

            <div className="sticky bottom-0 z-10 mt-3 flex shrink-0 flex-wrap items-center justify-end gap-3 border-t border-[#dae7f5] bg-[linear-gradient(180deg,rgba(247,251,255,0),#f7fbff_35%,#f3f8ff_100%)] pb-2 pt-4">
              <button type="button" onClick={() => setDrawerOpen(false)} className="rounded-full border border-[#b9cff0] bg-white px-6 py-2 text-sm font-medium text-[#1a4a73]">
                Cancel
              </button>
              <button type="button" disabled={addSaving || !addForm.userId.trim()} onClick={() => void saveEmployee()} className="rounded-full bg-[#1b5585] px-6 py-2 text-sm font-medium text-white shadow-[0_8px_14px_-8px_#1b5585] disabled:cursor-not-allowed disabled:opacity-50">
                {addSaving ? "Saving..." : editingEmployeeId ? "Update Employee" : "Save Employee"}
              </button>
            </div>
            </div>
          </div>
        </div>
      </div>
      ) : null}

      {transferModalOpen ? (
        <div className="fixed inset-0 z-[55] grid place-items-center bg-[rgba(12,33,56,0.45)] p-4" onClick={() => setTransferModalOpen(false)}>
          <div className="w-full max-w-2xl rounded-2xl border border-[#d8e6f4] bg-white p-4 shadow-[0_20px_40px_-16px_rgba(10,40,70,0.6)]" onClick={(event) => event.stopPropagation()}>
            <div className="mb-3 flex items-center justify-between">
              <div>
                <h3 className="text-base font-semibold text-[#0f3857]">Transfer Selected Employees</h3>
                <p className="text-xs text-[#5f7f9f]">Choose one area, select one or more departments from that area, then apply the transfer to the selected employees.</p>
              </div>
              <button type="button" onClick={() => setTransferModalOpen(false)} className="rounded-full border border-[#d8e6f4] p-1.5 text-[#4f7697]">
                <X className="h-4 w-4" />
              </button>
            </div>

            <p className="mb-3 text-xs text-[#5f7f9f]">
              Selected employees: <span className="font-semibold">{selectedEmployeeIds.length}</span>
            </p>
            {transferError ? (
              <div className="mb-3 rounded-xl border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-xs text-[#8d2f2f]">
                {transferError}
              </div>
            ) : null}

            <div className="grid gap-3">
              <label className="grid gap-1 text-sm text-[#1e4162]">
                <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Target Area</span>
                <select
                  value={selectedTransferAreaFilter || areaSelectionOptions[0]?.filterKey || ""}
                  onChange={(event) => toggleTransferAreaFilter(event.target.value)}
                  className="w-full rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2"
                >
                  <option value="">Select area</option>
                  {areaSelectionOptions.map((item) => (
                    <option key={item.filterKey} value={item.filterKey}>
                      {item.label}
                    </option>
                  ))}
                </select>
              </label>

              <div className="relative block text-sm text-[#1e4162]">
                <span className="mb-1 block text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Department</span>
                <details className="group w-full">
                  <summary className="flex h-[42px] list-none cursor-pointer items-center justify-between gap-3 rounded-xl border border-[#b9d2f0] bg-white px-4 py-2.5 text-sm text-[#1e4162] outline-none transition hover:border-[#9fc2e6]">
                    <span className={`truncate ${selectedTransferDepartments.length === 0 ? "text-[#6f89a1]" : ""}`}>{transferDepartmentSummary}</span>
                    <ChevronDown className="h-4 w-4 shrink-0 text-[#5f7f9f] transition group-open:rotate-180" />
                  </summary>
                  <div className="absolute left-0 right-0 z-30 mt-2 w-full rounded-2xl border border-[#d2e5f7] bg-white p-3 shadow-[0_20px_40px_-18px_rgba(10,40,70,0.45)]">
                    <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                      <span className="text-[11px] font-semibold uppercase tracking-wide text-[#1e4f78]">Department List</span>
                      <div className="flex flex-wrap items-center gap-2">
                        <button
                          type="button"
                          onClick={selectAllVisibleTransferDepartments}
                          className="rounded-full border border-[#c7dbf2] bg-white px-3 py-1 text-[11px] font-semibold text-[#1a4a73] transition hover:bg-[#eef6ff]"
                        >
                          Select All
                        </button>
                        <button
                          type="button"
                          onClick={clearTransferDepartmentSelection}
                          className="rounded-full border border-[#f0caca] bg-white px-3 py-1 text-[11px] font-semibold text-[#8d2f2f] transition hover:bg-[#fff0f0]"
                        >
                          Clear
                        </button>
                      </div>
                    </div>
                    <div className="grid max-h-56 min-h-0 gap-2 overflow-y-auto pr-1 sm:grid-cols-2">
                      {availableTransferDepartmentSelections.length === 0 ? (
                        <p className="col-span-full px-2 py-6 text-center text-sm text-[#6f89a1]">
                          Select an area first.
                        </p>
                      ) : (
                        availableTransferDepartmentSelections.map((department) => {
                          const checked = transferDepartmentCodes.includes(department.code);
                          return (
                            <label
                              key={department.code}
                              className={`flex items-center justify-between gap-3 rounded-xl border px-3 py-2 text-sm transition ${checked ? "border-[#7fb2df] bg-[#eef7ff] text-[#103856]" : "border-[#dbe8f6] bg-white text-[#1e4162] hover:bg-[#f6fbff]"}`}
                            >
                              <span className="inline-flex items-center gap-2">
                                <input
                                  type="checkbox"
                                  checked={checked}
                                  onChange={(event) => toggleTransferDepartmentSelection(department.code, event.target.checked)}
                                />
                                <span>{department.name}</span>
                              </span>
                              <span className="text-[11px] text-[#6a89a5]">{department.code}</span>
                            </label>
                          );
                        })
                      )}
                    </div>
                  </div>
                </details>
              </div>
            </div>

            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setTransferModalOpen(false)} className="rounded-full border border-[#b9cff0] bg-white px-5 py-2 text-sm font-medium text-[#1a4a73]">
                Cancel
              </button>
              <button
                type="button"
                disabled={transferBusy || selectedEmployeeIds.length === 0 || transferDepartmentCodes.length === 0}
                onClick={() => void applyDepartmentTransfer()}
                className="rounded-full bg-[#1b5585] px-5 py-2 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-50"
              >
                {transferBusy ? "Transferring..." : "Apply Transfer"}
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {sendUserModalOpen ? (
        <div className="fixed inset-0 z-[55] grid place-items-center bg-[rgba(12,33,56,0.45)] p-4" onClick={() => setSendUserModalOpen(false)}>
          <div className="w-full max-w-lg rounded-2xl border border-[#d8e6f4] bg-white p-4 shadow-[0_20px_40px_-16px_rgba(10,40,70,0.6)]" onClick={(event) => event.stopPropagation()}>
            <div className="mb-3 flex items-center justify-between">
              <h3 className="text-base font-semibold text-[#0f3857]">Send Selected Users To Device</h3>
              <button type="button" onClick={() => setSendUserModalOpen(false)} className="rounded-full border border-[#d8e6f4] p-1.5 text-[#4f7697]">
                <X className="h-4 w-4" />
              </button>
            </div>

            <p className="mb-2 text-xs text-[#5f7f9f]">
              Selected employees: <span className="font-semibold">{selectedEmployeeIds.length}</span>
            </p>
            <div className="mb-2 flex items-center justify-between">
              <label className="inline-flex items-center gap-2 text-xs font-medium text-[#1a4a73]">
                <input
                  type="checkbox"
                  checked={managedDevices.length > 0 && managedDevices.every((d) => selectedSendDeviceSerials.includes(d.serialNumber))}
                  onChange={(event) =>
                    setSelectedSendDeviceSerials(event.target.checked ? managedDevices.map((d) => d.serialNumber) : [])
                  }
                />
                Select All Devices
              </label>
              <span className="text-[11px] text-[#5f7f9f]">
                {selectedSendDeviceSerials.length} selected
              </span>
            </div>

            <div className="max-h-[320px] space-y-2 overflow-y-auto pr-1">
              {managedDevices.length === 0 ? <p className="text-sm text-[#5f7f9f]">No devices available.</p> : null}
              {managedDevices.map((device) => (
                <label
                  key={device.id}
                  className="flex items-center justify-between rounded-xl border border-[#c7dbf2] bg-[#f7fbff] px-3 py-2 text-left hover:bg-[#eaf4ff]"
                >
                  <span className="inline-flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={selectedSendDeviceSerials.includes(device.serialNumber)}
                      onChange={(event) => toggleSendDevice(device.serialNumber, event.target.checked)}
                    />
                    <span className="text-sm font-medium text-[#1a4a73]">{device.serialNumber}</span>
                  </span>
                  <span className={`text-xs ${device.isOnline === false ? "text-[#b13f3f]" : "text-[#2b7a48]"}`}>
                    {device.isOnline === false ? "Offline" : "Online"}
                  </span>
                </label>
              ))}
            </div>

            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setSendUserModalOpen(false)} className="rounded-full border border-[#b9cff0] bg-white px-5 py-2 text-sm font-medium text-[#1a4a73]">
                Cancel
              </button>
              <button
                type="button"
                disabled={sendUserBusy || selectedEmployeeIds.length === 0 || selectedSendDeviceSerials.length === 0}
                onClick={() => void sendSelectedUsersToDevices()}
                className="rounded-full bg-[#1b5585] px-5 py-2 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-50"
              >
                {sendUserBusy ? "Sending..." : "Send Now"}
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {drawerOpen && cameraPickerOpen ? (
        <div className="fixed inset-0 z-[70] grid place-items-center bg-black/50 p-4" onClick={() => closeCameraPicker()}>
          <div className="w-full max-w-2xl rounded-2xl border border-[#d8e6f4] bg-white p-4 shadow-[0_20px_40px_-16px_rgba(10,40,70,0.6)]" onClick={(event) => event.stopPropagation()}>
            <div className="mb-3 flex items-center justify-between">
              <h3 className="text-base font-semibold text-[#0f3857]">Capture Face Photo</h3>
              <button type="button" onClick={() => closeCameraPicker()} className="rounded-full border border-[#d8e6f4] p-1.5 text-[#4f7697]">
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="grid gap-3 md:grid-cols-[260px_minmax(0,1fr)]">
              <label className="grid gap-1 text-sm text-[#1e4162]">
                <span className="text-xs font-semibold uppercase tracking-wide text-[#1e4f78]">Available Camera</span>
                <select
                  value={activeCameraId}
                  disabled={cameraBusy || cameraDevices.length === 0}
                  onChange={(event) => void switchCamera(event.target.value)}
                  className="rounded-full border border-[#b9d2f0] bg-white px-4 py-2 text-sm text-[#1e4162] outline-none ring-[#8db6df] focus:ring-2 disabled:opacity-50"
                >
                  {cameraDevices.length === 0 ? <option value="">No camera</option> : null}
                  {cameraDevices.map((camera) => (
                    <option key={camera.id} value={camera.id}>
                      {camera.label}
                    </option>
                  ))}
                </select>
                {cameraError ? <span className="text-xs text-[#b13f3f]">{cameraError}</span> : null}
              </label>

              <div className="relative overflow-hidden rounded-xl border border-[#c7dbf2] bg-[#0d2238]">
                <video ref={cameraVideoRef} autoPlay muted playsInline className="h-[260px] w-full object-cover sm:h-[320px]" />
                {cameraBusy ? (
                  <div className="absolute inset-0 grid place-items-center bg-black/45 text-xs font-medium text-white">
                    Starting camera...
                  </div>
                ) : null}
              </div>
            </div>

            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => closeCameraPicker()} className="rounded-full border border-[#b9cff0] bg-white px-5 py-2 text-sm font-medium text-[#1a4a73]">
                Cancel
              </button>
              <button
                type="button"
                disabled={cameraBusy}
                onClick={() => captureFromCamera()}
                className="rounded-full bg-[#1b5585] px-5 py-2 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-50"
              >
                Use Photo
              </button>
            </div>
            <canvas ref={cameraCanvasRef} className="hidden" />
          </div>
        </div>
      ) : null}

      {drawerOpen && remoteDevicePickerOpen ? (
        <div className="fixed inset-0 z-[60] grid place-items-center bg-black/40 p-4" onClick={() => { setRemoteDevicePickerOpen(false); setPendingRemoteToggle(null); }}>
          <div className="w-full max-w-lg rounded-2xl border border-[#d8e6f4] bg-white p-4 shadow-[0_20px_40px_-16px_rgba(10,40,70,0.6)]" onClick={(event) => event.stopPropagation()}>
            <div className="mb-3 flex items-center justify-between">
              <h3 className="text-base font-semibold text-[#0f3857]">Select Enrollment Device</h3>
              <button type="button" onClick={() => { setRemoteDevicePickerOpen(false); setPendingRemoteToggle(null); }} className="rounded-full border border-[#d8e6f4] p-1.5 text-[#4f7697]">
                <X className="h-4 w-4" />
              </button>
            </div>
            <div className="max-h-[300px] space-y-2 overflow-y-auto pr-1">
              {managedDevices.length === 0 ? <p className="text-sm text-[#5f7f9f]">No devices available.</p> : null}
              {managedDevices.map((device) => (
                <button
                  key={device.id}
                  type="button"
                  onClick={() => chooseRemoteDevice(device.serialNumber)}
                  className="flex w-full items-center justify-between rounded-xl border border-[#c7dbf2] bg-[#f7fbff] px-3 py-2 text-left hover:bg-[#eaf4ff]"
                >
                  <span className="text-sm font-medium text-[#1a4a73]">{device.serialNumber}</span>
                  <span className={`text-xs ${device.isOnline === false ? "text-[#b13f3f]" : "text-[#2b7a48]"}`}>
                    {device.isOnline === false ? "Offline" : "Online"}
                  </span>
                </button>
              ))}
            </div>
          </div>
        </div>
      ) : null}

      {exportModalOpen ? (
        <div className="fixed inset-0 z-[70] grid place-items-center bg-black/40 p-4" onClick={() => setExportModalOpen(false)}>
          <div className="w-full max-w-md rounded-2xl border border-[#d8e6f4] bg-white p-4 shadow-[0_20px_40px_-16px_rgba(10,40,70,0.6)]" onClick={(event) => event.stopPropagation()}>
            <div className="mb-3">
              <h3 className="text-base font-semibold text-[#0f3857]">Export {exportModalKind === "photo" ? "Employee Photos" : "Employees"}</h3>
              <p className="text-xs text-[#5f7f9f]">Choose scope for export.</p>
            </div>
            <div className="space-y-3">
              <label className="flex items-center gap-2">
                <input type="radio" name="exportScope" checked={exportModalScope === "all"} onChange={() => setExportModalScope("all")} />
                <span>All</span>
              </label>
              <label className="flex items-center gap-2">
                <input type="radio" name="exportScope" checked={exportModalScope === "selected"} onChange={() => setExportModalScope("selected")} />
                <span>Selected</span>
              </label>
              <label className="flex items-center gap-2">
                <input type="radio" name="exportScope" checked={exportModalScope === "department"} onChange={() => setExportModalScope("department")} />
                <span>Department</span>
              </label>
              <label className="flex items-center gap-2">
                <input type="radio" name="exportScope" checked={exportModalScope === "area"} onChange={() => setExportModalScope("area")} />
                <span>Area</span>
              </label>
              {exportModalScope === "department" ? (
                <select value={exportModalToken} onChange={(e) => setExportModalToken(e.target.value)} className="w-full rounded-xl border border-[#b9d2f0] bg-white px-3 py-2 text-sm text-[#1e4162]">
                  <option value="">Select department</option>
                  {departmentSelectOptions.map((opt) => (
                    <option key={`${opt.areaLabel}-${opt.name}`} value={opt.name}>{opt.name}</option>
                  ))}
                </select>
              ) : exportModalScope === "area" ? (
                <select value={exportModalToken} onChange={(e) => setExportModalToken(e.target.value)} className="w-full rounded-xl border border-[#b9d2f0] bg-white px-3 py-2 text-sm text-[#1e4162]">
                  <option value="">Select area</option>
                  {areaSelectionOptions.map((opt) => (
                    <option key={opt.filterKey} value={opt.label}>{opt.label}</option>
                  ))}
                </select>
              ) : null}
            </div>
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setExportModalOpen(false)} className="rounded-full border border-[#b9cff0] bg-white px-4 py-2 text-sm font-medium text-[#1a4a73]">Cancel</button>
              <button type="button" onClick={() => void performExportFromModal(exportModalKind)} className="rounded-full bg-[#1b5585] px-4 py-2 text-sm font-medium text-white">Export</button>
            </div>
          </div>
        </div>
      ) : null}

      {importModeTitle !== null ? (
        <div className="fixed inset-0 z-[80] grid place-items-center bg-black/40 p-4" onClick={() => resolveImportMode("cancel")}>
          <div className="w-full max-w-sm rounded-2xl border border-[#d8e6f4] bg-white p-4 shadow-[0_20px_40px_-16px_rgba(10,40,70,0.6)]" onClick={(event) => event.stopPropagation()}>
            <h3 className="text-base font-semibold text-[#0f3857]">{importModeTitle}</h3>
            <p className="mt-1 text-xs text-[#5f7f9f]">How should existing records be handled?</p>
            <div className="mt-4 flex flex-col gap-2">
              <button type="button" onClick={() => resolveImportMode("overwrite")} className="rounded-full bg-[#1b5585] px-4 py-2 text-sm font-medium text-white hover:bg-[#15466e]">Overwrite existing</button>
              <button type="button" onClick={() => resolveImportMode("skip")} className="rounded-full border border-[#b9cff0] bg-white px-4 py-2 text-sm font-medium text-[#1a4a73] hover:bg-[#eef6ff]">Skip existing</button>
              <button type="button" onClick={() => resolveImportMode("cancel")} className="rounded-full border border-[#e0c4c4] bg-white px-4 py-2 text-sm font-medium text-[#9a3b3b] hover:bg-[#fdeeee]">Cancel</button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}
