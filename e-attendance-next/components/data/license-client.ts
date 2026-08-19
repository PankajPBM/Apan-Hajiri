"use client";

import { resolveApiBases } from "@/lib/api-bases";

export type LicenseTrialInfo = {
  isActive: boolean;
  totalDays: number;
  startedUtc: string | null;
  expiryUtc: string | null;
  daysRemaining: number;
};

export type LicenseStatusResponse = {
  isValid: boolean;
  isAccessGranted: boolean;
  activationMode: string;
  activationLabel: string;
  activationMessage: string;
  code: string;
  message: string;
  validationMessage: string;
  checkedAtUtc: string | null;
  machineHash: string;
  licenseMachineHash: string | null;
  licenseActivatedAtUtc: string | null;
  expiryUtc: string | null;
  licenseExpiryUtc: string | null;
  licenseDaysRemaining: number;
  trialExpiryUtc: string | null;
  customerName: string | null;
  licenseType: string | null;
  contactNumber: string | null;
  email: string | null;
  maxDevices: number | null;
  effectiveMaxDevices: number;
  currentDeviceCount: number;
  licenseTypeWithDeviceLimit: string | null;
  trial: LicenseTrialInfo;
  files: {
    licenseData: string | null;
    key: string | null;
    publicKey: string | null;
  };
  paths: {
    licenseDirectory: string | null;
    trialState: string | null;
  };
};

type LicenseStatusEnvelope = {
  success?: boolean;
  status?: unknown;
};

let preferredApiBase = "";

function apiBases(): string[] {
  return resolveApiBases({ preferredBase: preferredApiBase, includeCurrentOrigin: true });
}

async function requestWithFallback(path: string, init?: RequestInit): Promise<Response> {
  let lastMessage = "API service is offline or starting up.";
  for (const base of apiBases()) {
    try {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 8000);
      const response = await fetch(`${base}${path}`, {
        ...init,
        cache: "no-store",
        signal: controller.signal,
      });
      clearTimeout(timeout);

      const contentType = response.headers.get("content-type") || "";
      if (response.ok && !contentType.includes("text/html")) {
        preferredApiBase = base;
        return response;
      }

      const errorText = await response.text();
      if (errorText && !errorText.trim().startsWith("<")) {
        lastMessage = errorText;
      } else {
        lastMessage = `API service is offline or starting up (${response.status}).`;
      }
    } catch (error) {
      lastMessage = error instanceof Error ? error.message : "Unknown request error.";
    }
  }

  throw new Error(lastMessage);
}

function asText(value: unknown): string | null {
  if (typeof value !== "string") {
    return null;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function asBoolean(value: unknown, fallback = false): boolean {
  return typeof value === "boolean" ? value : fallback;
}

function asNumber(value: unknown, fallback = 0): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function normalizeLicenseStatus(raw: unknown): LicenseStatusResponse {
  const source = raw && typeof raw === "object" ? (raw as Record<string, unknown>) : {};
  const trialRaw = source.trial && typeof source.trial === "object" ? (source.trial as Record<string, unknown>) : {};
  const filesRaw = source.files && typeof source.files === "object" ? (source.files as Record<string, unknown>) : {};
  const pathsRaw = source.paths && typeof source.paths === "object" ? (source.paths as Record<string, unknown>) : {};

  return {
    isValid: asBoolean(source.isValid),
    isAccessGranted: asBoolean(source.isAccessGranted, asBoolean(source.isValid)),
    activationMode: asText(source.activationMode) ?? "unknown",
    activationLabel: asText(source.activationLabel) ?? "Unknown",
    activationMessage: asText(source.activationMessage) ?? (asText(source.message) ?? "No status available."),
    code: asText(source.code) ?? "unknown",
    message: asText(source.message) ?? "",
    validationMessage: asText(source.validationMessage) ?? "",
    checkedAtUtc: asText(source.checkedAtUtc),
    machineHash: asText(source.machineHash) ?? "",
    licenseMachineHash: asText(source.licenseMachineHash),
    licenseActivatedAtUtc: asText(source.licenseActivatedAtUtc),
    expiryUtc: asText(source.expiryUtc),
    licenseExpiryUtc: asText(source.licenseExpiryUtc),
    licenseDaysRemaining: asNumber(source.licenseDaysRemaining),
    trialExpiryUtc: asText(source.trialExpiryUtc),
    customerName: asText(source.customerName),
    licenseType: asText(source.licenseType),
    contactNumber: asText(source.contactNumber),
    email: asText(source.email),
    maxDevices: typeof source.maxDevices === "number" ? source.maxDevices : null,
    effectiveMaxDevices: asNumber(source.effectiveMaxDevices, 0),
    currentDeviceCount: asNumber(source.currentDeviceCount, 0),
    licenseTypeWithDeviceLimit: asText(source.licenseTypeWithDeviceLimit),
    trial: {
      isActive: asBoolean(trialRaw.isActive),
      totalDays: asNumber(trialRaw.totalDays, 30),
      startedUtc: asText(trialRaw.startedUtc),
      expiryUtc: asText(trialRaw.expiryUtc),
      daysRemaining: asNumber(trialRaw.daysRemaining),
    },
    files: {
      licenseData: asText(filesRaw.licenseData),
      key: asText(filesRaw.key),
      publicKey: asText(filesRaw.publicKey),
    },
    paths: {
      licenseDirectory: asText(pathsRaw.licenseDirectory),
      trialState: asText(pathsRaw.trialState),
    },
  };
}

async function readJsonResponse(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text || !text.trim()) {
    return {};
  }
  const trimmed = text.trim();
  if (trimmed.startsWith("<")) {
    throw new Error("API server is offline or returned invalid response.");
  }
  try {
    return JSON.parse(trimmed);
  } catch {
    throw new Error("Invalid JSON response received from API server.");
  }
}

export async function fetchLicenseStatus(): Promise<LicenseStatusResponse> {
  const response = await requestWithFallback("/api/license/status");
  const payload = await readJsonResponse(response);
  return normalizeLicenseStatus(payload);
}

export async function fetchAppVersion(): Promise<string | null> {
  try {
    const response = await requestWithFallback("/api/version");
    const payload = (await readJsonResponse(response)) as { version?: string };
    return typeof payload?.version === "string" && payload.version.trim() ? payload.version.trim() : null;
  } catch {
    return null;
  }
}

export async function reloadLicenseStatus(): Promise<LicenseStatusResponse> {
  const response = await requestWithFallback("/api/license/reload", { method: "POST" });
  const payload = await readJsonResponse(response);
  return normalizeLicenseStatus(payload);
}

export async function activateLicenseFiles(licenseKey: File, licenseDat?: File | null): Promise<LicenseStatusResponse> {
  const form = new FormData();
  form.append("licenseKey", licenseKey, licenseKey.name || "PB-Activated.key");
  if (licenseDat) {
    form.append("licenseData", licenseDat, licenseDat.name || "license.dat");
  }
  const response = await requestWithFallback("/api/license/activate", {
    method: "POST",
    body: form,
  });
  const payload = (await readJsonResponse(response)) as LicenseStatusEnvelope;
  return normalizeLicenseStatus(payload.status ?? payload);
}

export async function deactivateLicenseFiles(): Promise<LicenseStatusResponse> {
  const response = await requestWithFallback("/api/license/deactivate", {
    method: "POST",
  });
  const payload = (await readJsonResponse(response)) as LicenseStatusEnvelope;
  return normalizeLicenseStatus(payload.status ?? payload);
}
