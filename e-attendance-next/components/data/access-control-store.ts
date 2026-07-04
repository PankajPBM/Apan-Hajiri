"use client";

import { resolveApiBases } from "@/lib/api-bases";

const STORAGE_KEY = "pb_attendance_access_control_v1";
export const ACCESS_CONTROL_UPDATED_EVENT = "pb_attendance_access_control_updated";
const API_PATH = "/api/app-config/access-control";
let preferredApiBase = "";

export const WEEKDAY_LABELS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"] as const;

export type AccessControlDayRule = {
  dayOfWeek: number;
  startTime: string;
  endTime: string;
  isActive: boolean;
};

export type AccessControlTimePeriod = {
  id: string;
  area: string;
  department: string;
  deviceSerials: string[];
  periodNo: string;
  name: string;
  remark: string;
  days: AccessControlDayRule[];
  createdAt: string;
  updatedAt: string;
};

export type AccessControlVerificationMode =
  | "any"
  | "card"
  | "fingerprint"
  | "face"
  | "personnel"
  | "group"
  | `${number}`;

export type AccessControlGroup = {
  id: string;
  area: string;
  department: string;
  deviceSerials: string[];
  groupNo: string;
  name: string;
  timePeriodIds: [string, string, string];
  verificationMode: AccessControlVerificationMode;
  includeHoliday: boolean;
  remark: string;
  createdAt: string;
  updatedAt: string;
};

export type AccessControlCombination = {
  id: string;
  area: string;
  department: string;
  deviceSerials: string[];
  combinationNo: string;
  name: string;
  groupIds: [string, string, string, string, string];
  remark: string;
  createdAt: string;
  updatedAt: string;
};

export type AccessControlPrivilege = {
  id: string;
  area: string;
  department: string;
  deviceSerials: string[];
  employeeIds: string[];
  employeeNames: string[];
  groupId: string;
  verificationMode: AccessControlVerificationMode;
  timePeriodIds: [string, string, string];
  remark: string;
  createdAt: string;
  updatedAt: string;
};

export type AccessControlSnapshot = {
  timePeriods: AccessControlTimePeriod[];
  groups: AccessControlGroup[];
  combinations: AccessControlCombination[];
  privileges: AccessControlPrivilege[];
};

function hasWindow() {
  return typeof window !== "undefined" && typeof window.localStorage !== "undefined";
}

async function accessControlRequest<T>(init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch access control configuration.";
  for (const base of resolveApiBases({ preferredBase: preferredApiBase, includeCurrentOrigin: true })) {
    try {
      const response = await fetch(`${base}${API_PATH}`, {
        ...init,
        headers: {
          "Content-Type": "application/json",
          ...(init?.headers ?? {}),
        },
        cache: "no-store",
      });
      if (response.ok) {
        preferredApiBase = base;
        return (await response.json()) as T;
      }
      lastError = (await response.text()) || `Request failed (${response.status})`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : lastError;
    }
  }
  throw new Error(lastError);
}

function nowIso() {
  return new Date().toISOString();
}

function normalizeTime(value: unknown, fallback: string): string {
  return typeof value === "string" && /^\d{2}:\d{2}(:\d{2})?$/.test(value.trim()) ? value.trim() : fallback;
}

function normalizeStringTuple(value: unknown, length: number): string[] {
  const list = Array.isArray(value) ? value : [];
  const filled = list.filter((item): item is string => typeof item === "string" && item.trim() !== "");
  return Array.from({ length }, (_, index) => filled[index]?.trim() ?? "");
}

function normalizeStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((x): x is string => typeof x === "string") : [];
}

function normalizeVerificationMode(value: unknown): AccessControlVerificationMode {
  if (typeof value === "number" && Number.isFinite(value)) {
    return String(value) as AccessControlVerificationMode;
  }
  if (typeof value === "string" && value.trim()) {
    return value.trim() as AccessControlVerificationMode;
  }
  return "0";
}

