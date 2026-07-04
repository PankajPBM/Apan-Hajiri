"use client";

type ResolveApiBasesOptions = {
  preferredBase?: string;
  includeCurrentOrigin?: boolean;
  includeLocalhost?: boolean;
};

const LOOPBACK_HOSTS = new Set(["localhost", "127.0.0.1", "::1", "[::1]"]);

function trimBase(value: string): string {
  return value.replace(/\/+$/, "");
}

function normalizeHttpOrigin(value: string): string {
  const trimmed = trimBase(value);
  if (!trimmed || trimmed === "null") {
    return "";
  }

  try {
    const url = new URL(trimmed);
    if ((url.protocol !== "http:" && url.protocol !== "https:") || !url.host) {
      return "";
    }
    return trimBase(url.toString());
  } catch {
    return "";
  }
}

function rehostLoopbackBase(base: string, currentHost: string): string[] {
  const trimmed = trimBase(base);
  if (!trimmed) {
    return [];
  }

  try {
    const url = new URL(trimmed);
    const candidates = [trimBase(url.toString())];
    if (currentHost && currentHost !== url.hostname && LOOPBACK_HOSTS.has(url.hostname)) {
      url.hostname = currentHost;
      candidates.unshift(trimBase(url.toString()));
    }
    return candidates;
  } catch {
    return [trimmed];
  }
}

function preferredPortsForCurrentOrigin(currentPort: string): string[] {
  // strictly only use same-port as a possible fallback if discovery hasn't happened yet
  return currentPort ? [currentPort] : [];
}

export function resolveApiBases(options: ResolveApiBasesOptions = {}): string[] {
  const currentHost = typeof window !== "undefined" ? window.location.hostname || "localhost" : "localhost";
  const currentOrigin = typeof window !== "undefined" ? normalizeHttpOrigin(window.location.origin) : "";
  const currentPort = typeof window !== "undefined" ? window.location.port : "";
  const envBase = process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/+$/, "") ?? "";
  const bases: string[] = [];
  const includeCurrentOrigin = options.includeCurrentOrigin ?? true;

  const pushUnique = (value: string) => {
    const trimmed = trimBase(value);
    if (trimmed && !bases.includes(trimmed)) {
      bases.push(trimmed);
    }
  };

  // 1. Explicitly preferred base (last known-working) — highest priority
  for (const value of [options.preferredBase ?? "", envBase]) {
    for (const candidate of rehostLoopbackBase(value, currentHost)) {
      pushUnique(candidate);
    }
  }

  // 2. Current origin — SAME-ORIGIN, no CORS needed, works from any machine.
  if (includeCurrentOrigin && currentOrigin) {
    pushUnique(currentOrigin);
  }

  // 3. Dynamically discovered API port (from /api-config.json)
  if (typeof window !== "undefined" && (window as any).DYNAMICAL_API_PORT) {
    pushUnique(`http://${currentHost}:${(window as any).DYNAMICAL_API_PORT}`);
  }

  // 4. Strict fallbacks only (no guessing)
  for (const port of preferredPortsForCurrentOrigin(currentPort)) {
    pushUnique(`http://${currentHost}:${port}`);
  }

  return bases;
}

/**
 * One-time background discovery of the API port from the current origin.
 * Sets window.DYNAMICAL_API_PORT for subsequent resolveApiBases calls.
 */
if (typeof window !== "undefined") {
  (async () => {
    try {
      const resp = await fetch(`${window.location.origin}/api-config.json`, { cache: "no-store" });
      if (resp.ok) {
        const config = await resp.json();
        if (config && config.apiPort) {
          (window as any).DYNAMICAL_API_PORT = config.apiPort;
          console.log(`[API] Discovered backend port: ${config.apiPort}`);
        }
      }
    } catch {
      // Ignore discovery errors, fallback to static guessing.
    }
  })();
}
