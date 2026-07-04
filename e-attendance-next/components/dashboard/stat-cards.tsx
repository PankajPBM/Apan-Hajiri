import { CalendarOff, Clock3, UserCheck, Users } from "lucide-react";
import { dashboardStats } from "@/components/data/mock-data";

const items = [
  { title: "Total Employees", value: dashboardStats.totalEmployees, hint: "No data", icon: Users },
  { title: "Present Today", value: dashboardStats.presentToday, hint: `${dashboardStats.attendanceRate}% attendance`, icon: UserCheck },
  { title: "On Leave", value: dashboardStats.onLeave, hint: `${dashboardStats.pendingLeaves} pending`, icon: CalendarOff },
  { title: "Late Arrivals", value: dashboardStats.lateArrivals, hint: "No data", icon: Clock3 },
];

export function StatCards() {
  return (
    <section className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      {items.map(({ title, value, hint, icon: Icon }) => (
        <article key={title} className="surface p-4 shadow-sm">
          <div className="flex items-center justify-between">
            <p className="text-sm text-[--muted-foreground]">{title}</p>
            <div className="rounded-lg bg-[--accent] p-2 text-[--accent-foreground]"><Icon className="h-4 w-4" /></div>
          </div>
          <p className="mt-3 text-3xl font-semibold">{value}</p>
          <p className="mt-1 text-xs text-[--muted-foreground]">{hint}</p>
        </article>
      ))}
    </section>
  );
}