function normalizeDay(row: unknown, dayOfWeek: number): AccessControlDayRule {
  const source = row && typeof row === "object" ? (row as Record<string, unknown>) : {};
  return {
    dayOfWeek,
    startTime: normalizeTime(source.startTime, "00:00:00"),
    endTime: normalizeTime(source.endTime, "23:59:00"),
    isActive: source.isActive !== false,
  };
}

function normalizePeriods(rows: unknown): AccessControlTimePeriod[] {
  const list = Array.isArray(rows) ? rows : [];
  return list
    .map((item, index): AccessControlTimePeriod | null => {
      if (!item || typeof item !== "object") return null;
      const source = item as Record<string, unknown>;
      const name = typeof source.name === "string" ? source.name.trim() : "";
      return {
        id: typeof source.id === "string" && source.id.trim() ? source.id : `period-${index + 1}`,
        area: typeof source.area === "string" ? source.area.trim() : "",
        department: typeof source.department === "string" ? source.department.trim() : "",
        deviceSerials: normalizeStringArray(source.deviceSerials),
        periodNo: typeof source.periodNo === "string" ? source.periodNo.trim() : String(index + 1),
        name: name || `Time Period ${index + 1}`,
        remark: typeof source.remark === "string" ? source.remark.trim() : "",
        days: Array.from({ length: 7 }, (_, day) => normalizeDay((source.days as unknown[] | undefined)?.[day], day)),
        createdAt: typeof source.createdAt === "string" ? source.createdAt : nowIso(),
        updatedAt: typeof source.updatedAt === "string" ? source.updatedAt : nowIso(),
      };
    })
    .filter((item): item is AccessControlTimePeriod => item !== null);
}

function normalizeGroups(rows: unknown): AccessControlGroup[] {
  const list = Array.isArray(rows) ? rows : [];
  return list
    .map((item, index): AccessControlGroup | null => {
      if (!item || typeof item !== "object") return null;
      const source = item as Record<string, unknown>;
      return {
        id: typeof source.id === "string" && source.id.trim() ? source.id : `group-${index + 1}`,
        area: typeof source.area === "string" ? source.area.trim() : "",
        department: typeof source.department === "string" ? source.department.trim() : "",
        deviceSerials: normalizeStringArray(source.deviceSerials),
        groupNo: typeof source.groupNo === "string" ? source.groupNo.trim() : String(index + 1),
        name: typeof source.name === "string" && source.name.trim() ? source.name.trim() : `Group ${index + 1}`,
        timePeriodIds: normalizeStringTuple(source.timePeriodIds, 3) as [string, string, string],
        verificationMode: normalizeVerificationMode(source.verificationMode),
        includeHoliday: source.includeHoliday === true,
        remark: typeof source.remark === "string" ? source.remark.trim() : "",
        createdAt: typeof source.createdAt === "string" ? source.createdAt : nowIso(),
        updatedAt: typeof source.updatedAt === "string" ? source.updatedAt : nowIso(),
      };
    })
    .filter((item): item is AccessControlGroup => item !== null);
}

function normalizeCombinations(rows: unknown): AccessControlCombination[] {
  const list = Array.isArray(rows) ? rows : [];
  return list
    .map((item, index): AccessControlCombination | null => {
      if (!item || typeof item !== "object") return null;
      const source = item as Record<string, unknown>;
      const groupIds = normalizeStringTuple(source.groupIds, 5) as [
        string,
        string,
        string,
        string,
        string,
      ];
      return {
        id: typeof source.id === "string" && source.id.trim() ? source.id : `combination-${index + 1}`,
        area: typeof source.area === "string" ? source.area.trim() : "",
        department: typeof source.department === "string" ? source.department.trim() : "",
        deviceSerials: normalizeStringArray(source.deviceSerials),
        combinationNo: typeof source.combinationNo === "string" ? source.combinationNo.trim() : String(index + 1),
        name: typeof source.name === "string" && source.name.trim() ? source.name.trim() : `Combination ${index + 1}`,
        groupIds,
        remark: typeof source.remark === "string" ? source.remark.trim() : "",
        createdAt: typeof source.createdAt === "string" ? source.createdAt : nowIso(),
        updatedAt: typeof source.updatedAt === "string" ? source.updatedAt : nowIso(),
      };
    })
    .filter((item): item is AccessControlCombination => item !== null);
}

