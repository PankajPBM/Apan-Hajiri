"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { CheckCircle2, KeyRound, Lock, RefreshCcw, ShieldAlert, Upload, X, XCircle } from "lucide-react";
import {
  activateLicenseFiles,
  deactivateLicenseFiles,
  fetchLicenseStatus,
  reloadLicenseStatus,
  type LicenseStatusResponse,
} from "@/components/data/license-client";
import { useCalendarPreference } from "@/components/date/calendar-context";
import { formatCalendarDateTime } from "@/components/date/date-display";
import { readAuthSession, type AuthSession } from "@/components/data/auth-store";
import { resolveApiBases } from "@/lib/api-bases";

type NoticeKind = "success" | "warning" | "error" | "info";

type NoticeState = {
  kind: NoticeKind;
  text: string;
};

type LicensePanelProps = {
  compact?: boolean;
  showHeader?: boolean;
  onClose?: () => void;
};

const noticeTone: Record<NoticeKind, string> = {
  success: "border-emerald-200 bg-emerald-50 text-emerald-800",
  warning: "border-amber-200 bg-amber-50 text-amber-800",
  error: "border-rose-200 bg-rose-50 text-rose-800",
  info: "border-slate-200 bg-slate-50 text-slate-800",
};

function toText(value: string | null | undefined): string {
  return value && value.trim().length > 0 ? value : "-";
}

function modeTone(mode: string): string {
  if (mode === "licensed") {
    return "border-emerald-200 bg-emerald-50 text-emerald-800";
  }
  if (mode === "trial") {
    return "border-amber-200 bg-amber-50 text-amber-800";
  }
  return "border-rose-200 bg-rose-50 text-rose-800";
}

function modeIcon(mode: string) {
  if (mode === "licensed") {
    return <CheckCircle2 className="h-4 w-4" />;
  }
  if (mode === "trial") {
    return <ShieldAlert className="h-4 w-4" />;
  }
  return <XCircle className="h-4 w-4" />;
}

function pickLicenseFiles(items: Iterable<File>): { dat: File | null; key: File | null } {
  let dat: File | null = null;
  let key: File | null = null;
  for (const file of items) {
    const lower = file.name.trim().toLowerCase();
    if (!dat && (lower === "license.dat" || lower.endsWith(".dat"))) {
      dat = file;
      continue;
    }
    if (!key && lower.endsWith(".key")) {
      key = file;
    }
  }
  return { dat, key };
}

function MetricCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-[--border] bg-[linear-gradient(120deg,#ffffff,#f8fbff)] px-3 py-3 shadow-[0_10px_20px_-20px_rgba(22,58,95,0.9)] transition duration-200 hover:-translate-y-0.5 hover:shadow-[0_18px_26px_-20px_rgba(22,58,95,0.9)]">
      <p className="text-[11px] uppercase tracking-wide text-[--muted-foreground]">{label}</p>
      <p className="mt-1 text-sm font-semibold text-[--foreground]">{value}</p>
    </div>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid grid-cols-[130px_minmax(0,1fr)] gap-3 border-b border-[--border] py-2 text-sm last:border-b-0">
      <span className="text-[--muted-foreground]">{label}</span>
      <span className="font-medium text-[--foreground] break-all">{value}</span>
    </div>
  );
}

