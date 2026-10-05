"use client";

import { useCallback, useState } from "react";
import { Check, CheckCircle2, ChevronRight, Fingerprint, Loader2, QrCode, Search, User } from "lucide-react";
import { resolveApiBases } from "@/lib/api-bases";
import { TimePicker } from "@/components/time/time-picker";

type EmployeeInfo = {
  id: string;
  employeeCode: string;
  fullName: string;
  department: string;
};

export default function QrCheckInPage() {
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [code, setCode] = useState("");
  const [emp, setEmp] = useState<EmployeeInfo | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [date, setDate] = useState(() => new Date().toISOString().split("T")[0]);
  const [time, setTime] = useState(() => new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false }));
  const [statusCode, setStatusCode] = useState(0); // 0 = In, 1 = Out
  const [remarks, setRemarks] = useState("");

  const verifyCode = async () => {
    if (!code.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const bases = resolveApiBases({ includeCurrentOrigin: true });
      let data: EmployeeInfo | null = null;
      for (const base of bases) {
        try {
          const r = await fetch(`${base}/api/personnel/verify/${code.trim()}`);
          if (r.ok) { data = await r.json(); break; }
        } catch { /* ignore */ }
      }
      if (data) {
        setEmp(data);
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

  const submitPunch = async () => {
    setLoading(true);
    setError(null);
    try {
      const bases = resolveApiBases({ includeCurrentOrigin: true });
      let success = false;
      for (const base of bases) {
        try {
          const r = await fetch(`${base}/api/attendance/request`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              employeeCode: code.trim(),
              timestamp: `${date}T${time}:00`,
              statusCode: statusCode,
              remarks: remarks.trim()
            })
          });
          if (r.ok) { success = true; break; }
        } catch { /* ignore */ }
      }
      if (success) {
        setStep(3);
      } else {
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
        <div className="bg-gradient-to-br from-[#1d4ed8] to-[#0284c7] p-8 text-white text-center">
          <div className="mx-auto mb-4 grid h-16 w-16 place-items-center rounded-2xl bg-white/20 shadow-inner backdrop-blur-md">
            <Fingerprint className="h-10 w-10" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight">Check-in Portal</h1>
          <p className="mt-1 text-sm text-blue-100 opacity-90 font-medium">Remote Attendance Entry</p>
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
                {loading ? <Loader2 className="h-5 w-5 animate-spin" /> : <>Verify ID <ChevronRight className="h-5 w-5" /></>}
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

              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-1.5">
                  <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">Punch Date</label>
                  <input
                    type="date"
                    value={date}
                    onChange={(e) => setDate(e.target.value)}
                    className="w-full rounded-xl border border-[#cbd5e1] bg-slate-50 p-3 text-sm font-bold text-[#1e293b] outline-none"
                  />
                </div>
                <div className="space-y-1.5">
                  <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">Punch Time</label>
                  <TimePicker
                    value={time}
                    onChange={(v) => setTime(v)}
                  />
                </div>
              </div>

              <div className="space-y-1.5">
                <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">Punch Type</label>
                <div className="grid grid-cols-2 gap-2">
                  <button
                    onClick={() => setStatusCode(0)}
                    className={`rounded-xl py-2.5 text-xs font-bold transition-all border ${statusCode === 0 ? "bg-blue-600 text-white border-blue-600 shadow-lg shadow-blue-900/10" : "bg-white text-slate-600 border-slate-200 hover:bg-slate-50"}`}
                  >
                    Check-In
                  </button>
                  <button
                    onClick={() => setStatusCode(1)}
                    className={`rounded-xl py-2.5 text-xs font-bold transition-all border ${statusCode === 1 ? "bg-orange-600 text-white border-orange-600 shadow-lg shadow-orange-900/10" : "bg-white text-slate-600 border-slate-200 hover:bg-slate-50"}`}
                  >
                    Check-Out
                  </button>
                </div>
              </div>

              <div className="space-y-1.5">
                <label className="text-[10px] font-bold uppercase tracking-wider text-[#64748b]">Remarks / Note</label>
                <textarea
                  value={remarks}
                  onChange={(e) => setRemarks(e.target.value)}
                  placeholder="Reason for manual check-in..."
                  rows={3}
                  className="w-full rounded-xl border border-[#cbd5e1] bg-slate-50 p-3 text-sm font-medium text-[#1e293b] outline-none focus:border-[#3b82f6]"
                />
              </div>

              {error && <p className="text-sm font-bold text-red-500 bg-red-50 p-3 rounded-xl border border-red-100 text-center">{error}</p>}

              <div className="flex gap-3">
                <button onClick={() => setStep(1)} className="flex-1 rounded-2xl border border-[#cbd5e1] py-4 text-sm font-bold text-slate-600 transition hover:bg-slate-50">Back</button>
                <button
                  onClick={submitPunch}
                  disabled={loading}
                  className="flex-[2] flex items-center justify-center gap-2 rounded-2xl bg-emerald-600 py-4 text-base font-bold text-white shadow-xl shadow-emerald-900/10 transition-all hover:bg-emerald-700 active:scale-95 disabled:opacity-50"
                >
                  {loading ? <Loader2 className="h-5 w-5 animate-spin" /> : <>Request Punch <Check className="h-5 w-5" /></>}
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
              <p className="mt-2 text-slate-500 font-medium">Your punch request has been sent for admin approval. Thank you!</p>
              <button
                onClick={() => { setStep(1); setCode(""); setRemarks(""); }}
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
