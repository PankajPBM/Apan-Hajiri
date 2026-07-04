export type Employee = {
  id: string;
  code: string;
  name: string;
  department: string;
  designation: string;
  phone: string;
  status: "active" | "inactive";
  avatar: string;
};

export type Device = {
  id: string;
  name: string;
  model: string;
  serial: string;
  ip: string;
  location: string;
  status: "online" | "offline" | "delayed";
  lastHeartbeat: string;
  lastPush: string;
};

export type AttendanceLog = {
  id: string;
  employeeCode: string;
  employeeName: string;
  department: string;
  direction: "IN" | "OUT";
  time: string;
  device: string;
  status: "on-time" | "late" | "early";
};

export const departments: Array<{ id: string; name: string; employeeCount: number }> = [];

export const shifts: Array<{ id: string; name: string; start: string; end: string }> = [];

export const employees: Employee[] = [];

export const devices: Device[] = [];

export const attendanceLogs: AttendanceLog[] = [];

export const leaveRequests: Array<{
  id: string;
  employee: string;
  type: string;
  from: string;
  to: string;
  status: string;
}> = [];

export const notifications: Array<{
  id: string;
  title: string;
  detail: string;
  level: string;
  unread: boolean;
}> = [];

export const weeklyTrend: Array<{ day: string; present: number; absent: number; late: number }> = [];

export const departmentAttendance: Array<{ department: string; present: number; absent: number }> = [];

export const monthlySummary: Array<{ date: string; attendanceRate: number }> = [];

export const dashboardStats = {
  totalEmployees: 0,
  presentToday: 0,
  onLeave: 0,
  lateArrivals: 0,
  attendanceRate: 0,
  pendingLeaves: 0,
};

export const todaySummary = {
  topLate: [] as Array<{ name: string; time: string; delay: string }>,
  earlyLeavers: [] as Array<{ name: string; time: string; earlyBy: string }>,
  absent: [] as string[],
};
