"use client";

import { useEffect, useMemo, useState } from "react";
import { CalendarClock, CalendarDays, Plus, RotateCcw, Save, Trash2 } from "lucide-react";
import {
  WEEKDAY_LABELS,
  loadShiftConfigFromApi,
  parseTimeToMinutes,
  readShiftConfigSnapshot,
  saveShiftConfigToApi,
  type BreakTimeDefinition,
  type ShiftConfigSnapshot,
  type TimetableDayRule,
  type TimetableDefinition,
} from "@/components/data/shift-store";

type Section = "timetables" | "breaks";

type TimetableForm = {
  id: string | null;
  name: string;
  isActive: boolean;
  days: TimetableDayRule[];
};

type BreakForm = {
  id: string | null;
  name: string;
  startTime: string;
  endTime: string;
  durationMinutes: number;
  calculateType: BreakTimeDefinition["calculateType"];
  minimumBreakMinutes: number;
  isActive: boolean;
};

const inputClass =
  "w-full rounded-lg border border-[--input] bg-white px-2.5 py-1.5 text-xs outline-none transition focus:border-[#7fb8e5]";

function slugify(value: string) {
  return value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/(^-|-$)/g, "");
}

function nowIso() {
  return new Date().toISOString();
}

function defaultShiftId(snapshot: ShiftConfigSnapshot) {
  return snapshot.shiftDefinitions.find((item) => item.isActive)?.id ?? snapshot.shiftDefinitions[0]?.id ?? "general";
}

function createDay(dayOfWeek: number, shiftId: string): TimetableDayRule {
  return {
    dayOfWeek,
    shiftId: dayOfWeek === 6 ? null : shiftId,
    isWeeklyOff: dayOfWeek === 6,
    checkInTime: "09:00",
    checkInStart: "05:00",
    checkInEnd: "09:15",
    checkOutStart: "15:45",
    checkOutTime: "17:00",
    checkOutEnd: "23:59",
    crossDay: false,
    useBreakTime: false,
    breakTimeId: null,
    breakTimeIds: [],
  };
}

function createTimetableForm(shiftId: string): TimetableForm {
  return {
    id: null,
    name: "",
    isActive: true,
    days: Array.from({ length: 7 }, (_, dayOfWeek) => createDay(dayOfWeek, shiftId)),
  };
}

function createBreakForm(): BreakForm {
  return {
    id: null,
    name: "",
    startTime: "13:00",
    endTime: "14:00",
    durationMinutes: 60,
    calculateType: "auto_deduct",
    minimumBreakMinutes: 0,
    isActive: true,
  };
}

function toTimetableForm(row: TimetableDefinition): TimetableForm {
  return {
    id: row.id,
    name: row.name,
    isActive: row.isActive,
    days: Array.from({ length: 7 }, (_, dayOfWeek) => {
      const day = row.days.find((item) => item.dayOfWeek === dayOfWeek) ?? null;
      return day ?? createDay(dayOfWeek, row.days.find((item) => item.shiftId)?.shiftId ?? "general");
    }),
  };
}

function toBreakForm(row: BreakTimeDefinition): BreakForm {
  return {
    id: row.id,
    name: row.name,
    startTime: row.startTime,
    endTime: row.endTime,
    durationMinutes: row.durationMinutes,
    calculateType: row.calculateType,
    minimumBreakMinutes: row.minimumBreakMinutes,
    isActive: row.isActive,
  };
}

function normalizeDay(day: TimetableDayRule, fallbackShiftId: string): TimetableDayRule {
  const breakTimeIds = Array.from(
    new Set((day.breakTimeIds ?? []).map((value) => value.trim()).filter(Boolean)),
  );
  const breakTimeId = (day.breakTimeId ?? breakTimeIds[0] ?? "").trim() || null;
  return {
    ...day,
    shiftId: day.isWeeklyOff ? null : (day.shiftId?.trim() || fallbackShiftId),
    breakTimeId,
    breakTimeIds,
  };
}

function timeWithinWindow(value: number, start: number, end: number, wrap: boolean) {
  return wrap || end < start ? value >= start || value <= end : value >= start && value <= end;
}

