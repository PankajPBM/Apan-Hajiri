"use client";

import { useEffect, useRef, useState, useTransition } from "react";
import type { Route } from "next";
import { useRouter } from "next/navigation";
import {
  AlertCircle,
  ChevronDown,
  Eye,
  EyeOff,
  Globe,
  KeyRound,
  Lock,
  Mail,
  MessageCircle,
  RefreshCcw,
  ShieldCheck,
  Sparkles,
  User,
} from "lucide-react";
import {
  firstAllowedRouteForSession,
  loginUser,
  notifyAuthUpdated,
  readAuthSession,
  saveAuthSession,
  toAuthSession,
} from "@/components/data/auth-store";
import { clearAuthToken, saveAuthToken } from "@/lib/auth-fetch";
import {
  activateLicenseFiles,
  fetchLicenseStatus,
  type LicenseStatusResponse,
} from "@/components/data/license-client";
import { formatCalendarDateTime, getCalendarTypeFromStorage } from "@/components/date/date-display";
import packageInfo from "@/package.json";

const DEVELOPER_NAME = "PB Tech Zone";
const DEVELOPER_PHONE = "+9779814891277";
const DEVELOPER_EMAIL = "pbtechzone77@gmail.com";
const DEVELOPER_WEBSITE = "https://prakash77.com.np/";
const WHATSAPP_URL = "https://wa.me/9779814891277";
const SOFTWARE_VERSION = process.env.NEXT_PUBLIC_APP_VERSION ?? packageInfo.version;

const QUICK_POINTS = ["Device sync", "Shift control", "Reports"] as const;

function formatUtc(value: string | null | undefined): string {
  if (!value) {
    return "-";
  }
  const calendarType = getCalendarTypeFromStorage("bs");
  return formatCalendarDateTime(value, calendarType, value);
}