function normalizePrivileges(rows: unknown): AccessControlPrivilege[] {
  const list = Array.isArray(rows) ? rows : [];
  return list
    .map((item, index): AccessControlPrivilege | null => {
      if (!item || typeof item !== "object") return null;
      const source = item as Record<string, unknown>;
      return {
        id: typeof source.id === "string" && source.id.trim() ? source.id : `privilege-${index + 1}`,
        area: typeof source.area === "string" ? source.area.trim() : "",
        department: typeof source.department === "string" ? source.department.trim() : "",
        deviceSerials: normalizeStringArray(source.deviceSerials),
        employeeIds: Array.isArray(source.employeeIds) ? source.employeeIds.filter((x) => typeof x === "string") : [],
        employeeNames: Array.isArray(source.employeeNames) ? source.employeeNames.filter((x) => typeof x === "string") : [],
        groupId: typeof source.groupId === "string" ? source.groupId.trim() : "",
        verificationMode: normalizeVerificationMode(source.verificationMode),
        timePeriodIds: normalizeStringTuple(source.timePeriodIds, 3) as [string, string, string],
        remark: typeof source.remark === "string" ? source.remark.trim() : "",
        createdAt: typeof source.createdAt === "string" ? source.createdAt : nowIso(),
        updatedAt: typeof source.updatedAt === "string" ? source.updatedAt : nowIso(),
      };
    })
    .filter((item): item is AccessControlPrivilege => item !== null);
}

export function defaultAccessControlSnapshot(): AccessControlSnapshot {
  const now = nowIso();
  return {
    timePeriods: [
      {
        id: "period-default-1",
        area: "General",
        department: "All",
        deviceSerials: [],
        periodNo: "1",
        name: "Time Period 1",
        remark: "Default",
        days: Array.from({ length: 7 }, (_, dayOfWeek) => ({
          dayOfWeek,
          startTime: "00:00:00",
          endTime: "23:59:00",
          isActive: true,
        })),
        createdAt: now,
        updatedAt: now,
      },
    ],
    groups: [
      {
        id: "group-default-1",
        area: "General",
        department: "All",
        deviceSerials: [],
        groupNo: "1",
        name: "Access Group 1",
        timePeriodIds: ["period-default-1", "", ""],
        verificationMode: "0",
        includeHoliday: false,
        remark: "Default",
        createdAt: now,
        updatedAt: now,
      },
    ],
    combinations: [
      {
        id: "comb-default-1",
        area: "General",
        department: "All",
        deviceSerials: [],
        combinationNo: "1",
        name: "Combination 1",
        groupIds: ["group-default-1", "", "", "", ""],
        remark: "Default",
        createdAt: now,
        updatedAt: now,
      }
    ],
    privileges: [],
  };
}