function validateTimetable(form: TimetableForm): string | null {
  if (!form.name.trim()) {
    return "Timetable name is required.";
  }

  const workingDays = form.days.filter((day) => !day.isWeeklyOff);
  if (workingDays.length === 0) {
    return "At least one working day is required.";
  }

  for (const day of workingDays) {
    const requiredTimes = [
      day.checkInTime,
      day.checkInStart,
      day.checkInEnd,
      day.checkOutStart,
      day.checkOutTime,
      day.checkOutEnd,
    ];
    if (requiredTimes.some((value) => !value.trim())) {
      return `${WEEKDAY_LABELS[day.dayOfWeek]} needs all time fields.`;
    }

    const checkIn = parseTimeToMinutes(day.checkInTime);
    const checkInStart = parseTimeToMinutes(day.checkInStart);
    const checkInEnd = parseTimeToMinutes(day.checkInEnd);
    const checkOut = parseTimeToMinutes(day.checkOutTime);
    const checkOutStart = parseTimeToMinutes(day.checkOutStart);
    const checkOutEnd = parseTimeToMinutes(day.checkOutEnd);

    if (!timeWithinWindow(checkIn, checkInStart, checkInEnd, false)) {
      return `${WEEKDAY_LABELS[day.dayOfWeek]} check-in must stay within its window.`;
    }
    if (!timeWithinWindow(checkOut, checkOutStart, checkOutEnd, day.crossDay)) {
      return `${WEEKDAY_LABELS[day.dayOfWeek]} check-out must stay within its window.`;
    }
    if (!day.crossDay && checkOut <= checkIn) {
      return `${WEEKDAY_LABELS[day.dayOfWeek]} check-out must be later than check-in or enable cross day.`;
    }
    if (day.useBreakTime && day.breakTimeIds.length === 0) {
      return `${WEEKDAY_LABELS[day.dayOfWeek]} needs a break profile.`;
    }
  }

  return null;
}

function validateBreak(form: BreakForm): string | null {
  if (!form.name.trim()) {
    return "Break name is required.";
  }
  if (!form.startTime.trim() || !form.endTime.trim()) {
    return "Break start and end times are required.";
  }
  if (form.durationMinutes < 0) {
    return "Break duration cannot be negative.";
  }
  return null;
}

function summarizeTimetable(row: TimetableDefinition, breakMap: Map<string, string>) {
  const workingDays = row.days.filter((day) => !day.isWeeklyOff);
  const first = workingDays[0];
  if (!first) {
    return "No working days configured.";
  }

  const shiftName = first.shiftId ? `Shift: ${first.shiftId}` : "Shift: default";
  const breakName = first.useBreakTime
    ? `Break: ${(first.breakTimeIds.length ? first.breakTimeIds : first.breakTimeId ? [first.breakTimeId] : [])
        .map((id) => breakMap.get(id) ?? id)
        .filter(Boolean)
        .join(", ")}`
    : "Break: none";

  return `${shiftName} | ${breakName} | Working days: ${workingDays.length}`;
}

function summarizeBreak(row: BreakTimeDefinition) {
  return `${row.startTime} - ${row.endTime} | ${row.durationMinutes} min | ${row.calculateType === "required_punch" ? "Required punch" : "Auto deduct"}`;
}

