import bsMap from "@/components/date/bs-map.json";

export type CalendarType = "ad" | "bs";
export type BsDate = { year: number; month: number; day: number; dayOfWeek: number };

const EPOCH_AD_UTC_MS = Date.UTC(1943, 3, 13);
const MIN_DAY = 1;

const orderedYears = Object.keys(bsMap)
  .map((x) => Number.parseInt(x, 10))
  .filter((x) => Number.isFinite(x))
  .sort((a, b) => a - b);

const yearMonthDays = orderedYears.map((year) => (bsMap as Record<string, number[]>)[String(year)]);
const yearDaysMapping: Array<[number, number]> = [];
const monthDaysMappings: Array<Array<[number, number]>> = [];

let daysPassedTotal = 0;
for (const months of yearMonthDays) {
  const yearDays = months.reduce((acc, d) => acc + d, 0);
  yearDaysMapping.push([yearDays, daysPassedTotal]);

  let monthPassed = 0;
  const monthRows: Array<[number, number]> = [];
  for (const d of months) {
    monthRows.push([d, monthPassed]);
    monthPassed += d;
  }
  monthDaysMappings.push(monthRows);
  daysPassedTotal += yearDays;
}

const MAX_DAY = daysPassedTotal;
const yearIndexByYear = new Map<number, number>(orderedYears.map((year, i) => [year, i]));

export function toAdString(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, "0");
  const d = String(date.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

export function fromAdString(value: string): Date | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const d = new Date(`${value}T00:00:00`);
  return Number.isNaN(d.getTime()) ? null : d;
}

export function getSupportedBsRange() {
  return {
    minYear: orderedYears[0] ?? 2000,
    maxYear: orderedYears[orderedYears.length - 1] ?? 2090,
  };
}

export function adToBs(adDate: Date): BsDate | null {
  const local = new Date(adDate.getFullYear(), adDate.getMonth(), adDate.getDate());
  const daysPassed = Math.floor((Date.UTC(local.getFullYear(), local.getMonth(), local.getDate()) - EPOCH_AD_UTC_MS) / 86400000);
  if (daysPassed < MIN_DAY || daysPassed > MAX_DAY) return null;

  const yearIndex = yearDaysMapping.findIndex(
    ([total, passed]) => daysPassed > passed && daysPassed <= passed + total,
  );
  if (yearIndex < 0) return null;
  const monthRemainder = daysPassed - yearDaysMapping[yearIndex][1];
  const monthIndex = monthDaysMappings[yearIndex].findIndex(
    ([total, passed]) => monthRemainder > passed && monthRemainder <= passed + total,
  );
  if (monthIndex < 0) return null;
  const day = monthRemainder - monthDaysMappings[yearIndex][monthIndex][1];

  return {
    year: orderedYears[yearIndex],
    month: monthIndex + 1,
    day,
    dayOfWeek: local.getDay(),
  };
}

export function bsToAd(bsYear: number, bsMonth: number, bsDay: number): Date | null {
  const yearIndex = yearIndexByYear.get(bsYear);
  if (yearIndex === undefined) return null;
  if (bsMonth < 1 || bsMonth > 12) return null;

  const monthIndex = bsMonth - 1;
  const monthDays = yearMonthDays[yearIndex][monthIndex];
  if (bsDay < 1 || bsDay > monthDays) return null;

  const daysPassed =
    yearDaysMapping[yearIndex][1] +
    monthDaysMappings[yearIndex][monthIndex][1] +
    bsDay;

  if (daysPassed < MIN_DAY || daysPassed > MAX_DAY) return null;
  const dt = new Date(EPOCH_AD_UTC_MS + daysPassed * 86400000);
  return new Date(dt.getUTCFullYear(), dt.getUTCMonth(), dt.getUTCDate());
}

export function daysInBsMonth(bsYear: number, bsMonth: number): number {
  const yearIndex = yearIndexByYear.get(bsYear);
  if (yearIndex === undefined || bsMonth < 1 || bsMonth > 12) return 30;
  return yearMonthDays[yearIndex][bsMonth - 1] ?? 30;
}

export function formatBs(bs: BsDate | null): string {
  if (!bs) return "BS n/a";
  return `${bs.year}-${String(bs.month).padStart(2, "0")}-${String(bs.day).padStart(2, "0")}`;
}