export async function ensureAccessControlDefaults(
  areas: Array<{ name: string; code: string }>,
  devices: Array<{ serialNumber: string; area?: string }>,
  currentSnapshot: AccessControlSnapshot
): Promise<AccessControlSnapshot> {
  let changed = false;
  const next: AccessControlSnapshot = {
    timePeriods: [...currentSnapshot.timePeriods],
    groups: [...currentSnapshot.groups],
    combinations: [...currentSnapshot.combinations],
    privileges: [...currentSnapshot.privileges],
  };

  const createId = (p: string) => `${p}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
  const createDefaultDays = () => Array.from({ length: 7 }, (_, i) => ({
    dayOfWeek: i,
    startTime: "00:00:00",
    endTime: "23:59:00",
    isActive: true
  }));
  const sameSet = (a: string[], b: string[]) => a.length === b.length && a.every((x) => b.includes(x));

  // Access control is scoped by AREA only (department is not used here). Each area gets a
  // default Time Period 1 (00:00-23:59 all days), Access Group 1 (verify=Auto, TP1=1, TP2/3=none),
  // and Combination 1 (Group1=Access Group 1). All are synced to every device in that area.
  for (const area of areas) {
    const areaSerials = devices
      .filter((d) => (d.area ?? "") === area.name)
      .map((d) => d.serialNumber)
      .filter((s) => !!s);

    // 1. Time Period 1
    let tp1 = next.timePeriods.find((x) => x.area === area.name && x.periodNo === "1");
    if (!tp1) {
      tp1 = {
        id: createId("period"),
        area: area.name,
        department: "All",
        deviceSerials: areaSerials,
        periodNo: "1",
        name: "Time Period 1",
        remark: "Default",
        days: createDefaultDays(),
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      };
      next.timePeriods.push(tp1);
      changed = true;
    } else if (!sameSet(tp1.deviceSerials, areaSerials)) {
      tp1 = { ...tp1, deviceSerials: areaSerials, updatedAt: new Date().toISOString() };
      next.timePeriods = next.timePeriods.map((x) => (x.id === tp1!.id ? tp1! : x));
      changed = true;
    }

    // 2. Access Group 1
    let grp1 = next.groups.find((x) => x.area === area.name && x.groupNo === "1");
    if (!grp1) {
      grp1 = {
        id: createId("group"),
        area: area.name,
        department: "All",
        deviceSerials: areaSerials,
        groupNo: "1",
        name: "Access Group 1",
        verificationMode: "0",
        timePeriodIds: [tp1.id, "", ""],
        includeHoliday: false,
        remark: "Default",
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      };
      next.groups.push(grp1);
      changed = true;
    } else if (!sameSet(grp1.deviceSerials, areaSerials)) {
      grp1 = { ...grp1, deviceSerials: areaSerials, updatedAt: new Date().toISOString() };
      next.groups = next.groups.map((x) => (x.id === grp1!.id ? grp1! : x));
      changed = true;
    }

    // 3. Combination 1
    const comb1 = next.combinations.find((x) => x.area === area.name && x.combinationNo === "1");
    if (!comb1) {
      next.combinations.push({
        id: createId("combination"),
        area: area.name,
        department: "All",
        deviceSerials: areaSerials,
        combinationNo: "1",
        name: "Combination 1",
        groupIds: [grp1.id, "", "", "", ""],
        remark: "Default",
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      });
      changed = true;
    } else if (!sameSet(comb1.deviceSerials, areaSerials)) {
      const updated = { ...comb1, deviceSerials: areaSerials, updatedAt: new Date().toISOString() };
      next.combinations = next.combinations.map((x) => (x.id === comb1.id ? updated : x));
      changed = true;
    }
  }

  if (changed) {
    return await saveAccessControlToApi(next);
  }
  return currentSnapshot;
}

function withDefaultAccessControlRows(snapshot: AccessControlSnapshot): AccessControlSnapshot {
  const defaults = defaultAccessControlSnapshot();
  const timePeriods = snapshot.timePeriods.length > 0 ? [...snapshot.timePeriods] : [...defaults.timePeriods];
  
  // Use existing groups; deduplicate if multiple default-looking groups exist
  let groups = snapshot.groups.length > 0 ? [...snapshot.groups] : [];
  
  // Deduplicate: if we have multiple groups with groupNo "1" and name "Access Group 1", keep only one
  if (groups.length > 1) {
    const defaultName = defaults.groups[0].name;
    const defaultNo = defaults.groups[0].groupNo;
    const firstDefaultIdx = groups.findIndex(g => g.groupNo === defaultNo && g.name === defaultName);
    if (firstDefaultIdx !== -1) {
      groups = groups.filter((g, idx) => idx === firstDefaultIdx || !(g.groupNo === defaultNo && g.name === defaultName));
    }
  }

  // Only inject a default group if there are NO groups at all
  if (groups.length === 0) {
    const defaultPeriod = timePeriods[0] ?? defaults.timePeriods[0];
    groups.push({ ...defaults.groups[0], timePeriodIds: [defaultPeriod.id, "", ""] });
  }

  return {
    timePeriods,
    groups,
    combinations: snapshot.combinations,
    privileges: snapshot.privileges,
  };
}

export function readAccessControlSnapshot(): AccessControlSnapshot {
  if (!hasWindow()) {
    return defaultAccessControlSnapshot();
  }

  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return defaultAccessControlSnapshot();
    }
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    return withDefaultAccessControlRows({
      timePeriods: normalizePeriods(parsed.timePeriods),
      groups: normalizeGroups(parsed.groups),
      combinations: normalizeCombinations(parsed.combinations),
      privileges: normalizePrivileges(parsed.privileges),
    });
  } catch {
    return defaultAccessControlSnapshot();
  }
}

export async function loadAccessControlFromApi(): Promise<AccessControlSnapshot> {
  const payload = await accessControlRequest<Partial<AccessControlSnapshot>>();
  const snapshot = withDefaultAccessControlRows({
    timePeriods: normalizePeriods(payload.timePeriods),
    groups: normalizeGroups(payload.groups),
    combinations: normalizeCombinations(payload.combinations),
    privileges: normalizePrivileges(payload.privileges),
  });
  saveAccessControlSnapshot(snapshot);
  return snapshot;
}

export function saveAccessControlSnapshot(snapshot: AccessControlSnapshot) {
  if (!hasWindow()) return;
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
  window.dispatchEvent(new Event(ACCESS_CONTROL_UPDATED_EVENT));
}

export async function saveAccessControlToApi(snapshot: AccessControlSnapshot): Promise<AccessControlSnapshot> {
  saveAccessControlSnapshot(snapshot);
  const saved = await accessControlRequest<Partial<AccessControlSnapshot>>({
    method: "PUT",
    body: JSON.stringify(snapshot),
  });
  const normalized = withDefaultAccessControlRows({
    timePeriods: normalizePeriods(saved.timePeriods),
    groups: normalizeGroups(saved.groups),
    combinations: normalizeCombinations(saved.combinations),
    privileges: normalizePrivileges(saved.privileges),
  });
  saveAccessControlSnapshot(normalized);
  return normalized;
}

export function updateAccessControlSnapshot(partial: Partial<AccessControlSnapshot>) {
  const current = readAccessControlSnapshot();
  saveAccessControlSnapshot({
    timePeriods: partial.timePeriods ?? current.timePeriods,
    groups: partial.groups ?? current.groups,
    combinations: partial.combinations ?? current.combinations,
    privileges: partial.privileges ?? current.privileges,
  });
}

export function labelForVerificationMode(mode: AccessControlVerificationMode): string {
  const deviceModeLabels: Record<string, string> = {
    "-1": "Inherit from group mode",
    "0": "Auto identify",
    "1": "Fingerprint only",
    "2": "User ID only (PIN)",
    "3": "Password only",
    "4": "Card only",
    "5": "Fingerprint OR password",
    "6": "Fingerprint OR card",
    "7": "Card OR password",
    "8": "User ID + fingerprint",
    "9": "Fingerprint + password",
    "10": "Card + fingerprint",
    "11": "Card + password",
    "12": "Fingerprint + password + card",
    "13": "User ID + fingerprint + password",
    "14": "(User ID + fingerprint) OR (card + fingerprint)",
    "15": "Face only",
    "16": "Face + fingerprint",
    "17": "Face + password",
    "18": "Face + card",
    "19": "Face + fingerprint + card",
    "20": "Face + fingerprint + password",
    "21": "Finger vein only",
    "22": "Finger vein + password",
    "23": "Finger vein + card",
    "24": "Finger vein + password + card",
    "25": "Palm print only",
    "26": "Palm print + card",
    "27": "Palm print + face",
    "28": "Palm print + fingerprint",
    "29": "Palm print + fingerprint + face",
  };
  if (deviceModeLabels[mode]) {
    return deviceModeLabels[mode];
  }
  switch (mode) {
    case "card":
      return "Card";
    case "fingerprint":
      return "Fingerprint";
    case "face":
      return "Face";
    case "personnel":
      return "Personnel";
    case "group":
      return "Group";
    default:
      return "Any";
  }
}
