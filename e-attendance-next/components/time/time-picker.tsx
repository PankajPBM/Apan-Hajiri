"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Clock } from "lucide-react";

export type TimePickerProps = {
  value?: string; // 24-hour format: "HH:mm" or "HH:mm:ss"
  onChange: (time24: string) => void;
  placeholder?: string;
  className?: string;
  inputClassName?: string;
  disabled?: boolean;
};

const HOURS_12 = ["01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12"];
const MINUTES_60 = Array.from({ length: 60 }, (_, i) => String(i).padStart(2, "0"));
const PERIODS = ["AM", "PM"] as const;

function pad2(n: number): string {
  return String(n).padStart(2, "0");
}

export function parse24HourTo12Hour(time24?: string): { hour12: string; minute: string; period: "AM" | "PM" } {
  if (!time24 || typeof time24 !== "string") {
    return { hour12: "09", minute: "00", period: "AM" };
  }
  const match = time24.trim().match(/^(\d{1,2}):(\d{1,2})/);
  if (!match) {
    return { hour12: "09", minute: "00", period: "AM" };
  }
  let h = parseInt(match[1], 10);
  const m = Math.min(59, Math.max(0, parseInt(match[2], 10)));
  const period: "AM" | "PM" = h >= 12 ? "PM" : "AM";
  if (h === 0) {
    h = 12;
  } else if (h > 12) {
    h = h - 12;
  }
  return {
    hour12: pad2(h),
    minute: pad2(m),
    period,
  };
}

export function to24HourString(hour12: number | string, minute: number | string, period: "AM" | "PM"): string {
  let h = typeof hour12 === "string" ? parseInt(hour12, 10) : hour12;
  const m = typeof minute === "string" ? parseInt(minute, 10) : minute;
  if (period === "AM") {
    h = h === 12 ? 0 : h;
  } else {
    h = h === 12 ? 12 : h + 12;
  }
  return `${pad2(h)}:${pad2(m)}`;
}

export function format12HourDisplay(time24?: string): string {
  const { hour12, minute, period } = parse24HourTo12Hour(time24);
  return `${hour12}:${minute} ${period}`;
}

export function parseInputTo24Hour(text: string): string | null {
  const clean = text.trim().toUpperCase();
  if (!clean) return null;

  // Pattern: "09:00 AM" or "9:00 PM" or "9:00AM" or "9:00P"
  const ampmMatch = clean.match(/^(\d{1,2}):(\d{1,2})\s*(AM|PM|A|P)?$/i);
  if (ampmMatch) {
    let h = parseInt(ampmMatch[1], 10);
    const m = Math.min(59, Math.max(0, parseInt(ampmMatch[2], 10)));
    const rawPeriod = (ampmMatch[3] || "").toUpperCase();

    if (rawPeriod.startsWith("P")) {
      h = h === 12 ? 12 : h < 12 ? h + 12 : h;
    } else if (rawPeriod.startsWith("A")) {
      h = h === 12 ? 0 : h;
    }
    h = Math.min(23, Math.max(0, h));
    return `${pad2(h)}:${pad2(m)}`;
  }

  // Pattern: "14:30" (24-hour raw)
  const raw24Match = clean.match(/^(\d{1,2}):(\d{1,2})$/);
  if (raw24Match) {
    const h = Math.min(23, Math.max(0, parseInt(raw24Match[1], 10)));
    const m = Math.min(59, Math.max(0, parseInt(raw24Match[2], 10)));
    return `${pad2(h)}:${pad2(m)}`;
  }

  // Pattern: "0930 AM" or "0930"
  const digits4Match = clean.match(/^(\d{2})(\d{2})\s*(AM|PM|A|P)?$/i);
  if (digits4Match) {
    let h = parseInt(digits4Match[1], 10);
    const m = Math.min(59, Math.max(0, parseInt(digits4Match[2], 10)));
    const rawPeriod = (digits4Match[3] || "").toUpperCase();
    if (rawPeriod.startsWith("P")) {
      h = h === 12 ? 12 : h < 12 ? h + 12 : h;
    } else if (rawPeriod.startsWith("A")) {
      h = h === 12 ? 0 : h;
    }
    h = Math.min(23, Math.max(0, h));
    return `${pad2(h)}:${pad2(m)}`;
  }

  // Pattern: "9 AM" or "9PM"
  const simpleMatch = clean.match(/^(\d{1,2})\s*(AM|PM|A|P)$/i);
  if (simpleMatch) {
    let h = parseInt(simpleMatch[1], 10);
    const rawPeriod = simpleMatch[2].toUpperCase();
    if (rawPeriod.startsWith("P")) {
      h = h === 12 ? 12 : h < 12 ? h + 12 : h;
    } else {
      h = h === 12 ? 0 : h;
    }
    h = Math.min(23, Math.max(0, h));
    return `${pad2(h)}:00`;
  }

  return null;
}

