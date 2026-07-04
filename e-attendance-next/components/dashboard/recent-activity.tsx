import { attendanceLogs } from "@/components/data/mock-data";

export function RecentActivity() {
  return (
    <article className="surface p-4 shadow-sm">
      <h3 className="text-sm font-semibold">Recent Punch Activity</h3>
      <div className="mt-4 space-y-3">
        {attendanceLogs.slice(0, 10).map((log) => (
          <div key={log.id} className="flex items-center justify-between rounded-lg border border-[--border] bg-[--muted] px-3 py-2">
            <div>
              <p className="text-sm font-medium">{log.employeeName} <span className="text-xs text-[--muted-foreground]">({log.employeeCode})</span></p>
              <p className="text-xs text-[--muted-foreground]">{log.department} - {log.device}</p>
            </div>
            <div className="text-right">
              <p className="text-sm font-semibold">{log.time}</p>
              <span className={`inline-flex rounded-full px-2 py-0.5 text-xs ${log.direction === "IN" ? "badge-online" : "badge-pending"}`}>{log.direction}</span>
            </div>
          </div>
        ))}
      </div>
    </article>
  );
}
