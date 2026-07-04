"use client";

import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { departmentAttendance } from "@/components/data/mock-data";

export function DepartmentChart() {
  return (
    <article className="surface p-4 shadow-sm">
      <h3 className="text-sm font-semibold">Department-wise Attendance</h3>
      <p className="text-xs text-[--muted-foreground]">Current day snapshot by department</p>
      <div className="mt-4 h-72">
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={departmentAttendance} layout="vertical" margin={{ left: 24 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
            <XAxis type="number" />
            <YAxis type="category" dataKey="department" width={100} />
            <Tooltip />
            <Bar dataKey="present" fill="var(--chart-2)" radius={[4, 4, 4, 4]} />
            <Bar dataKey="absent" fill="var(--chart-5)" radius={[4, 4, 4, 4]} />
          </BarChart>
        </ResponsiveContainer>
      </div>
    </article>
  );
}