export default function LoginPage() {
  const router = useRouter();
  const mountedRef = useRef(true);
  const [isNavigating, startTransition] = useTransition();
  const [username, setUsername] = useState(() => (typeof window !== "undefined" ? localStorage.getItem("login_saved_user") ?? "" : ""));
  const [password, setPassword] = useState(() => (typeof window !== "undefined" ? localStorage.getItem("login_saved_pass") ?? "" : ""));
  const [remember, setRemember] = useState(() => typeof window !== "undefined" && !!localStorage.getItem("login_saved_user"));
  const [showPassword, setShowPassword] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [aboutOpen, setAboutOpen] = useState(false);
  const [activationOpen, setActivationOpen] = useState(false);
  const [licenseStatus, setLicenseStatus] = useState<LicenseStatusResponse | null>(null);
  const [licenseNotice, setLicenseNotice] = useState<string | null>(null);
  const [licenseKeyFile, setLicenseKeyFile] = useState<File | null>(null);
  const [loadingLicense, setLoadingLicense] = useState(false);
  const [activatingLicense, setActivatingLicense] = useState(false);

  // --- Lockout state ---
  const LOCKOUT_KEY = "login_lockout";
  function getLockout() {
    try { return JSON.parse(localStorage.getItem(LOCKOUT_KEY) ?? "{}") as { fails?: number; until?: number }; } catch { return {}; }
  }
  function saveLockout(fails: number, until: number) {
    localStorage.setItem(LOCKOUT_KEY, JSON.stringify({ fails, until }));
  }
  const [lockoutUntil, setLockoutUntil] = useState<number>(() => {
    if (typeof window === "undefined") return 0;
    return getLockout().until ?? 0;
  });
  const [countdown, setCountdown] = useState(0);

  useEffect(() => {
    if (lockoutUntil <= Date.now()) { setCountdown(0); return; }
    const tick = () => {
      const remaining = Math.ceil((lockoutUntil - Date.now()) / 1000);
      if (remaining <= 0) { setCountdown(0); } else { setCountdown(remaining); }
    };
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [lockoutUntil]);

  const isLockedOut = countdown > 0;
  const licenseAccessGranted = licenseStatus?.isAccessGranted ?? false;
  const loginDisabled = isSubmitting || isNavigating || loadingLicense || !licenseAccessGranted || isLockedOut;
  const showLicenseBlock = !loadingLicense && !licenseAccessGranted;
  const licenseBlockMessage =
    licenseStatus?.activationMessage ?? licenseNotice ?? "License status unavailable.";
  const licenseMachineHash = licenseStatus?.machineHash ?? "-";
  const trialExpiryUtc = licenseStatus?.trial?.expiryUtc ?? licenseStatus?.trialExpiryUtc ?? null;
  const licenseExpiryUtc = licenseStatus?.licenseExpiryUtc ?? licenseStatus?.expiryUtc ?? null;
  const loginButtonLabel = isLockedOut
    ? `Locked (${countdown}s)`
    : isSubmitting
      ? "Signing in..."
      : licenseAccessGranted
        ? "Login"
        : "License Required";

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    if (loadingLicense || !licenseAccessGranted) {
      return;
    }

    const existing = readAuthSession();
    if (!existing) {
      return;
    }

    startTransition(() => {
      router.replace(firstAllowedRouteForSession(existing) as Route);
    });
  }, [licenseAccessGranted, loadingLicense, router, startTransition]);

  useEffect(() => {
    let mounted = true;

    async function loadStatus() {
      setLoadingLicense(true);
      try {
        const status = await fetchLicenseStatus();
        if (mounted) {
          setLicenseStatus(status);
        }
      } catch (err) {
        if (mounted) {
          setLicenseNotice(err instanceof Error ? err.message : "Unable to load license status.");
        }
      } finally {
        if (mounted) {
          setLoadingLicense(false);
        }
      }
    }

    void loadStatus();
    return () => {
      mounted = false;
    };
  }, []);

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    if (isLockedOut) return;
    if (!licenseAccessGranted) {
      setError("License is not active. Activate a license to continue.");
      return;
    }

    setIsSubmitting(true);
    try {
      const user = await loginUser(username, password);
      // Success — clear lockout
      localStorage.removeItem(LOCKOUT_KEY);
      setLockoutUntil(0);
      if (remember) {
        localStorage.setItem("login_saved_user", username.trim());
        localStorage.setItem("login_saved_pass", password);
      } else {
        localStorage.removeItem("login_saved_user");
        localStorage.removeItem("login_saved_pass");
      }
      if (user.token) {
        saveAuthToken(user.token, remember);
      } else {
        clearAuthToken();
      }
      const session = toAuthSession(user);
      saveAuthSession(session, remember);
      notifyAuthUpdated();
      startTransition(() => {
        router.replace(firstAllowedRouteForSession(session) as Route);
      });
    } catch (err) {
      if (mountedRef.current) {
        const { fails: prevFails = 0 } = getLockout();
        const fails = prevFails + 1;
        const lockSecs = fails >= 15 ? 180 : fails >= 10 ? 120 : fails >= 5 ? 60 : 0;
        const until = lockSecs > 0 ? Date.now() + lockSecs * 1000 : 0;
        saveLockout(fails, until);
        if (until > 0) setLockoutUntil(until);
        const baseMsg = err instanceof Error ? err.message : "Invalid username or password.";
        const remaining = Math.max(0, 5 - fails);
        const warningMsg = fails < 5
          ? `${baseMsg} (${fails} of 5 attempts used — ${remaining} remaining before lockout)`
          : baseMsg;
        setError(warningMsg);
      }
    } finally {
      if (mountedRef.current) {
        setIsSubmitting(false);
      }
    }
  }

  async function handleRefreshLicense() {
    setLicenseNotice(null);
    setLoadingLicense(true);
    try {
      const status = await fetchLicenseStatus();
      setLicenseStatus(status);
      setLicenseNotice("License status refreshed.");
    } catch (err) {
      setLicenseNotice(err instanceof Error ? err.message : "Failed to refresh license status.");
    } finally {
      setLoadingLicense(false);
    }
  }

  async function handleActivateLicense() {
    if (!licenseKeyFile) {
      setLicenseNotice("Please select a .key file first.");
      return;
    }

    setLicenseNotice(null);
    setActivatingLicense(true);
    try {
      const status = await activateLicenseFiles(licenseKeyFile);
      setLicenseStatus(status);
      if (status.isAccessGranted) {
        setLicenseNotice("License activated. You can now login.");
      } else {
        setLicenseNotice(status.activationMessage || "Activation completed but access is still blocked.");
      }
      setLicenseKeyFile(null);
    } catch (err) {
      setLicenseNotice(err instanceof Error ? err.message : "License activation failed.");
    } finally {
      setActivatingLicense(false);
    }
  }

  return (
    <main className="login-shell relative isolate min-h-screen overflow-hidden p-4 sm:p-6">
      <div className="pointer-events-none absolute inset-0">
        <div className="login-grid absolute inset-0 opacity-70" />
        <span className="login-orb login-orb--one" />
        <span className="login-orb login-orb--two" />
        <span className="login-orb login-orb--three" />
      </div>

      <section className="relative mx-auto flex min-h-[calc(100vh-2rem)] max-w-4xl items-center justify-center">
        <div className="w-full overflow-hidden rounded-[30px] border border-white/55 bg-white/24 shadow-[0_40px_90px_-50px_rgba(15,23,42,0.72)] backdrop-blur-2xl">
          <div className="grid lg:grid-cols-[0.88fr_1.12fr]">
            <aside className="relative overflow-hidden bg-[linear-gradient(160deg,#082f49_0%,#155e75_46%,#2563eb_100%)] px-6 py-7 text-white sm:px-8 sm:py-8">
              <span className="absolute -left-12 top-0 h-36 w-36 rounded-full bg-cyan-300/20 blur-3xl" />
              <span className="absolute bottom-0 right-0 h-36 w-36 rounded-full bg-blue-200/20 blur-3xl" />

              <div className="relative flex h-full flex-col">
                <div className="inline-flex w-fit items-center gap-2 rounded-full border border-white/20 bg-white/10 px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.22em] text-cyan-50">
                  <Sparkles className="size-3.5" />
                  E-Attendance
                </div>

                <h1 className="mt-5 text-3xl font-semibold tracking-[-0.03em] text-white sm:text-[2.25rem]">
                  Simple, Secure Login.
                </h1>
                <p className="mt-3 max-w-sm text-sm leading-6 text-cyan-50/84">
                  Fast access to devices, shifts, attendance, and reports in one clean workspace.
                </p>

                <div className="mt-6 grid gap-3">
                  {QUICK_POINTS.map((item) => (
                    <div
                      key={item}
                      className="flex items-center gap-3 rounded-2xl border border-white/16 bg-white/10 px-4 py-3 backdrop-blur-sm"
                    >
                      <span className="h-2.5 w-2.5 rounded-full bg-cyan-300 shadow-[0_0_0_6px_rgba(103,232,249,0.12)]" />
                      <span className="text-sm font-medium text-blue-50">{item}</span>
                    </div>
                  ))}
                </div>

                <div className="mt-6 rounded-[24px] border border-white/16 bg-white/10 px-4 py-4 backdrop-blur-sm">
                  <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-cyan-100/82">Version</p>
                  <p className="mt-2 text-lg font-semibold text-white">{SOFTWARE_VERSION}</p>
                  <p className="mt-1 text-sm text-cyan-50/80">{DEVELOPER_NAME}</p>
                </div>
              </div>
            </aside>

            <div className="relative bg-[linear-gradient(180deg,rgba(255,255,255,0.95),rgba(245,249,255,0.92))] px-5 py-6 sm:px-8 sm:py-8">
              <span className="pointer-events-none absolute left-8 top-6 h-24 w-24 rounded-full bg-sky-200/45 blur-3xl" />
              <span className="pointer-events-none absolute bottom-0 right-0 h-28 w-28 rounded-full bg-cyan-200/45 blur-3xl" />

              <div className="relative mx-auto flex w-full max-w-md flex-col">
                <div className="text-center">
                  <div className="inline-flex items-center gap-2 rounded-full border border-[#d8e5f2] bg-white/85 px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.22em] text-[#5f7891]">
                    <ShieldCheck className="size-3.5 text-[#2563eb]" />
                    Secure Access
                  </div>
                  <div className="mx-auto mt-4 grid h-16 w-16 place-items-center rounded-[22px] overflow-hidden shadow-[0_24px_38px_-20px_rgba(37,99,235,0.75)]">
                    <img src="/E_Att.ico" alt="E-Attendance" className="h-full w-full object-contain" />
                  </div>
                  <h2 className="mt-4 text-[1.9rem] font-semibold tracking-[-0.03em] text-[#0f172a] sm:text-[2.1rem]">
                    Welcome back
                  </h2>
                  <p className="mt-2 text-sm leading-6 text-[#60748a]">Sign in to continue.</p>
                </div>

                {showLicenseBlock ? (
                  <div className="mt-5 rounded-2xl border border-amber-200 bg-[linear-gradient(135deg,#fff7ed,#ffffff)] px-4 py-3 text-xs text-amber-900 shadow-[0_18px_30px_-24px_rgba(180,83,9,0.55)]">
                    <div className="flex items-center gap-2 text-sm font-semibold">
                      <AlertCircle className="size-4 text-amber-600" />
                      Product Not Activated
                    </div>
                    <p className="mt-1 text-xs text-amber-800">{licenseBlockMessage}</p>
                    <div className="mt-3 grid gap-2 text-[11px] text-amber-900 sm:grid-cols-2">
                      <p>
                        <span className="font-semibold">Machine Hash:</span> {licenseMachineHash}
                      </p>
                      <p>
                        <span className="font-semibold">Trial Expiry:</span> {formatUtc(trialExpiryUtc)}
                      </p>
                      <p>
                        <span className="font-semibold">License Expiry:</span> {formatUtc(licenseExpiryUtc)}
                      </p>
                    </div>
                    <p className="mt-2 text-[11px] font-semibold text-amber-900">
                      Login is disabled until a valid license is activated.
                    </p>
                  </div>
                ) : null}

                <form className="mt-7 space-y-4" onSubmit={handleSubmit}>
                  <label className="block text-sm">
                    <span className="mb-2 block text-[11px] font-semibold uppercase tracking-[0.18em] text-[#66839e]">
                      Username
                    </span>
                    <div className="group relative">
                      <User className="pointer-events-none absolute left-4 top-1/2 size-[18px] -translate-y-1/2 text-[#7b93ab] transition-colors duration-200 group-focus-within:text-[#2563eb]" />
                      <input
                        className="w-full rounded-2xl border border-[#d5e3f1] bg-white/92 py-3.5 pl-12 pr-4 text-[15px] text-[#123454] shadow-[0_18px_28px_-24px_rgba(37,99,235,0.6)] outline-none transition-all duration-300 placeholder:text-[#8ca0b4] hover:border-[#93c5fd] hover:bg-white focus:border-[#2563eb] focus:ring-4 focus:ring-[#bfdbfe]"
                        type="text"
                        placeholder="Enter username"
                        value={username}
                        onChange={(event) => setUsername(event.target.value)}
                        autoComplete="username"
                      />
                    </div>
                  </label>

                  <label className="block text-sm">
                    <span className="mb-2 block text-[11px] font-semibold uppercase tracking-[0.18em] text-[#66839e]">
                      Password
                    </span>
                    <div className="group relative">
                      <Lock className="pointer-events-none absolute left-4 top-1/2 size-[18px] -translate-y-1/2 text-[#7b93ab] transition-colors duration-200 group-focus-within:text-[#2563eb]" />
                      <input
                        className="w-full rounded-2xl border border-[#d5e3f1] bg-white/92 py-3.5 pl-12 pr-12 text-[15px] text-[#123454] shadow-[0_18px_28px_-24px_rgba(37,99,235,0.6)] outline-none transition-all duration-300 placeholder:text-[#8ca0b4] hover:border-[#93c5fd] hover:bg-white focus:border-[#2563eb] focus:ring-4 focus:ring-[#bfdbfe]"
                        type={showPassword ? "text" : "password"}
                        placeholder="Enter password"
                        value={password}
                        onChange={(event) => setPassword(event.target.value)}
                        autoComplete="current-password"
                      />
                      <button
                        type="button"
                        onClick={() => setShowPassword((value) => !value)}
                        className="absolute right-2.5 top-1/2 -translate-y-1/2 rounded-xl p-2 text-[#6a88a4] transition duration-300 hover:bg-[#eff6ff] hover:text-[#2563eb] focus:outline-none focus:ring-2 focus:ring-[#bfdbfe]"
                        aria-label={showPassword ? "Hide password" : "Show password"}
                      >
                        {showPassword ? <EyeOff className="size-4" /> : <Eye className="size-4" />}
                      </button>
                    </div>
                  </label>

                  <div className="flex flex-wrap items-center justify-between gap-3">
                    <label className="inline-flex items-center gap-2 text-sm font-medium text-[#526f8a]">
                      <input
                        type="checkbox"
                        className="h-4 w-4 rounded border-[#b8cde3] text-[#2563eb] focus:ring-[#2563eb]"
                        checked={remember}
                        onChange={(event) => {
                          setRemember(event.target.checked);
                          if (!event.target.checked) {
                            localStorage.removeItem("login_saved_user");
                            localStorage.removeItem("login_saved_pass");
                          }
                        }}
                      />
                      Remember me
                    </label>
                    <div className="inline-flex items-center gap-2 rounded-full border border-[#d7e6f6] bg-white/85 px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.18em] text-[#66839e]">
                      <span className="h-2.5 w-2.5 rounded-full bg-emerald-400 shadow-[0_0_0_6px_rgba(52,211,153,0.12)]" />
                      Active protection
                    </div>
                  </div>

                  {error || isLockedOut ? (
                    <div className="flex items-start gap-2 rounded-2xl border border-rose-200 bg-[linear-gradient(135deg,#fff1f2,#fff7f7)] px-4 py-3 text-sm text-rose-700 shadow-[0_16px_26px_-24px_rgba(225,29,72,0.92)]">
                      <AlertCircle className="mt-0.5 size-4 shrink-0" />
                      <span>
                        {isLockedOut
                          ? `Too many failed attempts. Try again in ${countdown}s.`
                          : error}
                      </span>
                    </div>
                  ) : null}

                  <button
                    type="submit"
                    disabled={loginDisabled}
                    className="group relative w-full overflow-hidden rounded-2xl bg-[linear-gradient(135deg,#0f766e,#2563eb,#38bdf8)] px-4 py-3.5 text-base font-semibold text-white shadow-[0_26px_40px_-24px_rgba(37,99,235,0.96)] transition-all duration-300 hover:-translate-y-1 hover:shadow-[0_32px_46px_-24px_rgba(14,165,233,0.96)] disabled:cursor-not-allowed disabled:opacity-70"
                  >
                    <span className="pointer-events-none absolute inset-y-0 -left-1/3 w-1/3 -skew-x-12 bg-white/35 blur-sm transition-transform duration-700 group-hover:translate-x-[380%]" />
                    <span className="relative">{loginButtonLabel}</span>
                  </button>
                </form>

                <div className="mt-6 grid gap-3 sm:grid-cols-2">
                  <button
                    type="button"
                    onClick={() => setActivationOpen((prev) => !prev)}
                    className="flex items-center justify-between rounded-2xl border border-[#d8e5f4] bg-[linear-gradient(135deg,#ffffff,#f5faff)] px-4 py-3 text-left text-sm font-semibold text-[#143a5f] shadow-[0_18px_28px_-24px_rgba(37,99,235,0.82)] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd]"
                  >
                    <span className="inline-flex items-center gap-2">
                      <span className="inline-flex h-8 w-8 items-center justify-center rounded-xl bg-[linear-gradient(135deg,#2563eb,#0ea5e9)] text-white">
                        <KeyRound className="size-4" />
                      </span>
                      License
                    </span>
                    <ChevronDown className={`size-4 transition ${activationOpen ? "rotate-180" : ""}`} />
                  </button>

                  <button
                    type="button"
                    onClick={() => setAboutOpen((prev) => !prev)}
                    className="flex items-center justify-between rounded-2xl border border-[#d8e5f4] bg-[linear-gradient(135deg,#ffffff,#f5faff)] px-4 py-3 text-left text-sm font-semibold text-[#143a5f] shadow-[0_18px_28px_-24px_rgba(37,99,235,0.82)] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd]"
                  >
                    <span className="inline-flex items-center gap-2">
                      <span className="inline-flex h-8 w-8 items-center justify-center rounded-xl bg-[linear-gradient(135deg,#0f766e,#14b8a6)] text-white">
                        <Sparkles className="size-4" />
                      </span>
                      About
                    </span>
                    <ChevronDown className={`size-4 transition ${aboutOpen ? "rotate-180" : ""}`} />
                  </button>
                </div>

                {activationOpen ? (
                  <div className="mt-3 rounded-[24px] border border-[#dce8f5] bg-[linear-gradient(135deg,#ffffff,#f7fbff)] p-4 text-xs text-[#5d748c] shadow-[0_22px_36px_-30px_rgba(15,23,42,0.82)]">
                    <div className="grid gap-2 sm:grid-cols-2">
                      <p>
                        <span className="font-semibold text-[#163b60]">Mode:</span> {licenseStatus?.activationLabel ?? "-"}
                      </p>
                      <p>
                        <span className="font-semibold text-[#163b60]">Status:</span> {licenseStatus?.activationMessage ?? "-"}
                      </p>
                      <p>
                        <span className="font-semibold text-[#163b60]">Type:</span>{" "}
                        {licenseStatus?.licenseTypeWithDeviceLimit ?? licenseStatus?.licenseType ?? "-"}
                      </p>
                      <p>
                        <span className="font-semibold text-[#163b60]">Expiry:</span> {formatUtc(licenseStatus?.expiryUtc)}
                      </p>
                    </div>

                    <div className="mt-4 flex flex-wrap items-center gap-2">
                      <input
                        type="file"
                        accept=".key"
                        onChange={(event) => setLicenseKeyFile(event.target.files?.[0] ?? null)}
                        className="w-full rounded-xl border border-[#d7e5f4] bg-white px-3 py-2 text-xs text-[#47627b] file:mr-2 file:rounded-lg file:border-0 file:bg-[#dbeafe] file:px-3 file:py-1.5 file:font-semibold file:text-[#1d4ed8] sm:w-auto"
                      />
                      <button
                        type="button"
                        disabled={activatingLicense}
                        onClick={() => void handleActivateLicense()}
                        className="inline-flex items-center gap-1 rounded-xl border border-[#2563eb] bg-[#2563eb] px-3 py-2 text-xs font-semibold text-white shadow-[0_16px_24px_-18px_rgba(37,99,235,0.9)] disabled:cursor-not-allowed disabled:opacity-70"
                      >
                        <KeyRound className="size-3.5" />
                        {activatingLicense ? "Activating..." : "Activate"}
                      </button>
                      <button
                        type="button"
                        disabled={loadingLicense}
                        onClick={() => void handleRefreshLicense()}
                        className="inline-flex items-center gap-1 rounded-xl border border-[#d1dfef] bg-white px-3 py-2 text-xs font-semibold text-[#355779] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd] disabled:cursor-not-allowed disabled:opacity-70"
                      >
                        <RefreshCcw className="size-3.5" />
                        {loadingLicense ? "Refreshing..." : "Refresh"}
                      </button>
                    </div>

                    {licenseNotice ? (
                      <div className="mt-3 rounded-xl border border-[#cfe1f3] bg-[#eef7ff] px-3 py-2 text-[11px] text-[#1f4f7c]">
                        {licenseNotice}
                      </div>
                    ) : null}
                  </div>
                ) : null}

                {aboutOpen ? (
                  <div className="mt-3 rounded-[24px] border border-[#dce8f5] bg-[linear-gradient(135deg,#ffffff,#f7fbff)] p-4 text-xs text-[#5d748c] shadow-[0_22px_36px_-30px_rgba(15,23,42,0.82)]">
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div className="rounded-2xl border border-[#dce8f5] bg-[linear-gradient(140deg,#ffffff,#eff7ff)] p-4">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-[#7a93aa]">Developer</p>
                        <p className="mt-1 text-sm font-semibold text-[#173a5d]">{DEVELOPER_NAME}</p>
                      </div>
                      <div className="rounded-2xl border border-[#dce8f5] bg-[linear-gradient(140deg,#ffffff,#f2fffa)] p-4">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-[#7a93aa]">Version</p>
                        <p className="mt-1 text-sm font-semibold text-[#0f5b8a]">{SOFTWARE_VERSION}</p>
                      </div>
                      <div className="rounded-2xl border border-[#dce8f5] bg-[linear-gradient(140deg,#ffffff,#fffbea)] p-4 sm:col-span-2">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-[#7a93aa]">Support</p>
                        <p className="mt-1 text-sm font-semibold text-[#173a5d]">{DEVELOPER_PHONE}</p>
                        <p className="mt-2 break-all text-sm text-[#56708a]">{DEVELOPER_EMAIL}</p>
                      </div>
                    </div>

                    <div className="mt-4 flex flex-wrap gap-2">
                      <a
                        href={WHATSAPP_URL}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center gap-1 rounded-xl border border-[#d1dfef] bg-white px-3 py-2 text-[11px] font-semibold text-[#355779] transition duration-300 hover:-translate-y-0.5 hover:border-[#86efac] hover:text-[#166534]"
                      >
                        <MessageCircle className="size-3.5 text-[#0b8f3a]" />
                        WhatsApp
                      </a>
                      <a
                        href={DEVELOPER_WEBSITE}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center gap-1 rounded-xl border border-[#d1dfef] bg-white px-3 py-2 text-[11px] font-semibold text-[#355779] transition duration-300 hover:-translate-y-0.5 hover:border-[#93c5fd] hover:text-[#1d4ed8]"
                      >
                        <Globe className="size-3.5" />
                        Website
                      </a>
                      <a
                        href={`mailto:${DEVELOPER_EMAIL}`}
                        className="inline-flex items-center gap-1 rounded-xl border border-[#d1dfef] bg-white px-3 py-2 text-[11px] font-semibold text-[#355779] transition duration-300 hover:-translate-y-0.5 hover:border-[#f9a8d4] hover:text-[#be185d]"
                      >
                        <Mail className="size-3.5" />
                        Mail
                      </a>
                    </div>
                  </div>
                ) : null}
              </div>
            </div>
          </div>
        </div>
      </section>
    </main>
  );
}
