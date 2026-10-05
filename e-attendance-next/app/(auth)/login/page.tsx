"use client";

import { useEffect, useRef, useState, useTransition } from "react";
import type { Route } from "next";
import { useRouter } from "next/navigation";
import {
  AlertCircle,
  Eye,
  EyeOff,
  Globe,
  KeyRound,
  Lock,
  Mail,
  MessageCircle,
  RefreshCcw,
  ShieldCheck,
  User,
  CheckCircle2,
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
  fetchAppVersion,
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

function formatUtc(value: string | null | undefined): string {
  if (!value) return "-";
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
  const [appVersion, setAppVersion] = useState(SOFTWARE_VERSION);

  useEffect(() => {
    fetchAppVersion()
      .then((v) => {
        if (v) setAppVersion(v);
      })
      .catch(() => {});
  }, []);

  const [capsLockActive, setCapsLockActive] = useState(false);

  // --- Lockout state ---
  const LOCKOUT_KEY = "login_lockout";
  function getLockout() {
    try {
      return JSON.parse(localStorage.getItem(LOCKOUT_KEY) ?? "{}") as { fails?: number; until?: number };
    } catch {
      return {};
    }
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
    if (lockoutUntil <= Date.now()) {
      setCountdown(0);
      return;
    }
    const tick = () => {
      const remaining = Math.ceil((lockoutUntil - Date.now()) / 1000);
      if (remaining <= 0) {
        setCountdown(0);
      } else {
        setCountdown(remaining);
      }
    };
    tick();
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  }, [lockoutUntil]);

  const recordFailedAttempt = () => {
    const data = getLockout();
    const fails = (data.fails ?? 0) + 1;
    let until = 0;
    if (fails >= 10) {
      until = Date.now() + 60 * 1000;
    } else if (fails >= 5) {
      until = Date.now() + 30 * 1000;
    }
    saveLockout(fails, until);
    if (until > 0) setLockoutUntil(until);
  };

  const recordSuccess = () => {
    localStorage.removeItem(LOCKOUT_KEY);
    setLockoutUntil(0);
    setCountdown(0);
  };

  const loadStatus = async () => {
    setLoadingLicense(true);
    try {
      const status = await fetchLicenseStatus();
      setLicenseStatus(status);
    } catch {
      setLicenseStatus(null);
    } finally {
      setLoadingLicense(false);
    }
  };

  useEffect(() => {
    void loadStatus();
  }, []);

  const isLocked = countdown > 0;
  const showLicenseBlock = licenseStatus !== null && !licenseStatus.isAccessGranted;
  const isPermanentlyBlocked = licenseStatus?.code === "permanently_blocked";
  const licenseBlockMessage = isPermanentlyBlocked
    ? `System access permanently blocked: ${licenseStatus?.message || "Security tamper detected"}. To restore access, contact PB Tech Zone (+9779814891277) and activate a valid license file.`
    : licenseStatus?.message || "Please activate your software license to continue.";
  const licenseMachineHash = licenseStatus?.machineHash || "-";
  const trialExpiryUtc = licenseStatus?.expiryUtc;
  const licenseExpiryUtc = licenseStatus?.licenseExpiryUtc;

  async function handleLogin(e: React.FormEvent) {
    e.preventDefault();
    if (isLocked || isSubmitting) return;

    if (!username.trim() || !password.trim()) {
      setError("Please enter both username and password.");
      return;
    }

    setIsSubmitting(true);
    setError(null);

    try {
      const res = await loginUser(username.trim(), password.trim());
      if (res.token) {
        saveAuthToken(res.token, remember);
      }
      const session = toAuthSession(res);
      saveAuthSession(session, remember);

      if (remember) {
        localStorage.setItem("login_saved_user", username.trim());
        localStorage.setItem("login_saved_pass", password.trim());
      } else {
        localStorage.removeItem("login_saved_user");
        localStorage.removeItem("login_saved_pass");
      }

      recordSuccess();
      notifyAuthUpdated();
      const target = firstAllowedRouteForSession(session);
      startTransition(() => {
        router.replace(target as Route);
      });
    } catch (err) {
      recordFailedAttempt();
      setError(err instanceof Error ? err.message : "Invalid username or password.");
    } finally {
      if (mountedRef.current) setIsSubmitting(false);
    }
  }

  async function handleActivateLicense(e: React.FormEvent) {
    e.preventDefault();
    if (!licenseKeyFile) {
      setLicenseNotice("Please select a license file (.lic).");
      return;
    }
    setActivatingLicense(true);
    setLicenseNotice(null);
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
    <main className="flex min-h-screen w-full flex-col justify-center bg-slate-100 p-4 sm:p-6 md:p-8">
      <div className="mx-auto flex w-full max-w-[1000px] overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-xl md:min-h-[580px]">
        {/* Left Side: Brand Panel */}
        <aside className="relative hidden w-1/2 flex-col justify-between bg-gradient-to-br from-blue-700 via-blue-800 to-indigo-950 p-10 text-white md:flex">
          <div>
            <div className="flex items-center gap-3">
              <div className="flex h-12 w-12 items-center justify-center">
                <img src="/apan_logo.png" alt="Apan Hajiri" className="h-full w-full object-contain drop-shadow-md" />
              </div>
              <div>
                <span className="block font-nepali text-xl font-bold tracking-wide">अपन हाजिरी</span>
                <span className="block text-xs text-blue-200">Apan Hajiri Management</span>
              </div>
            </div>

            <div className="mt-12 space-y-4">
              <h2 className="text-3xl font-extrabold tracking-tight leading-tight">
                Modern Attendance & Biometric Control
              </h2>
              <p className="text-sm leading-relaxed text-blue-100/80">
                A seamless, automated attendance platform connecting your ZKTeco biometric devices, shifts, and payroll reporting.
              </p>
            </div>

            <div className="mt-8 space-y-3">
              {[
                "Realtime device sync & access control",
                "Automated Nepali (Bikram Sambat) calendar",
                "Advanced shift schedules & leaves",
              ].map((feature) => (
                <div key={feature} className="flex items-center gap-3 text-xs font-medium text-blue-100">
                  <CheckCircle2 className="h-4 w-4 text-emerald-400 shrink-0" />
                  <span>{feature}</span>
                </div>
              ))}
            </div>
          </div>

          <div className="border-t border-white/10 pt-4 text-xs text-blue-200/70 flex items-center justify-between">
            <span>Version: {appVersion}</span>
            <span>Developed by {DEVELOPER_NAME}</span>
          </div>
        </aside>

        {/* Right Side: Login Form */}
        <div className="flex flex-1 flex-col justify-between p-8 sm:p-12">
          <div>
            <div className="flex items-center justify-between">
              <div className="md:hidden flex items-center gap-2">
                <img src="/apan_logo.png" alt="Logo" className="h-8 w-8 object-contain" />
                <span className="font-nepali text-lg font-bold text-slate-900">अपन हाजिरी</span>
              </div>
              <span className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-600">
                <ShieldCheck className="h-3.5 w-3.5 text-blue-600" />
                Secure Portal
              </span>
            </div>

            <div className="mt-8">
              <h3 className="text-2xl font-bold text-slate-900">Sign in to your account</h3>
              <p className="mt-1 text-sm text-slate-500">Enter your credentials to access the console</p>
            </div>

            {error && (
              <div className="mt-4 flex items-center gap-2 rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs font-medium text-rose-700">
                <AlertCircle className="h-4 w-4 shrink-0" />
                <span>{error}</span>
              </div>
            )}

            {isLocked && (
              <div className="mt-4 flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs font-medium text-amber-800">
                <AlertCircle className="h-4 w-4 shrink-0" />
                <span>Too many failed attempts. Please wait {countdown} seconds.</span>
              </div>
            )}

            {/* License Warning/Block */}
            {showLicenseBlock && (
              <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-xs text-amber-900">
                <div className="flex items-center justify-between font-semibold">
                  <span className="flex items-center gap-1.5">
                    <AlertCircle className="h-4 w-4 text-amber-600" />
                    License Activation Needed
                  </span>
                  <button
                    type="button"
                    onClick={() => setActivationOpen((prev) => !prev)}
                    className="text-xs text-blue-700 underline font-semibold hover:text-blue-800"
                  >
                    {activationOpen ? "Close Activation" : "Activate License"}
                  </button>
                </div>
                <p className="mt-1 text-[11px] text-amber-800">{licenseBlockMessage}</p>

                {activationOpen && (
                  <form onSubmit={handleActivateLicense} className="mt-3 border-t border-amber-200/60 pt-3 space-y-2">
                    <label className="block text-[11px] font-bold text-slate-700">Upload License File (.lic)</label>
                    <input
                      type="file"
                      accept=".lic"
                      onChange={(e) => setLicenseKeyFile(e.target.files?.[0] ?? null)}
                      className="block w-full text-xs text-slate-600 file:mr-2 file:rounded-md file:border-0 file:bg-blue-600 file:px-2.5 file:py-1 file:text-xs file:font-semibold file:text-white"
                    />
                    <button
                      type="submit"
                      disabled={activatingLicense || !licenseKeyFile}
                      className="inline-flex items-center justify-center rounded-md bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-50"
                    >
                      {activatingLicense ? "Activating..." : "Apply License"}
                    </button>
                    {licenseNotice && <p className="text-[11px] font-medium text-blue-800 mt-1">{licenseNotice}</p>}
                  </form>
                )}
              </div>
            )}

            {/* Login Form */}
            <form onSubmit={handleLogin} className="mt-6 space-y-4">
              <div>
                <label className="block text-xs font-semibold text-slate-700">Username</label>
                <div className="relative mt-1">
                  <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3 text-slate-400">
                    <User className="h-4 w-4" />
                  </div>
                  <input
                    type="text"
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    autoComplete="username"
                    placeholder="Enter username"
                    disabled={isLocked || isSubmitting}
                    className="block w-full rounded-lg border border-slate-300 bg-white py-2 pl-9 pr-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-600/20"
                  />
                </div>
              </div>

              <div>
                <label className="block text-xs font-semibold text-slate-700">Password</label>
                <div className="relative mt-1">
                  <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3 text-slate-400">
                    <Lock className="h-4 w-4" />
                  </div>
                  <input
                    type={showPassword ? "text" : "password"}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    onKeyDown={(e) => setCapsLockActive(e.getModifierState("CapsLock"))}
                    onKeyUp={(e) => setCapsLockActive(e.getModifierState("CapsLock"))}
                    autoComplete="current-password"
                    placeholder="Enter password"
                    disabled={isLocked || isSubmitting}
                    className="block w-full rounded-lg border border-slate-300 bg-white py-2 pl-9 pr-10 text-sm text-slate-900 placeholder:text-slate-400 focus:border-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-600/20"
                  />
                  <button
                    type="button"
                    onClick={() => setShowPassword((prev) => !prev)}
                    className="absolute inset-y-0 right-0 flex items-center pr-3 text-slate-400 hover:text-slate-600"
                  >
                    {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                  </button>
                </div>
                {capsLockActive && (
                  <p className="mt-1 text-[11px] font-semibold text-amber-700">
                    ⚠️ Caps Lock is active
                  </p>
                )}
              </div>

              <div className="flex items-center justify-between text-xs">
                <label className="flex items-center gap-2 cursor-pointer text-slate-600">
                  <input
                    type="checkbox"
                    checked={remember}
                    onChange={(e) => setRemember(e.target.checked)}
                    className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  <span>Remember my credentials</span>
                </label>
              </div>

              <button
                type="submit"
                disabled={isLocked || isSubmitting || isNavigating}
                className="flex w-full items-center justify-center rounded-lg bg-blue-600 py-2.5 text-sm font-semibold text-white shadow-sm hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500/20 disabled:opacity-50 transition"
              >
                {isSubmitting || isNavigating ? "Signing in..." : "Sign In"}
              </button>
            </form>
          </div>

          {/* Footer details */}
          <div className="mt-8 border-t border-slate-100 pt-4 flex items-center justify-between text-[11px] text-slate-400">
            <span>© 2026 {DEVELOPER_NAME}</span>
            <div className="flex items-center gap-3">
              <a href={WHATSAPP_URL} target="_blank" rel="noreferrer" className="hover:text-blue-600">
                WhatsApp
              </a>
              <span>•</span>
              <a href={DEVELOPER_WEBSITE} target="_blank" rel="noreferrer" className="hover:text-blue-600">
                Support
              </a>
            </div>
          </div>
        </div>
      </div>
    </main>
  );
}
