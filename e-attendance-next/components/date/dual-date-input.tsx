"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { CalendarDays, ChevronLeft, ChevronRight } from "lucide-react";
import { adToBs, bsToAd, daysInBsMonth, formatBs, fromAdString, getSupportedBsRange, toAdString, type BsDate } from "@/components/date/bs-converter";
import { useCalendarPreference } from "@/components/date/calendar-context";
import {
  readShiftCalendarSettings,
  SHIFT_CALENDAR_UPDATED_EVENT,
  type HolidayItem,
} from "@/components/data/shift-store";

const BS_MONTHS = ["Baisakh", "Jestha", "Asar", "Shrawan", "Bhadra", "Aswin", "Kartik", "Mangsir", "Poush", "Magh", "Falgun", "Chaitra"];
const AD_MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
const WEEK_LABELS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

type DualDateInputProps = {
  value: string;
  onChange: (adValue: string) => void;
  className?: string;
};

type Cell = {
  adDate: Date;
  inCurrentMonth: boolean;
  bs: BsDate | null;
};

function classNames(...tokens: Array<string | false | null | undefined>) {
  return tokens.filter(Boolean).join(" ");
}

function pad2(value: number) {
  return String(value).padStart(2, "0");
}

export function DualDateInput({ value, onChange, className }: DualDateInputProps) {
  const { calendarType } = useCalendarPreference();
  const [open, setOpen] = useState(false);
  const [popupPos, setPopupPos] = useState<{ top: number; left: number } | null>(null);
  const [portalReady, setPortalReady] = useState(false);
  const [manualValue, setManualValue] = useState("");
  const [manualError, setManualError] = useState<string | null>(null);
  const selectedDate = useMemo(() => fromAdString(value) ?? new Date(), [value]);
  const [displayAd, setDisplayAd] = useState<Date>(new Date(selectedDate.getFullYear(), selectedDate.getMonth(), 1));
  const rootRef = useRef<HTMLDivElement | null>(null);
  const popupRef = useRef<HTMLDivElement | null>(null);
  const supportedBsRange = useMemo(() => getSupportedBsRange(), []);
  const selectedBs = useMemo(() => adToBs(selectedDate), [selectedDate]);
  const displayBs = useMemo(() => adToBs(displayAd), [displayAd]);
  const [holidayRows, setHolidayRows] = useState<HolidayItem[]>(
    () => readShiftCalendarSettings().holidays ?? [],
  );

  useEffect(() => {
    const syncHolidays = () => {
      setHolidayRows(readShiftCalendarSettings().holidays ?? []);
    };
    syncHolidays();
    window.addEventListener("storage", syncHolidays);
    window.addEventListener(SHIFT_CALENDAR_UPDATED_EVENT, syncHolidays);
    return () => {
      window.removeEventListener("storage", syncHolidays);
      window.removeEventListener(SHIFT_CALENDAR_UPDATED_EVENT, syncHolidays);
    };
  }, []);

  const holidayLabelForDate = (adDate: string) => {
    const monthDay = adDate.slice(5);
    const found = holidayRows.find(
      (holiday) =>
        holiday.isActive &&
        holiday.date &&
        (holiday.date === adDate ||
          (holiday.isRecurring && holiday.date.slice(5) === monthDay)),
    );
    return found?.name || null;
  };

  useEffect(() => {
    setDisplayAd(new Date(selectedDate.getFullYear(), selectedDate.getMonth(), 1));
  }, [selectedDate.getFullYear(), selectedDate.getMonth()]);

  useEffect(() => {
    if (calendarType === "bs") {
      setManualValue(formatBs(selectedBs));
    } else {
      setManualValue(toAdString(selectedDate));
    }
    setManualError(null);
  }, [calendarType, selectedBs, selectedDate]);

  useEffect(() => {
    setPortalReady(true);
  }, []);

  useEffect(() => {
    if (!open) return;
    const updatePopupPosition = () => {
      if (!rootRef.current) return;
      const rect = rootRef.current.getBoundingClientRect();
      const popupWidth = 356;
      const viewportPadding = 8;
      const maxLeft = Math.max(viewportPadding, window.innerWidth - popupWidth - viewportPadding);
      const left = Math.min(Math.max(rect.left, viewportPadding), maxLeft);
      const spaceBelow = window.innerHeight - rect.bottom;
      const estimatedHeight = 390;
      const openUpward = spaceBelow < estimatedHeight && rect.top > estimatedHeight;
      const top = openUpward ? Math.max(viewportPadding, rect.top - estimatedHeight - 6) : (rect.bottom + 6);
      setPopupPos({ top, left });
    };
    updatePopupPosition();
    const onDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (rootRef.current?.contains(target)) return;
      if (popupRef.current?.contains(target)) return;
      setOpen(false);
    };
    const onEsc = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(false);
    };
    const onWindowChange = () => updatePopupPosition();
    window.addEventListener("mousedown", onDown);
    window.addEventListener("keydown", onEsc);
    window.addEventListener("resize", onWindowChange);
    window.addEventListener("scroll", onWindowChange, true);
    return () => {
      window.removeEventListener("mousedown", onDown);
      window.removeEventListener("keydown", onEsc);
      window.removeEventListener("resize", onWindowChange);
      window.removeEventListener("scroll", onWindowChange, true);
    };
  }, [open]);

  const supportedAdRange = useMemo(() => {
    const minAd = bsToAd(supportedBsRange.minYear, 1, 1);
    const maxDay = daysInBsMonth(supportedBsRange.maxYear, 12);
    const maxAd = bsToAd(supportedBsRange.maxYear, 12, maxDay);
    const minYear = minAd?.getFullYear() ?? 1944;
    const maxYear = maxAd?.getFullYear() ?? 2090;
    return { minYear, maxYear };
  }, [supportedBsRange.maxYear, supportedBsRange.minYear]);

  const bsYearOptions = useMemo(
    () => Array.from({ length: supportedBsRange.maxYear - supportedBsRange.minYear + 1 }, (_, index) => supportedBsRange.minYear + index),
    [supportedBsRange.maxYear, supportedBsRange.minYear],
  );
  const adYearOptions = useMemo(
    () => Array.from({ length: supportedAdRange.maxYear - supportedAdRange.minYear + 1 }, (_, index) => supportedAdRange.minYear + index),
    [supportedAdRange.maxYear, supportedAdRange.minYear],
  );

  const headerLabel = useMemo(() => {
    if (calendarType === "ad") return displayAd.toLocaleString(undefined, { month: "long", year: "numeric" });
    if (!displayBs) return displayAd.toLocaleString(undefined, { month: "long", year: "numeric" });
    return `${BS_MONTHS[displayBs.month - 1]} ${displayBs.year}`;
  }, [calendarType, displayAd, displayBs]);

  const cells = useMemo(() => {
    const result: Cell[] = [];
    if (calendarType === "ad") {
      const first = new Date(displayAd.getFullYear(), displayAd.getMonth(), 1);
      const start = new Date(first);
      start.setDate(first.getDate() - first.getDay());
      for (let i = 0; i < 42; i += 1) {
        const ad = new Date(start);
        ad.setDate(start.getDate() + i);
        result.push({ adDate: ad, inCurrentMonth: ad.getMonth() === first.getMonth(), bs: adToBs(ad) });
      }
      return result;
    }

    if (!displayBs) return result;
    const firstAd = bsToAd(displayBs.year, displayBs.month, 1);
    if (!firstAd) return result;
    const start = new Date(firstAd);
    start.setDate(firstAd.getDate() - firstAd.getDay());
    for (let i = 0; i < 42; i += 1) {
      const ad = new Date(start);
      ad.setDate(start.getDate() + i);
      const bs = adToBs(ad);
      result.push({
        adDate: ad,
        inCurrentMonth: Boolean(bs && bs.year === displayBs.year && bs.month === displayBs.month),
        bs,
      });
    }
    return result;
  }, [calendarType, displayAd, displayBs]);

  const moveMonth = (delta: number) => {
    if (calendarType === "ad") {
      setDisplayAd((prev) => new Date(prev.getFullYear(), prev.getMonth() + delta, 1));
      return;
    }
    if (!displayBs) return;
    let y = displayBs.year;
    let m = displayBs.month + delta;
    while (m < 1) {
      y -= 1;
      m += 12;
    }
    while (m > 12) {
      y += 1;
      m -= 12;
    }
    const target = bsToAd(y, m, 1);
    if (target) setDisplayAd(target);
  };

  const applyManualDate = () => {
    const fallbackValue = calendarType === "bs" ? formatBs(selectedBs) : toAdString(selectedDate);
    const clean = manualValue.trim().replaceAll("/", "-");
    const [yRaw, mRaw, dRaw] = clean.split("-");
    const year = Number.parseInt(yRaw ?? "", 10);
    const month = Number.parseInt(mRaw ?? "", 10);
    const day = Number.parseInt(dRaw ?? "", 10);
    if (!clean || !Number.isFinite(year) || !Number.isFinite(month) || !Number.isFinite(day) || !yRaw || !mRaw || !dRaw) {
      setManualValue(fallbackValue);
      setManualError("Use YYYY-MM-DD format.");
      return;
    }

    let ad: Date | null = null;
    if (calendarType === "bs") {
      ad = bsToAd(year, month, day);
      if (!ad) {
        setManualValue(fallbackValue);
        setManualError("Invalid BS date.");
        return;
      }
    } else {
      if (month < 1 || month > 12 || day < 1 || day > 31) {
        setManualValue(fallbackValue);
        setManualError("Invalid AD date.");
        return;
      }
      const candidate = new Date(year, month - 1, day);
      if (candidate.getFullYear() !== year || candidate.getMonth() !== month - 1 || candidate.getDate() !== day) {
        setManualValue(fallbackValue);
        setManualError("Invalid AD date.");
        return;
      }
      ad = candidate;
    }

    onChange(toAdString(ad));
    setDisplayAd(new Date(ad.getFullYear(), ad.getMonth(), 1));
    setManualError(null);
  };

  const setMonth = (month: number) => {
    if (calendarType === "ad") {
      setDisplayAd((prev) => new Date(prev.getFullYear(), month - 1, 1));
      return;
    }
    if (!displayBs) return;
    const target = bsToAd(displayBs.year, month, 1);
    if (target) setDisplayAd(target);
  };

  const setYear = (year: number) => {
    if (calendarType === "ad") {
      setDisplayAd((prev) => new Date(year, prev.getMonth(), 1));
      return;
    }
    if (!displayBs) return;
    const target = bsToAd(year, displayBs.month, 1);
    if (target) setDisplayAd(target);
  };

  return (
    <div ref={rootRef} className={classNames("relative min-w-0 w-full", className)}>
      <div className="flex w-full items-center gap-2 overflow-hidden rounded-lg border border-[#bfd4ea] bg-[linear-gradient(120deg,#ffffff,#f7fbff)] px-2 py-1.5">
        <input
          type="text"
          value={manualValue}
          maxLength={10}
          onChange={(event) => {
            let val = event.target.value.replace(/[^0-9/-]/g, "").replace(/\//g, "-");
            val = val.replace(/--+/g, "-");
            if (val.length > 10) {
              val = val.slice(0, 10);
            }
            setManualValue(val);
            if (manualError) setManualError(null);
          }}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              applyManualDate();
            }
          }}
          onBlur={applyManualDate}
          placeholder="YYYY-MM-DD"
          className="min-w-0 flex-1 border-0 bg-transparent px-1 py-1 text-sm text-[#1e4162] outline-none"
          aria-label="Date input"
        />
        <button
          type="button"
          onClick={() => setOpen((prev) => !prev)}
          className="inline-flex shrink-0 items-center justify-center rounded-md border border-[#c8dbf0] bg-white p-1.5 text-[#1e4f78] hover:bg-[#eef6ff]"
          aria-label="Open calendar"
        >
          <CalendarDays className="h-4 w-4" />
        </button>
      </div>
      {manualError ? <div className="mt-1 text-[10px] text-[#be2f2f]">{manualError}</div> : null}

      {open && portalReady ? createPortal(
        <div
          ref={popupRef}
          className="fixed z-[9999] w-[356px] rounded-xl border border-[#bfd4ea] bg-white p-3 shadow-[0_20px_34px_-22px_rgba(18,54,88,0.85)]"
          style={{
            top: popupPos?.top ?? 0,
            left: popupPos?.left ?? 0,
          }}
        >
          <div className="mb-2 rounded-lg bg-[linear-gradient(120deg,#e9f5ff,#eef5ff,#f2f9ff)] p-2">
            <div className="mb-2 flex items-center justify-between">
              <button type="button" onClick={() => moveMonth(-1)} className="rounded-md border border-[#c8dbf0] bg-white p-1 text-[#1e4f78]">
                <ChevronLeft className="h-4 w-4" />
              </button>
              <div className="text-sm font-semibold text-[#11395e]">{headerLabel}</div>
              <button type="button" onClick={() => moveMonth(1)} className="rounded-md border border-[#c8dbf0] bg-white p-1 text-[#1e4f78]">
                <ChevronRight className="h-4 w-4" />
              </button>
            </div>
            <div className="grid grid-cols-2 gap-2">
              <select
                value={calendarType === "ad" ? displayAd.getMonth() + 1 : (displayBs?.month ?? 1)}
                onChange={(event) => setMonth(Number.parseInt(event.target.value, 10))}
                className="rounded-lg border border-[#c8dbf0] bg-white px-2 py-1.5 text-xs text-[#1e4f78]"
              >
                {(calendarType === "ad" ? AD_MONTHS : BS_MONTHS).map((month, index) => (
                  <option key={month} value={index + 1}>
                    {month}
                  </option>
                ))}
              </select>
              <select
                value={calendarType === "ad" ? displayAd.getFullYear() : (displayBs?.year ?? supportedBsRange.minYear)}
                onChange={(event) => setYear(Number.parseInt(event.target.value, 10))}
                className="rounded-lg border border-[#c8dbf0] bg-white px-2 py-1.5 text-xs text-[#1e4f78]"
              >
                {(calendarType === "ad" ? adYearOptions : bsYearOptions).map((year) => (
                  <option key={year} value={year}>
                    {year}
                  </option>
                ))}
              </select>
            </div>
          </div>

          <div className="grid grid-cols-7 gap-1">
            {WEEK_LABELS.map((w, i) => (
              <div key={w} className={classNames("px-1 pb-1 text-center text-[10px] font-semibold", i === 6 ? "text-[#cf4747]" : "text-[#4a6d8d]")}>
                {w}
              </div>
            ))}
            {cells.map((cell) => {
              const adKey = toAdString(cell.adDate);
              const isSelected = adKey === toAdString(selectedDate);
              const isToday = adKey === toAdString(new Date());
              const isWeeklyOff = cell.adDate.getDay() === 6;
              const holidayName = holidayLabelForDate(adKey);
              const isHoliday = Boolean(holidayName);
              const primary = calendarType === "bs" ? String(cell.bs?.day ?? "--") : String(cell.adDate.getDate());
              const secondary = calendarType === "bs"
                ? `${pad2(cell.adDate.getMonth() + 1)}-${pad2(cell.adDate.getDate())}`
                : (cell.bs ? `${pad2(cell.bs.month)}-${pad2(cell.bs.day)}` : "--");

              return (
                <button
                  key={`${adKey}_${primary}`}
                  type="button"
                  onClick={() => {
                    onChange(adKey);
                    setOpen(false);
                  }}
                  className={classNames(
                    "flex h-14 flex-col rounded-md border px-1 py-1 text-left transition-colors overflow-hidden",
                    isSelected ? "border-[#1f5c8f] bg-[#e6f1ff]" : "border-[#e2edf8] bg-white hover:bg-[#f4f9ff]",
                    !cell.inCurrentMonth && "opacity-45",
                    isToday && !isSelected && "ring-1 ring-[#0f8a45]",
                  )}
                  title={holidayName ?? ""}
                >
                  <div className={classNames("text-sm font-semibold leading-none", isHoliday || isWeeklyOff ? "text-[#be2f2f]" : "text-[#11395e]")}>
                    {primary}
                  </div>
                  <div className="mt-auto text-right text-[9.5px] font-medium leading-none text-[#5f7f9f] tabular-nums whitespace-nowrap tracking-tight">
                    {secondary}
                  </div>
                </button>
              );
            })}
          </div>
          <div className="mt-2 text-[10px] text-[#5f7f9f]">Sunday start. Weekly-off and holidays are shown in red.</div>
        </div>,
        document.body,
      ) : null}
    </div>
  );
}

export function BsDateField({ value, onChange, className }: DualDateInputProps) {
  return <DualDateInput value={value} onChange={onChange} className={className} />;
}

export function defaultTodayAdString() {
  return toAdString(new Date());
}

export function shiftDateByDays(adValue: string, days: number): string {
  const parsed = fromAdString(adValue);
  if (!parsed) return adValue;
  parsed.setDate(parsed.getDate() + days);
  return toAdString(parsed);
}

export function clampBsDay(bsYear: number, bsMonth: number, day: number) {
  return Math.max(1, Math.min(daysInBsMonth(bsYear, bsMonth), day));
}
