"use client";

import { useEffect, useMemo, useState } from "react";
import {
  Brush,
  Check,
  Clock3,
  Cpu,
  DatabaseBackup,
  Download,
  Globe2,
  KeyRound,
  Lock,
  Pencil,
  Shield,
  SlidersHorizontal,
  Trash2,
  UserPlus,
  Users,
} from "lucide-react";
import {
  applyBrandingSettings,
  extractBrandingSettings,
} from "@/components/data/app-settings";
import { LicensePanel } from "@/components/license-panel";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDateTime } from "@/components/date/date-display";
import { loadUsersFromApi, notifyAuthUpdated, readAuthSession, saveUsersToApi } from "@/components/data/auth-store";
import { resolveApiBases } from "@/lib/api-bases";

type SettingsTab = "general" | "attendance" | "device" | "branding" | "license" | "user" | "backup";
type ActionKind = "success" | "warning" | "error";
type AuditStatus = "pass" | "warn" | "fail";
type UserRole = "Admin" | "Manager" | "Operator";
type FeatureKey =
  | "dashboard"
  | "departments"
  | "devices"
  | "employees"
  | "timetable"
  | "shift"
  | "assign"
  | "transactions"
  | "leaves"
  | "reports"
  | "logs"
  | "settings";

type SettingsState = {
  organizationName: string;
  timezone: string;
  defaultBranch: string;
  language: string;
  holidayOt: boolean;
  weeklyOffOt: boolean;
  pushEndpointPort: string;
  webUiPort: string;
  systemTitle: string;
  supportEmail: string;
  primaryColor: string;
  logoUrl: string;
};

type AuditItem = {
  status: AuditStatus;
  title: string;
  detail: string;
};

type AuditReport = {
  generatedAt: string;
  items: AuditItem[];
};

type FeatureAccess = Record<FeatureKey, boolean>;

type UserAccessRecord = {
  id: string;
  username: string;
  fullName: string;
  password: string;
  role: UserRole;
  isActive: boolean;
  featureAccess: FeatureAccess;
  createdAt: string;
  updatedAt: string;
};

type UserFormState = {
  username: string;
  fullName: string;
  password: string;
  role: UserRole;
  isActive: boolean;
  featureAccess: FeatureAccess;
};

type RuntimeServerSettings = {
  port: number;
  frontendPort: number;
  serverIp: string;
  restartRequired?: boolean;
  message?: string;
};

type SettingsTabItem = {
  id: SettingsTab;
  label: string;
  description: string;
  icon: React.ComponentType<{ className?: string }>;
};

type BackupSchedule = {
  enabled: boolean;
  frequency: "daily" | "weekly" | "monthly";
  runTime: string;
  backupLocation: string;
  retentionCount: number;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
};

type BackupHistoryEntry = {
  id: string;
  fileName: string;
  fileSizeBytes: number | null;
  status: "running" | "completed" | "failed";
  trigger: "manual" | "scheduled";
  errorMessage: string | null;
  startedAtUtc: string;
  finishedAtUtc: string | null;
};

