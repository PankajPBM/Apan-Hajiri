"use client";

import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { weeklyTrend } from "@/components/data/mock-data";

export function AttendanceChart() {
  return (
    <article className="surface p-4 shadow-sm">
      <h3 className="text-sm font-semibold">Weekly Attendance Trend</h3>
      <p className="text-xs text-[--muted-foreground]">Present vs Absent vs Late</p>
      <div className="mt-4 h-72">
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart data={weeklyTrend}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
            <XAxis dataKey="day" />
            <YAxis />
            <Tooltip />
            <Area type="monotone" dataKey="present" stackId="1" stroke="var(--chart-1)" fill="var(--chart-1)" fillOpacity={0.6} />
            <Area type="monotone" dataKey="late" stackId="1" stroke="var(--chart-4)" fill="var(--chart-4)" fillOpacity={0.65} />
            <Area type="monotone" dataKey="absent" stackId="1" stroke="var(--chart-5)" fill="var(--chart-5)" fillOpacity={0.6} />
          </AreaChart>
        </ResponsiveContainer>
      </div>
    </article>
  );
}
