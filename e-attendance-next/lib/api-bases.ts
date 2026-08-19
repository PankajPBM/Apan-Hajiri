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

  // 2. Dynamically discovered API port or cached API port from localStorage / window
  let discoveredPort = typeof window !== "undefined" ? (window as any).DYNAMICAL_API_PORT : "";
  if (!discoveredPort && typeof window !== "undefined") {
    try {
      discoveredPort = window.localStorage.getItem("pb_attendance_api_port") || "";
    } catch {
      // Ignore storage errors.
    }
  }

  // If running on default Web UI port 8001 and no discovered port yet, default candidate API port is 8081
  if (!discoveredPort && (currentPort === "8001" || currentPort === "")) {
    discoveredPort = "8081";
  }

  if (discoveredPort) {
    pushUnique(`http://${currentHost}:${discoveredPort}`);
  }

  // 3. Current origin — same-origin fallback for single-port or proxy deployments
  if (options.includeCurrentOrigin ?? true) {
    if (currentOrigin) {
      pushUnique(currentOrigin);
    }
  }

  return bases;
}

/**
 * One-time background discovery of the API port from the current origin.
 * Sets window.DYNAMICAL_API_PORT and stores in localStorage for subsequent resolveApiBases calls.
 */
if (typeof window !== "undefined") {
  (async () => {
    try {
      const resp = await fetch(`${window.location.origin}/api-config.json`, { cache: "no-store" });
      if (resp.ok) {
        const contentType = resp.headers.get("content-type") ?? "";
        if (contentType.includes("application/json")) {
          const config = await resp.json();
          if (config && config.apiPort) {
            (window as any).DYNAMICAL_API_PORT = config.apiPort;
            try {
              window.localStorage.setItem("pb_attendance_api_port", String(config.apiPort));
            } catch {
              // Ignore storage errors.
            }
            console.log(`[API] Discovered backend port: ${config.apiPort}`);
          }
        }
      }
    } catch {
      // Ignore discovery errors, fallback to static guessing.
    }
  })();
}