function formatBackupSize(bytes: number | null): string {
  if (bytes === null || bytes === undefined) return "-";
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

const DEFAULT_ADMIN_USER_ID = "default-admin";
const DEFAULT_ADMIN_USERNAME = "admin";
const DEFAULT_ADMIN_PASSWORD = "PB@14891277";
const DEFAULT_ADMIN_FULL_NAME = "Admin User";
let preferredSettingsBase = "";

// Full IANA timezone list from the browser's own database instead of a hand-maintained subset —
// stays complete and correct as the underlying tz database updates, no manual list to fall behind.
// Computed once at module load (pure/static), not per-render.
const ALL_TIMEZONES: string[] = (() => {
  try {
    const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf?.("timeZone");
    if (supported && supported.length > 0) {
      return supported;
    }
  } catch {
    // Older browser without Intl.supportedValuesOf support — fall back to the short list below.
  }
  return ["UTC", "Asia/Kathmandu", "Asia/Kolkata"];
})();

const defaultSettings: SettingsState = {
  organizationName: "अपन हाजिरी",
  timezone: "Asia/Kathmandu",
  defaultBranch: "Main Branch",
  language: "English",
  holidayOt: true,
  weeklyOffOt: true,
  pushEndpointPort: "8081",
  webUiPort: "8001",
  systemTitle: "Apan Hajiri",
  supportEmail: "pbtechzone77@gmail.com",
  primaryColor: "#134f8a",
  logoUrl: "/apan_logo.png",
};

const tabFields: Record<SettingsTab, Array<keyof SettingsState>> = {
  general: ["organizationName", "timezone", "defaultBranch", "language"],
  attendance: ["holidayOt", "weeklyOffOt"],
  device: ["pushEndpointPort", "webUiPort"],
  branding: ["systemTitle", "primaryColor"],
  license: [],
  user: [],
  backup: [],
};

const tabs: SettingsTabItem[] = [
  { id: "general", label: "General Settings", description: "Organization profile, timezone, branch, and language.", icon: Globe2 },
  { id: "attendance", label: "Attendance Rules", description: "Shift/timetable behavior and overtime policy controls.", icon: Clock3 },
  { id: "device", label: "Device Settings", description: "Push endpoint port and connectivity.", icon: Cpu },
  { id: "branding", label: "Branding", description: "System name, logo, and visual identity.", icon: Brush },
  { id: "license", label: "License", description: "Trial, activation, expiry, and deactivation controls.", icon: KeyRound },
  { id: "user", label: "User", description: "Create users and assign feature access permissions.", icon: Users },
  { id: "backup", label: "Backup", description: "Database backup schedule, manual backup, and restore history.", icon: DatabaseBackup },
];

const featureDefinitions: Array<{ key: FeatureKey; label: string; detail: string }> = [
  { key: "dashboard", label: "Dashboard", detail: "Access dashboard widgets and summary cards." },
  { key: "departments", label: "Setup", detail: "Manage area, department, and position setup." },
  { key: "devices", label: "Devices", detail: "Manage devices, commands, and sync actions." },
  { key: "employees", label: "Employees", detail: "Create, edit, and manage employee records." },
  { key: "timetable", label: "Time Table", detail: "Configure timetable setup and windows." },
  { key: "shift", label: "Shifts", detail: "Configure shifts, plans, and timetables." },
  { key: "assign", label: "Schedule Shift", detail: "Schedule shifts by department, employee, or group." },
  { key: "transactions", label: "Transactions", detail: "View and filter attendance transactions." },
  { key: "leaves", label: "Leaves and Holiday", detail: "Manage leave and holiday modules." },
  { key: "reports", label: "Reports", detail: "View and export attendance reports." },
  { key: "logs", label: "Logs", detail: "View system and notification logs." },
  { key: "settings", label: "Settings", detail: "Access system settings and security audit." },
];

function createDefaultFeatureAccess(): FeatureAccess {
  return featureDefinitions.reduce((acc, item) => {
    acc[item.key] = item.key === "dashboard";
    return acc;
  }, {} as FeatureAccess);
}

function createFullFeatureAccess(): FeatureAccess {
  return featureDefinitions.reduce((acc, item) => {
    acc[item.key] = true;
    return acc;
  }, {} as FeatureAccess);
}

function normalizeFeatureAccess(raw: unknown): FeatureAccess {
  const source = raw && typeof raw === "object" ? (raw as Record<string, unknown>) : {};
  const fallback = createDefaultFeatureAccess();
  for (const item of featureDefinitions) {
    if (typeof source[item.key] === "boolean") {
      fallback[item.key] = source[item.key] as boolean;
    }
  }
  return fallback;
}

function isDefaultAdminUsername(value: string): boolean {
  return value.trim().toLowerCase() === DEFAULT_ADMIN_USERNAME;
}

function createDefaultAdminUser(): UserAccessRecord {
  const nowIso = new Date().toISOString();
  return {
    id: DEFAULT_ADMIN_USER_ID,
    username: DEFAULT_ADMIN_USERNAME,
    fullName: DEFAULT_ADMIN_FULL_NAME,
    password: DEFAULT_ADMIN_PASSWORD,
    role: "Admin",
    isActive: true,
    featureAccess: createFullFeatureAccess(),
    createdAt: nowIso,
    updatedAt: nowIso,
  };
}

function sortUsersWithAdminFirst(rows: UserAccessRecord[]): UserAccessRecord[] {
  return rows.sort((a, b) => {
    const aAdmin = isDefaultAdminUsername(a.username);
    const bAdmin = isDefaultAdminUsername(b.username);
    if (aAdmin && !bAdmin) {
      return -1;
    }
    if (!aAdmin && bAdmin) {
      return 1;
    }
    return a.username.localeCompare(b.username, undefined, { sensitivity: "base" });
  });
}

function createEmptyUserForm(): UserFormState {
  return {
    username: "",
    fullName: "",
    password: "",
    role: "Operator",
    isActive: true,
    featureAccess: createDefaultFeatureAccess(),
  };
}

type FieldProps = {
  label: string;
  hint: string;
  children: React.ReactNode;
};

function Field({ label, hint, children }: FieldProps) {
  return (
    <label className="space-y-1.5">
      <div className="text-sm font-medium text-[--foreground]">{label}</div>
      <div className="text-xs text-[--muted-foreground]">{hint}</div>
      {children}
    </label>
  );
}

type ToggleFieldProps = {
  label: string;
  hint: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
};

function ToggleField({ label, hint, checked, onChange }: ToggleFieldProps) {
  return (
    <div className="flex items-start justify-between gap-3 rounded-lg border border-[--border] bg-[--card] px-3 py-3">
      <div className="min-w-0">
        <p className="text-sm font-medium">{label}</p>
        <p className="mt-1 text-xs text-[--muted-foreground]">{hint}</p>
      </div>
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="mt-1 h-4 w-4 rounded border-[--input]"
      />
    </div>
  );
}

function SettingsMetric({
  label,
  value,
  detail,
  tone = "default",
}: {
  label: string;
  value: string;
  detail: string;
  tone?: "default" | "good" | "warn";
}) {
  const toneClass =
    tone === "good"
      ? "border-emerald-200 bg-emerald-50/80 text-emerald-900"
      : tone === "warn"
        ? "border-amber-200 bg-amber-50/80 text-amber-900"
        : "border-sky-200 bg-[linear-gradient(135deg,#f7fbff,#eef7ff)] text-[#0d334d]";

  return (
    <div className={`rounded-2xl border px-4 py-3 shadow-sm ${toneClass}`}>
      <p className="text-[11px] font-semibold uppercase tracking-[0.18em] opacity-75">{label}</p>
      <p className="mt-2 text-lg font-semibold">{value}</p>
      <p className="mt-1 text-xs opacity-80">{detail}</p>
    </div>
  );
}

function parsePortValue(value: string): number | null {
  const parsed = Number.parseInt(value.trim(), 10);
  return Number.isInteger(parsed) && parsed >= 1 && parsed <= 65535 ? parsed : null;
}

function normalizeLoadedSettings(raw: unknown): Partial<SettingsState> {
  if (!raw || typeof raw !== "object") {
    return {};
  }

  const source = raw as Record<string, unknown>;
  const result: Record<string, string | boolean> = {};
  for (const key of Object.keys(defaultSettings) as Array<keyof SettingsState>) {
    const expected = defaultSettings[key];
    const value = source[key];
    if (typeof expected === "boolean" && typeof value === "boolean") {
      result[key] = value;
    }

    if (typeof expected === "string" && typeof value === "string") {
      if (key === "supportEmail" && value.trim().toLowerCase() === "support@pbattendance.com") {
        result[key] = "pbtechzone77@gmail.com";
        continue;
      }
      if (key === "supportEmail" && value.trim().toLowerCase() === "pbtechzon77@gmail.com") {
        result[key] = "pbtechzone77@gmail.com";
        continue;
      }
      if (key === "logoUrl" && value.trim() === "/logo.svg") {
        result[key] = "/logo_pb.ico";
        continue;
      }
      result[key] = value;
    }
  }

  return result as Partial<SettingsState>;
}

function normalizeLoadedUsers(raw: unknown): UserAccessRecord[] {
  const normalized = (Array.isArray(raw) ? raw : [])
    .map((row, index): UserAccessRecord | null => {
      if (!row || typeof row !== "object") {
        return null;
      }

      const source = row as Record<string, unknown>;
      const username = typeof source.username === "string" ? source.username.trim() : "";
      if (!username) {
        return null;
      }

      const role: UserRole =
        source.role === "Admin" || source.role === "Manager" || source.role === "Operator"
          ? source.role
          : "Operator";

      const nowIso = new Date().toISOString();
      return {
        id: typeof source.id === "string" && source.id.trim().length > 0 ? source.id : `user-${index + 1}`,
        username,
        fullName: typeof source.fullName === "string" ? source.fullName.trim() : username,
        password: typeof source.password === "string" ? source.password : "",
        role,
        isActive: typeof source.isActive === "boolean" ? source.isActive : true,
        featureAccess: normalizeFeatureAccess(source.featureAccess),
        createdAt: typeof source.createdAt === "string" ? source.createdAt : nowIso,
        updatedAt: typeof source.updatedAt === "string" ? source.updatedAt : nowIso,
      };
    })
    .filter((row): row is UserAccessRecord => row !== null);

  const deduped = new Map<string, UserAccessRecord>();
  for (const row of normalized) {
    const key = row.username.toLowerCase();
    if (!deduped.has(key)) {
      deduped.set(key, row);
    }
  }

  const users = Array.from(deduped.values());
  const adminIndex = users.findIndex((row) => isDefaultAdminUsername(row.username));
  if (adminIndex >= 0) {
    const current = users[adminIndex];
    users[adminIndex] = {
      ...current,
      // Keep the real backend id if we have one — overwriting it with the placeholder
      // "default-admin" breaks the save round-trip's ability to match admin back to its real row.
      id: current.id && current.id !== DEFAULT_ADMIN_USER_ID ? current.id : DEFAULT_ADMIN_USER_ID,
      username: DEFAULT_ADMIN_USERNAME,
      fullName: DEFAULT_ADMIN_FULL_NAME,
      // The backend never returns a real password hash (always blank after a load), so defaulting
      // a blank password to DEFAULT_ADMIN_PASSWORD meant every save — even one just creating an
      // unrelated user — silently re-sent and re-saved that fixed password for admin, permanently
      // overwriting whatever the real admin password had been changed to. Leave it blank; the
      // backend already treats a blank password on save as "keep the existing hash unchanged".
      password: current.password,
      role: "Admin",
      isActive: true,
      featureAccess: createFullFeatureAccess(),
    };
  } else {
    users.push(createDefaultAdminUser());
  }

  return sortUsersWithAdminFirst(users);
}

async function requestSettingsJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch settings.";

  for (const base of resolveApiBases({ preferredBase: preferredSettingsBase, includeCurrentOrigin: true })) {
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
        preferredSettingsBase = base;
        return (await response.json()) as T;
      }

      try {
        const payload = await response.json();
        if (payload && typeof payload === "object" && typeof (payload as { message?: unknown }).message === "string") {
          lastError = (payload as { message: string }).message;
        } else {
          lastError = `Request failed (${response.status})`;
        }
      } catch {
        const text = await response.text();
        lastError = text.trim() || `Request failed (${response.status})`;
      }
    } catch (error) {
      lastError = error instanceof Error ? error.message : lastError;
    }
  }

  throw new Error(lastError);
}

async function loadSettingsFromApi(): Promise<SettingsState> {
  const payload = await requestSettingsJson<Partial<SettingsState>>("/api/app-config/settings");
  return {
    ...defaultSettings,
    ...normalizeLoadedSettings(payload),
  };
}

async function saveSettingsToApi(settings: SettingsState): Promise<SettingsState> {
  const payload = {
    ...settings,
  };
  const saved = await requestSettingsJson<Partial<SettingsState>>("/api/app-config/settings", {
    method: "PUT",
    body: JSON.stringify(payload),
  });
  return {
    ...defaultSettings,
    ...normalizeLoadedSettings(saved),
  };
}

async function loadRuntimeSettings(): Promise<RuntimeServerSettings> {
  const payload = await requestSettingsJson<Partial<RuntimeServerSettings>>("/api/settings");
  return {
    port: typeof payload.port === "number" ? payload.port : (parsePortValue(defaultSettings.pushEndpointPort) ?? 8081),
    frontendPort: typeof payload.frontendPort === "number" ? payload.frontendPort : (parsePortValue(defaultSettings.webUiPort) ?? 8001),
    serverIp: typeof payload.serverIp === "string" && payload.serverIp.trim() ? payload.serverIp.trim() : "127.0.0.1",
    restartRequired: false,
    message: "",
  };
}

async function updateRuntimePort(port: number, frontendPort?: number): Promise<RuntimeServerSettings> {
  const payload = await requestSettingsJson<Partial<RuntimeServerSettings>>("/api/settings/port", {
    method: "PUT",
    body: JSON.stringify({ port, frontendPort }),
  });

  return {
    port: typeof payload.port === "number" ? payload.port : port,
    frontendPort: typeof payload.frontendPort === "number" ? payload.frontendPort : (frontendPort ?? parsePortValue(defaultSettings.webUiPort) ?? 8001),
    serverIp: typeof payload.serverIp === "string" && payload.serverIp.trim() ? payload.serverIp.trim() : "127.0.0.1",
    restartRequired: Boolean(payload.restartRequired),
    message: typeof payload.message === "string" ? payload.message : "",
  };
}

