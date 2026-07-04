"use client";

import { createContext, useContext, useEffect, useMemo, useState } from "react";
import type { CalendarType } from "@/components/date/bs-converter";

type CalendarContextValue = {
  calendarType: CalendarType;
  setCalendarType: (value: CalendarType) => void;
};

const CalendarContext = createContext<CalendarContextValue | null>(null);
export const CALENDAR_STORAGE_KEY = "pb_attendance_calendar_type";
export const CALENDAR_PREF_EVENT = "pb-calendar-type-changed";

export function CalendarProvider({ children }: { children: React.ReactNode }) {
  const [calendarType, setCalendarTypeState] = useState<CalendarType>("bs");

  useEffect(() => {
    try {
      const saved = window.localStorage.getItem(CALENDAR_STORAGE_KEY);
      if (saved === "ad" || saved === "bs") {
        setCalendarTypeState(saved);
      }
    } catch {
      // Ignore localStorage issues.
    }
  }, []);

  const setCalendarType = (value: CalendarType) => {
    setCalendarTypeState(value);
    try {
      window.localStorage.setItem(CALENDAR_STORAGE_KEY, value);
      window.dispatchEvent(new CustomEvent(CALENDAR_PREF_EVENT, { detail: value }));
    } catch {
      // Ignore localStorage issues.
    }
  };

  const value = useMemo(() => ({ calendarType, setCalendarType }), [calendarType]);
  return <CalendarContext.Provider value={value}>{children}</CalendarContext.Provider>;
}

export function useCalendarPreference() {
  const ctx = useContext(CalendarContext);
  if (!ctx) {
    throw new Error("useCalendarPreference must be used inside CalendarProvider.");
  }
  return ctx;
}
