"use client";

import { adToBs, bsToAd, daysInBsMonth, toAdString, type CalendarType } from "@/components/date/bs-converter";

type CalendarYearRange = {
  start: string;
  end: string;
};

export function resolveCalendarYearRange(referenceDate: Date, calendarType: CalendarType): CalendarYearRange {
  if (calendarType === "ad") {
    const year = referenceDate.getFullYear();
    return {
      start: `${year}-01-01`,
      end: `${year}-12-31`,
    };
  }

  const bs = adToBs(referenceDate);
  if (!bs) {
    const year = referenceDate.getFullYear();
    return {
      start: `${year}-01-01`,
      end: `${year}-12-31`,
    };
  }

  const startAd = bsToAd(bs.year, 1, 1);
  const endAd = bsToAd(bs.year, 12, daysInBsMonth(bs.year, 12));
  if (!startAd || !endAd) {
    const year = referenceDate.getFullYear();
    return {
      start: `${year}-01-01`,
      end: `${year}-12-31`,
    };
  }

  return {
    start: toAdString(startAd),
    end: toAdString(endAd),
  };
}