export function TimePicker({
  value = "09:00",
  onChange,
  placeholder = "HH:mm AM/PM",
  className = "",
  inputClassName = "",
  disabled = false,
}: TimePickerProps) {
  const [open, setOpen] = useState(false);
  const [portalReady, setPortalReady] = useState(false);
  const [popupPos, setPopupPos] = useState<{ top: number; left: number } | null>(null);

  const containerRef = useRef<HTMLDivElement | null>(null);
  const popupRef = useRef<HTMLDivElement | null>(null);
  const hourListRef = useRef<HTMLDivElement | null>(null);
  const minuteListRef = useRef<HTMLDivElement | null>(null);

  const { hour12, minute, period } = useMemo(() => parse24HourTo12Hour(value), [value]);

  const [typedValue, setTypedValue] = useState(() => format12HourDisplay(value));

  // Sync typedValue when external value changes
  useEffect(() => {
    setTypedValue(format12HourDisplay(value));
  }, [value]);

  useEffect(() => {
    setPortalReady(true);
  }, []);

  const updatePopupPosition = () => {
    if (!containerRef.current) return;
    const rect = containerRef.current.getBoundingClientRect();
    const popupWidth = 196;
    const viewportPadding = 8;
    const maxLeft = Math.max(viewportPadding, window.innerWidth - popupWidth - viewportPadding);
    const left = Math.min(Math.max(rect.left, viewportPadding), maxLeft);
    const spaceBelow = window.innerHeight - rect.bottom;
    const estimatedHeight = 260;
    const openUpward = spaceBelow < estimatedHeight && rect.top > estimatedHeight;
    const top = openUpward ? Math.max(viewportPadding, rect.top - estimatedHeight - 4) : rect.bottom + 4;
    setPopupPos({ top, left });
  };

  useEffect(() => {
    if (!open) return;
    updatePopupPosition();

    // Auto-scroll selected hour and minute into view
    const timer = setTimeout(() => {
      if (hourListRef.current) {
        const selectedHourEl = hourListRef.current.querySelector('[data-selected="true"]') as HTMLElement | null;
        if (selectedHourEl) {
          hourListRef.current.scrollTop = selectedHourEl.offsetTop - 70;
        }
      }
      if (minuteListRef.current) {
        const selectedMinEl = minuteListRef.current.querySelector('[data-selected="true"]') as HTMLElement | null;
        if (selectedMinEl) {
          minuteListRef.current.scrollTop = selectedMinEl.offsetTop - 70;
        }
      }
    }, 50);

    const onMouseDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (containerRef.current?.contains(target)) return;
      if (popupRef.current?.contains(target)) return;
      setOpen(false);
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(false);
    };

    const onScrollOrResize = () => updatePopupPosition();

    window.addEventListener("mousedown", onMouseDown);
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("resize", onScrollOrResize);
    window.addEventListener("scroll", onScrollOrResize, true);

    return () => {
      clearTimeout(timer);
      window.removeEventListener("mousedown", onMouseDown);
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("resize", onScrollOrResize);
      window.removeEventListener("scroll", onScrollOrResize, true);
    };
  }, [open]);

  const commitManualInput = () => {
    const parsed = parseInputTo24Hour(typedValue);
    if (parsed) {
      onChange(parsed);
      setTypedValue(format12HourDisplay(parsed));
    } else {
      // Revert to current valid value
      setTypedValue(format12HourDisplay(value));
    }
  };

  const handleSelectHour = (h: string) => {
    const next24 = to24HourString(h, minute, period);
    onChange(next24);
  };

  const handleSelectMinute = (m: string) => {
    const next24 = to24HourString(hour12, m, period);
    onChange(next24);
  };

  const handleSelectPeriod = (p: "AM" | "PM") => {
    const next24 = to24HourString(hour12, minute, p);
    onChange(next24);
  };

  return (
    <div ref={containerRef} className={`relative inline-block w-full min-w-0 ${className}`}>
      <div className="relative flex items-center overflow-hidden rounded-xl border border-slate-300 bg-white transition focus-within:border-blue-500 focus-within:ring-2 focus-within:ring-blue-100">
        <input
          type="text"
          value={typedValue}
          disabled={disabled}
          placeholder={placeholder}
          maxLength={8}
          onChange={(e) => {
            let val = e.target.value.replace(/[^0-9: apmAPM]/g, "");
            if (val.length > 8) {
              val = val.slice(0, 8);
            }
            setTypedValue(val);
          }}
          onFocus={() => {
            if (!open) {
              setOpen(true);
            }
          }}
          onBlur={commitManualInput}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              commitManualInput();
              setOpen(false);
            }
          }}
          className={`w-full min-w-0 border-0 bg-transparent px-3 py-2 text-sm font-semibold tracking-wide text-slate-800 outline-none placeholder:text-slate-400 ${inputClassName}`}
        />
        <button
          type="button"
          disabled={disabled}
          onClick={() => setOpen((prev) => !prev)}
          className="inline-flex h-full shrink-0 items-center justify-center px-2.5 text-slate-400 transition hover:text-blue-600 disabled:opacity-50"
          aria-label="Toggle time picker"
        >
          <Clock className="h-4 w-4" />
        </button>
      </div>

      {open && portalReady && createPortal(
        <div
          ref={popupRef}
          className="fixed z-[99999] flex w-[196px] rounded-xl border border-slate-200 bg-white p-1.5 shadow-[0_18px_36px_-12px_rgba(15,35,60,0.35)] animate-in fade-in zoom-in-95 duration-150 select-none"
          style={{
            top: popupPos?.top ?? 0,
            left: popupPos?.left ?? 0,
          }}
        >
          {/* Column 1: Hours (01 - 12) */}
          <div
            ref={hourListRef}
            className="flex-1 overflow-y-auto max-h-56 pr-0.5 space-y-1 scrollbar-thin scrollbar-thumb-slate-200"
          >
            {HOURS_12.map((h) => {
              const isSelected = h === hour12;
              return (
                <button
                  key={h}
                  type="button"
                  data-selected={isSelected}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => handleSelectHour(h)}
                  className={`w-full py-1.5 text-center text-xs font-bold transition rounded-lg ${
                    isSelected
                      ? "bg-blue-600 text-white shadow-xs"
                      : "text-slate-700 hover:bg-slate-100 hover:text-slate-900"
                  }`}
                >
                  {h}
                </button>
              );
            })}
          </div>

          <div className="w-[1px] bg-slate-100 my-1 mx-1 shrink-0" />

          {/* Column 2: Minutes (00 - 59) */}
          <div
            ref={minuteListRef}
            className="flex-1 overflow-y-auto max-h-56 pr-0.5 space-y-1 scrollbar-thin scrollbar-thumb-slate-200"
          >
            {MINUTES_60.map((m) => {
              const isSelected = m === minute;
              return (
                <button
                  key={m}
                  type="button"
                  data-selected={isSelected}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => handleSelectMinute(m)}
                  className={`w-full py-1.5 text-center text-xs font-bold transition rounded-lg ${
                    isSelected
                      ? "bg-blue-600 text-white shadow-xs"
                      : "text-slate-700 hover:bg-slate-100 hover:text-slate-900"
                  }`}
                >
                  {m}
                </button>
              );
            })}
          </div>

          <div className="w-[1px] bg-slate-100 my-1 mx-1 shrink-0" />

          {/* Column 3: AM / PM */}
          <div className="flex-1 space-y-1">
            {PERIODS.map((p) => {
              const isSelected = p === period;
              return (
                <button
                  key={p}
                  type="button"
                  data-selected={isSelected}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => handleSelectPeriod(p)}
                  className={`w-full py-2 text-center text-xs font-bold transition rounded-lg ${
                    isSelected
                      ? "bg-blue-600 text-white shadow-xs"
                      : "text-slate-700 hover:bg-slate-100 hover:text-slate-900"
                  }`}
                >
                  {p}
                </button>
              );
            })}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

export function TimeField({
  label,
  value,
  onChange,
  className = "",
  disabled = false,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  className?: string;
  disabled?: boolean;
}) {
  return (
    <label className={`grid gap-1 text-xs text-slate-600 ${className}`}>
      <span className="text-xs font-bold text-[#1f4f79]">{label}</span>
      <TimePicker value={value} onChange={onChange} disabled={disabled} />
    </label>
  );
}