export function LicensePanel({ compact = false, showHeader = true, onClose }: LicensePanelProps) {
  const { calendarType } = useCalendarPreference();
  const [session, setSession] = useState<AuthSession | null>(null);
  const [status, setStatus] = useState<LicenseStatusResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const [licenseDatFile, setLicenseDatFile] = useState<File | null>(null);
  const [licenseKeyFile, setLicenseKeyFile] = useState<File | null>(null);
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  // Deactivate modal state
  const [deactivateModalOpen, setDeactivateModalOpen] = useState(false);
  const [adminPassword, setAdminPassword] = useState("");
  const [adminPasswordError, setAdminPasswordError] = useState<string | null>(null);
  const [deactivating, setDeactivating] = useState(false);

  useEffect(() => {
    setSession(readAuthSession());
  }, []);

  const isAdmin = session?.role === "Admin";
  const statusTone = useMemo(() => modeTone(status?.activationMode ?? "locked"), [status?.activationMode]);

  // Handle ESC key for modal
  useEffect(() => {
    if (!onClose) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        if (deactivateModalOpen) {
          setDeactivateModalOpen(false);
        } else {
          onClose();
        }
      }
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [onClose, deactivateModalOpen]);

  const loadStatus = useCallback(async (withNotice: boolean) => {
    try {
      const latest = await fetchLicenseStatus();
      setStatus(latest);
      if (withNotice) {
        setNotice({ kind: "success", text: "License status refreshed." });
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unable to load license status.";
      setNotice({ kind: "error", text: message });
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadStatus(false);
  }, [loadStatus]);

  async function handleReload() {
    setBusy(true);
    try {
      const latest = await reloadLicenseStatus();
      setStatus(latest);
      setNotice({ kind: "success", text: "License status reloaded." });
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unable to reload license status.";
      setNotice({ kind: "error", text: message });
    } finally {
      setBusy(false);
    }
  }

  function attachFiles(files: FileList | File[]) {
    if (!isAdmin) {
      setNotice({ kind: "error", text: "Only Administrator accounts can activate the license." });
      return;
    }
    const picked = pickLicenseFiles(Array.from(files));
    if (picked.dat) {
      setLicenseDatFile(picked.dat);
    }
    if (picked.key) {
      setLicenseKeyFile(picked.key);
    }

    if (!picked.dat && !picked.key) {
      setNotice({ kind: "warning", text: "Please provide a .key activation file." });
      return;
    }

    if (!picked.key) {
      setNotice({
        kind: "warning",
        text: "Please add a .key activation file.",
      });
      return;
    }

    setNotice({ kind: "info", text: "Files selected. Click Activate." });
  }

  async function handleActivate() {
    if (!isAdmin) {
      setNotice({ kind: "error", text: "Only Administrator accounts can activate the license." });
      return;
    }

    if (!licenseKeyFile) {
      setNotice({ kind: "warning", text: "Drop or select a .key activation file." });
      return;
    }

    setBusy(true);
    try {
      const latest = await activateLicenseFiles(licenseKeyFile, licenseDatFile);
      setStatus(latest);
      setLicenseDatFile(null);
      setLicenseKeyFile(null);

      if (latest.isValid) {
        setNotice({ kind: "success", text: "License activated successfully." });
      } else if (latest.isAccessGranted) {
        setNotice({ kind: "warning", text: "License validation failed. Trial mode is still active." });
      } else {
        setNotice({ kind: "error", text: "License invalid and trial expired." });
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : "License activation failed.";
      setNotice({ kind: "error", text: message });
    } finally {
      setBusy(false);
    }
  }

  function handleDeactivateClick() {
    if (!isAdmin) {
      setNotice({ kind: "error", text: "Only Administrator accounts can deactivate the license." });
      return;
    }
    setAdminPassword("");
    setAdminPasswordError(null);
    setDeactivateModalOpen(true);
  }

  async function confirmDeactivate() {
    const pwd = adminPassword.trim();
    if (!pwd) {
      setAdminPasswordError("Please enter the administrator password.");
      return;
    }

    setDeactivating(true);
    setAdminPasswordError(null);

    try {
      let verified = false;
      const bases = resolveApiBases();
      for (const base of bases) {
        try {
          const res = await fetch(`${base}/api/auth/login`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              username: session?.username || "admin",
              password: pwd,
            }),
            cache: "no-store",
          });

          if (res.ok) {
            const data = (await res.json()) as { role?: string; Role?: string };
            const role = data.role || data.Role;
            if (role === "Admin") {
              verified = true;
              break;
            } else {
              setAdminPasswordError("This account does not have Administrator privileges.");
              setDeactivating(false);
              return;
            }
          } else {
            const errData = (await res.json().catch(() => null)) as { message?: string } | null;
            setAdminPasswordError(errData?.message || "Invalid administrator password.");
            setDeactivating(false);
            return;
          }
        } catch {
          // try next base
        }
      }

      if (!verified) {
        setAdminPasswordError("Invalid administrator password. Please try again.");
        setDeactivating(false);
        return;
      }

      const latest = await deactivateLicenseFiles();
      setStatus(latest);
      setDeactivateModalOpen(false);
      setNotice({ kind: "warning", text: "License successfully deactivated." });
    } catch (error) {
      setAdminPasswordError(error instanceof Error ? error.message : "License deactivation failed.");
    } finally {
      setDeactivating(false);
    }
  }

  const summaryGrid = compact ? "sm:grid-cols-2" : "md:grid-cols-3";
  const bodyGrid = compact ? "grid-cols-1" : "xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]";

  const panelContent = (
    <div className="space-y-5">
      {showHeader ? (
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="text-sm font-semibold text-[--foreground]">License</h3>
            <p className="text-xs text-[--muted-foreground]">30-day trial, activation, expiry and deactivation.</p>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={handleReload}
              disabled={busy}
              className="inline-flex items-center gap-1.5 rounded-lg border border-[#c7dcf2] bg-[linear-gradient(120deg,#ffffff,#f1f7ff)] px-3 py-1.5 text-xs font-medium transition duration-200 hover:-translate-y-0.5 hover:shadow-[0_12px_18px_-14px_rgba(20,59,94,0.9)] disabled:cursor-not-allowed disabled:opacity-70"
            >
              <RefreshCcw className="h-3.5 w-3.5" />
              Refresh
            </button>
            <button
              type="button"
              onClick={handleDeactivateClick}
              disabled={busy || !isAdmin}
              title={!isAdmin ? "Administrator privilege required" : "Deactivate current license"}
              className="inline-flex items-center gap-1.5 rounded-lg border border-rose-200 bg-[linear-gradient(120deg,#fff1f1,#ffe9e9)] px-3 py-1.5 text-xs font-medium text-rose-700 transition duration-200 hover:-translate-y-0.5 hover:shadow-[0_12px_18px_-14px_rgba(153,58,58,0.6)] disabled:cursor-not-allowed disabled:opacity-50"
            >
              <XCircle className="h-3.5 w-3.5" />
              Deactivate
            </button>
          </div>
        </div>
      ) : null}

      {!isAdmin ? (
        <div className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3.5 py-2.5 text-xs text-amber-800">
          <Lock className="h-4 w-4 shrink-0 text-amber-600" />
          <span>
            You are signed in as <strong>{session?.role || "User"}</strong>. Only an <strong>Administrator</strong> can activate or deactivate the software license.
          </span>
        </div>
      ) : null}

      {loading ? (
        <div className="rounded-xl border border-[--border] bg-[--card] px-3 py-2 text-sm text-[--muted-foreground]">
          Loading license status...
        </div>
      ) : null}

      {status ? (
        <>
          <div className={`rounded-xl border px-3 py-3 shadow-[0_12px_24px_-20px_rgba(28,67,106,0.9)] ${statusTone}`}>
            <div className="flex items-center gap-2 text-sm font-semibold">
              {modeIcon(status.activationMode)}
              <span>{status.activationLabel}</span>
            </div>
            <p className="mt-1 text-xs">{status.activationMessage}</p>
          </div>

          <div className={`grid gap-3 ${summaryGrid}`}>
            {status.activationMode === "licensed" ? (
              <>
                <MetricCard label="Activated On" value={formatCalendarDateTime(status.licenseActivatedAtUtc, calendarType)} />
                <MetricCard label="Remaining Days" value={`${status.licenseDaysRemaining} day(s)`} />
                <MetricCard label="License Expiry" value={formatCalendarDateTime(status.licenseExpiryUtc, calendarType)} />
              </>
            ) : (
              <>
                <MetricCard label="Trial Remaining" value={`${status.trial.daysRemaining} day(s)`} />
                <MetricCard label="Trial Expiry" value={formatCalendarDateTime(status.trial.expiryUtc, calendarType)} />
                <MetricCard label="License Expiry" value={formatCalendarDateTime(status.licenseExpiryUtc, calendarType)} />
              </>
            )}
          </div>

          <div className={`grid gap-4 ${bodyGrid}`}>
            <div className="rounded-xl border border-[--border] bg-[linear-gradient(120deg,#ffffff,#f9fbff)] p-3 shadow-[0_14px_24px_-22px_rgba(27,66,102,1)]">
              <h4 className="text-sm font-semibold text-[--foreground]">Activation Details</h4>
              <div className="mt-2">
                <DetailRow label="Machine Hash" value={toText(status.machineHash)} />
                <DetailRow label="License Type" value={toText(status.licenseTypeWithDeviceLimit ?? status.licenseType)} />
                <DetailRow
                  label="Device Limit"
                  value={
                    status.effectiveMaxDevices < 0
                      ? `Unlimited (Current: ${status.currentDeviceCount})`
                      : `${status.currentDeviceCount} / ${status.effectiveMaxDevices}`
                  }
                />
                <DetailRow label="Company" value={toText(status.customerName)} />
                <DetailRow label="Contact Number" value={toText(status.contactNumber)} />
                <DetailRow label="Email" value={toText(status.email)} />
                {status.activationMode === "licensed" ? (
                  <DetailRow label="Activated On" value={formatCalendarDateTime(status.licenseActivatedAtUtc, calendarType)} />
                ) : null}
              </div>
            </div>

            <div className="rounded-xl border border-[--border] bg-[linear-gradient(120deg,#ffffff,#f9fbff)] p-3 shadow-[0_14px_24px_-22px_rgba(27,66,102,1)]">
              <h4 className="text-sm font-semibold text-[--foreground]">Activate License</h4>
              <p className="mt-1 text-xs text-[--muted-foreground]">
                Upload your <span className="font-medium">.key</span> activation file and click Activate.
              </p>

              <div
                onDragOver={(event) => {
                  event.preventDefault();
                  if (!isAdmin) return;
                  setDragActive(true);
                }}
                onDragLeave={(event) => {
                  event.preventDefault();
                  setDragActive(false);
                }}
                onDrop={(event) => {
                  event.preventDefault();
                  setDragActive(false);
                  if (!isAdmin) {
                    setNotice({ kind: "error", text: "Only Administrator accounts can activate the license." });
                    return;
                  }
                  attachFiles(event.dataTransfer.files);
                }}
                className={`mt-3 rounded-xl border-2 border-dashed px-4 py-6 text-center transition ${
                  !isAdmin
                    ? "border-slate-200 bg-slate-50/50 opacity-60 cursor-not-allowed"
                    : dragActive
                    ? "border-[#2f6e9c] bg-[#eef5fd]"
                    : "border-[#cfe0f2] bg-[#f8fbff]"
                }`}
              >
                <Upload className="mx-auto h-5 w-5 text-[#2f6e9c]" />
                <p className="mt-2 text-sm font-medium text-[#1d446b]">Drag and drop files here</p>
                <p className="mt-1 text-xs text-[#53738d]">or choose files manually</p>
                <button
                  type="button"
                  onClick={() => {
                    if (!isAdmin) {
                      setNotice({ kind: "error", text: "Only Administrator accounts can activate the license." });
                      return;
                    }
                    fileInputRef.current?.click();
                  }}
                  disabled={!isAdmin}
                  className="mt-3 inline-flex items-center gap-1.5 rounded-lg border border-[#b9d3ea] bg-white px-3 py-1.5 text-xs font-medium text-[#1d446b] transition duration-200 hover:-translate-y-0.5 hover:shadow-[0_12px_18px_-14px_rgba(29,68,107,0.75)] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <KeyRound className="h-3.5 w-3.5" />
                  Browse Files
                </button>
                <input
                  ref={fileInputRef}
                  type="file"
                  accept=".key"
                  multiple
                  disabled={!isAdmin}
                  onChange={(event) => {
                    attachFiles(event.target.files ?? []);
                    event.target.value = "";
                  }}
                  className="hidden"
                />
              </div>

              <div className="mt-3 grid gap-2">
                <div className="rounded-lg border border-[--border] bg-[--muted] px-3 py-2 text-xs">
                  <p className="text-[--muted-foreground]">Selected .key file</p>
                  <p className="mt-1 font-medium text-[--foreground]">{licenseKeyFile?.name ?? "Not selected"}</p>
                </div>
              </div>

              <div className="mt-3 flex flex-wrap items-center gap-2">
                <button
                  type="button"
                  onClick={handleActivate}
                  disabled={busy || !isAdmin}
                  title={!isAdmin ? "Administrator privilege required" : "Activate license"}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-[linear-gradient(120deg,#1d4ed8,#0284c7)] px-3 py-1.5 text-xs font-semibold text-white shadow-[0_12px_18px_-14px_rgba(30,78,216,0.9)] transition duration-200 hover:-translate-y-0.5 hover:shadow-[0_16px_22px_-14px_rgba(30,78,216,0.95)] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <CheckCircle2 className="h-3.5 w-3.5" />
                  Activate
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setLicenseDatFile(null);
                    setLicenseKeyFile(null);
                    setNotice({ kind: "info", text: "Selected files cleared." });
                  }}
                  disabled={busy || !isAdmin}
                  className="inline-flex items-center gap-1.5 rounded-lg border border-[--border] bg-[--card] px-3 py-1.5 text-xs font-medium transition duration-200 hover:-translate-y-0.5 hover:bg-[#f5f9ff] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  Clear Files
                </button>
              </div>
            </div>
          </div>
        </>
      ) : null}

      {notice ? <div className={`rounded-lg border px-3 py-2 text-sm ${noticeTone[notice.kind]}`}>{notice.text}</div> : null}

      {/* Application-based Deactivation Confirmation Modal */}
      {deactivateModalOpen ? (
        <div
          className="fixed inset-0 z-[70] flex items-center justify-center bg-slate-900/60 backdrop-blur-xs p-4 animate-in fade-in duration-150"
          onClick={(e) => {
            if (e.target === e.currentTarget && !deactivating) {
              setDeactivateModalOpen(false);
            }
          }}
        >
          <div className="w-full max-w-md rounded-2xl border border-slate-200 bg-white p-6 shadow-2xl space-y-4">
            <div className="flex items-start gap-3">
              <div className="rounded-full bg-rose-100 p-2.5 text-rose-600 shrink-0">
                <ShieldAlert className="h-6 w-6" />
              </div>
              <div>
                <h4 className="text-base font-bold text-slate-900">Deactivate License</h4>
                <p className="mt-1 text-xs text-slate-500 leading-relaxed">
                  Deactivating your license will remove the active license key and return the software to trial or restricted mode. This action requires Administrator credentials.
                </p>
              </div>
            </div>

            <div className="space-y-1.5 pt-2">
              <label className="block text-xs font-semibold text-slate-700">
                Admin Password:
              </label>
              <input
                type="password"
                value={adminPassword}
                onChange={(e) => {
                  setAdminPassword(e.target.value);
                  setAdminPasswordError(null);
                }}
                placeholder="Enter Administrator Password"
                className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm outline-none focus:border-rose-500 focus:ring-2 focus:ring-rose-200"
                autoFocus
                disabled={deactivating}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && adminPassword.trim() && !deactivating) {
                    void confirmDeactivate();
                  }
                }}
              />
              {adminPasswordError ? (
                <p className="text-xs text-rose-600 font-medium pt-0.5">{adminPasswordError}</p>
              ) : null}
            </div>

            <div className="flex items-center justify-end gap-2 pt-3 border-t border-slate-100">
              <button
                type="button"
                onClick={() => setDeactivateModalOpen(false)}
                disabled={deactivating}
                className="rounded-lg border border-slate-300 bg-white px-3.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-50 transition"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={confirmDeactivate}
                disabled={deactivating || !adminPassword.trim()}
                className="inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-1.5 text-xs font-semibold text-white hover:bg-rose-700 disabled:opacity-50 transition shadow-xs"
              >
                {deactivating ? <RefreshCcw className="h-3.5 w-3.5 animate-spin" /> : null}
                Confirm Deactivation
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );

  // If opened via Topbar modal (onClose is passed), wrap in an application dialog
  if (onClose) {
    return (
      <div
        className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/50 backdrop-blur-xs p-4 overflow-y-auto animate-in fade-in duration-200"
        onClick={(e) => {
          if (e.target === e.currentTarget && !deactivateModalOpen) {
            onClose();
          }
        }}
      >
        <div className="relative w-full max-w-4xl rounded-2xl border border-slate-200 bg-white p-6 shadow-2xl max-h-[90vh] overflow-y-auto">
          <button
            type="button"
            onClick={onClose}
            className="absolute right-4 top-4 rounded-lg p-1.5 text-slate-400 hover:bg-slate-100 hover:text-slate-600 transition"
            title="Close License Details"
          >
            <X className="h-5 w-5" />
          </button>
          {panelContent}
        </div>
      </div>
    );
  }

  return panelContent;
}