async function loadBackupScheduleFromApi(): Promise<BackupSchedule> {
  return requestSettingsJson<BackupSchedule>("/api/backup/settings");
}

async function saveBackupScheduleToApi(schedule: Pick<BackupSchedule, "enabled" | "frequency" | "runTime" | "backupLocation" | "retentionCount">): Promise<BackupSchedule> {
  return requestSettingsJson<BackupSchedule>("/api/backup/settings", {
    method: "PUT",
    body: JSON.stringify(schedule),
  });
}

async function loadBackupHistoryFromApi(): Promise<BackupHistoryEntry[]> {
  const payload = await requestSettingsJson<{ data: BackupHistoryEntry[] }>("/api/backup/history?page=1&pageSize=20");
  return Array.isArray(payload?.data) ? payload.data : [];
}

async function runBackupNowApi(): Promise<BackupHistoryEntry> {
  return requestSettingsJson<BackupHistoryEntry>("/api/backup/run", { method: "POST" });
}

async function trySyncAttendancePolicy(settings: SettingsState): Promise<boolean> {
  if (typeof window === "undefined") {
    return false;
  }

  const bases = resolveApiBases({ includeCurrentOrigin: true });

  for (const base of bases) {
    try {
      const controller = new AbortController();
      const timer = window.setTimeout(() => controller.abort(), 5000);
      const response = await fetch(`${base}/api/attendance/policy`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          holidayWorkAsOvertime: settings.holidayOt,
          weeklyOffWorkAsOvertime: settings.weeklyOffOt,
        }),
        signal: controller.signal,
      });
      window.clearTimeout(timer);
      if (response.ok) {
        return true;
      }
    } catch {
      // Try next base.
    }
  }

  return false;
}

function generateSecurityAudit(settings: SettingsState): AuditReport {
  const items: AuditItem[] = [];

  if (settings.organizationName.trim().length >= 3) {
    items.push({ status: "pass", title: "Organization Name", detail: "Organization name is configured." });
  } else {
    items.push({ status: "fail", title: "Organization Name", detail: "Organization name is missing or too short." });
  }

  const emailValid = /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(settings.supportEmail.trim());
  items.push(
    emailValid
      ? { status: "pass", title: "Support Email", detail: "Support email format looks valid." }
      : { status: "fail", title: "Support Email", detail: "Support email format is invalid." },
  );

  const port = Number.parseInt(settings.pushEndpointPort, 10);
  items.push(
    Number.isInteger(port) && port >= 1 && port <= 65535
      ? { status: "pass", title: "Push Endpoint Port", detail: `Configured port ${port} is within valid range.` }
      : { status: "fail", title: "Push Endpoint Port", detail: "Port should be a number between 1 and 65535." },
  );

  const logo = settings.logoUrl.trim();
  const logoValid = logo.startsWith("/") || logo.startsWith("http://") || logo.startsWith("https://");
  items.push(
    logoValid
      ? { status: "pass", title: "Logo URL", detail: "Logo path format looks valid." }
      : { status: "warn", title: "Logo URL", detail: "Use either absolute URL or root-relative path like /logo.svg." },
  );

  return { generatedAt: new Date().toISOString(), items };
}

function validateSettingsForTab(tab: SettingsTab, settings: SettingsState): string[] {
  const issues: string[] = [];

  if (tab === "general") {
    if (settings.organizationName.trim().length < 3) {
      issues.push("Organization name must be at least 3 characters.");
    }
    if (!settings.defaultBranch.trim()) {
      issues.push("Default branch is required.");
    }
  }

  if (tab === "device") {
    if (parsePortValue(settings.pushEndpointPort) === null) {
      issues.push("Push endpoint port must be a valid port between 1 and 65535.");
    }
  }

  if (tab === "branding") {
    if (!settings.systemTitle.trim()) {
      issues.push("System title is required.");
    }
  }

  return issues;
}

