import { todaySummary } from "@/components/data/mock-data";

export function TodaySummary() {
  return (
    <article className="surface p-4 shadow-sm">
      <h3 className="text-sm font-semibold">Today's Summary</h3>
      <div className="mt-4 grid gap-4 md:grid-cols-3">
        <div>
          <p className="text-xs font-semibold text-[--muted-foreground]">Top Late Comers</p>
          <ul className="mt-2 space-y-1 text-sm">
            {todaySummary.topLate.map((x) => (
              <li key={x.name} className="rounded-md bg-[--muted] px-2 py-1">{x.name} - {x.delay}</li>
            ))}
          </ul>
        </div>
        <div>
          <p className="text-xs font-semibold text-[--muted-foreground]">Early Leavers</p>
          <ul className="mt-2 space-y-1 text-sm">
            {todaySummary.earlyLeavers.map((x) => (
              <li key={x.name} className="rounded-md bg-[--muted] px-2 py-1">{x.name} - {x.earlyBy}</li>
            ))}
          </ul>
        </div>
        <div>
          <p className="text-xs font-semibold text-[--muted-foreground]">Absent</p>
          <ul className="mt-2 space-y-1 text-sm">
            {todaySummary.absent.map((x) => (
              <li key={x} className="rounded-md bg-[--muted] px-2 py-1">{x}</li>
            ))}
          </ul>
        </div>
      </div>
    </article>
  );
}
