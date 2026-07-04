"use client";

import { useEffect, useState } from "react";

export type AppBrandingSettings = {
  organizationName: string;
  systemTitle: string;
  logoUrl: string;
  primaryColor: string;
  supportEmail: string;
};

export const APP_BRANDING_EVENT = "e-attendance:branding-updated";
const BRANDING_STORAGE_KEY = "e_attendance_branding";

const defaultBranding: AppBrandingSettings = {
  organizationName: "E-Attendance",
  systemTitle: "E-Attendance",
  logoUrl: "/logo_pb.ico",
  // Previous navy-blue brand tone (original sidebar gradient base); the sidebar renders a
  // gradient from this down to a darker shade, reproducing the prior gradient look.
  primaryColor: "#134f8a",
  supportEmail: "pbtechzone77@gmail.com",
};

const namedColors: Record<string, string> = {
  blue: "#2563eb",
  cyan: "#0891b2",
  emerald: "#059669",
  green: "#16a34a",
  orange: "#ea580c",
  red: "#dc2626",
  rose: "#e11d48",
  slate: "#334155",
  sky: "#0284c7",
  violet: "#7c3aed",
};

function normalizeLegacySupportEmail(value: string | undefined): string | undefined {
  const trimmed = value?.trim();
  if (!trimmed) {
    return trimmed;
  }
  const lower = trimmed.toLowerCase();
  if (lower === "support@pbattendance.com" || lower === "pbtechzon77@gmail.com") {
    return "pbtechzone77@gmail.com";
  }
  return trimmed;
}

function normalizeLegacyLogoUrl(value: string | undefined): string | undefined {
  const trimmed = value?.trim();
  if (!trimmed) {
    return trimmed;
  }
  return trimmed === "/logo.svg" ? "/logo_pb.ico" : trimmed;
}

function normalizeColorValue(value: string | undefined): string {
  const trimmed = value?.trim() ?? "";
  if (!trimmed) return defaultBranding.primaryColor;
  const lower = trimmed.toLowerCase();
  if (namedColors[lower]) return namedColors[lower];
  if (/^#[0-9a-f]{6}$/i.test(trimmed)) return trimmed;
  // expand 3-digit hex
  if (/^#[0-9a-f]{3}$/i.test(trimmed)) {
    const [, r, g, b] = trimmed;
    return `#${r}${r}${g}${g}${b}${b}`;
  }
  return defaultBranding.primaryColor;
}

export function pickForegroundColor(hex: string): string {
  const normalized = hex.replace("#", "");
  const red = Number.parseInt(normalized.slice(0, 2), 16);
  const green = Number.parseInt(normalized.slice(2, 4), 16);
  const blue = Number.parseInt(normalized.slice(4, 6), 16);
  const brightness = (red * 299 + green * 587 + blue * 114) / 1000;
  return brightness >= 150 ? "#06203a" : "#ffffff";
}

function syncBrandingDocument(next: AppBrandingSettings) {
  const root = document.documentElement;
  root.style.setProperty("--primary", next.primaryColor);
  root.style.setProperty("--ring", next.primaryColor);
  root.style.setProperty("--primary-foreground", pickForegroundColor(next.primaryColor));
  document.title = next.systemTitle;
}

function normalizeBranding(raw: Partial<AppBrandingSettings> | null | undefined): AppBrandingSettings {
  return {
    organizationName: raw?.organizationName?.trim() || defaultBranding.organizationName,
    systemTitle: raw?.systemTitle?.trim() || raw?.organizationName?.trim() || defaultBranding.systemTitle,
    logoUrl: normalizeLegacyLogoUrl(raw?.logoUrl) || defaultBranding.logoUrl,
    primaryColor: normalizeColorValue(raw?.primaryColor),
    supportEmail: normalizeLegacySupportEmail(raw?.supportEmail) || defaultBranding.supportEmail,
  };
}

export function extractBrandingSettings(raw: Partial<AppBrandingSettings> | Record<string, unknown> | null | undefined): AppBrandingSettings {
  if (!raw || typeof raw !== "object") {
    return defaultBranding;
  }

  return normalizeBranding({
    organizationName: typeof raw.organizationName === "string" ? raw.organizationName : undefined,
    systemTitle: typeof raw.systemTitle === "string" ? raw.systemTitle : undefined,
    logoUrl: typeof raw.logoUrl === "string" ? raw.logoUrl : undefined,
    primaryColor: typeof raw.primaryColor === "string" ? raw.primaryColor : undefined,
    supportEmail: typeof raw.supportEmail === "string" ? raw.supportEmail : undefined,
  });
}

export function readBrandingSettings(): AppBrandingSettings {
  if (typeof window === "undefined") {
    return defaultBranding;
  }

  try {
    const raw = window.localStorage.getItem(BRANDING_STORAGE_KEY);
    if (!raw) {
      return defaultBranding;
    }
    return extractBrandingSettings(JSON.parse(raw) as Record<string, unknown>);
  } catch {
    return defaultBranding;
  }
}

export function applyBrandingSettings(raw: Partial<AppBrandingSettings> | null | undefined) {
  if (typeof window === "undefined") {
    return;
  }

  const next = normalizeBranding(raw);
  syncBrandingDocument(next);
  window.localStorage.setItem(BRANDING_STORAGE_KEY, JSON.stringify(next));
  window.dispatchEvent(new CustomEvent<AppBrandingSettings>(APP_BRANDING_EVENT, { detail: next }));
}

export function useBrandingSettings() {
  const [branding, setBranding] = useState<AppBrandingSettings>(() => {
    const b = readBrandingSettings();
    if (typeof window !== "undefined") syncBrandingDocument(b);
    return b;
  });

  useEffect(() => {
    syncBrandingDocument(branding);
  }, [branding]);

  useEffect(() => {
    const syncBranding = () => setBranding(readBrandingSettings());
    const onStorage = (event: StorageEvent) => {
      if (!event.key || event.key === BRANDING_STORAGE_KEY) {
        syncBranding();
      }
    };

    window.addEventListener(APP_BRANDING_EVENT, syncBranding);
    window.addEventListener("storage", onStorage);
    return () => {
      window.removeEventListener(APP_BRANDING_EVENT, syncBranding);
      window.removeEventListener("storage", onStorage);
    };
  }, []);

  return branding;
}