export default function SettingsPage() {
  const { calendarType } = useCalendarPreference();
  // Creating/editing/deleting software users is Admin-only — the backend enforces this too
  // (403 for non-Admin callers), this just hides the controls so a Manager/Operator with
  // "settings" feature access doesn't see buttons that would just fail.
  const isAdmin = readAuthSession()?.role === "Admin";
  const [tab, setTab] = useState<SettingsTab>("general");
  const [settings, setSettings] = useState<SettingsState>(defaultSettings);
  const [savedSettings, setSavedSettings] = useState<SettingsState>(defaultSettings);
  const [runtimeSettings, setRuntimeSettings] = useState<RuntimeServerSettings | null>(null);
  const [users, setUsers] = useState<UserAccessRecord[]>([]);
  const [userForm, setUserForm] = useState<UserFormState>(createEmptyUserForm());
  const [editingUserId, setEditingUserId] = useState<string | null>(null);
  const [showUserEditor, setShowUserEditor] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [actionKind, setActionKind] = useState<ActionKind>("success");
  const [auditReport, setAuditReport] = useState<AuditReport | null>(null);
  const [lastSavedAt, setLastSavedAt] = useState<string | null>(null);
  const [backupSchedule, setBackupSchedule] = useState<BackupSchedule | null>(null);
  const [backupHistory, setBackupHistory] = useState<BackupHistoryEntry[]>([]);
  const [backupLoading, setBackupLoading] = useState(false);
  const [backupSaving, setBackupSaving] = useState(false);
  const [backupRunning, setBackupRunning] = useState(false);
  const [backupError, setBackupError] = useState<string | null>(null);
  const [backupMessage, setBackupMessage] = useState<string | null>(null);

  const currentTab = useMemo(() => tabs.find((item) => item.id === tab) ?? tabs[0], [tab]);
  const sectionFieldCount = tabFields[tab].length;
  const dirtyFieldCount = useMemo(() => {
    const fields = tabFields[tab];
    return fields.filter((field) => settings[field] !== savedSettings[field]).length;
  }, [savedSettings, settings, tab]);
  const validationIssues = useMemo(() => validateSettingsForTab(tab, settings), [settings, tab]);

  useEffect(() => {
    if (tab === "user") {
      return;
    }
    setShowUserEditor(false);
    setEditingUserId(null);
    setUserForm(createEmptyUserForm());
  }, [tab]);

  const loadBackupData = async () => {
    setBackupLoading(true);
    setBackupError(null);
    try {
      const [schedule, history] = await Promise.all([loadBackupScheduleFromApi(), loadBackupHistoryFromApi()]);
      setBackupSchedule(schedule);
      setBackupHistory(history);
    } catch (error) {
      setBackupError(error instanceof Error ? error.message : "Failed to load backup settings.");
    } finally {
      setBackupLoading(false);
    }
  };

  useEffect(() => {
    if (tab === "backup" && isAdmin) {
      void loadBackupData();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab]);

  const saveBackupSchedule = async () => {
    if (!backupSchedule) return;
    setBackupSaving(true);
    setBackupError(null);
    setBackupMessage(null);
    try {
      const saved = await saveBackupScheduleToApi(backupSchedule);
      setBackupSchedule(saved);
      setBackupMessage("Backup settings saved.");
    } catch (error) {
      setBackupError(error instanceof Error ? error.message : "Failed to save backup settings.");
    } finally {
      setBackupSaving(false);
    }
  };

  const runBackupNow = async () => {
    setBackupRunning(true);
    setBackupError(null);
    setBackupMessage(null);
    try {
      await runBackupNowApi();
      setBackupMessage("Backup completed.");
      await loadBackupData();
    } catch (error) {
      setBackupError(error instanceof Error ? error.message : "Backup failed.");
      await loadBackupData();
    } finally {
      setBackupRunning(false);
    }
  };

  const downloadBackup = async (id: string, fileName: string) => {
    setBackupError(null);
    try {
      // Plain window.open() can't attach the bearer auth header the API requires, so fetch the
      // file as a blob (the global fetch patch attaches it automatically) and trigger a download.
      const bases = resolveApiBases({ includeCurrentOrigin: true });
      let lastError = "Failed to download backup.";
      for (const base of bases) {
        try {
          const response = await fetch(`${base}/api/backup/download/${encodeURIComponent(id)}`, { cache: "no-store" });
          if (!response.ok) {
            lastError = `Request failed (${response.status})`;
            continue;
          }
          const blob = await response.blob();
          const url = URL.createObjectURL(blob);
          const link = document.createElement("a");
          link.href = url;
          link.download = fileName;
          link.click();
          URL.revokeObjectURL(url);
          return;
        } catch (error) {
          lastError = error instanceof Error ? error.message : lastError;
        }
      }
      setBackupError(lastError);
    } catch (error) {
      setBackupError(error instanceof Error ? error.message : "Failed to download backup.");
    }
  };

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      try {
        const [loadedSettings, runtime] = await Promise.all([
          loadSettingsFromApi(),
          loadRuntimeSettings().catch(() => null),
        ]);
        const mergedSettings = runtime
          ? { ...loadedSettings, pushEndpointPort: String(runtime.port) }
          : loadedSettings;
        if (!cancelled) {
          setRuntimeSettings(runtime);
          setSettings(mergedSettings);
          setSavedSettings(mergedSettings);
          applyBrandingSettings(extractBrandingSettings(mergedSettings));
        }
      } catch {
        // Keep bundled defaults if the backend config endpoint is unavailable.
        if (!cancelled) {
          applyBrandingSettings(extractBrandingSettings(defaultSettings));
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    applyBrandingSettings(extractBrandingSettings(settings));
  }, [
    settings.organizationName,
    settings.systemTitle,
    settings.logoUrl,
    settings.primaryColor,
    settings.supportEmail,
  ]);

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      try {
        const loadedUsers = await loadUsersFromApi();
        if (!cancelled) {
          setUsers(normalizeLoadedUsers(loadedUsers));
        }
      } catch {
        if (!cancelled) {
          setUsers(normalizeLoadedUsers([]));
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  function setField<K extends keyof SettingsState>(key: K, value: SettingsState[K]) {
    setSettings((prev) => ({ ...prev, [key]: value }));
  }

  function showMessage(kind: ActionKind, message: string) {
    setActionKind(kind);
    setActionMessage(message);
  }

  async function persistUsers(nextUsers: UserAccessRecord[]): Promise<UserAccessRecord[]> {
    const normalized = normalizeLoadedUsers(nextUsers);
    const saved = await saveUsersToApi(normalized);
    notifyAuthUpdated();
    return normalizeLoadedUsers(saved);
  }

  function setUserField<K extends keyof UserFormState>(key: K, value: UserFormState[K]) {
    setUserForm((prev) => ({ ...prev, [key]: value }));
  }

  function setUserFeature(key: FeatureKey, checked: boolean) {
    if (editingUserId) {
      const target = users.find((row) => row.id === editingUserId);
      if (target && isDefaultAdminUsername(target.username)) {
        return;
      }
    }

    setUserForm((prev) => ({
      ...prev,
      featureAccess: {
        ...prev.featureAccess,
        [key]: checked,
      },
    }));
  }

  function setAllUserFeatures(checked: boolean) {
    if (editingUserId) {
      const target = users.find((row) => row.id === editingUserId);
      if (target && isDefaultAdminUsername(target.username)) {
        return;
      }
    }

    setUserForm((prev) => ({
      ...prev,
      featureAccess: featureDefinitions.reduce((acc, item) => {
        acc[item.key] = checked;
        return acc;
      }, {} as FeatureAccess),
    }));
  }

  const selectedFeatureCount = useMemo(
    () => featureDefinitions.filter((item) => userForm.featureAccess[item.key]).length,
    [userForm.featureAccess],
  );

  const editingUser = useMemo(
    () => users.find((row) => row.id === editingUserId) ?? null,
    [editingUserId, users],
  );
  const isEditingDefaultAdmin = editingUser ? isDefaultAdminUsername(editingUser.username) : false;

  function openCreateUserForm() {
    setEditingUserId(null);
    setUserForm(createEmptyUserForm());
    setShowUserEditor(true);
  }

  function resetUserForm() {
    setEditingUserId(null);
    setUserForm(createEmptyUserForm());
    setShowUserEditor(false);
  }

  function handleEditUser(id: string) {
    const target = users.find((row) => row.id === id);
    if (!target) {
      showMessage("error", "Selected user was not found.");
      return;
    }
    const isDefaultAdmin = isDefaultAdminUsername(target.username);

    setShowUserEditor(true);
    setEditingUserId(id);
    setUserForm({
      username: isDefaultAdmin ? DEFAULT_ADMIN_USERNAME : target.username,
      fullName: isDefaultAdmin ? DEFAULT_ADMIN_FULL_NAME : target.fullName,
      password: "",
      role: isDefaultAdmin ? "Admin" : target.role,
      isActive: isDefaultAdmin ? true : target.isActive,
      featureAccess: isDefaultAdmin ? createFullFeatureAccess() : normalizeFeatureAccess(target.featureAccess),
    });
  }

  async function handleDeleteUser(id: string) {
    const target = users.find((row) => row.id === id);
    if (!target) {
      return;
    }
    if (isDefaultAdminUsername(target.username)) {
      showMessage("warning", "Default admin user cannot be deleted.");
      return;
    }

    const ok = typeof window !== "undefined"
      ? window.confirm(`Delete user "${target.username}"?`)
      : true;
    if (!ok) {
      return;
    }

    const nextUsers = users.filter((row) => row.id !== id);
    try {
      const normalized = await persistUsers(nextUsers);
      setUsers(normalized);
      if (editingUserId === id) {
        resetUserForm();
      }

      setLastSavedAt(new Date().toISOString());
      showMessage("success", `User "${target.username}" deleted.`);
    } catch (error) {
      showMessage("error", error instanceof Error ? error.message : "Failed to delete user.");
    }
  }

  async function handleSaveUser() {
    const username = userForm.username.trim();
    const fullName = userForm.fullName.trim();
    const password = userForm.password.trim();
    const editingTarget = editingUserId ? users.find((row) => row.id === editingUserId) ?? null : null;

    if (editingTarget && isDefaultAdminUsername(editingTarget.username)) {
      if (!password) {
        showMessage("error", "Default admin password is required.");
        return;
      }

      const nowIso = new Date().toISOString();
      const nextUsers = users.map((row) => {
        if (row.id !== editingTarget.id) {
          return row;
        }

        return {
          ...row,
          id: DEFAULT_ADMIN_USER_ID,
          username: DEFAULT_ADMIN_USERNAME,
          fullName: DEFAULT_ADMIN_FULL_NAME,
          password,
          role: "Admin" as const,
          isActive: true,
          featureAccess: createFullFeatureAccess(),
          updatedAt: nowIso,
        };
      });

      try {
        const normalized = await persistUsers(nextUsers);
        setUsers(normalized);
        setLastSavedAt(new Date().toISOString());
        showMessage("success", "Default admin password updated.");
        resetUserForm();
      } catch (error) {
        showMessage("error", error instanceof Error ? error.message : "Failed to update admin password.");
      }
      return;
    }

    if (!username || !fullName) {
      showMessage("error", "Username and full name are required.");
      return;
    }

    if (isDefaultAdminUsername(username)) {
      showMessage("error", "Username 'admin' is reserved for the default admin user.");
      return;
    }

    if (!editingUserId && !password) {
      showMessage("error", "Password is required for new users.");
      return;
    }

    if (selectedFeatureCount === 0) {
      showMessage("error", "Select at least one feature for user access.");
      return;
    }

    const duplicate = users.some(
      (row) => row.id !== editingUserId && row.username.toLowerCase() === username.toLowerCase(),
    );
    if (duplicate) {
      showMessage("error", "Username already exists.");
      return;
    }

    const nowIso = new Date().toISOString();
    let nextUsers: UserAccessRecord[];

    if (editingUserId) {
      nextUsers = users.map((row) => {
        if (row.id !== editingUserId) {
          return row;
        }

        return {
          ...row,
          username,
          fullName,
          password: password || row.password,
          role: userForm.role,
          isActive: userForm.isActive,
          featureAccess: normalizeFeatureAccess(userForm.featureAccess),
          updatedAt: nowIso,
        };
      });
    } else {
      nextUsers = [
        ...users,
        {
          id: `user-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`,
          username,
          fullName,
          password,
          role: userForm.role,
          isActive: userForm.isActive,
          featureAccess: normalizeFeatureAccess(userForm.featureAccess),
          createdAt: nowIso,
          updatedAt: nowIso,
        },
      ];
    }

    const sorted = sortUsersWithAdminFirst(nextUsers.slice());
    try {
      const normalized = await persistUsers(sorted);
      setUsers(normalized);
      setLastSavedAt(new Date().toISOString());
      showMessage("success", editingUserId ? `User "${username}" updated.` : `User "${username}" created.`);
      resetUserForm();
    } catch (error) {
      showMessage("error", error instanceof Error ? error.message : "Failed to save user.");
    }
  }

  async function handleSaveChanges() {
    setIsSaving(true);
    setActionMessage(null);

    try {
      if (tab !== "user" && tab !== "license") {
        const issues = validateSettingsForTab(tab, settings);
        if (issues.length > 0) {
          showMessage("error", issues[0]);
          return;
        }
      }

      let nextRuntimeSettings = runtimeSettings;
      if (tab === "device") {
        const desiredPort = parsePortValue(settings.pushEndpointPort);
        const desiredWebPort = parsePortValue(settings.webUiPort);
        if (desiredPort === null || desiredWebPort === null) {
          showMessage("error", "Both API and Web UI ports must be valid numbers.");
          return;
        }

        if (!runtimeSettings || desiredPort !== runtimeSettings.port || desiredWebPort !== runtimeSettings.frontendPort) {
          nextRuntimeSettings = await updateRuntimePort(desiredPort, desiredWebPort);
          setRuntimeSettings(nextRuntimeSettings);
        }
      }

      const persistedSettings = tab === "user" ? settings : await saveSettingsToApi(settings);
      const mergedSettings =
        tab === "device" && nextRuntimeSettings
          ? { ...persistedSettings, pushEndpointPort: String(nextRuntimeSettings.port) }
          : persistedSettings;

      if (tab !== "user") {
        setSettings(mergedSettings);
        setSavedSettings(mergedSettings);
        applyBrandingSettings(extractBrandingSettings(mergedSettings));
      }

      const now = new Date();
      setLastSavedAt(now.toISOString());

      if (tab === "user") {
        const normalized = await persistUsers(users);
        setUsers(normalized);
        showMessage("success", "User access settings saved.");
      } else if (tab === "attendance") {
        const synced = await trySyncAttendancePolicy(settings);
        if (synced) {
          showMessage("success", "Attendance policy saved and synced to API.");
        } else {
          showMessage("warning", "Settings were saved, but attendance policy sync endpoint was not reachable.");
        }
      } else if (tab === "device" && nextRuntimeSettings?.restartRequired) {
        showMessage("warning", nextRuntimeSettings.message || "Device settings saved. Restart application to apply the new API port.");
      } else if (tab === "device" && nextRuntimeSettings?.message) {
        showMessage("success", nextRuntimeSettings.message);
      } else {
        showMessage("success", `${currentTab.label} saved successfully.`);
      }
    } catch (error) {
      showMessage("error", error instanceof Error ? error.message : "Failed to save settings.");
    } finally {
      setIsSaving(false);
    }
  }

  async function handleResetSection() {
    if (tab === "user") {
      if (showUserEditor) {
        resetUserForm();
        showMessage("success", "User form has been reset.");
      } else {
        try {
          const normalized = normalizeLoadedUsers(await loadUsersFromApi());
          setUsers(normalized);
          showMessage("success", "User list refreshed.");
        } catch (error) {
          showMessage("error", error instanceof Error ? error.message : "Failed to refresh user list.");
        }
      }
      return;
    }

    const keys = tabFields[tab];
    setSettings((prev) => {
      const next = { ...prev } as Record<string, string | boolean>;
      for (const key of keys) {
        next[key] = key === "pushEndpointPort" && runtimeSettings
          ? String(runtimeSettings.port)
          : defaultSettings[key];
      }

      return next as SettingsState;
    });
    showMessage("success", `${currentTab.label} has been reset to defaults.`);
  }

  function handleSecurityAudit() {
    const report = generateSecurityAudit(settings);
    setAuditReport(report);
    const failCount = report.items.filter((item) => item.status === "fail").length;
    const warnCount = report.items.filter((item) => item.status === "warn").length;
    if (failCount > 0) {
      showMessage("error", `Security audit completed with ${failCount} critical issue(s) and ${warnCount} warning(s).`);
    } else if (warnCount > 0) {
      showMessage("warning", `Security audit completed with ${warnCount} warning(s).`);
    } else {
      showMessage("success", "Security audit passed with no issues.");
    }
  }

  const messageClass =
    actionKind === "success"
      ? "border-emerald-200 bg-emerald-50 text-emerald-800"
      : actionKind === "warning"
        ? "border-amber-200 bg-amber-50 text-amber-800"
        : "border-rose-200 bg-rose-50 text-rose-800";

  return (
    <div className="space-y-4">
      <div className="tab-header">
        <h1 className="text-2xl font-semibold">Settings</h1>
        <p className="text-sm text-[--muted-foreground]">Cleaner section-based settings with live branding preview, real device port sync, and working save/reset actions.</p>
      </div>

      <div className="grid gap-4 xl:grid-cols-[280px_1fr]">
        <aside className="surface p-3 shadow-sm">
          <div className="space-y-2">
            {tabs.filter((item) => (item.id !== "user" && item.id !== "backup") || isAdmin).map((item) => {
              const Icon = item.icon;
              return (
                <button
                  key={item.id}
                  type="button"
                  onClick={() => setTab(item.id)}
                  className={`w-full rounded-lg border px-3 py-2.5 text-left transition ${
                    tab === item.id
                      ? "border-[#c7daf2] bg-[#f5f9ff] text-[#1e4c78]"
                      : "border-transparent hover:border-[#d9e8f7] hover:bg-[#f8fbff]"
                  }`}
                >
                  <div className="flex items-center gap-2">
                    <Icon className="h-4 w-4 shrink-0" />
                    <span className="text-sm font-medium">{item.label}</span>
                  </div>
                  <p className="mt-1 text-xs text-[--muted-foreground]">{item.description}</p>
                </button>
              );
            })}
          </div>
        </aside>

        <section className="surface p-4 shadow-sm">
          <div className="mb-4 border-b border-[--border] pb-3">
            <h2 className="text-base font-semibold">{currentTab.label}</h2>
            <p className="mt-1 text-sm text-[--muted-foreground]">{currentTab.description}</p>
            {lastSavedAt ? <p className="mt-1 text-xs text-[--muted-foreground]">Last saved: {formatCalendarDateTime(lastSavedAt, calendarType)}</p> : null}
          </div>

          <div className="mb-4 grid gap-3 xl:grid-cols-3">
            <SettingsMetric
              label="Section Fields"
              value={tab === "user" ? `${users.length}` : `${sectionFieldCount}`}
              detail={tab === "user" ? "Users currently available in this workspace." : "Configurable fields in the active settings section."}
            />
            <SettingsMetric
              label="Pending Changes"
              value={tab === "user" ? (showUserEditor ? "Editing" : "Synced") : `${dirtyFieldCount}`}
              detail={tab === "user" ? "User access editor state for the current session." : "Fields changed since the last successful save."}
              tone={tab === "user" ? (showUserEditor ? "warn" : "good") : dirtyFieldCount > 0 ? "warn" : "good"}
            />
            <SettingsMetric
              label={tab === "device" ? "Runtime Status" : "Validation"}
              value={tab === "device" && runtimeSettings ? `${runtimeSettings.serverIp}:${runtimeSettings.port}` : validationIssues.length > 0 ? `${validationIssues.length} issue(s)` : "Ready"}
              detail={tab === "device" && runtimeSettings
                ? `Web UI Port ${runtimeSettings.frontendPort}${runtimeSettings.restartRequired ? " • restart required" : ""}`
                : validationIssues.length > 0
                  ? validationIssues[0]
                  : "Current section is ready to save."}
              tone={tab === "device" ? "default" : validationIssues.length > 0 ? "warn" : "good"}
            />
          </div>

          {tab === "general" ? (
            <div className="space-y-4">
              <div className="rounded-2xl border border-sky-200 bg-[linear-gradient(135deg,#f8fdff,#eef7ff)] p-4">
                <p className="text-xs font-semibold uppercase tracking-[0.18em] text-[#5a7b98]">Workspace Preview</p>
                <p className="mt-2 text-lg font-semibold text-[#0d334d]">{settings.organizationName || "E-Attendance"}</p>
                <p className="mt-1 text-sm text-[#5a7b98]">{settings.defaultBranch || "Main Branch"} • {settings.timezone} • {settings.language}</p>
              </div>
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Organization Name" hint="Company name shown in dashboard, reports, and exports.">
                  <input
                    value={settings.organizationName}
                    onChange={(event) => setField("organizationName", event.target.value)}
                    className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                  />
                </Field>
                <Field label="Timezone" hint="Primary timezone used for attendance and report timestamps.">
                  <select
                    value={settings.timezone}
                    onChange={(event) => setField("timezone", event.target.value)}
                    className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                  >
                    {!ALL_TIMEZONES.includes(settings.timezone) ? (
                      <option value={settings.timezone}>{settings.timezone}</option>
                    ) : null}
                    {ALL_TIMEZONES.map((tz) => (
                      <option key={tz} value={tz}>{tz}</option>
                    ))}
                  </select>
                </Field>
                <Field label="Default Branch" hint="Applied as fallback branch for new employee records.">
                  <input
                    value={settings.defaultBranch}
                    onChange={(event) => setField("defaultBranch", event.target.value)}
                    className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                  />
                </Field>
                <Field label="Language" hint="Multi-language support isn't built yet — English only for now.">
                  <select
                    value="English"
                    disabled
                    className="w-full rounded-lg border border-[--input] bg-[--muted] px-3 py-2 text-sm text-[--muted-foreground] disabled:cursor-not-allowed"
                  >
                    <option>English</option>
                  </select>
                </Field>
              </div>
            </div>
          ) : null}

          {tab === "attendance" ? (
            <div className="space-y-4">
              <div className="rounded-lg border border-[--border] bg-[--muted] px-3 py-3 text-sm text-[--muted-foreground]">
                Late/Grace/Half-day/Overtime thresholds are controlled from Shift and Timetable configuration.
              </div>
              <div className="grid gap-3 md:grid-cols-2">
                <ToggleField
                  label="Count Holiday Work as Overtime"
                  hint="When enabled, worked minutes on holiday are treated as OT."
                  checked={settings.holidayOt}
                  onChange={(value) => setField("holidayOt", value)}
                />
                <ToggleField
                  label="Count Weekly Off Work as Overtime"
                  hint="When enabled, worked minutes on weekly off are treated as OT."
                  checked={settings.weeklyOffOt}
                  onChange={(value) => setField("weeklyOffOt", value)}
                />
              </div>
            </div>
          ) : null}

          {tab === "device" ? (
            <div className="space-y-4">
              <div className="grid gap-3 md:grid-cols-3">
                <div className="rounded-2xl border border-sky-200 bg-[linear-gradient(135deg,#f8fdff,#eef7ff)] p-4">
                  <p className="text-xs font-semibold uppercase tracking-[0.18em] text-[#5a7b98]">Server IP</p>
                  <p className="mt-2 text-lg font-semibold text-[#0d334d]">{runtimeSettings?.serverIp ?? "-"}</p>
                  <p className="mt-1 text-xs text-[#5a7b98]">Use this in the device cloud server setting.</p>
                </div>
                <div className="rounded-2xl border border-emerald-200 bg-emerald-50/80 p-4">
                  <p className="text-xs font-semibold uppercase tracking-[0.18em] text-emerald-700">API Port</p>
                  <p className="mt-2 text-lg font-semibold text-emerald-900">{runtimeSettings?.port ?? settings.pushEndpointPort}</p>
                  <p className="mt-1 text-xs text-emerald-700">Changing this updates the actual device push/API port.</p>
                </div>
                <div className="rounded-2xl border border-amber-200 bg-amber-50/80 p-4">
                  <p className="text-xs font-semibold uppercase tracking-[0.18em] text-amber-700">Web UI Port</p>
                  <p className="mt-2 text-lg font-semibold text-amber-900">{runtimeSettings?.frontendPort ?? "-"}</p>
                  <p className="mt-1 text-xs text-amber-700">Keep this different from the API port.</p>
                </div>
              </div>
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Push Endpoint Port" hint="Port where biometric devices push attendance logs.">
                  <input
                    value={settings.pushEndpointPort}
                    onChange={(event) => setField("pushEndpointPort", event.target.value)}
                    className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                  />
                </Field>
                <Field label="Web UI Port" hint="Port for the web dashboard. Keep different from API port.">
                  <input
                    value={settings.webUiPort ?? String(runtimeSettings?.frontendPort ?? defaultSettings.webUiPort)}
                    onChange={(event) => setField("webUiPort", event.target.value)}
                    className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                  />
                </Field>
              </div>
              <div className="rounded-2xl border border-sky-200 bg-[linear-gradient(135deg,#f8fdff,#fefeff)] px-4 py-3 text-sm text-[#1e4c78]">
                Notice: Save this section after changing the API port. If the backend asks for restart, restart the application once so devices start using the new port.
              </div>
            </div>
          ) : null}

          {tab === "branding" ? (
            <div className="space-y-4">
              <div className="rounded-2xl border border-sky-200 bg-[linear-gradient(135deg,#f8fdff,#eef7ff)] p-4">
                <p className="text-xs font-semibold uppercase tracking-[0.18em] text-[#5a7b98]">Live Branding Preview</p>
                <div className="mt-3 flex items-center gap-3">
                  <div className="grid h-14 w-14 shrink-0 place-items-center">
                    <img
                      src={settings.logoUrl || "/apan_logo.png"}
                      alt={settings.systemTitle || settings.organizationName}
                      className="h-full w-full object-contain filter drop-shadow-sm"
                    />
                  </div>
                  <div className="min-w-0">
                    <p className="truncate text-lg font-bold text-[#0d334d] font-nepali">
                      {settings.organizationName || "अपन हाजिरी"}
                    </p>
                    <p className="truncate text-xs text-[#5a7b98]">
                      {settings.systemTitle || "Apan Hajiri"}
                    </p>
                  </div>
                  <div className="ml-auto flex items-center gap-2 rounded-full border border-white/70 bg-white px-3 py-1.5 shadow-sm">
                    <span className="h-3 w-3 rounded-full" style={{ backgroundColor: settings.primaryColor }} />
                    <span className="text-xs font-semibold text-[#1e4c78]">{settings.primaryColor}</span>
                  </div>
                </div>
              </div>
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="System Title" hint="Title shown in top navigation and browser tab areas.">
                  <input
                    value={settings.systemTitle}
                    onChange={(event) => setField("systemTitle", event.target.value)}
                    className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                  />
                </Field>
                <Field label="Primary Color" hint="Brand accent color used across buttons and highlights.">
                  <div className="flex items-center gap-3 rounded-lg border border-[--input] bg-[--card] px-3 py-2">
                    <input
                      type="color"
                      value={settings.primaryColor}
                      onChange={(event) => setField("primaryColor", event.target.value)}
                      className="h-10 w-12 cursor-pointer rounded border-0 bg-transparent p-0"
                    />
                    <input
                      value={settings.primaryColor}
                      onChange={(event) => setField("primaryColor", event.target.value)}
                      className="w-full bg-transparent text-sm outline-none"
                    />
                  </div>
                </Field>
              </div>
            </div>
          ) : null}

          {tab === "license" ? (
            <LicensePanel />
          ) : null}

          {tab === "backup" ? (
            <div className="space-y-4">
              {backupError ? <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">{backupError}</div> : null}
              {backupMessage ? <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800">{backupMessage}</div> : null}

              <div className="rounded-lg border border-[--border] bg-[--card] p-3">
                <h3 className="mb-3 text-sm font-semibold">Backup Schedule</h3>
                {backupLoading || !backupSchedule ? (
                  <p className="text-sm text-[--muted-foreground]">Loading...</p>
                ) : (
                  <>
                    <div className="grid gap-3 md:grid-cols-2">
                      <label className="grid gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-[11px] font-semibold text-[#1f4f79]">Backup Location (server folder)</span>
                        <input
                          value={backupSchedule.backupLocation}
                          onChange={(event) => setBackupSchedule((prev) => (prev ? { ...prev, backupLocation: event.target.value } : prev))}
                          placeholder="D:\E_AttendanceBackup"
                          className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm"
                        />
                      </label>
                      <label className="grid gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-[11px] font-semibold text-[#1f4f79]">Frequency</span>
                        <select
                          value={backupSchedule.frequency}
                          onChange={(event) => setBackupSchedule((prev) => (prev ? { ...prev, frequency: event.target.value as BackupSchedule["frequency"] } : prev))}
                          className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm"
                        >
                          <option value="daily">Daily</option>
                          <option value="weekly">Weekly</option>
                          <option value="monthly">Monthly</option>
                        </select>
                      </label>
                      <label className="grid gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-[11px] font-semibold text-[#1f4f79]">Run Time (server local time)</span>
                        <input
                          type="time"
                          value={backupSchedule.runTime}
                          onChange={(event) => setBackupSchedule((prev) => (prev ? { ...prev, runTime: event.target.value } : prev))}
                          className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm"
                        />
                      </label>
                      <label className="grid gap-1 text-xs text-[--muted-foreground]">
                        <span className="text-[11px] font-semibold text-[#1f4f79]">Keep Last N Backups</span>
                        <input
                          type="number"
                          min={1}
                          max={365}
                          value={backupSchedule.retentionCount}
                          onChange={(event) => setBackupSchedule((prev) => (prev ? { ...prev, retentionCount: Number.parseInt(event.target.value || "1", 10) } : prev))}
                          className="rounded-lg border border-[--input] bg-white px-3 py-2 text-sm"
                        />
                      </label>
                      <label className="flex items-center gap-2 rounded-lg border border-[--input] px-3 py-2 text-sm text-[#1f4f79]">
                        <input
                          type="checkbox"
                          checked={backupSchedule.enabled}
                          onChange={(event) => setBackupSchedule((prev) => (prev ? { ...prev, enabled: event.target.checked } : prev))}
                        />
                        Enable automatic scheduled backups
                      </label>
                    </div>
                    <p className="mt-2 text-xs text-[--muted-foreground]">
                      Last run: {backupSchedule.lastRunUtc ? formatCalendarDateTime(backupSchedule.lastRunUtc, calendarType) : "Never"}
                      {" | "}
                      Next run: {backupSchedule.enabled && backupSchedule.nextRunUtc ? formatCalendarDateTime(backupSchedule.nextRunUtc, calendarType) : "Not scheduled"}
                    </p>
                    <div className="mt-3 flex flex-wrap items-center gap-2">
                      <button
                        type="button"
                        onClick={() => void saveBackupSchedule()}
                        disabled={backupSaving}
                        className="inline-flex items-center gap-2 rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground] disabled:opacity-70"
                      >
                        <Check className="h-4 w-4" />
                        {backupSaving ? "Saving..." : "Save Settings"}
                      </button>
                      <button
                        type="button"
                        onClick={() => void runBackupNow()}
                        disabled={backupRunning}
                        className="inline-flex items-center gap-2 rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm font-medium disabled:opacity-70"
                      >
                        <DatabaseBackup className="h-4 w-4" />
                        {backupRunning ? "Running..." : "Run Backup Now"}
                      </button>
                    </div>
                    <p className="mt-2 text-xs text-[--muted-foreground]">
                      This backs up the full database (settings, employees, devices, attendance, license). Restoring a backup is a destructive operation and is only available from the E-Attendance tray app on the server, not from this web page.
                    </p>
                  </>
                )}
              </div>

              <div className="rounded-lg border border-[--border] bg-[--card] p-3">
                <div className="mb-3 flex items-center justify-between gap-2">
                  <h3 className="text-sm font-semibold">Backup History</h3>
                  <button type="button" onClick={() => void loadBackupData()} className="rounded-lg border border-[--border] bg-[--card] px-3 py-1.5 text-xs font-medium">
                    Refresh
                  </button>
                </div>
                <div className="overflow-x-auto rounded-lg border border-[--border]">
                  <table className="min-w-full text-left text-sm">
                    <thead className="bg-[#f7fbff] text-xs uppercase text-[--muted-foreground]">
                      <tr>
                        <th className="px-3 py-2">File</th>
                        <th className="px-3 py-2">Size</th>
                        <th className="px-3 py-2">Status</th>
                        <th className="px-3 py-2">Trigger</th>
                        <th className="px-3 py-2">Started</th>
                        <th className="px-3 py-2">Finished</th>
                        <th className="px-3 py-2">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {backupHistory.map((entry) => (
                        <tr key={entry.id} className="border-t border-[--border]">
                          <td className="px-3 py-2 font-medium">{entry.fileName || "-"}</td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground]">{formatBackupSize(entry.fileSizeBytes)}</td>
                          <td className="px-3 py-2">
                            <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${
                              entry.status === "completed" ? "bg-emerald-100 text-emerald-800" : entry.status === "failed" ? "bg-red-100 text-red-800" : "bg-amber-100 text-amber-800"
                            }`}>
                              {entry.status}
                            </span>
                            {entry.status === "failed" && entry.errorMessage ? (
                              <div className="mt-1 text-[11px] text-red-700">{entry.errorMessage}</div>
                            ) : null}
                          </td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground]">{entry.trigger}</td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground]">{formatCalendarDateTime(entry.startedAtUtc, calendarType)}</td>
                          <td className="px-3 py-2 text-xs text-[--muted-foreground]">{entry.finishedAtUtc ? formatCalendarDateTime(entry.finishedAtUtc, calendarType) : "-"}</td>
                          <td className="px-3 py-2">
                            {entry.status === "completed" ? (
                              <button
                                type="button"
                                onClick={() => void downloadBackup(entry.id, entry.fileName)}
                                className="inline-flex items-center gap-1 rounded-lg border border-[#c8dbf0] bg-white px-2 py-1 text-xs text-[#1f5c8f]"
                              >
                                <Download className="h-3.5 w-3.5" />
                                Download
                              </button>
                            ) : null}
                          </td>
                        </tr>
                      ))}
                      {backupHistory.length === 0 ? (
                        <tr>
                          <td colSpan={7} className="px-3 py-4 text-center text-sm text-[--muted-foreground]">
                            No backups yet.
                          </td>
                        </tr>
                      ) : null}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          ) : null}

          {tab === "user" ? (
            <div className="space-y-4">
              <div className="rounded-lg border border-[--border] bg-[--card] p-3">
                <div className="mb-3 flex items-center justify-between gap-2">
                  <div>
                    <h3 className="text-sm font-semibold">User List</h3>
                    <p className="text-xs text-[--muted-foreground]">
                      Default admin is fixed as <span className="font-medium">admin</span>.
                    </p>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className="text-xs text-[--muted-foreground]">{users.length} user(s)</span>
                    {isAdmin ? (
                      <button
                        type="button"
                        onClick={openCreateUserForm}
                        className="inline-flex items-center gap-1.5 rounded-lg bg-[--primary] px-3 py-1.5 text-xs font-semibold text-[--primary-foreground]"
                      >
                        <UserPlus className="h-3.5 w-3.5" />
                        Create User
                      </button>
                    ) : null}
                  </div>
                </div>
                {!isAdmin ? (
                  <div className="rounded-lg border border-[#cfe0f2] bg-[#f5f9ff] px-3 py-2.5 text-sm text-[#1e4c78]">
                    Only Admin can create, edit, or delete software users.
                  </div>
                ) : null}
                {users.length === 0 ? (
                  <p className="text-sm text-[--muted-foreground]">No user created yet.</p>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="min-w-[980px] w-full text-left text-sm">
                      <thead>
                        <tr className="border-b border-[--border] text-xs uppercase tracking-wide text-[--muted-foreground]">
                          <th className="px-3 py-2 font-medium">Username</th>
                          <th className="px-3 py-2 font-medium">Full Name</th>
                          <th className="px-3 py-2 font-medium">Role</th>
                          <th className="px-3 py-2 font-medium">Status</th>
                          <th className="px-3 py-2 font-medium">Features</th>
                          <th className="px-3 py-2 font-medium">Updated</th>
                          <th className="px-3 py-2 text-right font-medium">Actions</th>
                        </tr>
                      </thead>
                      <tbody>
                        {users.map((row) => {
                          const activeFeatures = featureDefinitions
                            .filter((item) => row.featureAccess[item.key])
                            .map((item) => item.label);
                          const rowIsAdmin = isDefaultAdminUsername(row.username);
                          return (
                            <tr key={row.id} className="border-b border-[--border] align-top last:border-b-0">
                              <td className="px-3 py-2">
                                <div className="inline-flex items-center gap-2">
                                  <span className="font-medium text-[--foreground]">{row.username}</span>
                                  {rowIsAdmin ? (
                                    <span className="rounded-full bg-[#e6f0fe] px-2 py-0.5 text-[10px] font-semibold uppercase text-[#1e4c78]">
                                      Default
                                    </span>
                                  ) : null}
                                </div>
                              </td>
                              <td className="px-3 py-2 text-[--foreground]">{row.fullName}</td>
                              <td className="px-3 py-2">{row.role}</td>
                              <td className="px-3 py-2">
                                <span
                                  className={`rounded-full px-2 py-0.5 text-xs font-medium ${
                                    row.isActive
                                      ? "bg-emerald-100 text-emerald-800"
                                      : "bg-rose-100 text-rose-700"
                                  }`}
                                >
                                  {row.isActive ? "Active" : "Inactive"}
                                </span>
                              </td>
                              <td className="px-3 py-2">
                                <div className="max-w-[340px]">
                                  <p className="text-xs font-medium text-[--foreground]">{activeFeatures.length} selected</p>
                                  <p className="mt-1 text-xs text-[--muted-foreground]">
                                    {activeFeatures.length > 0 ? activeFeatures.join(", ") : "No features assigned"}
                                  </p>
                                </div>
                              </td>
                              <td className="px-3 py-2 text-xs text-[--muted-foreground]">
                                {formatCalendarDateTime(row.updatedAt, calendarType)}
                              </td>
                              <td className="px-3 py-2 text-right">
                                {isAdmin ? (
                                  <div className="inline-flex items-center gap-1.5">
                                    <button
                                      type="button"
                                      onClick={() => handleEditUser(row.id)}
                                      className="inline-flex items-center gap-1 rounded-md border border-[--border] bg-[--card] px-2 py-1.5 text-xs font-medium"
                                    >
                                      <Pencil className="h-3.5 w-3.5" />
                                      Edit
                                    </button>
                                    {rowIsAdmin ? (
                                      <span
                                        title="Default admin can't be deleted. Username, role, and access are locked, but the password can still be changed via Edit."
                                        className="inline-flex items-center rounded-md border border-[#cfe0f2] bg-[#f5f9ff] px-2 py-1.5 text-xs font-medium text-[#1e4c78]"
                                      >
                                        <Lock className="h-3.5 w-3.5" />
                                      </span>
                                    ) : (
                                      <button
                                        type="button"
                                        onClick={() => handleDeleteUser(row.id)}
                                        className="inline-flex items-center gap-1 rounded-md border border-rose-200 bg-rose-50 px-2 py-1.5 text-xs font-medium text-rose-700"
                                      >
                                        <Trash2 className="h-3.5 w-3.5" />
                                        Delete
                                      </button>
                                    )}
                                  </div>
                                ) : (
                                  <span className="text-xs text-[--muted-foreground]">—</span>
                                )}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>

              {showUserEditor ? (
                <>
                  <div className="rounded-lg border border-[#cfe0f2] bg-[#f5f9ff] px-3 py-2.5 text-sm text-[#1e4c78]">
                    {isEditingDefaultAdmin
                      ? "Default admin username is locked. Only password can be changed."
                      : "Configure user details and select feature rights before saving."}
                  </div>

                  <div className="rounded-lg border border-[--border] bg-[--card] p-4">
                    <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                      <div>
                        <h3 className="text-sm font-semibold">{editingUserId ? "Edit User" : "Create User"}</h3>
                        <p className="text-xs text-[--muted-foreground]">
                          {isEditingDefaultAdmin
                            ? "You are editing default admin. Only password can be changed."
                            : "Create user and assign the feature access needed for that user."}
                        </p>
                      </div>
                      <span className="rounded-full bg-[#e6f0fe] px-2.5 py-1 text-xs font-medium text-[#1e4c78]">
                        Features Selected: {selectedFeatureCount}/{featureDefinitions.length}
                      </span>
                    </div>

                    <div className="grid gap-4 lg:grid-cols-2">
                      <Field label="Username" hint={isEditingDefaultAdmin ? "Default admin username is fixed." : "Unique login/user key for this account."}>
                        <input
                          value={userForm.username}
                          onChange={(event) => setUserField("username", event.target.value)}
                          disabled={isEditingDefaultAdmin}
                          className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-[--muted]"
                        />
                      </Field>
                      <Field label="Full Name" hint={isEditingDefaultAdmin ? "Default admin name is fixed." : "Display name shown in the system."}>
                        <input
                          value={userForm.fullName}
                          onChange={(event) => setUserField("fullName", event.target.value)}
                          disabled={isEditingDefaultAdmin}
                          className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-[--muted]"
                        />
                      </Field>
                      <Field
                        label="Password"
                        hint={isEditingDefaultAdmin ? "Enter new password for default admin." : editingUserId ? "Leave blank to keep current password." : "Set initial password for this user."}
                      >
                        <input
                          type="password"
                          value={userForm.password}
                          onChange={(event) => setUserField("password", event.target.value)}
                          className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm"
                        />
                      </Field>
                      <Field label="Role" hint={isEditingDefaultAdmin ? "Default admin role is fixed." : "Role label used for account grouping."}>
                        <select
                          value={userForm.role}
                          onChange={(event) => setUserField("role", event.target.value as UserRole)}
                          disabled={isEditingDefaultAdmin}
                          className="w-full rounded-lg border border-[--input] bg-[--card] px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-[--muted]"
                        >
                          <option value="Admin">Admin</option>
                          <option value="Manager">Manager</option>
                          <option value="Operator">Operator</option>
                        </select>
                      </Field>
                    </div>

                    <div className="mt-3 grid gap-3 lg:grid-cols-2">
                      {isEditingDefaultAdmin ? (
                        <div className="rounded-lg border border-[--border] bg-[--muted] px-3 py-3">
                          <p className="text-sm font-medium">Active User</p>
                          <p className="mt-1 text-xs text-[--muted-foreground]">Default admin always stays active.</p>
                        </div>
                      ) : (
                        <ToggleField
                          label="Active User"
                          hint="Inactive users are kept but should not get operational access."
                          checked={userForm.isActive}
                          onChange={(value) => setUserField("isActive", value)}
                        />
                      )}
                      <div className="rounded-lg border border-[--border] bg-[--muted] px-3 py-3">
                        <p className="text-sm font-medium">Feature Access Selection</p>
                        <p className="mt-1 text-xs text-[--muted-foreground]">
                          {isEditingDefaultAdmin
                            ? "Default admin always has full feature access."
                            : "Quick-select all access or clear everything."}
                        </p>
                        <div className="mt-2 flex flex-wrap gap-2">
                          <button
                            type="button"
                            onClick={() => setAllUserFeatures(true)}
                            disabled={isEditingDefaultAdmin}
                            className="rounded-lg border border-[--border] bg-[--card] px-3 py-1.5 text-xs font-medium disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            Select All
                          </button>
                          <button
                            type="button"
                            onClick={() => setAllUserFeatures(false)}
                            disabled={isEditingDefaultAdmin}
                            className="rounded-lg border border-[--border] bg-[--card] px-3 py-1.5 text-xs font-medium disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            Clear All
                          </button>
                        </div>
                      </div>
                    </div>

                    <div className="mt-3 grid gap-2 lg:grid-cols-2">
                      {featureDefinitions.map((feature) => (
                        <div key={feature.key} className="flex items-start justify-between gap-3 rounded-lg border border-[--border] bg-[--muted] px-3 py-2.5">
                          <div className="min-w-0">
                            <p className="text-sm font-medium">{feature.label}</p>
                            <p className="mt-1 text-xs text-[--muted-foreground]">{feature.detail}</p>
                          </div>
                          <input
                            type="checkbox"
                            checked={userForm.featureAccess[feature.key]}
                            disabled={isEditingDefaultAdmin}
                            onChange={(event) => setUserFeature(feature.key, event.target.checked)}
                            className="mt-1 h-4 w-4 rounded border-[--input] disabled:cursor-not-allowed"
                          />
                        </div>
                      ))}
                    </div>

                    <div className="mt-4 flex flex-wrap items-center gap-2">
                      <button
                        type="button"
                        onClick={handleSaveUser}
                        className="inline-flex items-center gap-2 rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground]"
                      >
                        <UserPlus className="h-4 w-4" />
                        {isEditingDefaultAdmin ? "Update Admin Password" : editingUserId ? "Update User" : "Create User"}
                      </button>
                      <button
                        type="button"
                        onClick={resetUserForm}
                        className="inline-flex items-center gap-2 rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm font-medium"
                      >
                        Cancel
                      </button>
                    </div>
                  </div>
                </>
              ) : null}
            </div>
          ) : null}

          {validationIssues.length > 0 && tab !== "user" && tab !== "license" && tab !== "backup" ? (
            <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
              {validationIssues[0]}
            </div>
          ) : null}

          {actionMessage ? <div className={`mt-4 rounded-lg border px-3 py-2 text-sm ${messageClass}`}>{actionMessage}</div> : null}

          {tab !== "license" && tab !== "backup" ? (
            <div className="mt-5 flex flex-wrap items-center gap-2 border-t border-[--border] pt-4">
              <button
                type="button"
                onClick={handleSaveChanges}
                disabled={isSaving || (tab !== "user" && validationIssues.length > 0)}
                className="inline-flex items-center gap-2 rounded-lg bg-[--primary] px-4 py-2 text-sm font-medium text-[--primary-foreground] disabled:cursor-not-allowed disabled:opacity-70"
              >
                <Check className="h-4 w-4" />
                {isSaving ? "Saving..." : tab === "user" ? "Save User Access" : "Save Changes"}
              </button>
              <button
                type="button"
                onClick={handleResetSection}
                className="inline-flex items-center gap-2 rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm font-medium"
              >
                <SlidersHorizontal className="h-4 w-4" />
                {tab === "user" ? (showUserEditor ? "Reset User Form" : "Refresh User List") : "Reset Section"}
              </button>
              <button
                type="button"
                onClick={handleSecurityAudit}
                className="inline-flex items-center gap-2 rounded-lg border border-[--border] bg-[--card] px-4 py-2 text-sm font-medium"
              >
                <Shield className="h-4 w-4" />
                Security Audit
              </button>
            </div>
          ) : null}

          {auditReport && tab !== "license" && tab !== "backup" ? (
            <div className="mt-4 rounded-lg border border-[--border] bg-[--card] p-3">
              <div className="mb-2">
                <h3 className="text-sm font-semibold">Security Audit Report</h3>
                <p className="text-xs text-[--muted-foreground]">Generated at: {formatCalendarDateTime(auditReport.generatedAt, calendarType)}</p>
              </div>
              <div className="space-y-2">
                {auditReport.items.map((item) => (
                  <div key={`${item.title}-${item.detail}`} className="rounded-md border border-[--border] px-3 py-2">
                    <div className="flex items-center gap-2">
                      <span
                        className={`rounded-full px-2 py-0.5 text-xs font-medium ${
                          item.status === "pass"
                            ? "bg-emerald-100 text-emerald-800"
                            : item.status === "warn"
                              ? "bg-amber-100 text-amber-800"
                              : "bg-rose-100 text-rose-800"
                        }`}
                      >
                        {item.status.toUpperCase()}
                      </span>
                      <span className="text-sm font-medium">{item.title}</span>
                    </div>
                    <p className="mt-1 text-xs text-[--muted-foreground]">{item.detail}</p>
                  </div>
                ))}
              </div>
            </div>
          ) : null}
        </section>
      </div>
    </div>
  );
}