export default function TimetablePage() {
  const [snapshot, setSnapshot] = useState<ShiftConfigSnapshot>(() => readShiftConfigSnapshot());
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [section, setSection] = useState<Section>("timetables");
  const [timetableForm, setTimetableForm] = useState<TimetableForm>(() => createTimetableForm("general"));
  const [breakForm, setBreakForm] = useState<BreakForm>(() => createBreakForm());

  useEffect(() => {
    let cancelled = false;

    const run = async () => {
      setLoading(true);
      try {
        const local = readShiftConfigSnapshot();
        if (!cancelled) {
          setSnapshot(local);
        }
        const remote = await loadShiftConfigFromApi();
        if (!cancelled) {
          setSnapshot(remote);
        }
      } catch {
        if (!cancelled) {
          setSnapshot(readShiftConfigSnapshot());
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void run();
    return () => {
      cancelled = true;
    };
  }, []);

  const fallbackShiftId = useMemo(() => defaultShiftId(snapshot), [snapshot]);
  const breakMap = useMemo(() => new Map(snapshot.breakDefinitions.map((item) => [item.id, item.name])), [snapshot.breakDefinitions]);

  useEffect(() => {
    setTimetableForm((current) => (current.id ? current : createTimetableForm(fallbackShiftId)));
  }, [fallbackShiftId]);

  const openTimetable = (row?: TimetableDefinition) => {
    setTimetableForm(row ? toTimetableForm(row) : createTimetableForm(fallbackShiftId));
    setSection("timetables");
    setError(null);
    setMessage(null);
  };

  const openBreak = (row?: BreakTimeDefinition) => {
    setBreakForm(row ? toBreakForm(row) : createBreakForm());
    setSection("breaks");
    setError(null);
    setMessage(null);
  };

  const saveTimetable = async () => {
    const validation = validateTimetable(timetableForm);
    if (validation) {
      setError(validation);
      return;
    }

    const now = nowIso();
    const nextRow: TimetableDefinition = {
      id:
        timetableForm.id ??
        (slugify(timetableForm.name) || `timetable-${Date.now()}`),
      name: timetableForm.name.trim(),
      isActive: timetableForm.isActive,
      days: timetableForm.days.map((day) => normalizeDay(day, fallbackShiftId)),
      createdAt:
        snapshot.timetables.find((item) => item.id === timetableForm.id)?.createdAt ?? now,
      updatedAt: now,
    };
    const nextTimetables = snapshot.timetables.some((item) => item.id === nextRow.id)
      ? snapshot.timetables.map((item) => (item.id === nextRow.id ? nextRow : item))
      : [...snapshot.timetables, nextRow];

    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const saved = await saveShiftConfigToApi({ timetables: nextTimetables });
      setSnapshot(saved);
      setTimetableForm(createTimetableForm(defaultShiftId(saved)));
      setMessage(timetableForm.id ? "Timetable updated." : "Timetable created.");
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "Failed to save timetable.");
    } finally {
      setSaving(false);
    }
  };

  const saveBreak = async () => {
    const validation = validateBreak(breakForm);
    if (validation) {
      setError(validation);
      return;
    }

    const now = nowIso();
    const nextRow: BreakTimeDefinition = {
      id: breakForm.id ?? (slugify(breakForm.name) || `break-${Date.now()}`),
      name: breakForm.name.trim(),
      startTime: breakForm.startTime,
      endTime: breakForm.endTime,
      durationMinutes: Math.max(0, Math.round(breakForm.durationMinutes)),
      calculateType: breakForm.calculateType,
      minimumBreakMinutes: Math.max(0, Math.round(breakForm.minimumBreakMinutes)),
      isActive: breakForm.isActive,
    };
    const nextBreaks = snapshot.breakDefinitions.some((item) => item.id === nextRow.id)
      ? snapshot.breakDefinitions.map((item) => (item.id === nextRow.id ? nextRow : item))
      : [...snapshot.breakDefinitions, nextRow];

    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const saved = await saveShiftConfigToApi({ breakDefinitions: nextBreaks });
      setSnapshot(saved);
      setBreakForm(createBreakForm());
      setMessage(breakForm.id ? "Break time updated." : "Break time created.");
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "Failed to save break time.");
    } finally {
      setSaving(false);
    }
  };

  const deleteTimetable = async (id: string) => {
    if (snapshot.timetables.length <= 1) {
      setError("At least one timetable must remain.");
      return;
    }

    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const saved = await saveShiftConfigToApi({ timetables: snapshot.timetables.filter((item) => item.id !== id) });
      setSnapshot(saved);
      if (timetableForm.id === id) {
        setTimetableForm(createTimetableForm(defaultShiftId(saved)));
      }
      setMessage("Timetable deleted.");
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : "Failed to delete timetable.");
    } finally {
      setSaving(false);
    }
  };

  const deleteBreak = async (id: string) => {
    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const saved = await saveShiftConfigToApi({ breakDefinitions: snapshot.breakDefinitions.filter((item) => item.id !== id) });
      setSnapshot(saved);
      if (breakForm.id === id) {
        setBreakForm(createBreakForm());
      }
      setMessage("Break time deleted.");
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : "Failed to delete break time.");
    } finally {
      setSaving(false);
    }
  };

  const activeTimetableCount = snapshot.timetables.filter((item) => item.isActive).length;
  const activeBreakCount = snapshot.breakDefinitions.filter((item) => item.isActive).length;

  return (
    <div className="space-y-4">
      <div className="tab-header">
        <div>
          <h1 className="text-2xl font-semibold text-[#123454]">Time Table</h1>
          <p className="text-sm text-[--muted-foreground]">
            Manage timetable windows and break profiles from the shared shift configuration.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <span className="inline-flex items-center gap-1.5 rounded-xl border border-[#cfe1f2] bg-white px-3 py-1.5 text-xs font-semibold text-[#35658d] shadow-sm"><span className="text-sm font-bold text-[#1b4b73]">{snapshot.timetables.length}</span> Timetables</span>
          <span className="inline-flex items-center gap-1.5 rounded-xl border border-[#cfe1f2] bg-white px-3 py-1.5 text-xs font-semibold text-[#35658d] shadow-sm"><span className="text-sm font-bold text-emerald-600">{activeTimetableCount}</span> Active</span>
          <span className="inline-flex items-center gap-1.5 rounded-xl border border-[#cfe1f2] bg-white px-3 py-1.5 text-xs font-semibold text-[#35658d] shadow-sm"><span className="text-sm font-bold text-[#1b4b73]">{snapshot.breakDefinitions.length}</span> Breaks</span>
          <span className="inline-flex items-center gap-1.5 rounded-xl border border-[#cfe1f2] bg-white px-3 py-1.5 text-xs font-semibold text-[#35658d] shadow-sm"><span className="text-sm font-bold text-emerald-600">{activeBreakCount}</span> Active</span>
        </div>
      </div>

      {error ? <div className="rounded-lg border border-[#f2c6c6] bg-[#fff0f0] px-3 py-2 text-sm text-[#8d2f2f]">{error}</div> : null}
      {message ? <div className="rounded-lg border border-[#c9e7d1] bg-[#eefcf1] px-3 py-2 text-sm text-[#22603a]">{message}</div> : null}

      <section className="surface p-4 shadow-sm">
        <div className="tab-toolbar mb-4">
          <div className="tab-toolbar-controls">
            <button
              type="button"
              onClick={() => setSection("breaks")}
              className={`inline-flex items-center gap-2 rounded-xl px-4 py-2 text-sm font-semibold transition ${
                section === "breaks"
                  ? "bg-[--primary] text-[--primary-foreground]"
                  : "bg-[#f4f9fd] text-[#31597f] hover:bg-[#eaf4ff]"
              }`}
            >
              <CalendarClock className="h-4 w-4" />
              Break Time
            </button>
            <button
              type="button"
              onClick={() => setSection("timetables")}
              className={`inline-flex items-center gap-2 rounded-xl px-4 py-2 text-sm font-semibold transition ${
                section === "timetables"
                  ? "bg-[--primary] text-[--primary-foreground]"
                  : "bg-[#f4f9fd] text-[#31597f] hover:bg-[#eaf4ff]"
              }`}
            >
              <CalendarDays className="h-4 w-4" />
              Time Table
            </button>
          </div>
          <div className="ml-auto flex w-full flex-wrap justify-end gap-2 xl:w-auto">
            {section === "timetables" ? (
              <button
                type="button"
                onClick={() => openTimetable()}
                className="inline-flex h-10 items-center gap-2 rounded-xl bg-[--primary] px-4 text-sm font-semibold text-[--primary-foreground]"
              >
                <Plus className="h-4 w-4" />
                Add Timetable
              </button>
            ) : (
              <button
                type="button"
                onClick={() => openBreak()}
                className="inline-flex h-10 items-center gap-2 rounded-xl bg-[--primary] px-4 text-sm font-semibold text-[--primary-foreground]"
              >
                <Plus className="h-4 w-4" />
                Add Break Time
              </button>
            )}
            <button
              type="button"
              onClick={() => {
                const current = readShiftConfigSnapshot();
                setSnapshot(current);
                setTimetableForm(createTimetableForm(defaultShiftId(current)));
                setBreakForm(createBreakForm());
                setMessage("Reloaded cached configuration.");
              }}
              className="inline-flex h-10 items-center gap-2 rounded-xl border border-[--border] bg-white px-4 text-sm font-semibold text-[#31597f]"
            >
              <RotateCcw className="h-4 w-4" />
              Reset
            </button>
          </div>
        </div>

        {loading ? (
          <div className="rounded-2xl border border-dashed border-[#d6e7f6] bg-[#fbfdff] px-4 py-10 text-center text-sm text-[#6d8398]">
            Loading timetable configuration...
          </div>
        ) : section === "timetables" ? (
          <div className="grid gap-4 xl:grid-cols-[300px_minmax(0,1fr)]">
            <aside className="rounded-2xl border border-[--border] bg-white p-4 shadow-sm">
              <div className="mb-3 text-sm font-semibold text-[#1f4f79]">Timetables</div>
              <div className="space-y-2">
                {snapshot.timetables.length > 0 ? (
                  snapshot.timetables.map((row) => (
                    <div
                      key={row.id}
                      className={`rounded-xl border px-3 py-3 transition ${
                        timetableForm.id === row.id
                          ? "border-[#7fb8e5] bg-[#eef7ff]"
                          : "border-[#dbe8f4] bg-[#fbfdff] hover:bg-white"
                      }`}
                    >
                      <div className="flex items-start gap-2">
                        <button
                          type="button"
                          onClick={() => openTimetable(row)}
                          className="min-w-0 flex-1 text-left"
                        >
                          <div className="truncate text-sm font-semibold text-[#173d61]">{row.name}</div>
                          <div className="mt-1 text-xs text-[#6c8297]">
                            {row.isActive ? "Active" : "Inactive"} | {summarizeTimetable(row, breakMap)}
                          </div>
                        </button>
                        <button
                          type="button"
                          onClick={() => void deleteTimetable(row.id)}
                          className="rounded-lg border border-red-200 px-2 py-1 text-xs text-red-600"
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </button>
                      </div>
                    </div>
                  ))
                ) : (
                  <div className="rounded-xl border border-dashed border-[#d6e7f6] px-3 py-4 text-sm text-[#6d8398]">
                    No timetable records yet.
                  </div>
                )}
              </div>
            </aside>

            <section className="rounded-2xl border border-[--border] bg-white p-4 shadow-sm">
              <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                <div>
                  <div className="text-base font-semibold text-[#1f4f79]">
                    {timetableForm.id ? "Edit Timetable" : "Create Timetable"}
                  </div>
                  <p className="text-xs text-[--muted-foreground]">
                    Define the weekly windows and connect each day to a shift and break profile.
                  </p>
                </div>
                <label className="flex items-center gap-2 rounded-lg border border-[--input] px-3 py-2 text-sm text-[#1f4f79]">
                  <input
                    type="checkbox"
                    checked={timetableForm.isActive}
                    onChange={(event) => setTimetableForm((current) => ({ ...current, isActive: event.target.checked }))}
                  />
                  Active
                </label>
              </div>

              <div className="mb-4 grid gap-4 md:grid-cols-2">
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-[11px] font-semibold text-[#1f4f79]">Timetable Name</span>
                  <input
                    value={timetableForm.name}
                    onChange={(event) => setTimetableForm((current) => ({ ...current, name: event.target.value }))}
                    placeholder="e.g. Office Weekly"
                    className={inputClass}
                  />
                </label>
                <div className="rounded-xl border border-[#dbe8f4] bg-[#fbfdff] px-3 py-3 text-xs text-[#6d8398]">
                  Define time structure and break requirements here. Assign timetables to employees/departments via Shift Assignment.
                </div>
              </div>

              <div className="space-y-2">
                {timetableForm.days.map((day) => {
                  return (
                    <div key={day.dayOfWeek} className={`rounded-xl border p-2.5 transition ${day.isWeeklyOff ? "border-[#e6edf4] bg-[#f7f9fc]" : "border-[#d6e7f6] bg-gradient-to-br from-white to-[#f4faff] shadow-sm"}`}>
                      <div className="mb-2 flex flex-wrap items-center justify-between gap-2 border-b border-[#e8eef5] pb-2">
                        <span className="inline-flex items-center gap-2 text-sm font-bold text-[#1f4f79]"><span className={`h-2 w-2 rounded-full ${day.isWeeklyOff ? "bg-slate-300" : "bg-emerald-500"}`} />{WEEKDAY_LABELS[day.dayOfWeek]}</span>
                        <button
                          type="button"
                          onClick={() =>
                            setTimetableForm((current) => ({
                              ...current,
                              days: current.days.map((item) =>
                                item.dayOfWeek === day.dayOfWeek
                                  ? { ...item, isWeeklyOff: !item.isWeeklyOff }
                                  : item,
                              ),
                            }))
                          }
                          className={`rounded-md border px-3 py-1 text-xs font-bold ${
                            day.isWeeklyOff
                              ? "border-[#d3e2f3] bg-white text-[#5f7f9f]"
                              : "border-[#1f5c8f] bg-[#1f5c8f] text-white"
                          }`}
                        >
                          {day.isWeeklyOff ? "OFF" : "WORKING"}
                        </button>
                      </div>

                      {!day.isWeeklyOff ? (
                        <div className="grid gap-2">
                          <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-6">
                            <label className="grid gap-1 text-xs text-[--muted-foreground]">
                             <span className="text-[11px] font-semibold text-[#1f4f79]">Check In Time</span>
                             <input
                               type="time"
                               value={day.checkInTime}
                               onChange={(event) =>
                                 setTimetableForm((current) => ({
                                   ...current,
                                   days: current.days.map((item) =>
                                     item.dayOfWeek === day.dayOfWeek
                                       ? { ...item, checkInTime: event.target.value }
                                       : item,
                                   ),
                                 }))
                               }
                               className={inputClass}
                             />
                            </label>
                            <label className="grid gap-1 text-xs text-[--muted-foreground]">
                              <span className="text-[11px] font-semibold text-[#1f4f79]">Check In Start</span>
                              <input
                                type="time"
                                value={day.checkInStart}
                                onChange={(event) =>
                                  setTimetableForm((current) => ({
                                    ...current,
                                    days: current.days.map((item) =>
                                      item.dayOfWeek === day.dayOfWeek
                                        ? { ...item, checkInStart: event.target.value }
                                        : item,
                                    ),
                                  }))
                                }
                                className={inputClass}
                              />
                            </label>
                            <label className="grid gap-1 text-xs text-[--muted-foreground]">
                              <span className="text-[11px] font-semibold text-[#1f4f79]">Check In End</span>
                              <input
                                type="time"
                                value={day.checkInEnd}
                                onChange={(event) =>
                                  setTimetableForm((current) => ({
                                    ...current,
                                    days: current.days.map((item) =>
                                      item.dayOfWeek === day.dayOfWeek
                                        ? { ...item, checkInEnd: event.target.value }
                                        : item,
                                    ),
                                  }))
                                }
                                className={inputClass}
                              />
                            </label>
                            <label className="grid gap-1 text-xs text-[--muted-foreground]">
                              <span className="text-[11px] font-semibold text-[#1f4f79]">Check Out Start</span>
                              <input
                                type="time"
                                value={day.checkOutStart}
                                onChange={(event) =>
                                  setTimetableForm((current) => ({
                                    ...current,
                                    days: current.days.map((item) =>
                                      item.dayOfWeek === day.dayOfWeek
                                        ? { ...item, checkOutStart: event.target.value }
                                        : item,
                                    ),
                                  }))
                                }
                                className={inputClass}
                              />
                            </label>
                            <label className="grid gap-1 text-xs text-[--muted-foreground]">
                              <span className="text-[11px] font-semibold text-[#1f4f79]">Check Out Time</span>
                              <input
                                type="time"
                                value={day.checkOutTime}
                                onChange={(event) =>
                                  setTimetableForm((current) => ({
                                    ...current,
                                    days: current.days.map((item) =>
                                      item.dayOfWeek === day.dayOfWeek
                                        ? { ...item, checkOutTime: event.target.value }
                                        : item,
                                    ),
                                  }))
                                }
                                className={inputClass}
                              />
                            </label>
                            <label className="grid gap-1 text-xs text-[--muted-foreground]">
                              <span className="text-[11px] font-semibold text-[#1f4f79]">Check Out End</span>
                              <input
                                type="time"
                                value={day.checkOutEnd}
                                onChange={(event) =>
                                  setTimetableForm((current) => ({
                                    ...current,
                                    days: current.days.map((item) =>
                                      item.dayOfWeek === day.dayOfWeek
                                        ? { ...item, checkOutEnd: event.target.value }
                                        : item,
                                    ),
                                  }))
                                }
                                className={inputClass}
                              />
                            </label>
                          </div>

                          <div className="flex flex-wrap items-center gap-3 border-t border-[#f0f4f8] pt-3">
                            <button
                              type="button"
                              aria-pressed={day.crossDay}
                              onClick={() =>
                                setTimetableForm((current) => ({
                                  ...current,
                                  days: current.days.map((item) =>
                                    item.dayOfWeek === day.dayOfWeek
                                      ? { ...item, crossDay: !item.crossDay }
                                      : item,
                                  ),
                                }))
                              }
                              className={`h-9 rounded-lg border px-3 text-xs font-semibold ${
                                day.crossDay
                                  ? "border-[#1f5c8f] bg-[#1f5c8f] text-white"
                                  : "border-[#d3e2f3] bg-white text-[#1f4f79]"
                              }`}
                            >
                              Cross day
                            </button>
                            <label className="flex h-9 items-center gap-2 rounded-lg border border-[#e1eef9] bg-[#f8fbff] px-3 text-xs font-medium text-[#1f4f79]">
                              <input
                                type="checkbox"
                                checked={day.useBreakTime}
                                onChange={(event) =>
                                  setTimetableForm((current) => ({
                                    ...current,
                                    days: current.days.map((item) => {
                                      if (item.dayOfWeek !== day.dayOfWeek) return item;
                                      const nextUseBreak = event.target.checked;
                                      const nextBreakIds = nextUseBreak
                                        ? item.breakTimeIds.length > 0
                                          ? item.breakTimeIds
                                          : snapshot.breakDefinitions[0]?.id
                                            ? [snapshot.breakDefinitions[0].id]
                                            : []
                                        : [];
                                      return {
                                        ...item,
                                        useBreakTime: nextUseBreak,
                                        breakTimeIds: nextBreakIds,
                                        breakTimeId: nextBreakIds[0] ?? null,
                                      };
                                    }),
                                  }))
                                }
                              />
                              Use break
                            </label>
                            {day.useBreakTime ? (
                              <label className="flex min-w-[220px] items-center gap-2">
                                <span className="shrink-0 text-[11px] font-bold uppercase tracking-wider text-[#5f7f9f]">
                                  Break profile
                                </span>
                                <select
                                  value={day.breakTimeIds[0] ?? ""}
                                  onChange={(event) => {
                                    const selected = event.target.value ? [event.target.value] : [];
                                    setTimetableForm((current) => ({
                                      ...current,
                                      days: current.days.map((item) =>
                                        item.dayOfWeek === day.dayOfWeek
                                          ? {
                                              ...item,
                                              breakTimeIds: selected,
                                              breakTimeId: selected[0] ?? null,
                                              useBreakTime: selected.length > 0,
                                            }
                                          : item,
                                      ),
                                    }));
                                  }}
                                  className="h-9 min-w-[150px] rounded-lg border border-[#d3e2f3] bg-white px-3 text-xs font-semibold text-[#1f4f79] outline-none"
                                  disabled={snapshot.breakDefinitions.length === 0}
                                >
                                  <option value="">Select break</option>
                                  {snapshot.breakDefinitions.length > 0 ? (
                                    snapshot.breakDefinitions.map((item) => (
                                      <option key={item.id} value={item.id}>
                                        {item.name}
                                      </option>
                                    ))
                                  ) : (
                                    <option value="">No break profiles</option>
                                  )}
                                </select>
                              </label>
                            ) : null}
                          </div>
                        </div>
                      ) : (
                        <div className="py-1 text-[11px] italic text-[#9db2c7]">
                          This day is marked as a weekly off day.
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>

              <div className="mt-6 flex justify-end gap-3 border-t border-[#f0f4f8] pt-4">
                <button
                  type="button"
                  onClick={() => setTimetableForm(createTimetableForm(fallbackShiftId))}
                  className="rounded-lg border border-[#d3e2f3] px-6 py-2 text-sm font-semibold text-[#5f7f9f] hover:bg-[#f8fbff]"
                >
                  Reset
                </button>
                <button
                  type="button"
                  disabled={saving}
                  onClick={() => void saveTimetable()}
                  className="inline-flex items-center gap-2 rounded-lg bg-[#1f5c8f] px-8 py-2 text-sm font-bold text-white shadow-md hover:bg-[#164a75] disabled:opacity-60"
                >
                  <Save className="h-4 w-4" />
                  {timetableForm.id ? "Update Timetable" : "Save Timetable"}
                </button>
              </div>
            </section>
          </div>
        ) : (
          <div className="grid gap-4 xl:grid-cols-[300px_minmax(0,1fr)]">
            <aside className="rounded-2xl border border-[--border] bg-white p-4 shadow-sm">
              <div className="mb-3 text-sm font-semibold text-[#1f4f79]">Break profiles</div>
              <div className="space-y-2">
                {snapshot.breakDefinitions.length > 0 ? (
                  snapshot.breakDefinitions.map((row) => (
                    <div
                      key={row.id}
                      className={`rounded-xl border px-3 py-3 transition ${
                        breakForm.id === row.id
                          ? "border-[#7fb8e5] bg-[#eef7ff]"
                          : "border-[#dbe8f4] bg-[#fbfdff] hover:bg-white"
                      }`}
                    >
                      <div className="flex items-start gap-2">
                        <button
                          type="button"
                          onClick={() => openBreak(row)}
                          className="min-w-0 flex-1 text-left"
                        >
                          <div className="truncate text-sm font-semibold text-[#173d61]">{row.name}</div>
                          <div className="mt-1 text-xs text-[#6c8297]">
                            {row.isActive ? "Active" : "Inactive"} | {summarizeBreak(row)}
                          </div>
                        </button>
                        <button
                          type="button"
                          onClick={() => void deleteBreak(row.id)}
                          className="rounded-lg border border-red-200 px-2 py-1 text-xs text-red-600"
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </button>
                      </div>
                    </div>
                  ))
                ) : (
                  <div className="rounded-xl border border-dashed border-[#d6e7f6] px-3 py-4 text-sm text-[#6d8398]">
                    No break profiles yet.
                  </div>
                )}
              </div>
            </aside>

            <section className="rounded-2xl border border-[--border] bg-white p-4 shadow-sm">
              <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                <div>
                  <div className="text-base font-semibold text-[#1f4f79]">
                    {breakForm.id ? "Edit Break Time" : "Create Break Time"}
                  </div>
                  <p className="text-xs text-[--muted-foreground]">
                    Define reusable breaks and attach them to timetable days.
                  </p>
                </div>
                <label className="flex items-center gap-2 rounded-lg border border-[--input] px-3 py-2 text-sm text-[#1f4f79]">
                  <input
                    type="checkbox"
                    checked={breakForm.isActive}
                    onChange={(event) => setBreakForm((current) => ({ ...current, isActive: event.target.checked }))}
                  />
                  Active
                </label>
              </div>

              <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                <label className="grid gap-1 text-xs text-[--muted-foreground] xl:col-span-2">
                  <span className="text-[11px] font-semibold text-[#1f4f79]">Break Name</span>
                  <input
                    value={breakForm.name}
                    onChange={(event) => setBreakForm((current) => ({ ...current, name: event.target.value }))}
                    placeholder="e.g. Lunch Break"
                    className={inputClass}
                  />
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-[11px] font-semibold text-[#1f4f79]">Calculate Type</span>
                  <select
                    value={breakForm.calculateType}
                    onChange={(event) =>
                      setBreakForm((current) => ({
                        ...current,
                        calculateType: event.target.value as BreakTimeDefinition["calculateType"],
                      }))
                    }
                    className={inputClass}
                  >
                    <option value="auto_deduct">Auto deduct</option>
                    <option value="required_punch">Required punch</option>
                  </select>
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-[11px] font-semibold text-[#1f4f79]">Start Time</span>
                  <input
                    type="time"
                    value={breakForm.startTime}
                    onChange={(event) => setBreakForm((current) => ({ ...current, startTime: event.target.value }))}
                    className={inputClass}
                  />
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-[11px] font-semibold text-[#1f4f79]">End Time</span>
                  <input
                    type="time"
                    value={breakForm.endTime}
                    onChange={(event) => setBreakForm((current) => ({ ...current, endTime: event.target.value }))}
                    className={inputClass}
                  />
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-[11px] font-semibold text-[#1f4f79]">Duration (minutes)</span>
                  <input
                    type="number"
                    min={0}
                    value={breakForm.durationMinutes}
                    onChange={(event) =>
                      setBreakForm((current) => ({
                        ...current,
                        durationMinutes: Number.parseInt(event.target.value, 10) || 0,
                      }))
                    }
                    className={inputClass}
                  />
                </label>
                <label className="grid gap-1 text-xs text-[--muted-foreground]">
                  <span className="text-[11px] font-semibold text-[#1f4f79]">Minimum Break Minutes</span>
                  <input
                    type="number"
                    min={0}
                    value={breakForm.minimumBreakMinutes}
                    onChange={(event) =>
                      setBreakForm((current) => ({
                        ...current,
                        minimumBreakMinutes: Number.parseInt(event.target.value, 10) || 0,
                      }))
                    }
                    className={inputClass}
                  />
                </label>
              </div>

              <div className="mt-6 flex justify-end gap-3 border-t border-[#f0f4f8] pt-4">
                <button
                  type="button"
                  onClick={() => setBreakForm(createBreakForm())}
                  className="rounded-lg border border-[#d3e2f3] px-6 py-2 text-sm font-semibold text-[#5f7f9f] hover:bg-[#f8fbff]"
                >
                  Reset
                </button>
                <button
                  type="button"
                  disabled={saving}
                  onClick={() => void saveBreak()}
                  className="inline-flex items-center gap-2 rounded-lg bg-[#1f5c8f] px-8 py-2 text-sm font-bold text-white shadow-md hover:bg-[#164a75] disabled:opacity-60"
                >
                  <Save className="h-4 w-4" />
                  {breakForm.id ? "Update Break Time" : "Save Break Time"}
                </button>
              </div>
            </section>
          </div>
        )}
      </section>
    </div>
  );
}
