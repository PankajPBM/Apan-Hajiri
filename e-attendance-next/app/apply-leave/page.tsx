"use client";

import { useCallback, useEffect, useState } from "react";
import { Check, CheckCircle2, ChevronRight, Fingerprint, Loader2, QrCode, Search, User, Calendar, FileText, Type } from "lucide-react";
import { resolveApiBases } from "@/lib/api-bases";

type EmployeeInfo = {
  id: string;
  employeeCode: string;
  fullName: string;
  department: string;
  personnelId?: number;
};

type LeaveType = {
  Id: number;
  LeaveName: string;
};

export default function ApplyLeavePage() {
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [code, setCode] = useState("");
  const [emp, setEmp] = useState<EmployeeInfo | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([]);
  const [leaveTypeId, setLeaveTypeId] = useState<string>("");
  const [fromDate, setFromDate] = useState(() => new Date().toISOString().split("T")[0]);
  const [toDate, setToDate] = useState(() => new Date().toISOString().split("T")[0]);
  const [reason, setReason] = useState("");

  useEffect(() => {
    const fetchLeaveTypes = async () => {
      try {
        const bases = resolveApiBases({ includeCurrentOrigin: true });
        for (const base of bases) {
          try {
            let r = await fetch(`${base}/api/leave/types?activeOnly=true`);
            if (r.ok) {
              let data = await r.json();
              if (Array.isArray(data) && data.length === 0) {
                 // Try to seed if empty
                 const defaults = [
                   { leaveName: "Annual Leave", maxPerYear: 15, isPaid: true },
                   { leaveName: "Sick Leave", maxPerYear: 12, isPaid: true },
                   { leaveName: "Casual Leave", maxPerYear: 6, isPaid: true },
                 ];
                 for (const item of defaults) {
                   try { await fetch(`${base}/api/leave/types`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(item) }); } catch { /* ignore */ }
                 }
                 r = await fetch(`${base}/api/leave/types?activeOnly=true`);
                 if (r.ok) data = await r.json();
              }

              if (Array.isArray(data)) {
                // Standardize to PascalCase for the UI component
                const mapped = data.map((t: any) => ({
                  Id: t.Id ?? t.id,
                  LeaveName: t.LeaveName ?? t.leaveName ?? "Unnamed"
                }));
                setLeaveTypes(mapped);
                if (mapped.length > 0) setLeaveTypeId(String(mapped[0].Id));
                break;
              }
            }
          } catch { /* ignore */ }
        }
      } catch { /* ignore */ }
    };
    void fetchLeaveTypes();
  }, []);

  const verifyCode = async () => {
    if (!code.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const bases = resolveApiBases({ includeCurrentOrigin: true });
      let verifiedData: any = null;
      for (const base of bases) {
        try {
          const r = await fetch(`${base}/api/personnel/verify/${code.trim()}`);
          if (r.ok) { 
            const json = await r.json();

            const personnelId = json.personnelId ?? json.PersonnelId ?? json.id ?? json.Id;
            const firstName = json.firstName ?? json.FirstName;
            const lastName = json.lastName ?? json.LastName;
            const fullName = json.fullName ?? json.FullName ?? (firstName ? `${firstName} ${lastName ?? ""}`.trim() : json.employeeName ?? json.personnelName ?? "");
            const employeeCode = json.employeeCode ?? json.EmployeeCode ?? json.PersonnelCode ?? json.personnelCode ?? code.trim();

            verifiedData = {
              ...json,
              personnelId: personnelId ? String(personnelId) : undefined,
              fullName: fullName || employeeCode,
              employeeCode: employeeCode
            };
            break; 
          }
        } catch { /* ignore */ }
      }
      if (verifiedData && (verifiedData.personnelId || verifiedData.id)) {
        setEmp(verifiedData);
        setStep(2);
      } else {
        setError("Employee ID not found or inactive.");
      }
    } catch {
      setError("Connection error. Please try again.");
    } finally {
      setLoading(false);
    }
  };

  const submitLeave = async () => {
    if (!reason.trim()) {
      setError("Reason is mandatory.");
      return;
    }
    if (!leaveTypeId) {
      setError("Please select a leave type.");
      return;
    }

    setLoading(true);
    setError(null);
    try {
      const bases = resolveApiBases({ includeCurrentOrigin: true });
      let success = false;
      for (const base of bases) {
        try {
          const r = await fetch(`${base}/api/leave`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              // The backend's LeaveCreateRequest binds this field as "employeeId" — it was
              // previously sent as "personnelId", which doesn't match, so ASP.NET's model binder
              // silently left it at Guid.Empty and every submission failed with "personnel_id and
              // leave_type_id are required" regardless of what was actually selected.
              employeeId: emp?.personnelId,
              leaveTypeId: parseInt(leaveTypeId, 10),
              fromDate: fromDate,
              toDate: toDate,
              reason: reason.trim(),
              remarks: "Requested via QR Terminal"
            })
          });
          if (r.ok) { success = true; break; }
          else {
            const errData = await r.json();
            setError(errData.message || "Failed to submit request.");
          }
        } catch { /* ignore */ }
      }
      if (success) {
        setStep(3);
      } else if (!error) {
        setError("Failed to submit request. Please try again.");
      }
    } catch {
      setError("Connection error.");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="min-h-screen bg-[#f4f7fa] flex flex-col items-center justify-center p-4">
      <div className="w-full max-w-md bg-white rounded-[32px] shadow-2xl overflow-hidden border border-[#e2eaf1]">
        <div className="bg-gradient-to-br from-[#1e40af] to-[#1e3a8a] p-8 text-white text-center">
          <div className="mx-auto mb-4 grid h-16 w-16 place-items-center rounded-2xl bg-white/20 shadow-inner backdrop-blur-md">
            <FileText className="h-10 w-10" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight">Leave Application</h1>
          <p className="mt-1 text-sm text-blue-100 opacity-90 font-medium">Remote Leave Request</p>
        </div>

        <div className="p-8">
          {step === 1 && (
            <div className="space-y-6 animate-in fade-in slide-in-from-bottom-4 duration-500">
              <div className="space-y-2">
                <label className="text-xs font-bold uppercase tracking-wider text-[#4a6d8c]">Employee ID</label>
                <div className="relative">
                  <User className="absolute left-3 top-1/2 -translate-y-1/2 h-5 w-5 text-[#94a3b8]" />
                  <input
                    type="text"
                    value={code}
                    onChange={(e) => setCode(e.target.value)}
                    placeholder="Enter your ID..."
                    className="w-full rounded-2xl border border-[#cbd5e1] bg-slate-50 py-3.5 pl-10 pr-4 text-base font-bold text-[#1e293b] outline-none ring-[#bfdbfe] transition-all focus:border-[#3b82f6] focus:ring-4"
                    onKeyDown={(e) => e.key === "Enter" && verifyCode()}
                  />
                </div>
              </div>

              {error && <p className="text-sm font-bold text-red-500 bg-red-50 p-3 rounded-xl border border-red-100 text-center">{error}</p>}

              <button
                onClick={verifyCode}
                disabled={loading || !code.trim()}
                className="w-full flex items-center justify-center gap-2 rounded-2xl bg-[#1e293b] py-4 text-base font-bold text-white shadow-xl shadow-slate-900/10 transition-all hover:bg-slate-800 active:scale-95 disabled:opacity-50"
              >
                {loading ? <Loader2 className="h-5 w-5 animate-spin" /> : <>Verify Identity <ChevronRight className="h-5 w-5" /></>}
              </button>
            </div>
          )}

          {step === 2 && emp && (
            <div className="space-y-5 animate-in fade-in slide-in-from-right-4 duration-500">
              <div className="rounded-2xl bg-blue-50 border border-blue-100 p-4">
                <p className="text-[10px] font-bold uppercase tracking-widest text-blue-600">Identity Verified</p>
                <p className="mt-1 text-lg font-bold text-blue-900">{emp.fullName}</p>
                <p className="text-xs font-medium text-blue-700">{emp.department} • {emp.employeeCode}</p>
              </div>

              <div className="space-y-1.5">
                <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">Leave Type</label>
                <div className="relative">
                  <Type className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
                  <select
                    value={leaveTypeId}
                    onChange={(e) => setLeaveTypeId(e.target.value)}
                    className="w-full rounded-xl border border-[#cbd5e1] bg-slate-50 py-3 pl-10 pr-4 text-sm font-bold text-[#1e293b] outline-none"
                  >
                    {leaveTypes.map(t => (
                      <option key={t.Id} value={t.Id}>{t.LeaveName}</option>
                    ))}
                  </select>
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-1.5">
                  <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">From Date</label>
                  <input
                    type="date"
                    value={fromDate}
                    onChange={(e) => setFromDate(e.target.value)}
                    className="w-full rounded-xl border border-[#cbd5e1] bg-slate-50 p-3 text-sm font-bold text-[#1e293b] outline-none"
                  />
                </div>
                <div className="space-y-1.5">
                  <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">To Date</label>
                  <input
                    type="date"
                    value={toDate}
                    onChange={(e) => setToDate(e.target.value)}
                    className="w-full rounded-xl border border-[#cbd5e1] bg-slate-50 p-3 text-sm font-bold text-[#1e293b] outline-none"
                  />
                </div>
              </div>

              <div className="space-y-1.5">
                <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">Reason *</label>
                <textarea
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  placeholder="Reason for leave request..."
                  rows={3}
                  className="w-full rounded-xl border border-[#cbd5e1] bg-slate-50 p-3 text-sm font-medium text-[#1e293b] outline-none focus:border-[#3b82f6]"
                />
              </div>

              {error && <p className="text-sm font-bold text-red-500 bg-red-50 p-3 rounded-xl border border-red-100 text-center">{error}</p>}

              <div className="flex gap-3">
                <button onClick={() => setStep(1)} className="flex-1 rounded-2xl border border-[#cbd5e1] py-4 text-sm font-bold text-slate-600 transition hover:bg-slate-50">Back</button>
                <button
                  onClick={submitLeave}
                  disabled={loading}
                  className="flex-[2] flex items-center justify-center gap-2 rounded-2xl bg-blue-600 py-4 text-base font-bold text-white shadow-xl shadow-blue-900/10 transition-all hover:bg-blue-700 active:scale-95 disabled:opacity-50"
                >
                  {loading ? <Loader2 className="h-5 w-5 animate-spin" /> : <>Submit Request <Check className="h-5 w-5" /></>}
                </button>
              </div>
            </div>
          )}

          {step === 3 && (
            <div className="py-6 text-center animate-in fade-in zoom-in duration-500">
              <div className="mx-auto mb-6 grid h-20 w-20 place-items-center rounded-full bg-emerald-100 text-emerald-600">
                <CheckCircle2 className="h-12 w-12" />
              </div>
              <h2 className="text-2xl font-bold text-[#1e293b]">Request Submitted</h2>
              <p className="mt-2 text-slate-500 font-medium">Your leave request has been sent for admin approval. You can close this page.</p>
              <button
                onClick={() => { setStep(1); setCode(""); setReason(""); }}
                className="mt-8 w-full rounded-2xl bg-slate-900 py-4 text-base font-bold text-white transition hover:bg-slate-800 active:scale-95"
              >
                Submit Another
              </button>
            </div>
          )}
        </div>

        <div className="bg-slate-50 p-4 border-t border-[#f1f5f9] flex items-center justify-center gap-2">
           <QrCode className="h-3 w-3 text-slate-400" />
           <p className="text-[10px] font-bold text-slate-400 uppercase tracking-widest">Secured PB Terminal</p>
        </div>
      </div>
    </div>
  );
}
