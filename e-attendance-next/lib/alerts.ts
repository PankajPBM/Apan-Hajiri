"use client";

import { resolveApiBases } from "@/lib/api-bases";

export type AppLogItem = {
  timestampUtc: string;
  level: string;
  source: string;
  message: string;
  detail?: string | null;
};

type PendingLeaveRow = {
  id: number;
  personnelId?: number | null;
  fromDate: string;
  toDate: string;
  status?: string;
};

type DeviceRow = {
  id: string;
  serialNumber: string;
  name: string;
  isOnline?: boolean;
  state?: string;
  lastSeenUtc?: string;
};

export type AlertLevel = "info" | "success" | "warning";
export type AlertCategory = "attendance" | "leave" | "device" | "system";

export type AlertItem = {
  id: string;
  title: string;
  detail: string;
  level: AlertLevel;
  category: AlertCategory;
  timestampUtc: string;
  href: string;
};

let preferredAlertsBase = "";

async function requestAlertJson<T>(path: string, init?: RequestInit): Promise<T> {
  let lastError = "Failed to fetch alerts.";

  for (const base of resolveApiBases({ preferredBase: preferredAlertsBase, includeCurrentOrigin: true })) {
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
        preferredAlertsBase = base;
        return (await response.json()) as T;
      }

      const text = await response.text();
      lastError = text || `Request failed (${response.status})`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : lastError;
    }
  }

  throw new Error(lastError);
}

function normalizeLevel(level: string): AlertLevel {
  const value = level.trim().toLowerCase();
  if (value === "warn" || value === "warning" || value === "error") {
    return "warning";
  }
  if (value === "success") {
    return "success";
  }
  return "info";
}

function normalizeCategory(source: string): AlertCategory {
  const value = source.trim().toLowerCase();
  if (value === "attendance") return "attendance";
  if (value === "device" || value === "deviceevent" || value === "deviceaction") return "device";
  if (value === "server") return "system";
  return "system";
}

function buildLogAlert(log: AppLogItem, index: number): AlertItem | null {
  const source = (log.source || "").trim().toLowerCase();
  const message = (log.message || "").trim();
  const lowerMessage = message.toLowerCase();
  const detail = (log.detail || "").trim();

  const isManualAttendance =
    source === "attendance" &&
    (lowerMessage.includes("manual attendance added") || lowerMessage.includes("manual clock"));
  const isDeviceIssue =
    (source === "device" || source === "deviceevent") &&
    (lowerMessage.includes("disconnected") || lowerMessage.includes("offline"));
  const isSystemState =
    source === "server" &&
    (lowerMessage.includes("started") || lowerMessage.includes("stopped") || lowerMessage.includes("restarted"));
  const isActionableAttendance =
    source === "attendance" && (isManualAttendance || lowerMessage.includes("attendance updated") || lowerMessage.includes("attendance deleted"));

  if (!(isManualAttendance || isDeviceIssue || isSystemState || isActionableAttendance)) {
    return null;
  }

  const title = isManualAttendance
    ? "Manual attendance activity"
    : isDeviceIssue
      ? "Device status alert"
      : isSystemState
        ? "Server status changed"
        : "Attendance update";

  return {
    id: `log-${index}-${log.timestampUtc}-${message}`,
    title,
    detail: detail ? `${message} ${detail}` : message,
    level: normalizeLevel(log.level || ""),
    category: normalizeCategory(log.source || ""),
    timestampUtc: log.timestampUtc,
    href: source === "attendance" ? "/attendance" : source === "device" || source === "deviceevent" ? "/devices" : "/logs",
  };
}

export async function loadAlertFeed(): Promise<AlertItem[]> {
  const [logs, pendingLeaves, devices] = await Promise.all([
    requestAlertJson<AppLogItem[]>("/api/logs?take=250").catch(() => []),
    requestAlertJson<PendingLeaveRow[]>("/api/leave?status=pending").catch(() => []),
    requestAlertJson<DeviceRow[]>("/api/devices").catch(() => []),
  ]);

  const alerts: AlertItem[] = [];

  if (Array.isArray(logs)) {
    for (const [index, log] of logs.entries()) {
      const mapped = buildLogAlert(log, index);
      if (mapped) {
        alerts.push(mapped);
      }
    }
  }

  if (Array.isArray(pendingLeaves) && pendingLeaves.length > 0) {
    const latestPending = pendingLeaves
      .map((row) => new Date(`${row.fromDate}T00:00:00`))
      .filter((date) => !Number.isNaN(date.getTime()))
      .sort((a, b) => b.getTime() - a.getTime())[0];

    alerts.push({
      id: `pending-leave-${pendingLeaves.length}`,
      title: "Pending leave requests",
      detail: `${pendingLeaves.length} leave request(s) are waiting for action.`,
      level: "warning",
      category: "leave",
      timestampUtc: latestPending?.toISOString() ?? new Date().toISOString(),
      href: "/leaves",
    });
  }

  if (Array.isArray(devices)) {
    const offlineDevices = devices.filter((device) => {
      if (typeof device.isOnline === "boolean") {
        return !device.isOnline;
      }
      return String(device.state || "").toLowerCase() !== "online";
    });

    if (offlineDevices.length > 0) {
      alerts.push({
        id: `offline-devices-${offlineDevices.length}`,
        title: "Offline devices detected",
        detail: `${offlineDevices.length} device(s) are currently offline: ${offlineDevices
          .slice(0, 3)
          .map((device) => device.name || device.serialNumber)
          .join(", ")}${offlineDevices.length > 3 ? "..." : ""}`,
        level: "warning",
        category: "device",
        timestampUtc:
          offlineDevices
            .map((device) => device.lastSeenUtc)
            .find((value): value is string => typeof value === "string" && value.trim().length > 0) ??
          new Date().toISOString(),
        href: "/devices",
      });
    }
  }

  return alerts
    .sort((a, b) => new Date(b.timestampUtc).getTime() - new Date(a.timestampUtc).getTime())
    .slice(0, 24);
}
